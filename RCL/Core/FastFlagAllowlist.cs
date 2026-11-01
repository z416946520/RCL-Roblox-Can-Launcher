using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace RCL.Core;

/// <summary>
/// Roblox publishes the flags it officially supports through DFStringAllowedPublicFlags.
/// RCL uses that list for labelling only: every flag the player enters is still written to
/// ClientAppSettings.json, the list just decides whether the UI shows a reminder.
/// </summary>
public static class FastFlagAllowlist
{
    private const string SettingsUrl =
        "https://clientsettingscdn.roblox.com/v2/settings/application/PCDesktopClient";

    private static readonly HttpClient Http = CreateClient();
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);
    private static readonly object Gate = new();

    // Until the official list has been fetched once, the embedded common flags stand in so the
    // UI does not flag everything while offline.
    private static HashSet<string> _official = new(FastFlags.KnownFlags, StringComparer.OrdinalIgnoreCase);
    private static DateTime _fetchedUtc = DateTime.MinValue;
    private static int _refreshing;

    public static event Action? Updated;

    private static string CacheFile => Path.Combine(AppPaths.Base, "cache", "public-flags.json");

    public static int OfficialCount
    {
        get { lock (Gate) return _official.Count; }
    }

    /// <summary>True when the flag is in Roblox's own public allowlist.</summary>
    public static bool IsOfficial(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        lock (Gate)
            return _official.Contains(name.Trim());
    }

    /// <summary>Official flag, or one of the common flags RCL ships presets for.</summary>
    public static bool IsKnown(string? name) =>
        IsOfficial(name) || (!string.IsNullOrWhiteSpace(name) && FastFlags.KnownFlags.Contains(name.Trim()));

    public static string Describe(string? name) =>
        IsOfficial(name) ? "Roblox 公开 flag"
        : !string.IsNullOrWhiteSpace(name) && FastFlags.KnownFlags.Contains(name.Trim())
            ? "常见 flag（不在官方公开列表）"
            : "不在 Roblox 公开列表，仍可保存";

    public static void LoadCache()
    {
        try
        {
            if (!File.Exists(CacheFile))
                return;
            using var doc = JsonDocument.Parse(File.ReadAllText(CacheFile));
            if (!doc.RootElement.TryGetProperty("flags", out var flags) || flags.ValueKind != JsonValueKind.Array)
                return;
            var names = flags.EnumerateArray()
                .Select(f => f.GetString())
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => f!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (names.Count == 0)
                return;

            DateTime fetched = DateTime.MinValue;
            if (doc.RootElement.TryGetProperty("fetchedUtc", out var when) &&
                when.TryGetDateTime(out var parsed))
                fetched = parsed.ToUniversalTime();

            lock (Gate)
            {
                _official = names;
                _fetchedUtc = fetched;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("读取 FastFlag 公开列表缓存失败：" + ex.Message);
        }
    }

    public static async Task RefreshAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (!force && DateTime.UtcNow - _fetchedUtc < MaxAge)
            return;
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
            return;

        try
        {
            using var response = await Http.GetAsync(SettingsUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"获取 Roblox 公开 FastFlag 列表失败：HTTP {(int)response.StatusCode}");
                return;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var names = Parse(body);
            if (names.Count == 0)
            {
                Log.Warn("Roblox 公开 FastFlag 列表为空，保留本地列表");
                return;
            }

            lock (Gate)
            {
                _official = names;
                _fetchedUtc = DateTime.UtcNow;
            }
            SaveCache(names);
            Log.Info($"已更新 Roblox 公开 FastFlag 列表：{names.Count} 条");
            Updated?.Invoke();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("获取 Roblox 公开 FastFlag 列表失败：" + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    /// <summary>
    /// The settings payload carries the allowlist as base64 JSON:
    /// { "Signature": "...", "Allowed": ["FFlag...", ...] }.
    /// </summary>
    private static HashSet<string> Parse(string payload)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("applicationSettings", out var settings) ||
                !settings.TryGetProperty("DFStringAllowedPublicFlags", out var blob) ||
                blob.GetString() is not { Length: > 0 } encoded)
                return names;

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            using var allowed = JsonDocument.Parse(json);
            if (!allowed.RootElement.TryGetProperty("Allowed", out var list) ||
                list.ValueKind != JsonValueKind.Array)
                return names;

            foreach (var item in list.EnumerateArray())
            {
                var name = item.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name.Trim());
            }
        }
        catch (Exception ex)
        {
            Log.Warn("解析 Roblox 公开 FastFlag 列表失败：" + ex.Message);
        }
        return names;
    }

    private static void SaveCache(HashSet<string> names)
    {
        try
        {
            var dir = Path.GetDirectoryName(CacheFile)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(new
            {
                fetchedUtc = DateTime.UtcNow.ToString("O"),
                flags = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray()
            }, JsonOpts.Write);
            File.WriteAllText(CacheFile, json, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log.Warn("保存 FastFlag 公开列表缓存失败：" + ex.Message);
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) RCL/1.1");
        return client;
    }
}
