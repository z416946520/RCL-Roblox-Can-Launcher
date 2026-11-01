using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RCL.Core;

public sealed class UpdateInfo
{
    public string Version { get; init; } = "";
    public string Url { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public string? Notes { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
}

/// <summary>
/// Checks a JSON manifest, downloads the new exe and verifies its SHA-256 before handing the
/// replacement to a short-lived PowerShell helper, because a running exe cannot overwrite itself.
/// </summary>
public static class UpdateService
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly string[] VersionKeys = { "version", "latest", "tag_name" };
    private static readonly string[] UrlKeys = { "url", "download", "exe" };
    private static readonly string[] HashKeys = { "sha256", "hash" };
    private static readonly string[] NoteKeys = { "notes", "changelog", "body" };
    private static readonly string[] DateKeys = { "publishedAt", "published_at", "date" };

    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public static UpdateInfo? LastFound { get; private set; }
    public static string? LastError { get; private set; }

    public static string UpdatesDirectory => Path.Combine(AppPaths.Base, "updates");

    public static async Task<UpdateInfo?> CheckAsync(string manifestUrl, CancellationToken ct = default)
    {
        LastError = null;
        LastFound = null;
        if (string.IsNullOrWhiteSpace(manifestUrl))
            throw new InvalidOperationException("还没有配置更新源。");

        var uri = ParseUri(manifestUrl);
        RequireSecure(uri, "更新源");
        var json = await ReadTextAsync(uri, ct);
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("更新清单不是 JSON 对象。");

        var remoteVersionText = ReadString(doc.RootElement, VersionKeys).Trim().TrimStart('v', 'V');
        if (!Version.TryParse(remoteVersionText, out var remoteVersion))
            throw new InvalidOperationException("更新清单缺少有效的 version。");

        var localVersion = Version.TryParse(CurrentVersion, out var local) ? local : new Version(0, 0, 0);
        if (remoteVersion <= localVersion)
            return null;

        var downloadText = ReadString(doc.RootElement, UrlKeys).Trim();
        if (downloadText.Length == 0)
            throw new InvalidOperationException("更新清单缺少下载地址 url。");

        var downloadUri = Uri.TryCreate(downloadText, UriKind.Absolute, out var absolute)
            ? absolute
            : new Uri(uri, downloadText);
        if (downloadUri.Scheme != Uri.UriSchemeHttp && downloadUri.Scheme != Uri.UriSchemeHttps &&
            !downloadUri.IsFile)
            throw new InvalidOperationException("下载地址只支持 https 或本地文件。");
        RequireSecure(downloadUri, "下载地址");

        var hash = ReadString(doc.RootElement, HashKeys).Trim().ToLowerInvariant();
        if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit))
            throw new InvalidOperationException("更新清单缺少有效的 sha256（64 位十六进制）。");

        DateTimeOffset? published = null;
        var dateText = ReadString(doc.RootElement, DateKeys);
        if (DateTimeOffset.TryParse(dateText, out var parsedDate))
            published = parsedDate;

        var info = new UpdateInfo
        {
            Version = remoteVersionText,
            Url = downloadUri.ToString(),
            Sha256 = hash,
            Notes = ReadString(doc.RootElement, NoteKeys).Trim(),
            PublishedAt = published
        };
        LastFound = info;
        Log.Info($"发现 RCL 新版本 {info.Version}（当前 {CurrentVersion}）");
        return info;
    }

    public static async Task<string> DownloadAsync(
        UpdateInfo info,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(UpdatesDirectory);
        var target = Path.Combine(UpdatesDirectory, $"RCL-{info.Version}.exe");
        var part = target + ".part";
        TryDelete(part);
        TryDelete(target);

        var uri = ParseUri(info.Url);
        long written = 0;
        long? total = null;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        try
        {
            if (uri.IsFile)
            {
                await using var source = File.OpenRead(uri.LocalPath);
                total = source.Length;
                await using var dest = File.Create(part);
                await CopyAsync(source, dest, hash, ct, n =>
                {
                    written += n;
                    progress?.Report(total > 0 ? (double)written / total.Value : -1);
                });
            }
            else
            {
                using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var dest = File.Create(part);
                await CopyAsync(source, dest, hash, ct, n =>
                {
                    written += n;
                    progress?.Report(total is > 0 ? (double)written / total.Value : -1);
                });
            }

            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!actual.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("更新文件校验失败，文件已删除。");

            File.Move(part, target, true);
            Log.Info($"更新已下载并通过校验：{target}");
            return target;
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    public static void ApplyAndRestart(string downloadedPath)
    {
        if (!File.Exists(downloadedPath))
            throw new FileNotFoundException("更新文件不存在。", downloadedPath);

        var target = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
            throw new InvalidOperationException("无法定位当前 RCL.exe，不能自动替换。");

        var targetDir = Path.GetDirectoryName(target);
        if (string.IsNullOrWhiteSpace(targetDir))
            throw new InvalidOperationException("无法定位 RCL.exe 所在目录。");

        try
        {
            var probe = Path.Combine(targetDir, ".rcl-write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("RCL 所在目录不可写，无法自动更新：" + ex.Message, ex);
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), $"rcl-update-{Guid.NewGuid():N}.ps1");
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            Wait-Process -Id {{Environment.ProcessId}} -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 900
            $target = {{PsQuote(target)}}
            $tmp = {{PsQuote(Path.Combine(targetDir, ".rcl-update-new.exe"))}}
            try {
                Copy-Item -LiteralPath {{PsQuote(downloadedPath)}} -Destination $tmp -Force
                $done = $false
                for ($i = 0; $i -lt 40; $i++) {
                    try { Move-Item -LiteralPath $tmp -Destination $target -Force; $done = $true; break }
                    catch { Start-Sleep -Milliseconds 500 }
                }
                if (-not $done) { Copy-Item -LiteralPath $tmp -Destination $target -Force }
            } finally {
                Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
                Start-Process -FilePath $target -WorkingDirectory {{PsQuote(targetDir)}}
            }
            Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
            """;
        File.WriteAllText(scriptPath, script, new UTF8Encoding(false));
        Process.Start(new ProcessStartInfo(
            "powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
        Log.Info("已启动更新替换程序，RCL 即将退出");
        System.Windows.Application.Current.Shutdown();
    }

    private static async Task CopyAsync(
        Stream source,
        Stream dest,
        IncrementalHash hash,
        CancellationToken ct,
        Action<int> onRead)
    {
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await dest.WriteAsync(buffer.AsMemory(0, read), ct);
            hash.AppendData(buffer, 0, read);
            onRead(read);
        }
    }

    private static async Task<string> ReadTextAsync(Uri uri, CancellationToken ct)
    {
        if (uri.IsFile)
            return await File.ReadAllTextAsync(uri.LocalPath, ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await Http.GetAsync(uri, timeout.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(timeout.Token);
    }

    private static void RequireSecure(Uri uri, string label)
    {
        if (uri.IsFile || uri.Scheme == Uri.UriSchemeHttps)
            return;
        if (uri.Scheme == Uri.UriSchemeHttp &&
            (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
             uri.Host == "127.0.0.1" || uri.Host == "::1"))
            return;
        throw new InvalidOperationException($"{label}必须使用 HTTPS（本机地址与本地文件除外）。");
    }

    private static Uri ParseUri(string value)
    {
        value = value.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.IsFile))
            return uri;

        if (Path.IsPathRooted(value) && File.Exists(value))
            return new Uri(Path.GetFullPath(value));

        throw new InvalidOperationException("更新源地址无效，需要 HTTPS 或本地文件路径。");
    }

    private static string ReadString(JsonElement root, string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? "";
        }
        return "";
    }

    private static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // A previous update may still be locked; it will be overwritten next time.
        }
    }
}
