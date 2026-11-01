using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RCL.Core;

public sealed record AuthenticatedRobloxUser(long Id, string Name, string DisplayName);

public static class RobloxAccountApi
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) RCL/1.1");
        return client;
    }

    public static string SanitizeCookie(string input)
    {
        input = input.Trim().Trim('"');
        var marker = input.IndexOf(".ROBLOSECURITY", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
        {
            var rest = input[(marker + ".ROBLOSECURITY".Length)..];
            if (rest.StartsWith("="))
                rest = rest[1..];
            else if (rest.StartsWith('\t'))
                rest = rest[1..];
            rest = rest.TrimStart(' ', '\t', ':');
            var end = rest.IndexOfAny(new[] { ';', '\r', '\n', '\t', '"' });
            if (end >= 0)
                rest = rest[..end];
            input = rest.Trim();
        }
        else
        {
            var end = input.IndexOfAny(new[] { ';', '\r', '\n', '\t', '"' });
            if (end >= 0)
                input = input[..end];
        }
        return input.Trim().Trim('"');
    }

    public static bool LooksPlausible(string cookie) =>
        cookie.Length >= 50 &&
        !cookie.Contains('\n') &&
        !cookie.Contains('\r') &&
        !cookie.Contains(' ');

    public static async Task<(AuthenticatedRobloxUser? User, string? Error)> GetAuthenticatedAsync(
        string cookie, CancellationToken cancellationToken = default)
    {
        cookie = SanitizeCookie(cookie);
        if (!LooksPlausible(cookie))
            return (null, "Cookie 格式不正确，请粘贴完整的 .ROBLOSECURITY。");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://users.roblox.com/v1/users/authenticated");
            request.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
            request.Headers.Referrer = new Uri("https://www.roblox.com/");
            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or
                System.Net.HttpStatusCode.Forbidden)
                return (null, $"无法验证 Cookie（Roblox 返回 {(int)response.StatusCode}），可能是登录已失效或当前网络限制了该接口。");
            if (!response.IsSuccessStatusCode)
                return (null, $"Roblox 返回 HTTP {(int)response.StatusCode}，请稍后再试。");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id) || id <= 0)
                return (null, "无法从 Roblox 获取账号信息。");

            var name = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
            var display = root.TryGetProperty("displayName", out var displayEl)
                ? displayEl.GetString() ?? name
                : name;
            return (new AuthenticatedRobloxUser(id, name, display), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Warn("验证 Roblox Cookie 超时");
            return (null, "网络超时，暂时无法验证 Cookie。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("验证 Roblox Cookie 失败：" + ex.Message);
            return (null, "网络连接失败，无法验证 Cookie。");
        }
    }

    public static async Task<byte[]?> FetchAvatarAsync(long userId,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0)
            return null;
        try
        {
            var url =
                $"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={userId}&size=150x150&format=Png&isCircular=true";
            using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
                return null;
            if (!data[0].TryGetProperty("imageUrl", out var imageEl))
                return null;
            var imageUrl = imageEl.GetString();
            if (string.IsNullOrWhiteSpace(imageUrl))
                return null;
            return await Http.GetByteArrayAsync(imageUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("下载 Roblox 头像失败：" + ex.Message);
            return null;
        }
    }
}

public sealed class AccountFile
{
    [JsonPropertyName("ActiveUserId")] public long ActiveUserId { get; set; }
    [JsonPropertyName("Accounts")] public List<Models.RobloxAccount> Accounts { get; set; } = new();
}
