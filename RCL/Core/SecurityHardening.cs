using System.IO;
using System.Runtime.InteropServices;

namespace RCL.Core;

/// <summary>
/// Pins the system modules a launcher is most likely to be tricked into loading from its own
/// folder. Once the real System32 copy is loaded, a stray winmm.dll dropped next to RCL.exe can
/// never be picked up, because Windows reuses the module already in the process.
/// </summary>
public static class SecurityHardening
{
    private const uint LoadLibrarySearchSystem32 = 0x00000800;

    private static readonly string[] Modules =
    {
        "advapi32", "bcrypt", "crypt32", "dwmapi", "d3d11", "d3d9", "dbghelp", "dxgi",
        "msvcrt", "ncrypt", "ole32", "oleaut32", "opengl32", "psapi", "secur32",
        "shell32", "shlwapi", "uxtheme", "version", "winmm", "ws2_32"
    };

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);

    public static int Apply()
    {
        var system32 = Environment.SystemDirectory;
        var pinned = 0;
        foreach (var module in Modules)
        {
            var path = Path.Combine(system32, module + ".dll");
            if (!File.Exists(path))
                continue;
            try
            {
                if (LoadLibraryExW(path, IntPtr.Zero, LoadLibrarySearchSystem32) != IntPtr.Zero)
                    pinned++;
            }
            catch
            {
                // Hardening is best effort; it must never block startup.
            }
        }
        Log.Info($"已从 System32 预加载 {pinned} 个系统模块（DLL 侧载加固）");
        return pinned;
    }
}
