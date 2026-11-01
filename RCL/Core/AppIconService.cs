using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace RCL.Core;

/// <summary>
/// Gets the shell icon for a shortcut target, executable or script. Windows gives us the
/// embedded .exe icon, the shortcut's custom icon and the file-type icon for scripts.
/// </summary>
public static class AppIconService
{
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;

    private sealed record CacheEntry(long Stamp, BitmapSource? Icon);

    private static readonly ConcurrentDictionary<string, CacheEntry> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info,
        uint infoSize, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr icon);

    public static BitmapSource? GetIcon(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        long stamp;
        try
        {
            stamp = File.GetLastWriteTimeUtc(path).Ticks;
        }
        catch
        {
            return null;
        }

        if (Cache.TryGetValue(path, out var cached) && cached.Stamp == stamp)
            return cached.Icon;

        BitmapSource? source = null;
        try
        {
            var info = new SHFILEINFO();
            var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
                SHGFI_ICON | SHGFI_LARGEICON);
            if (result != IntPtr.Zero && info.hIcon != IntPtr.Zero)
            {
                try
                {
                    source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                }
                finally
                {
                    DestroyIcon(info.hIcon);
                }
            }
        }
        catch
        {
            source = null;
        }

        Cache[path] = new CacheEntry(stamp, source);
        return source;
    }
}
