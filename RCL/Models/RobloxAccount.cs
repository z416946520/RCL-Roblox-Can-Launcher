using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using RCL.Core;

namespace RCL.Models;

public sealed class RobloxAccount : INotifyPropertyChanged
{
    private string _username = "";
    private string _displayName = "";
    private long _userId;
    private string _avatarUrl = "";
    private DateTime _lastUsed;
    private bool _isActive;

    public long UserId { get => _userId; set { if (Set(ref _userId, value)) { NotifyAvatarState(); Notify(nameof(IsVerified)); Notify(nameof(NeedsVerification)); } } }
    public string Username { get => _username; set { if (Set(ref _username, value)) Notify(nameof(Subtitle)); } }
    public string DisplayName { get => _displayName; set { if (Set(ref _displayName, value)) { Notify(nameof(Display)); Notify(nameof(Initial)); } } }
    public string AvatarUrl { get => _avatarUrl; set => Set(ref _avatarUrl, value); }
    public string ProtectedToken { get; set; } = "";
    public DateTime AddedAt { get; set; } = DateTime.Now;
    public DateTime LastUsed { get => _lastUsed; set => Set(ref _lastUsed, value); }

    [JsonIgnore] public string SecurityToken { get; set; } = "";
    [JsonIgnore] public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    [JsonIgnore] public bool IsVerified => UserId > 0;
    [JsonIgnore] public bool NeedsVerification => HasToken && !IsVerified;

    [JsonIgnore]
    public string TokenFingerprint => HasToken
        ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SecurityToken)))
        : "";

    [JsonIgnore]
    public string Display => string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName;

    [JsonIgnore]
    public string Subtitle => string.IsNullOrWhiteSpace(Username) ? "Roblox 账号" : "@" + Username;

    [JsonIgnore]
    public string Initial => Display.Length == 0 ? "R" : Display[..1].ToUpperInvariant();

    [JsonIgnore]
    public string AvatarCachePath => UserId <= 0 ? "" : Path.Combine(AppPaths.Avatars, UserId + ".png");

    [JsonIgnore]
    public bool HasAvatar => AvatarCachePath.Length > 0 && File.Exists(AvatarCachePath);

    [JsonIgnore]
    public bool HasToken => !string.IsNullOrEmpty(SecurityToken);

    public override string ToString() => Display;

    public void NotifyAvatarState()
    {
        Notify(nameof(AvatarCachePath));
        Notify(nameof(HasAvatar));
        Notify(nameof(Initial));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Notify(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Notify(name ?? "");
        return true;
    }
}
