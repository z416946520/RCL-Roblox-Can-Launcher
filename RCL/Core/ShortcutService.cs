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

    public static readonly string[] Palette =
    {
        "#1370F3", "#0B84C9", "#27AE60", "#16A085", "#8E44AD",
        "#E74C3C", "#D35400", "#F39C12", "#2C3E50", "#7F8C8D"
    };

    public static void Apply(string accentHex)
    {
        if (!TryParse(accentHex, out var color))
            color = Color.FromRgb(0x13, 0x70, 0xF3);
        Accent = color;

        var resources = Application.Current?.Resources;
        if (resources is null)
            return;

        resources["BrushAccent"] = Frozen(color);
        resources["BrushAccentDark"] = Frozen(Blend(color, Colors.Black, 0.24));
        resources["BrushAccentLight"] = Frozen(Blend(color, Colors.White, 0.30));
        resources["BrushAccentSoft"] = Frozen(Blend(color, Colors.White, 0.92));
        resources["BrushAccentBorder"] = Frozen(Blend(color, Colors.White, 0.80));
        resources["BrushPressed"] = Frozen(Blend(color, Colors.White, 0.82));

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
