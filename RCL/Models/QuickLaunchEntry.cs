using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace RCL.Models;

public enum QuickLaunchKind
{
    Exe,
    Link,
    Cmd,
    Bat,
    Ps1,
    Other
}

public sealed class QuickLaunchEntry : INotifyPropertyChanged
{
    private string _name = "新快捷启动";
    private string _targetPath = "";
    private string _arguments = "";
    private string _workingDirectory = "";
    private string _processName = "";
    private bool _runAsAdmin;
    private QuickLaunchKind _kind = QuickLaunchKind.Other;
    private DateTime? _lastUsed;
    private bool _isRunning;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set { if (Set(ref _name, value)) Notify(nameof(Initial)); } }
    public string TargetPath { get => _targetPath; set { if (Set(ref _targetPath, value)) NotifyPathState(); } }
    public string Arguments { get => _arguments; set => Set(ref _arguments, value); }
    public string WorkingDirectory { get => _workingDirectory; set => Set(ref _workingDirectory, value); }
    public string ProcessName { get => _processName; set => Set(ref _processName, value); }
    public bool RunAsAdmin { get => _runAsAdmin; set => Set(ref _runAsAdmin, value); }
    public QuickLaunchKind Kind { get => _kind; set { if (Set(ref _kind, value)) { Notify(nameof(TypeLabel)); Notify(nameof(IconGlyph)); } } }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? LastUsed { get => _lastUsed; set { if (Set(ref _lastUsed, value)) Notify(nameof(LastUsedDisplay)); } }

    [JsonIgnore] public bool IsRunning { get => _isRunning; set => Set(ref _isRunning, value); }

    [JsonIgnore]
    public string TypeLabel => Kind switch
    {
        QuickLaunchKind.Exe => "可执行程序",
        QuickLaunchKind.Link => "快捷方式",
        QuickLaunchKind.Cmd => "CMD 脚本",
        QuickLaunchKind.Bat => "批处理",
        QuickLaunchKind.Ps1 => "PowerShell",
        _ => "其他"
    };

    [JsonIgnore]
    public string IconGlyph => Kind switch
    {
        QuickLaunchKind.Link => "\uE71B",
        QuickLaunchKind.Cmd or QuickLaunchKind.Bat or QuickLaunchKind.Ps1 => "\uE943",
        QuickLaunchKind.Exe => "\uE7C3",
        _ => "\uE7B8"
    };

    [JsonIgnore]
    public string Initial => string.IsNullOrWhiteSpace(Name) ? "Q" : Name.Trim()[..1].ToUpperInvariant();

    [JsonIgnore]
    public string LastUsedDisplay => LastUsed is null ? "尚未启动" : LastUsed.Value.ToString("yyyy-MM-dd HH:mm");

    [JsonIgnore]
    public bool PathExists => !string.IsNullOrWhiteSpace(TargetPath) && File.Exists(TargetPath);

    [JsonIgnore]
    public string PathDisplay => string.IsNullOrWhiteSpace(TargetPath) ? "未设置路径" : TargetPath;

    public override string ToString() => Name;

    public void NotifyPathState()
    {
        Notify(nameof(PathExists));
        Notify(nameof(PathDisplay));
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
