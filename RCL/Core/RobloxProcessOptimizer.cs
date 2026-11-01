using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RCL.Core;

/// <summary>
/// Optional post-launch tuning, mirroring Voidstrap's RobloxProcessOptimizer but strictly opt-in:
/// lower CPU priority, an optional working-set cap and an optional core limit. Safe launches skip
/// it entirely, because those knobs are exactly what a safe launch is meant to rule out.
/// </summary>
public static class RobloxProcessOptimizer
{
    public static void Apply(Process process)
    {
        var settings = AppSettings.Current;
        if (!settings.OptimizeRoblox)
            return;

        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
            Log.Info($"已把 Roblox 进程设为低于普通优先级（PID {process.Id}）");
        }
        catch (Exception ex)
        {
            Log.Warn("设置进程优先级失败：" + ex.Message);
        }

        if (settings.RobloxMaxCores > 0 && settings.RobloxMaxCores < Environment.ProcessorCount)
        {
            try
            {
                var mask = (1L << settings.RobloxMaxCores) - 1;
                process.ProcessorAffinity = (IntPtr)mask;
                Log.Info($"已限制 Roblox 使用前 {settings.RobloxMaxCores} 个核心");
            }
            catch (Exception ex)
            {
                Log.Warn("设置 CPU 亲和性失败：" + ex.Message);
            }
        }

        if (settings.RobloxMemoryLimitMb > 0)
        {
            try
            {
                long max = (long)settings.RobloxMemoryLimitMb * 1024 * 1024;
                long min = max / 4;
                var ok = SetProcessWorkingSetSizeEx(process.Handle, (IntPtr)min, (IntPtr)max,
                    QuotaLimitsHardwsMaxEnable);
                if (!ok)
                    ok = SetProcessWorkingSetSize(process.Handle, (IntPtr)min, (IntPtr)max);

                if (ok)
                    Log.Info($"已设置 Roblox 工作集上限 {settings.RobloxMemoryLimitMb} MB");
                else
                    Log.Warn("设置工作集上限失败，Win32 错误码 " + Marshal.GetLastWin32Error());
            }
            catch (Exception ex)
            {
                Log.Warn("设置工作集上限失败：" + ex.Message);
            }
        }
    }

    private const uint QuotaLimitsHardwsMaxEnable = 0x00000004;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSizeEx(
        IntPtr process, IntPtr minimumWorkingSetSize, IntPtr maximumWorkingSetSize, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(
        IntPtr process, IntPtr minimumWorkingSetSize, IntPtr maximumWorkingSetSize);
}
