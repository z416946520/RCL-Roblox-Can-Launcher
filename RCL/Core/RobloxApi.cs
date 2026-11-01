using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RCL.Core;

public sealed class RobloxDeploymentInfo
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("clientVersionUpload")] public string ClientVersionUpload { get; set; } = "";
    [JsonPropertyName("bootstrapperVersion")] public string BootstrapperVersion { get; set; } = "";

    public string ShortTag => ClientVersionUpload.StartsWith("version-", StringComparison.OrdinalIgnoreCase)
        ? ClientVersionUpload[8..]
        : ClientVersionUpload;
}

public sealed class RobloxGameInfo
{
    public long PlaceId { get; init; }
    public long UniverseId { get; init; }
    public string Name { get; init; } = "";
    public string Creator { get; init; } = "";
    public long Playing { get; init; }
    public long Visits { get; init; }
    public long MaxPlayers { get; init; }
    public string ThumbnailUrl { get; init; } = "";
}

public sealed class RobloxServerInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("playing")] public int Playing { get; set; }
    [JsonPropertyName("maxPlayers")] public int MaxPlayers { get; set; }
    [JsonPropertyName("fps")] public double Fps { get; set; }
    [JsonPropertyName("ping")] public int Ping { get; set; }
    [JsonPropertyName("playerTokens")] public List<string>? PlayerTokens { get; set; }

    public string PlayersDisplay => $"{Playing}/{MaxPlayers}";
    public string FpsDisplay => Fps <= 0 ? "—" : $"{Fps:0}";
    public string PingDisplay => Ping <= 0 ? "—" : $"{Ping} ms";
}

public static class RobloxApi
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly Dictionary<long, long> UniverseCache = new();
    private static readonly Regex PlaceIdRegex = new(@"(?:placeId=|/games/|/experiences/)(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DigitsRegex = new(@"^\d+$", RegexOptions.Compiled);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) RCL/1.0 (+https://github.com/)");
        return client;
    }

    public static long ParsePlaceId(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return 0;
        input = input.Trim();
        if (DigitsRegex.IsMatch(input))
            return long.TryParse(input, out var direct) ? direct : 0;
        var match = PlaceIdRegex.Match(input);
        return match.Success && long.TryParse(match.Groups[1].Value, out var id) ? id : 0;
    }

    public static async Task<RobloxDeploymentInfo> GetDeploymentAsync(string channel,
        CancellationToken cancellationToken = default)
    {
        var url = string.IsNullOrWhiteSpace(channel) || channel.Equals("default", StringComparison.OrdinalIgnoreCase)
            ? "https://clientsettingscdn.roblox.com/v2/client-version/WindowsPlayer"
            : $"https://clientsettingscdn.roblox.com/v2/client-version/WindowsPlayer/channel/{Uri.EscapeDataString(channel)}";

        using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"渠道「{channel}」不可用（HTTP {(int)response.StatusCode}）：{Trim(body)}");

        var info = JsonSerializer.Deserialize<RobloxDeploymentInfo>(body, JsonOpts.Read);
        if (info is null || string.IsNullOrWhiteSpace(info.ClientVersionUpload))
            throw new InvalidOperationException("部署接口返回了空版本信息。");
        return info;
    }

    public static async Task<long> ResolveUniverseIdAsync(long placeId, CancellationToken cancellationToken = default)
    {
        lock (UniverseCache)
        {
            if (UniverseCache.TryGetValue(placeId, out var cached))
                return cached;
        }

        using var response = await Http.PostAsync(
            $"https://apis.roblox.com/universes/v1/places/{placeId}/universe", null, cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"无法解析 Place {placeId}：{Trim(body)}");

        using var doc = JsonDocument.Parse(body);
        var universeId = doc.RootElement.TryGetProperty("universeId", out var el) ? el.GetInt64() : 0;
        if (universeId <= 0)
            throw new InvalidOperationException("该 Place 没有对应的游戏宇宙。");

        lock (UniverseCache)
            UniverseCache[placeId] = universeId;
        return universeId;
    }

    public static async Task<RobloxGameInfo> GetGameInfoAsync(long placeId,
        CancellationToken cancellationToken = default)
    {
        var universeId = await ResolveUniverseIdAsync(placeId, cancellationToken).ConfigureAwait(false);
        var body = await Http.GetStringAsync(
            $"https://games.roblox.com/v1/games?universeIds={universeId}", cancellationToken).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
            throw new InvalidOperationException("未查询到游戏信息。");

        var game = data[0];
        string creator = "";
        if (game.TryGetProperty("creator", out var creatorEl) &&
            creatorEl.TryGetProperty("name", out var creatorName))
            creator = creatorName.GetString() ?? "";

        var info = new RobloxGameInfo
        {
            PlaceId = placeId,
            UniverseId = universeId,
            Name = game.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "",
            Creator = creator,
            Playing = game.TryGetProperty("playing", out var playingEl) ? playingEl.GetInt64() : 0,
            Visits = game.TryGetProperty("visits", out var visitsEl) ? visitsEl.GetInt64() : 0,
            MaxPlayers = game.TryGetProperty("maxPlayers", out var maxEl) ? maxEl.GetInt64() : 0
        };

        try
        {
            var thumbBody = await Http.GetStringAsync(
                $"https://thumbnails.roblox.com/v1/games/multiget/thumbnails?universeIds={universeId}&size=512x512&format=Png&isCircular=false",
                cancellationToken).ConfigureAwait(false);
            using var thumbDoc = JsonDocument.Parse(thumbBody);
            if (thumbDoc.RootElement.TryGetProperty("data", out var thumbs) && thumbs.GetArrayLength() > 0 &&
                thumbs[0].TryGetProperty("thumbnails", out var list) && list.GetArrayLength() > 0 &&
                list[0].TryGetProperty("imageUrl", out var imageUrl))
                info = new RobloxGameInfo
                {
                    PlaceId = info.PlaceId,
                    UniverseId = info.UniverseId,
                    Name = info.Name,
                    Creator = info.Creator,
                    Playing = info.Playing,
                    Visits = info.Visits,
                    MaxPlayers = info.MaxPlayers,
                    ThumbnailUrl = imageUrl.GetString() ?? ""
                };
        }
        catch
        {
            // Thumbnails are cosmetic.
        }

        return info;
    }

    public static async Task<List<RobloxServerInfo>> GetPublicServersAsync(long placeId,
        CancellationToken cancellationToken = default)
    {
        var url = $"https://games.roblox.com/v1/games/{placeId}/servers/Public?limit=100&sortOrder=Desc";
        var body = await Http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data))
            return new List<RobloxServerInfo>();

        var list = JsonSerializer.Deserialize<List<RobloxServerInfo>>(data.GetRawText(), JsonOpts.Read)
                   ?? new List<RobloxServerInfo>();
        return list.Where(s => !string.IsNullOrWhiteSpace(s.Id)).ToList();
    }

    private static string Trim(string text) =>
        text.Length <= 180 ? text : text[..180] + "…";

    public static async Task<bool> CheckConnectivityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await Http.GetAsync("https://clientsettingscdn.roblox.com/v2/client-version/WindowsPlayer",
                cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
