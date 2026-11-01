using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using RCL.Models;

namespace RCL.Core;

public static class ShortcutService
{
    public static string CreateDesktopShortcut(RclInstance instance)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var name = Sanitize("RCL - " + instance.Name) + ".lnk";
        var path = Path.Combine(desktop, name);
        var exe = Environment.ProcessPath;

        if (string.IsNullOrWhiteSpace(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            exe = Path.Combine(AppContext.BaseDirectory, "RCL.exe");

        if (!File.Exists(exe))
            throw new FileNotFoundException("找不到 RCL.exe，无法创建快捷方式。", exe);

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
                        ?? throw new InvalidOperationException("当前系统没有 WScript.Shell，无法创建快捷方式。");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(path);
        link.TargetPath = exe;
        link.Arguments = $"--launch \"{instance.Id:N}\"";
        link.WorkingDirectory = Path.GetDirectoryName(exe)!;
        link.IconLocation = exe + ",0";
        link.Description = $"RCL - {instance.Name}";
        link.Save();

        Log.Info($"已创建快捷方式：{path}");
        return path;
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }
}

public static class ThemeManager
{
    public static Color Accent { get; private set; } = Color.FromRgb(0x13, 0x70, 0xF3);

    public static bool IsDark { get; private set; }

    public static readonly string[] Palette =
    {
        "#1370F3", "#0B84C9", "#27AE60", "#16A085", "#8E44AD",
        "#E74C3C", "#D35400", "#F39C12", "#2C3E50", "#7F8C8D"
    };

    /// <summary>One-click looks: accent plus the surface mode it belongs to.</summary>
    public static readonly (string Name, string Accent, bool Dark)[] Presets =
    {
        ("PCL 蓝", "#1370F3", false),
        ("深海", "#0B84C9", false),
        ("森林", "#27AE60", false),
        ("薄荷", "#16A085", false),
        ("紫罗兰", "#8E44AD", false),
        ("樱桃", "#E74C3C", false),
        ("落日", "#D35400", false),
        ("琥珀", "#F39C12", false),
        ("石墨", "#546E7A", false),
        ("午夜蓝", "#3F51B5", true),
        ("暗森林", "#1F8A4C", true),
        ("暗紫", "#7C4DFF", true)
    };

    /// <summary>Neutral surfaces that flip with the light/dark switch.</summary>
    private static readonly (string Key, string Light, string Dark)[] Neutrals =
    {
        ("BrushBackground", "#FBFBFB", "#1B1E24"),
        ("BrushSidebar", "#F7F8FA", "#21252D"),
        ("BrushCard", "#FFFFFF", "#262B34"),
        ("BrushText", "#343D4A", "#E7EAF0"),
        ("BrushTextSecondary", "#737373", "#A7AFBD"),
        ("BrushTextMuted", "#8C8C8C", "#7E8797"),
        ("BrushBorder", "#E3E6EA", "#333A45"),
        ("BrushBorderStrong", "#A6A6A6", "#5A6270"),
        ("BrushHover", "#F2F5F9", "#2E3440"),
        ("BrushOverlay", "#0F000000", "#80000000"),
        ("BrushDangerSoft", "#FDEcea", "#3B2326"),
        ("BrushWarningSoft", "#FFF6E5", "#3B3122"),
        ("BrushSuccessSoft", "#EAF7EF", "#1E3529")
    };

    private static readonly Color DarkSurface = Color.FromRgb(0x26, 0x2B, 0x34);

    public static void Apply(string accentHex, string? themeMode = null)
    {
        if (!TryParse(accentHex, out var color))
            color = Color.FromRgb(0x13, 0x70, 0xF3);
        Accent = color;
        IsDark = string.Equals(themeMode ?? AppSettings.Current.ThemeMode, "Dark",
            StringComparison.OrdinalIgnoreCase);

        var resources = Application.Current?.Resources;
        if (resources is null)
            return;

        foreach (var (key, light, dark) in Neutrals)
            resources[key] = Frozen((Color)ColorConverter.ConvertFromString(IsDark ? dark : light)!);

        var surface = IsDark ? DarkSurface : Colors.White;
        resources["BrushAccent"] = Frozen(color);
        resources["BrushAccentDark"] = Frozen(Blend(color, IsDark ? Colors.White : Colors.Black, IsDark ? 0.22 : 0.24));
        resources["BrushAccentLight"] = Frozen(Blend(color, Colors.White, 0.30));
        resources["BrushAccentSoft"] = Frozen(Blend(color, surface, IsDark ? 0.78 : 0.92));
        resources["BrushAccentBorder"] = Frozen(Blend(color, surface, IsDark ? 0.60 : 0.80));
        resources["BrushPressed"] = Frozen(Blend(color, surface, IsDark ? 0.70 : 0.82));

        var luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
        resources["BrushTitleForeground"] = Frozen(luminance > 0.60
            ? Color.FromRgb(0x1F, 0x2A, 0x37)
            : Colors.White);

        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            GradientStops =
            {
                new GradientStop(color, 0),
                new GradientStop(Blend(color, Colors.Black, 0.24), 0.5),
                new GradientStop(color, 1)
            }
        };
        gradient.Freeze();
        resources["BrushTitleBar"] = gradient;
    }

    /// <summary>Applies the saved accent and light/dark mode in one call.</summary>
    public static void ApplyFromSettings() =>
        Apply(AppSettings.Current.AccentHex, AppSettings.Current.ThemeMode);

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public static bool TryParse(string? hex, out Color color)
    {
        color = Colors.Transparent;
        if (string.IsNullOrWhiteSpace(hex))
            return false;
        hex = hex.Trim();
        if (!hex.StartsWith("#"))
            hex = "#" + hex;

        // WPF accepts #RGB, #ARGB, #RRGGBB and #AARRGGBB. Never append an alpha pair:
        // a 9-character string is already AARRGGBB, so appending would turn the
        // colour into an almost transparent tint.
        if (hex.Length != 4 && hex.Length != 7 && hex.Length != 9)
            return false;
        if (hex.Length == 9 && hex[1..3].Equals("00", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            color = (Color)ColorConverter.ConvertFromString(hex)!;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static Color Blend(Color a, Color b, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            (byte)(a.A + (b.A - a.A) * amount),
            (byte)(a.R + (b.R - a.R) * amount),
            (byte)(a.G + (b.G - a.G) * amount),
            (byte)(a.B + (b.B - a.B) * amount));
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
