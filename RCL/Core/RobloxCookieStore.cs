using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RCL.Core;

/// <summary>
/// Reads and writes Roblox's Windows cookie store. The file is a DPAPI protected Netscape
/// cookie string wrapped in a tiny JSON envelope, which is what the Windows client reads on
/// startup. RCL keeps a one-time copy of the previous store so account switching is reversible.
/// </summary>
public static class RobloxCookieStore
{
    private const string SecurityCookieName = ".ROBLOSECURITY";

    public static string CookiesPath => AppPaths.RobloxCookieFile;
    public static string OriginalBackupPath => Path.Combine(AppPaths.Backups, "RobloxCookies.original.dat");
    public static bool HasOriginalBackup => File.Exists(OriginalBackupPath);

    public static void WriteSecurityCookie(string securityToken, string? path = null)
    {
        if (!string.IsNullOrWhiteSpace(securityToken) &&
            securityToken.Contains(SecurityCookieName, StringComparison.Ordinal))
        {
            securityToken = ExtractTokenFromLooseString(securityToken);
        }

        if (string.IsNullOrWhiteSpace(securityToken))
            throw new ArgumentException("Cookie 不能为空。", nameof(securityToken));

        path ??= CookiesPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (path == CookiesPath)
            EnsureOriginalBackup(path);

        var existing = TryReadRaw(path);
        var netscape = existing is not null && ContainsSecurityCookie(existing)
            ? ReplaceSecurityCookie(existing, securityToken)
            : BuildNetscapeCookie(securityToken);
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(netscape), null, DataProtectionScope.CurrentUser);
        var envelope = new CookieEnvelope
        {
            CookiesVersion = "1",
            Cookies = Convert.ToBase64String(protectedBytes)
        };

        var json = JsonSerializer.Serialize(envelope, JsonOpts.Write);
        var temp = path + ".rcl-tmp";
        try
        {
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, path, true);
            Log.Info("已写入 Roblox 账号 Cookie 文件");
        }
        catch (UnauthorizedAccessException ex)
        {
            TryDelete(temp);
            throw new IOException("Roblox 正在运行或占用了 Cookie 文件，请先关闭 Roblox 后重试。", ex);
        }
        catch (IOException ex)
        {
            TryDelete(temp);
            throw new IOException(
                "无法写入 Roblox Cookie 文件，Roblox 可能正在运行并占用了它。", ex);
        }
    }

    public static string? TryReadSecurityCookie(string? path = null)
    {
        path ??= CookiesPath;
        var netscape = TryReadRaw(path);
        return netscape is null ? null : ExtractSecurityCookie(netscape);
    }

    private static string? TryReadRaw(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var json = File.ReadAllText(path);
            CookieEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<CookieEnvelope>(json, JsonOpts.Read);
            }
            catch (JsonException)
            {
                envelope = null;
            }

            if (envelope?.Cookies is { Length: > 0 } encrypted)
            {
                var bytes = Convert.FromBase64String(encrypted);
                var plain = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            return json;
        }
        catch (Exception ex)
        {
            Log.Warn("读取 Roblox Cookie 失败：" + ex.Message);
            return null;
        }
    }

    private static string? ExtractSecurityCookie(string netscape)
    {
        foreach (var line in netscape.Split('\n'))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            for (int i = 0; i + 1 < parts.Length; i++)
            {
                if (parts[i].Trim().Equals(SecurityCookieName, StringComparison.OrdinalIgnoreCase))
                    return CleanCookieValue(parts[i + 1]);
            }
        }

        var flat = netscape.Split('\t');
        for (int i = 0; i + 1 < flat.Length; i++)
        {
            if (flat[i].Trim().Equals(SecurityCookieName, StringComparison.OrdinalIgnoreCase))
                return CleanCookieValue(flat[i + 1]);
        }

        var marker = netscape.IndexOf(SecurityCookieName, StringComparison.OrdinalIgnoreCase);
        return marker >= 0
            ? CleanCookieValue(netscape[(marker + SecurityCookieName.Length)..])
            : null;
    }

    private static bool ContainsSecurityCookie(string netscape) =>
        ExtractSecurityCookie(netscape) is not null;

    /// <summary>
    /// Replaces only the .ROBLOSECURITY value and keeps every other cookie, delimiter and
    /// line layout exactly as the client wrote them. This is what makes account switching
    /// work with the single-line format used by current Windows clients.
    /// </summary>
    private static string ReplaceSecurityCookie(string netscape, string token)
    {
        var lines = netscape.Split('\n');
        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var carriage = line.EndsWith('\r');
            if (carriage)
                line = line[..^1];
            var parts = line.Split('\t');
            for (int i = 0; i + 1 < parts.Length; i++)
            {
                if (!parts[i].Trim().Equals(SecurityCookieName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var oldValue = parts[i + 1];
                var suffixIndex = oldValue.IndexOf(';');
                var suffix = suffixIndex >= 0 ? oldValue[suffixIndex..] : "";
                parts[i + 1] = token + suffix;
                lines[lineIndex] = string.Join("\t", parts) + (carriage ? "\r" : "");
                return string.Join("\n", lines);
            }
        }
        return netscape;
    }

    public static bool RestoreOriginalCookie()
    {
        try
        {
            if (!File.Exists(OriginalBackupPath))
                return false;
            Directory.CreateDirectory(Path.GetDirectoryName(CookiesPath)!);
            File.Copy(OriginalBackupPath, CookiesPath, true);
            Log.Info("已恢复 RCL 接管前的 Roblox Cookie");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("恢复 Roblox Cookie 失败：" + ex.Message);
            return false;
        }
    }

    private static void EnsureOriginalBackup(string path)
    {
        try
        {
            if (!File.Exists(OriginalBackupPath) && File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(OriginalBackupPath)!);
                File.Copy(path, OriginalBackupPath, true);
                Log.Info("已保存接管前的 Roblox Cookie 备份");
            }
            if (File.Exists(path))
            {
                try { File.Copy(path, path + ".bak", true); }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("备份 Roblox Cookie 失败：" + ex.Message);
        }
    }

    private static string BuildNetscapeCookie(string token)
    {
        var expiry = DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds();
        var guest = GenerateGuestData();
        var tracker = GenerateTrackerData();
        return
            $"#HttpOnly_.roblox.com\tTRUE\t/\tFALSE\t0\tGuestData\t{guest}\n" +
            $"#HttpOnly_.roblox.com\tTRUE\t/\tFALSE\t0\tRBXEventTrackerV2\t{tracker}\n" +
            $"#HttpOnly_.roblox.com\tTRUE\t/\tTRUE\t{expiry}\t{SecurityCookieName}\t{token}";
    }

    private static string GenerateGuestData()
    {
        var bytes = new byte[4];
        RandomNumberGenerator.Fill(bytes);
        var value = BitConverter.ToInt32(bytes, 0) & int.MaxValue;
        var userId = -(value % 999999999) - 1;
        return $"UserID={userId};";
    }

    private static string GenerateTrackerData()
    {
        var created = DateTime.UtcNow.ToString("MM/dd/yyyy HH:mm:ss",
            System.Globalization.CultureInfo.InvariantCulture);
        var bytes = new byte[8];
        RandomNumberGenerator.Fill(bytes);
        var browserId = BitConverter.ToInt64(bytes, 0) & long.MaxValue;
        if (browserId < 1_000_000_000_000_000L)
            browserId += 1_000_000_000_000_000L;
        return $"CreateDate={created}&rbxid=&rbxuid=&browserid={browserId};";
    }

    private static string CleanCookieValue(string input)
    {
        input = input.Trim().Trim('"');
        var semicolon = input.IndexOf(';');
        if (semicolon >= 0)
            input = input[..semicolon];
        input = input.Trim();
        if (input.Contains(".ROBLOSECURITY", StringComparison.OrdinalIgnoreCase))
            input = ExtractTokenFromLooseString(input);
        return input.Trim();
    }

    private static string ExtractTokenFromLooseString(string input)
    {
        var index = input.IndexOf(SecurityCookieName, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            var rest = input[(index + SecurityCookieName.Length)..].TrimStart('=', ':', ' ', '\t');
            var end = rest.IndexOfAny(new[] { ';', '\r', '\n' });
            if (end >= 0)
                rest = rest[..end];
            return rest.Trim().Trim('"');
        }
        return input.Trim();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed class CookieEnvelope
    {
        [JsonPropertyName("CookiesVersion")] public string CookiesVersion { get; set; } = "1";
        [JsonPropertyName("CookiesData")] public string Cookies { get; set; } = "";
    }
}
