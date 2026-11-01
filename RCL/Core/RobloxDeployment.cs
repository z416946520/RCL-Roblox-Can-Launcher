using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
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
        foreach (var host in CdnHosts)
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

    public static async Task<string> InstallAsync(
        RobloxDeploymentInfo deployment,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deployment.ClientVersionUpload))
            throw new InvalidOperationException("部署信息缺少 clientVersionUpload。");

        var versionGuid = deployment.ClientVersionUpload;
        var target = GetInstallDirectory(versionGuid);
        AppPaths.Ensure();

        var packages = await GetManifestAsync(versionGuid, cancellationToken).ConfigureAwait(false);
        var totalBytes = packages.Sum(p => p.CompressedSize);
        long receivedTotal = 0;

        Directory.CreateDirectory(target);

        var running = RobloxInstall.RunningCount();
        if (running > 0)
            Log.Warn($"安装时检测到 {running} 个 Roblox 进程正在运行，覆盖被占用的文件可能失败。");

        foreach (var package in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!package.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn("跳过不支持的包：" + package.Name);
                receivedTotal += package.CompressedSize;
                continue;
            }

            var temp = Path.Combine(AppPaths.Temp, versionGuid + "-" + package.Name);
            bool downloaded = false;
            Exception? last = null;

            foreach (var host in CdnHosts)
            {
                var url = $"{host}/{versionGuid}-{package.Name}";
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        progress?.Report(new InstallProgress
                        {
                            Percent = totalBytes <= 0 ? 0 : (double)receivedTotal / totalBytes * 100,
                            Message = $"下载 {package.Name}" + (attempt > 0 ? "（重试）" : ""),
                            Package = package.Name,
                            BytesReceived = receivedTotal,
                            TotalBytes = totalBytes
                        });

                        await DownloadAsync(url, temp, package.CompressedSize, package.Hash,
                            bytes => progress?.Report(new InstallProgress
                            {
                                Percent = totalBytes <= 0 ? 0 : (double)(receivedTotal + bytes) / totalBytes * 100,
                                Message = $"下载 {package.Name}",
                                Package = package.Name,
                                BytesReceived = receivedTotal + bytes,
                                TotalBytes = totalBytes
                            }), cancellationToken).ConfigureAwait(false);
                        downloaded = true;
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        Log.Warn($"从 {host} 下载 {package.Name} 失败（第 {attempt + 1} 次）：{ex.Message}");
                        if (attempt == 0)
                            await Task.Delay(700, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (downloaded)
                    break;
            }

            if (!downloaded)
                throw new InvalidOperationException(
                    $"下载 {package.Name} 失败，请检查网络或代理设置后重试。\n{last?.Message}", last);

            progress?.Report(new InstallProgress
            {
                Percent = totalBytes <= 0 ? 0 : (double)receivedTotal / totalBytes * 100,
                Message = $"解压 {package.Name}",
                Package = package.Name,
                BytesReceived = receivedTotal,
                TotalBytes = totalBytes
            });

            try
            {
                await Task.Run(() => ExtractSafely(temp, target), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    $"解压 {package.Name} 失败，可能是有 Roblox 进程正在运行并占用了文件。请先关闭 Roblox 再试。", ex);
            }
            finally
            {
                try { File.Delete(temp); } catch { }
            }

            receivedTotal += package.CompressedSize;
            progress?.Report(new InstallProgress
            {
                Percent = totalBytes <= 0 ? 0 : (double)receivedTotal / totalBytes * 100,
                Message = $"已解压 {package.Name}",
                Package = package.Name,
                BytesReceived = receivedTotal,
                TotalBytes = totalBytes
            });
        }

        if (!File.Exists(Path.Combine(target, "RobloxPlayerBeta.exe")))
            throw new InvalidDataException("安装完成后没有找到 RobloxPlayerBeta.exe，部署包可能不完整。");

        try
        {
            File.WriteAllText(Path.Combine(target, "rcl-install.json"), JsonSerializer.Serialize(new
            {
                channel = deployment.Version,
                versionGuid,
                installedAt = DateTime.Now.ToString("O"),
                by = "RCL 1.3.0"
            }, JsonOpts.Write), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log.Warn("写入安装标记失败：" + ex.Message);
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

    private static async Task DownloadAsync(string url, string destination, long expectedSize, string expectedHash,
        Action<long>? onBytes, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
            81920, true);

        var buffer = new byte[81920];
        long received = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            onBytes?.Invoke(received);
        }

        if (expectedSize > 0 && received < expectedSize * 0.9)
            throw new IOException($"文件不完整（{received} / {expectedSize} 字节）。");

        if (!string.IsNullOrWhiteSpace(expectedHash))
        {
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            target.Close();
            var actual = ComputeMd5(destination);
            if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(destination); } catch { }
                throw new IOException($"文件校验失败（MD5 不匹配：{actual}）。");
            }
        }
    }

    private static string ComputeMd5(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.MD5.HashData(stream));
    }
}
