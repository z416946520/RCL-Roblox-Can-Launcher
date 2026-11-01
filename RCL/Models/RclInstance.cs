using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using RCL.Core;

namespace RCL.Models;

public sealed class RclInstance : INotifyPropertyChanged
{
    private string _name = "新实例";
    private string _notes = "";
    private string _versionPath = "";
    private bool _multiInstance;
    private string _launchArgs = "";
    private string _iconColor = "#1370F3";
    private long _playtimeSeconds;
    private bool _applyMods = true;
    private DateTime? _lastPlayed;
    private GameRef? _lastGame;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Notes { get => _notes; set => Set(ref _notes, value); }
    public string VersionPath { get => _versionPath; set => Set(ref _versionPath, value); }
    public bool MultiInstance { get => _multiInstance; set => Set(ref _multiInstance, value); }
    public string LaunchArgs { get => _launchArgs; set => Set(ref _launchArgs, value); }
    public string IconColor { get => _iconColor; set => Set(ref _iconColor, value); }
    public long PlaytimeSeconds { get => _playtimeSeconds; set { if (Set(ref _playtimeSeconds, value)) Notify(nameof(PlaytimeDisplay)); } }
    public bool ApplyMods { get => _applyMods; set => Set(ref _applyMods, value); }
    public bool FollowExecutorVersion { get; set; }
    public string FollowExecutor { get; set; } = "";
    public bool AutoDownloadExecutorVersion { get; set; } = true;
    public DateTime? LastPlayed { get => _lastPlayed; set { if (Set(ref _lastPlayed, value)) Notify(nameof(LastPlayedDisplay)); } }
    public GameRef? LastGame { get => _lastGame; set { if (Set(ref _lastGame, value)) { Notify(nameof(HasLastGame)); Notify(nameof(LastGameDisplay)); } } }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public ObservableCollection<FastFlagEntry> FastFlags { get; set; } = new();

    [JsonIgnore] public string Folder => Path.Combine(AppPaths.Instances, Id.ToString("N"));
    [JsonIgnore] public string ModsFolder => Path.Combine(Folder, "Mods");
    [JsonIgnore] public string ClientSettingsFolder => Path.Combine(Folder, "ClientSettings");
    [JsonIgnore] public string FlagsFile => Path.Combine(ClientSettingsFolder, "ClientAppSettings.json");
    [JsonIgnore] public string BackupFolder => Path.Combine(Folder, "Backup");
    [JsonIgnore] public string MetaFile => Path.Combine(Folder, "instance.json");

    [JsonIgnore]
    public string PlaytimeDisplay
    {
        get
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, PlaytimeSeconds));
            if (t.TotalHours >= 1)
                return $"{(int)t.TotalHours} 小时 {t.Minutes} 分";
            if (t.TotalMinutes >= 1)
                return $"{t.Minutes} 分钟";
            return t.TotalSeconds < 1 ? "尚未游玩" : $"{(int)t.TotalSeconds} 秒";
        }
    }

    [JsonIgnore]
    public string LastPlayedDisplay => LastPlayed is null
        ? "从未启动"
        : LastPlayed.Value.ToString("yyyy-MM-dd HH:mm");

    [JsonIgnore] public bool HasLastGame => LastGame is not null && LastGame.PlaceId > 0;

    [JsonIgnore]
    public string LastGameDisplay => HasLastGame
        ? (string.IsNullOrWhiteSpace(LastGame!.Title) ? $"Place {LastGame.PlaceId}" : LastGame.Title!)
        : "还没有加入过游戏";

    [JsonIgnore] public int EnabledFlagCount => FastFlags.Count(f => f.Enabled);

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => Name;

    public void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void NotifyAll()
    {
        foreach (var prop in typeof(RclInstance).GetProperties())
            Notify(prop.Name);
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Notify(name ?? "");
        return true;
    }
}

public sealed class FastFlagEntry : INotifyPropertyChanged
{
    private string _name = "";
    private string _value = "true";
    private bool _enabled = true;

    public string Name { get => _name; set { if (Set(ref _name, value)) Notify(nameof(IsKnown)); } }
    public string Value { get => _value; set => Set(ref _value, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    [JsonIgnore] public bool IsKnown => FastFlags.KnownFlags.Contains(Name);

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Notify(name ?? "");
        return true;
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class GameRef
{
    public long PlaceId { get; set; }
    public string? JobId { get; set; }
    public string? Title { get; set; }
    public DateTime JoinedAt { get; set; } = DateTime.Now;
}

public sealed class RunningSession : INotifyPropertyChanged
{
    private string _elapsed = "00:00";
    private string _status = "运行中";

    public Guid InstanceId { get; set; }
    public string InstanceName { get; set; } = "";
    public int Pid { get; set; }
    public DateTime StartedAt { get; set; }
    public string PlaceName { get; set; } = "";
    public long? PlaceId { get; set; }
    public System.Diagnostics.Process? Process { get; set; }
    public bool IsExternal { get; set; }

    public string Elapsed { get => _elapsed; set { if (_elapsed != value) { _elapsed = value; PropertyChanged?.Invoke(this, new(nameof(Elapsed))); } } }
    public string Status { get => _status; set { if (_status != value) { _status = value; PropertyChanged?.Invoke(this, new(nameof(Status))); } } }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title => string.IsNullOrWhiteSpace(PlaceName) ? InstanceName : PlaceName;
}
