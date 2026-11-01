using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RCL.Core;

public sealed class InstallProgress
{
    public double Percent { get; init; }
    public string Message { get; init; } = "";
    public long BytesReceived { get; init; }
    public long TotalBytes { get; init; }
    public string Package { get; init; } = "";
}

public sealed record RobloxPackage(string Name, string Hash, long CompressedSize, long UncompressedSize);

public static class RobloxDeployment
{
    private static readonly string[] CdnHosts =
    {
        "https://setup.rbxcdn.com",
        "https://setup-aws.rbxcdn.com",
        "https://setup-cfly.rbxcdn.com"
    };

    private static readonly HttpClient Http = CreateClient();

    // Voidstrap's thresholds: anything that fits in one go is streamed, larger packages are cut
    // into ranged segments so a dropped connection only costs that segment.
    private const long SegmentThreshold = 4L * 1024 * 1024;
    private const long SegmentSize = 8L * 1024 * 1024;
    private const int MaxSegments = 16;
    private const int SegmentConcurrency = 4;

    private static readonly TimeSpan StaleStagingAge = TimeSpan.FromHours(6);

    private static readonly object ScoreGate = new();
    private static readonly Dictionary<string, double> HostScores = new(StringComparer.OrdinalIgnoreCase);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Roblox/WinInet");
        return client;
    }

    public static async Task<List<RobloxPackage>> GetManifestAsync(string versionGuid,
        CancellationToken cancellationToken = default)
    {
        Exception? last = null;
        foreach (var host in RankedHosts())
        {
            try
            {
                var url = $"{host}/{versionGuid}-rbxPkgManifest.txt";
                var text = await Http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
                var packages = ParseManifest(text);
                if (packages.Count == 0)
                    throw new InvalidDataException("部署清单为空。");
                return packages;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                Log.Warn($"从 {host} 获取部署清单失败：{ex.Message}");
            }
        }
        throw new InvalidOperationException("无法获取 Roblox 部署清单。请检查网络后重试。", last);
    }

    public static List<RobloxPackage> ParseManifest(string text)
    {
        var lines = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).ToList();
        var result = new List<RobloxPackage>();
        int index = 0;
        if (lines.Count > 0 && lines[0].StartsWith("v", StringComparison.OrdinalIgnoreCase) &&
            lines[0].Length <= 3)
            index = 1;

        while (index + 3 < lines.Count)
        {
            var name = lines[index];
            var hash = lines[index + 1];
            // skip malformed blank or hash-only groups
            if (!long.TryParse(lines[index + 2], out var compressed) ||
                !long.TryParse(lines[index + 3], out var uncompressed))
            {
                index++;
                continue;
            }
            result.Add(new RobloxPackage(name, hash, compressed, uncompressed));
            index += 4;
        }
        return result;
    }

    public static string GetInstallDirectory(string versionGuid) =>
        Path.Combine(AppPaths.RobloxVersions, versionGuid);

    public static bool IsInstalled(string versionGuid) =>
        File.Exists(Path.Combine(GetInstallDirectory(versionGuid), "RobloxPlayerBeta.exe"));

    /// <summary>
    /// Downloads every package into a staging folder, verifies it, and only then swaps it into
    /// place. A failed or cancelled run leaves the previous installation untouched, and the
    /// downloaded parts stay around so the next attempt resumes instead of starting over.
    /// </summary>
    public static async Task<string> InstallAsync(
        RobloxDeploymentInfo deployment,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deployment.ClientVersionUpload))
            throw new InvalidOperationException("部署信息缺少 clientVersionUpload。");

        var versionGuid = deployment.ClientVersionUpload;
        var target = GetInstallDirectory(versionGuid);
        var staging = target + ".installing";
        var downloads = Path.Combine(AppPaths.Temp, "downloads", versionGuid);

        AppPaths.Ensure();
        CleanStaleStaging();

        var packages = await GetManifestAsync(versionGuid, cancellationToken).ConfigureAwait(false);
        var totalBytes = packages.Sum(p => p.CompressedSize);
        long receivedTotal = 0;

        var running = RobloxInstall.RunningCount();
        if (running > 0)
            Log.Warn($"安装时检测到 {running} 个 Roblox 进程正在运行，替换安装目录可能失败。");

        TryDeleteDirectory(staging);
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(downloads);

        try
        {
            foreach (var package in packages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!package.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warn("跳过不支持的包：" + package.Name);
                    receivedTotal += package.CompressedSize;
                    continue;
                }

                var zipPath = Path.Combine(downloads, package.Name);
                var packageBase = receivedTotal;

                progress?.Report(new InstallProgress
                {
                    Percent = Percent(packageBase, totalBytes),
                    Message = $"下载 {package.Name}",
                    Package = package.Name,
                    BytesReceived = packageBase,
                    TotalBytes = totalBytes
                });

                try
                {
                    await DownloadPackageAsync(versionGuid, package, zipPath, bytes =>
                    {
                        progress?.Report(new InstallProgress
                        {
                            Percent = Percent(packageBase + bytes, totalBytes),
                            Message = $"下载 {package.Name}",
                            Package = package.Name,
                            BytesReceived = packageBase + bytes,
                            TotalBytes = totalBytes
                        });
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"下载 {package.Name} 失败，请检查网络或代理设置后重试（已下载的部分会保留）。\n{ex.Message}", ex);
                }

                progress?.Report(new InstallProgress
                {
                    Percent = Percent(packageBase, totalBytes),
                    Message = $"解压 {package.Name}",
                    Package = package.Name,
                    BytesReceived = packageBase,
                    TotalBytes = totalBytes
                });

                try
                {
                    await Task.Run(() => ExtractSafely(zipPath, staging), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    throw new InvalidOperationException(
                        $"解压 {package.Name} 失败：{ex.Message}", ex);
                }
                finally
                {
                    TryDelete(zipPath);
                }

                receivedTotal += package.CompressedSize;
                progress?.Report(new InstallProgress
                {
                    Percent = Percent(receivedTotal, totalBytes),
                    Message = $"已解压 {package.Name}",
                    Package = package.Name,
                    BytesReceived = receivedTotal,
                    TotalBytes = totalBytes
                });
            }

            if (!File.Exists(Path.Combine(staging, "RobloxPlayerBeta.exe")))
                throw new InvalidDataException("安装完成后没有找到 RobloxPlayerBeta.exe，部署包可能不完整。");

            WriteInstallMarker(staging, deployment.Version, versionGuid);
            CommitStaging(staging, target);
            TryDeleteDirectory(downloads);
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }

        progress?.Report(new InstallProgress
        {
            Percent = 100,
            Message = "安装完成",
            BytesReceived = totalBytes,
            TotalBytes = totalBytes
        });
        Log.Info($"Roblox {deployment.Version} 已安装到 {target}");
        return target;
    }

    private static double Percent(long received, long total) =>
        total <= 0 ? 0 : Math.Min(100, (double)received / total * 100);

    // ---------- download ----------

    private sealed class SegmentPlan
    {
        public long Size { get; set; }
        public string Hash { get; set; } = "";
        public List<SegmentRange> Segments { get; set; } = new();
    }

    private sealed class SegmentRange
    {
        public int Index { get; set; }
        public long Start { get; set; }
        public long End { get; set; }
    }

    private static async Task DownloadPackageAsync(
        string versionGuid,
        RobloxPackage package,
        string zipPath,
        Action<long> onReceived,
        CancellationToken cancellationToken)
    {
        if (package.CompressedSize >= SegmentThreshold)
            await DownloadSegmentedAsync(versionGuid, package, zipPath, onReceived, cancellationToken)
                .ConfigureAwait(false);
        else
            await DownloadSingleAsync(versionGuid, package, zipPath, onReceived, cancellationToken)
                .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(package.Hash))
        {
            var actual = ComputeMd5(zipPath);
            if (!string.Equals(actual, package.Hash, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(zipPath);
                throw new IOException($"文件校验失败（MD5 不匹配：{actual}）。");
            }
        }
        TryDelete(zipPath + ".meta");
    }

    private static async Task DownloadSingleAsync(
        string versionGuid,
        RobloxPackage package,
        string zipPath,
        Action<long> onReceived,
        CancellationToken cancellationToken)
    {
        var part = zipPath + ".part";
        var meta = zipPath + ".meta";
        long existing = 0;

        if (File.Exists(part) && MetaMatches(meta, package))
            existing = Math.Min(new FileInfo(part).Length, package.CompressedSize);
        else
        {
            TryDelete(part);
            WriteMeta(meta, package);
        }

        Exception? last = null;
        foreach (var host in RankedHosts())
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stopwatch = Stopwatch.StartNew();
                var startedAt = existing;
                try
                {
                    var url = $"{host}/{versionGuid}-{package.Name}";
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    if (existing > 0)
                        request.Headers.Range = new RangeHeaderValue(existing, null);

                    using var response = await Http
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                        .ConfigureAwait(false);

                    if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                    {
                        // The part file is already as long as the package: nothing left to fetch.
                        if (existing >= package.CompressedSize && File.Exists(part))
                        {
                            File.Move(part, zipPath, true);
                            return;
                        }
                        existing = 0;
                        TryDelete(part);
                        continue;
                    }

                    if (existing > 0 && response.StatusCode != HttpStatusCode.PartialContent)
                    {
                        // The host ignored the range; start this package over.
                        existing = 0;
                        TryDelete(part);
                    }
                    response.EnsureSuccessStatusCode();

                    await using var source = await response.Content
                        .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

                    long received = existing;
                    // The sink has to be closed before the part file can be renamed.
                    await using (var sink = new FileStream(part,
                                     existing > 0 ? FileMode.Append : FileMode.Create,
                                     FileAccess.Write, FileShare.None, 81920, true))
                    {
                        var buffer = new byte[81920];
                        int read;
                        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            await sink.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                            received += read;
                            onReceived(received);
                        }
                        await sink.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }

                    ScoreHost(host, received - startedAt, stopwatch.Elapsed, false);
                    File.Move(part, zipPath, true);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    ScoreHost(host, 0, stopwatch.Elapsed, true);
                    existing = File.Exists(part) ? new FileInfo(part).Length : 0;
                    Log.Warn($"从 {host} 下载 {package.Name} 失败（第 {attempt + 1} 次）：{ex.Message}");
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        throw new IOException($"下载 {package.Name} 失败，所有镜像均不可用。{last?.Message}", last);
    }

    private static async Task DownloadSegmentedAsync(
        string versionGuid,
        RobloxPackage package,
        string zipPath,
        Action<long> onReceived,
        CancellationToken cancellationToken)
    {
        var directory = zipPath + ".segments";
        var planPath = Path.Combine(directory, "plan.json");
        var plan = LoadPlan(planPath, package);
        if (plan is null)
        {
            TryDeleteDirectory(directory);
            Directory.CreateDirectory(directory);
            plan = BuildPlan(package);
            File.WriteAllText(planPath, JsonSerializer.Serialize(plan));
        }

        var received = new long[plan.Segments.Count];
        foreach (var segment in plan.Segments)
            received[segment.Index] = ExistingSegmentLength(directory, segment);

        void Report() =>
            onReceived(Math.Min(received.Sum(), plan.Size));

        var hosts = RankedHosts();
        await Parallel.ForEachAsync(
            plan.Segments,
            new ParallelOptions { MaxDegreeOfParallelism = SegmentConcurrency, CancellationToken = cancellationToken },
            async (segment, token) =>
            {
                var path = SegmentPath(directory, segment);
                var length = segment.End - segment.Start + 1;
                var have = ExistingSegmentLength(directory, segment);
                if (have >= length)
                    return;

                Exception? last = null;
                foreach (var host in hosts)
                {
                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        var stopwatch = Stopwatch.StartNew();
                        try
                        {
                            var url = $"{host}/{versionGuid}-{package.Name}";
                            using var request = new HttpRequestMessage(HttpMethod.Get, url);
                            request.Headers.Range = new RangeHeaderValue(segment.Start + have, segment.End);

                            using var response = await Http
                                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                                .ConfigureAwait(false);

                            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                            {
                                // Nothing left in this range; treat the segment as complete.
                                received[segment.Index] = length;
                                Report();
                                return;
                            }
                            response.EnsureSuccessStatusCode();

                            await using var source = await response.Content
                                .ReadAsStreamAsync(token).ConfigureAwait(false);
                            await using var sink = new FileStream(path,
                                have > 0 ? FileMode.Append : FileMode.Create,
                                FileAccess.Write, FileShare.None, 81920, true);

                            var buffer = new byte[81920];
                            int read;
                            while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                            {
                                await sink.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                                have += read;
                                received[segment.Index] = have;
                                Report();
                            }
                            await sink.FlushAsync(token).ConfigureAwait(false);

                            ScoreHost(host, have, stopwatch.Elapsed, false);
                            return;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            last = ex;
                            ScoreHost(host, 0, stopwatch.Elapsed, true);
                            have = ExistingSegmentLength(directory, segment);
                            Log.Warn($"从 {host} 下载 {package.Name} 分段 {segment.Index} 失败（第 {attempt + 1} 次）：{ex.Message}");
                            await Task.Delay(400, token).ConfigureAwait(false);
                        }
                    }
                }
                throw new IOException($"分段下载 {package.Name} 失败。{last?.Message}", last);
            }).ConfigureAwait(false);

        await using (var output = new FileStream(zipPath, FileMode.Create, FileAccess.Write,
                         FileShare.None, 81920, true))
        {
            foreach (var segment in plan.Segments.OrderBy(s => s.Start))
            {
                var path = SegmentPath(directory, segment);
                var expected = segment.End - segment.Start + 1;
                var actual = new FileInfo(path).Length;
                if (actual != expected)
                    throw new IOException($"分段 {segment.Index} 不完整（{actual} / {expected} 字节）。");

                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
        }

        TryDeleteDirectory(directory);
        TryDelete(zipPath + ".part");
        onReceived(plan.Size);
    }

    private static SegmentPlan BuildPlan(RobloxPackage package)
    {
        var size = package.CompressedSize;
        var count = (int)Math.Min(MaxSegments, Math.Max(1, (size + SegmentSize - 1) / SegmentSize));
        var chunk = (size + count - 1) / count;
        var plan = new SegmentPlan { Size = size, Hash = package.Hash };
        for (var i = 0; i < count; i++)
        {
            var start = i * chunk;
            if (start >= size)
                break;
            plan.Segments.Add(new SegmentRange
            {
                Index = i,
                Start = start,
                End = Math.Min(size - 1, start + chunk - 1)
            });
        }
        return plan;
    }

    private static SegmentPlan? LoadPlan(string planPath, RobloxPackage package)
    {
        try
        {
            if (!File.Exists(planPath))
                return null;
            var plan = JsonSerializer.Deserialize<SegmentPlan>(File.ReadAllText(planPath));
            if (plan is null || plan.Segments.Count == 0)
                return null;
            if (plan.Size != package.CompressedSize ||
                !string.Equals(plan.Hash, package.Hash, StringComparison.OrdinalIgnoreCase))
                return null;
            return plan;
        }
        catch
        {
            return null;
        }
    }

    private static string SegmentPath(string directory, SegmentRange segment) =>
        Path.Combine(directory, "seg-" + segment.Index + ".bin");

    private static long ExistingSegmentLength(string directory, SegmentRange segment)
    {
        try
        {
            var path = SegmentPath(directory, segment);
            if (!File.Exists(path))
                return 0;
            var expected = segment.End - segment.Start + 1;
            return Math.Min(new FileInfo(path).Length, expected);
        }
        catch
        {
            return 0;
        }
    }

    private static bool MetaMatches(string metaPath, RobloxPackage package)
    {
        try
        {
            if (!File.Exists(metaPath))
                return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
            var root = doc.RootElement;
            return root.TryGetProperty("size", out var size) &&
                   size.TryGetInt64(out var value) && value == package.CompressedSize &&
                   root.TryGetProperty("hash", out var hash) &&
                   string.Equals(hash.GetString(), package.Hash, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void WriteMeta(string metaPath, RobloxPackage package)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(metaPath)!);
            File.WriteAllText(metaPath, JsonSerializer.Serialize(new
            {
                size = package.CompressedSize,
                hash = package.Hash
            }));
        }
        catch (Exception ex)
        {
            Log.Warn("写入下载断点信息失败：" + ex.Message);
        }
    }

    // ---------- mirrors ----------

    private static string[] RankedHosts()
    {
        lock (ScoreGate)
            return CdnHosts
                .OrderByDescending(host => HostScores.TryGetValue(host, out var score) ? score : 0d)
                .ToArray();
    }

    /// <summary>
    /// Keeps a smoothed throughput score per mirror, so a slow or failing host drops behind the
    /// others for the rest of the run.
    /// </summary>
    private static void ScoreHost(string host, double bytes, TimeSpan elapsed, bool failed)
    {
        var rate = failed || elapsed.TotalSeconds <= 0
            ? 0d
            : bytes / elapsed.TotalSeconds / (1024 * 1024);
        lock (ScoreGate)
        {
            if (failed)
            {
                HostScores[host] = 0d;
                return;
            }
            var current = HostScores.TryGetValue(host, out var score) ? score : rate;
            HostScores[host] = current <= 0 ? rate : current * 0.6 + rate * 0.4;
        }
    }

    // ---------- install ----------

    private static void CommitStaging(string staging, string target)
    {
        var backup = target + ".old";
        try
        {
            if (Directory.Exists(target))
            {
                TryDeleteDirectory(backup);
                Directory.Move(target, backup);
                try
                {
                    Directory.Move(staging, target);
                }
                catch
                {
                    Directory.Move(backup, target);
                    throw;
                }
                TryDeleteDirectory(backup);
                return;
            }
            Directory.Move(staging, target);
        }
        catch (IOException ex)
        {
            throw new IOException(
                "无法替换安装目录，可能是有 Roblox 进程正在运行并占用文件。请先关闭 Roblox 后重试。\n" + ex.Message, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException("没有权限写入 Roblox 版本目录。请检查目录权限后重试。\n" + ex.Message, ex);
        }
    }

    private static void CleanStaleStaging()
    {
        try
        {
            if (!Directory.Exists(AppPaths.RobloxVersions))
                return;
            foreach (var dir in Directory.EnumerateDirectories(AppPaths.RobloxVersions))
            {
                var name = Path.GetFileName(dir);
                if (!name.EndsWith(".installing", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".old", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) < StaleStagingAge)
                    continue;
                TryDeleteDirectory(dir);
                Log.Info("已清理残留的安装暂存目录：" + name);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("清理安装暂存目录失败：" + ex.Message);
        }
    }

    private static void WriteInstallMarker(string directory, string channel, string versionGuid)
    {
        try
        {
            File.WriteAllText(Path.Combine(directory, "rcl-install.json"), JsonSerializer.Serialize(new
            {
                channel,
                versionGuid,
                installedAt = DateTime.Now.ToString("O"),
                by = "RCL " + UpdateService.CurrentVersion
            }, JsonOpts.Write), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log.Warn("写入安装标记失败：" + ex.Message);
        }
    }

    // ---------- files ----------

    private static void ExtractSafely(string zipPath, string targetDirectory)
    {
        var root = Path.GetFullPath(targetDirectory);
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        int skipped = 0;

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var raw = entry.FullName.Replace('\\', '/').TrimStart('/');
            if (raw.Length == 0)
                continue;
            if (raw.EndsWith('/'))
            {
                Directory.CreateDirectory(Path.Combine(root, raw.TrimEnd('/')));
                continue;
            }

            var segments = raw.Split('/');
            if (segments.Any(s => s is ".." or ".") || raw.Contains(':'))
            {
                skipped++;
                Log.Warn("跳过可疑压缩包路径：" + entry.FullName);
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine(root, raw));
            if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                Log.Warn("跳过越界压缩包路径：" + entry.FullName);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, true);
        }

        if (skipped > 0)
            Log.Warn($"解压 {Path.GetFileName(zipPath)} 时跳过了 {skipped} 个异常路径");
    }

    private static string ComputeMd5(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.MD5.HashData(stream));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
        }
    }
}
