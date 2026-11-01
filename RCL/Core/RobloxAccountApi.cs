using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RCL.Core;

public sealed record AuthenticatedRobloxUser(long Id, string Name, string DisplayName);

public static class RobloxAccountApi
{
    private static readonly HttpClient Http = CreateClient();

    /// <summary>
    /// Thumbnail requests get their own client: a slow image CDN must never eat the timeout that
    /// the cookie/account calls need, and thumbnails are worth retrying.
    /// </summary>
    private static readonly HttpClient Thumbnails = CreateThumbnailClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) RCL/1.1");
        return client;
    }

    private static HttpClient CreateThumbnailClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
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

        var imageUrl = await ResolveAvatarUrlAsync(userId, cancellationToken).ConfigureAwait(false);
        if (imageUrl is null)
            return null;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var bytes = await Thumbnails.GetByteArrayAsync(imageUrl, cancellationToken).ConfigureAwait(false);
                if (IsImageData(bytes))
                    return bytes;
                Log.Warn($"头像地址返回的不是图片（{bytes.Length} 字节）");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn($"下载头像图片失败（第 {attempt} 次）：{ex.Message}");
            }

            if (attempt < 3)
                await Task.Delay(300 * attempt, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private static async Task<string?> ResolveAvatarUrlAsync(long userId, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var url =
                    $"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={userId}&size=150x150&format=Png&isCircular=true";
                using var response = await Thumbnails.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    Log.Warn($"头像接口返回 HTTP {(int)response.StatusCode}（第 {attempt} 次）");
                }
                else
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("data", out var data) &&
                        data.GetArrayLength() > 0 &&
                        data[0].TryGetProperty("imageUrl", out var imageEl) &&
                        imageEl.GetString() is { Length: > 0 } imageUrl)
                        return imageUrl;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn($"请求头像地址失败（第 {attempt} 次）：{ex.Message}");
            }

            if (attempt < 2)
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private static bool IsImageData(byte[] bytes) =>
        bytes.Length > 64 &&
        ((bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) ||
         (bytes[0] == 0xFF && bytes[1] == 0xD8));
}

public sealed class AccountFile
{
    [JsonPropertyName("ActiveUserId")] public long ActiveUserId { get; set; }
    [JsonPropertyName("Accounts")] public List<Models.RobloxAccount> Accounts { get; set; } = new();
}
