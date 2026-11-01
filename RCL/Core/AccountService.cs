using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using RCL.Models;

namespace RCL.Core;

public static class Dpapi
{
    public static string Protect(string plainText)
    {
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), null,
            DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string? Unprotect(string base64)
    {
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(base64), null,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }
}

public static class AccountService
{
    private static readonly object Gate = new();
    private static bool _loaded;

    public static ObservableCollection<RobloxAccount> Accounts { get; } = new();
    public static RobloxAccount? ActiveAccount { get; private set; }
    public static event Action? Changed;

    public static bool HasAccounts => Accounts.Count > 0;

    public static void Load()
    {
        lock (Gate)
        {
            if (_loaded)
                return;
            _loaded = true;
            Accounts.Clear();
            ActiveAccount = null;
            try
            {
                if (!File.Exists(AppPaths.AccountsFile))
                    return;
                var data = JsonSerializer.Deserialize<AccountFile>(
                    File.ReadAllText(AppPaths.AccountsFile), JsonOpts.Read);
                if (data?.Accounts is null)
                    return;

                foreach (var account in data.Accounts)
                {
                    account.SecurityToken = string.IsNullOrWhiteSpace(account.ProtectedToken)
                        ? ""
                        : Dpapi.Unprotect(account.ProtectedToken) ?? "";
                    if (account.SecurityToken.Length == 0)
                        Log.Warn($"账号「{account.Display}」的登录凭据无法解密，需要重新登录。");
                    Accounts.Add(account);
                }

                ActiveAccount = Accounts.FirstOrDefault(a => a.UserId == data.ActiveUserId && a.HasToken);
                UpdateActiveFlags();
                Log.Info($"已加载 {Accounts.Count} 个 Roblox 账号" +
                         (ActiveAccount is null ? "" : "，当前：" + ActiveAccount.Display));
            }
            catch (Exception ex)
            {
                Log.Error("读取账号文件失败", ex);
            }
        }
    }

    public static async Task<(RobloxAccount? Account, string? Error)> AddFromCookieAsync(
        string rawCookie, AuthenticatedRobloxUser? knownUser = null, bool allowUnverified = false,
        CancellationToken cancellationToken = default)
    {
        var cookie = RobloxAccountApi.SanitizeCookie(rawCookie);
        if (!RobloxAccountApi.LooksPlausible(cookie))
            return (null, "Cookie 格式不正确，请重新登录后再试。");

        AuthenticatedRobloxUser? user = knownUser;
        string? validationError = null;
        if (user is null && !allowUnverified)
        {
            (user, validationError) = await RobloxAccountApi.GetAuthenticatedAsync(cookie, cancellationToken);
            if (user is null)
                return (null, validationError ?? "Cookie 无效。");
        }
        else if (user is null)
        {
            validationError = "账号信息暂未验证";
        }

        if (user is null)
            Log.Warn("账号接口暂时不可用，先保存登录 Cookie 并标记为待验证：" + validationError);

        var fingerprint = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(cookie)));

        var account = user is not null
            ? Accounts.FirstOrDefault(a => a.UserId == user.Id) ??
              Accounts.FirstOrDefault(a => string.Equals(a.TokenFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            : Accounts.FirstOrDefault(a => string.Equals(a.TokenFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));

        bool isNew = account is null;
        if (account is null)
        {
            account = new RobloxAccount
            {
                UserId = user?.Id ?? 0,
                Username = user?.Name ?? "",
                DisplayName = user?.DisplayName ?? ("Roblox 账号 " + fingerprint[..4].ToUpperInvariant())
            };
            Accounts.Add(account);
        }
        else if (user is not null)
        {
            account.UserId = user.Id;
            account.Username = user.Name;
            account.DisplayName = user.DisplayName;
        }

        account.SecurityToken = cookie;
        account.ProtectedToken = Dpapi.Protect(cookie);
        if (user is not null)
            account.AvatarUrl =
                $"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={user.Id}&size=150x150&format=Png&isCircular=true";

        Save();
        string? switchError = null;
        try
        {
            Switch(account);
        }
        catch (Exception ex)
        {
            switchError = ex.Message;
            Log.Warn("账号已保存，但切换 Cookie 失败：" + ex.Message);
        }

        Log.Info((isNew ? "添加账号：" : "更新账号：") + account.Display +
                 (user is null ? "（待验证）" : ""));
        RaiseChanged();

        // A thumbnail must never hold up the login flow; the homepage picks it up when it lands.
        if (user is not null)
            _ = RefreshAvatarAsync(account, force: true);

        return (account, switchError);
    }

    public static void Switch(RobloxAccount account)
    {
        if (!account.HasToken)
            throw new InvalidOperationException("这个账号没有可用的登录凭据，请重新登录后再切换。");

        RobloxCookieStore.WriteSecurityCookie(account.SecurityToken);
        ActiveAccount = account;
        account.LastUsed = DateTime.Now;
        UpdateActiveFlags();
        Save();
        Log.Info("已切换 Roblox 账号：" + account.Display + "（新启动的客户端生效）");
        RaiseChanged();
    }

    /// <summary>
    /// Ensures the Roblox cookie store matches the active account before any client starts.
    /// This is what makes every instance launch under the selected account.
    /// </summary>
    public static void PrepareLaunch()
    {
        var account = ActiveAccount;
        if (account is null || !account.HasToken)
            return;

        try
        {
            var existing = RobloxCookieStore.TryReadSecurityCookie();
            if (!string.Equals(existing, account.SecurityToken, StringComparison.Ordinal))
            {
                RobloxCookieStore.WriteSecurityCookie(account.SecurityToken);
                Log.Info("启动前已写入账号 Cookie：" + account.Display);
            }
        }
        catch (Exception ex)
        {
            throw new IOException(
                $"无法应用当前账号「{account.Display}」：{ex.Message}\n" +
                "如果 Roblox 正在运行，请先关闭它，或在「设置 → Roblox 账号」里恢复原登录。", ex);
        }
    }

    public static void Remove(RobloxAccount account)
    {
        Accounts.Remove(account);
        if (ReferenceEquals(ActiveAccount, account))
            ActiveAccount = null;
        UpdateActiveFlags();
        Save();
        RaiseChanged();
        Log.Info("移除账号：" + account.Display);
    }

    public static bool RestoreOriginalCookie()
    {
        var ok = RobloxCookieStore.RestoreOriginalCookie();
        if (ok)
        {
            ActiveAccount = null;
            UpdateActiveFlags();
            Save();
            RaiseChanged();
        }
        return ok;
    }

    private static readonly TimeSpan AvatarMaxAge = TimeSpan.FromDays(7);
    private static readonly HashSet<long> AvatarsInFlight = new();
    private static readonly object AvatarGate = new();

    /// <summary>
    /// Fetches the headshot for one account and replaces the cached PNG atomically. Missing files
    /// are downloaded, older ones are refreshed, so a changed avatar shows up on the next start
    /// without blocking anything.
    /// </summary>
    public static async Task RefreshAvatarAsync(
        RobloxAccount account,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (account.UserId <= 0)
            return;
        if (!force && account.HasAvatar && !IsAvatarStale(account))
            return;

        lock (AvatarGate)
        {
            if (!AvatarsInFlight.Add(account.UserId))
                return;
        }

        try
        {
            var bytes = await RobloxAccountApi.FetchAvatarAsync(account.UserId, cancellationToken)
                .ConfigureAwait(false);
            if (bytes is not { Length: > 0 })
                return;

            Directory.CreateDirectory(AppPaths.Avatars);
            var path = account.AvatarCachePath;
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, true);
            account.NotifyAvatarState();
            Log.Info($"已更新账号头像：{account.Display}");
            RaiseChanged();
        }
        catch (Exception ex)
        {
            Log.Warn("保存账号头像失败：" + ex.Message);
        }
        finally
        {
            lock (AvatarGate)
                AvatarsInFlight.Remove(account.UserId);
        }
    }

    public static async Task EnsureAvatarsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var account in Accounts.ToList())
            await RefreshAvatarAsync(account, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static bool IsAvatarStale(RobloxAccount account)
    {
        try
        {
            return File.GetLastWriteTimeUtc(account.AvatarCachePath) < DateTime.UtcNow - AvatarMaxAge;
        }
        catch
        {
            return true;
        }
    }

    private static void UpdateActiveFlags()
    {
        foreach (var account in Accounts)
            account.IsActive = ReferenceEquals(account, ActiveAccount);
    }

    private static void Save()
    {
        try
        {
            foreach (var account in Accounts)
            {
                if (account.HasToken)
                    account.ProtectedToken = Dpapi.Protect(account.SecurityToken);
            }

            var data = new AccountFile
            {
                ActiveUserId = ActiveAccount?.UserId ?? 0,
                Accounts = Accounts.ToList()
            };
            var json = JsonSerializer.Serialize(data, JsonOpts.Write);
            var temp = AppPaths.AccountsFile + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, AppPaths.AccountsFile, true);
        }
        catch (Exception ex)
        {
            Log.Error("保存账号文件失败", ex);
        }
    }

    private static void RaiseChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(Changed);
        else
            Changed?.Invoke();
    }
}
