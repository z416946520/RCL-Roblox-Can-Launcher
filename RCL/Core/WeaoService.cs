using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RCL.Core;

public sealed class WeaoExecutor
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("rbxversion")] public string RbxVersion { get; set; } = "";
    [JsonPropertyName("updateStatus")] public bool UpdateStatus { get; set; }
    [JsonPropertyName("free")] public bool Free { get; set; }
    [JsonPropertyName("platform")] public string Platform { get; set; } = "";
    [JsonPropertyName("detected")] public bool Detected { get; set; }
    [JsonPropertyName("updatedDate")] public string UpdatedDate { get; set; } = "";
    [JsonPropertyName("websitelink")] public string WebsiteLink { get; set; } = "";

    [JsonIgnore] public string SupportedVersionShort => ShortenVersion(RbxVersion);
    [JsonIgnore] public string StatusText => UpdateStatus ? "已更新" : "未更新";

    public static string ShortenVersion(string version)
    {
        if (version.StartsWith("version-", StringComparison.OrdinalIgnoreCase) && version.Length > 20)
            return "version-" + version[8..16] + "…";
        return version;
    }
}

public sealed class WeaoSnapshot
{
    public List<WeaoExecutor> WindowsExecutors { get; init; } = new();
    public string CurrentWindowsVersion { get; init; } = "";
    public DateTime FetchedAt { get; init; } = DateTime.Now;
    public string? Error { get; init; }

    public bool IsEmpty => WindowsExecutors.Count == 0;
}

public static class WeaoService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static WeaoSnapshot? _cache;
    private static DateTime _cacheAt = DateTime.MinValue;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) RCL/1.1");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    public static async Task<WeaoSnapshot> GetSnapshotAsync(bool force = false,
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!force && _cache is not null && DateTime.Now - _cacheAt < CacheLifetime)
                return _cache;

            string? error = null;
            var executors = new List<WeaoExecutor>();
            string current = "";

            try
            {
                var json = await Http.GetStringAsync("https://weao.xyz/api/status/exploits", cancellationToken)
                    .ConfigureAwait(false);
                var list = JsonSerializer.Deserialize<List<WeaoExecutor>>(json, JsonOpts.Read)
                           ?? new List<WeaoExecutor>();
                executors = list
                    .Where(e => e.Platform.Contains("Windows", StringComparison.OrdinalIgnoreCase) &&
                                !string.IsNullOrWhiteSpace(e.RbxVersion) &&
                                e.RbxVersion.StartsWith("version-", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                Log.Info($"WEAO：已获取 {executors.Count} 个 Windows 执行器的版本信息");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                error = "无法连接 WEAO：" + ex.Message;
                Log.Warn(error);
            }

            try
            {
                var json = await Http.GetStringAsync("https://weao.xyz/api/versions/current", cancellationToken)
                    .ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("Windows", out var windows))
                    current = windows.GetString() ?? "";
            }
            catch (Exception ex)
            {
                Log.Warn("WEAO 当前版本查询失败：" + ex.Message);
            }

            if (executors.Count == 0 && _cache is not null)
                return _cache;

            _cache = new WeaoSnapshot
            {
                WindowsExecutors = executors,
                CurrentWindowsVersion = current,
                FetchedAt = DateTime.Now,
                Error = error
            };
            _cacheAt = DateTime.Now;
            return _cache;
        }
        finally
        {
            Gate.Release();
        }
    }
}

public sealed class ExecutorVersionGroup
{
    public string Version { get; init; } = "";
    public List<WeaoExecutor> Executors { get; init; } = new();
    public bool IsCurrent { get; init; }
    public bool IsInstalled { get; init; }
    public string? InstallDirectory { get; init; }

    public int ExecutorCount => Executors.Count;
    public string ShortVersion => WeaoExecutor.ShortenVersion(Version);
    public string StateText => IsInstalled ? "已安装" : "未安装";
    public string ActionText => IsInstalled ? "绑定此版本" : "下载并绑定";
    public string ExecutorsDisplay => string.Join("、", Executors.Select(e => e.Title).Take(5)) +
                                      (Executors.Count > 5 ? $" 等 {Executors.Count} 个" : "");
}

public static class ExecutorVersionService
{
    public static List<ExecutorVersionGroup> BuildGroups(WeaoSnapshot snapshot)
    {
        var installed = RobloxInstall.ScanPlayerVersions()
            .GroupBy(v => v.FolderName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Directory, StringComparer.OrdinalIgnoreCase);

        return snapshot.WindowsExecutors
            .GroupBy(e => e.RbxVersion, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                installed.TryGetValue(g.Key, out var dir);
                return new ExecutorVersionGroup
                {
                    Version = g.Key,
                    Executors = g.OrderBy(e => e.Title, StringComparer.OrdinalIgnoreCase).ToList(),
                    IsCurrent = string.Equals(g.Key, snapshot.CurrentWindowsVersion,
                        StringComparison.OrdinalIgnoreCase),
                    IsInstalled = dir is not null,
                    InstallDirectory = dir
                };
            })
            .OrderByDescending(g => g.IsCurrent)
            .ThenByDescending(g => g.IsInstalled)
            .ThenByDescending(g => g.ExecutorCount)
            .ThenBy(g => g.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string? ResolveTargetVersion(WeaoSnapshot snapshot, string executorTitle)
    {
        if (!string.IsNullOrWhiteSpace(executorTitle))
        {
            var executor = snapshot.WindowsExecutors.FirstOrDefault(e =>
                e.Title.Equals(executorTitle, StringComparison.OrdinalIgnoreCase));
            return executor?.RbxVersion;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.CurrentWindowsVersion) &&
            snapshot.WindowsExecutors.Any(e => string.Equals(e.RbxVersion, snapshot.CurrentWindowsVersion,
                StringComparison.OrdinalIgnoreCase)))
            return snapshot.CurrentWindowsVersion;

        return snapshot.WindowsExecutors
            .GroupBy(e => e.RbxVersion, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();
    }

    public static string? FindInstalledDirectory(string versionGuid)
    {
        if (string.IsNullOrWhiteSpace(versionGuid))
            return null;
        var match = RobloxInstall.ScanPlayerVersions().FirstOrDefault(v =>
            v.FolderName.Equals(versionGuid, StringComparison.OrdinalIgnoreCase));
        return match?.Directory;
    }

    public static async Task<string> EnsureInstalledAsync(string versionGuid,
        IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var existing = FindInstalledDirectory(versionGuid);
        if (existing is not null)
            return existing;

        Log.Info("开始下载执行器支持版本：" + versionGuid);
        var deployment = new RobloxDeploymentInfo
        {
            Version = versionGuid,
            ClientVersionUpload = versionGuid
        };
        var directory = await RobloxDeployment.InstallAsync(deployment, progress, cancellationToken)
            .ConfigureAwait(false);
        Log.Info("执行器支持版本安装完成：" + versionGuid);
        return directory;
    }
}
