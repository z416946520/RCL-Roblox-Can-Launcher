using System.Diagnostics;
using System.IO;
using System.Text;

namespace RCL.Core;

public enum LaunchHealth
{
    /// <summary>The client reached its renderer, or showed no crash evidence in the watch window.</summary>
    Ready,

    /// <summary>The client died with an NTSTATUS crash code before its renderer came up.</summary>
    Crashed,

    /// <summary>There was nothing to watch (no log folder, unreadable log), so no verdict.</summary>
    Unknown
}

public readonly record struct StartupWatchResult(LaunchHealth Health, string? LogFile, int? ExitCode);

/// <summary>
/// Voidstrap's startup crash probe: watch Roblox's log folder for the logs of this launch, then
/// wait for the renderer marker. A process that exits with an NTSTATUS code before the marker is
/// a startup crash; anything else is left alone. This is what drives the retry ladder in
/// <see cref="LaunchService"/>.
/// </summary>
public static class StartupCrashGuard
{
    private const string RendererReadyMarker = "shaders from pack";

    private static readonly TimeSpan LogTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RendererTimeout = TimeSpan.FromSeconds(8);

    public static HashSet<string> SnapshotLogs(string? logDirectory = null)
    {
        var directory = logDirectory ?? AppPaths.RobloxLogs;
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(directory))
                return existing;
            foreach (var file in Directory.EnumerateFiles(directory, "*.log"))
                existing.Add(file);
        }
        catch (Exception ex)
        {
            Log.Warn("读取 Roblox 日志目录失败：" + ex.Message);
        }
        return existing;
    }

    public static async Task<StartupWatchResult> WaitAsync(
        Process process,
        HashSet<string> existingLogs,
        DateTime startedUtc,
        string? logDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var directory = logDirectory ?? AppPaths.RobloxLogs;
        try
        {
            if (!await WaitForAnyLogAsync(process, existingLogs, startedUtc, directory, cancellationToken)
                    .ConfigureAwait(false))
            {
                return new StartupWatchResult(ClassifyExited(process), null, ExitCodeOf(process));
            }

            return await WaitForRendererAsync(process, existingLogs, startedUtc, directory, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new StartupWatchResult(LaunchHealth.Unknown, null, null);
        }
        catch (Exception ex)
        {
            Log.Warn("启动监视失败：" + ex.Message);
            return new StartupWatchResult(LaunchHealth.Unknown, null, null);
        }
    }

    private static async Task<bool> WaitForAnyLogAsync(
        Process process,
        HashSet<string> existingLogs,
        DateTime startedUtc,
        string logDirectory,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(logDirectory))
            return false;

        var deadline = DateTime.UtcNow + LogTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (NewLogs(existingLogs, startedUtc, logDirectory).Any())
                return true;
            if (HasExitedSafely(process))
                return NewLogs(existingLogs, startedUtc, logDirectory).Any();

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
        return NewLogs(existingLogs, startedUtc, logDirectory).Any();
    }

    private static async Task<StartupWatchResult> WaitForRendererAsync(
        Process process,
        HashSet<string> existingLogs,
        DateTime startedUtc,
        string logDirectory,
        CancellationToken cancellationToken)
    {
        // A launch writes several logs (tray, crash handler, game). Watch every new one, because
        // the renderer marker only lands in the game log.
        var positions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var deadline = DateTime.UtcNow + RendererTimeout;

        while (true)
        {
            var readyLog = FindRendererLog(existingLogs, startedUtc, logDirectory, positions);
            if (readyLog is not null)
                return new StartupWatchResult(LaunchHealth.Ready, readyLog, ExitCodeOf(process));

            if (HasExitedSafely(process))
            {
                readyLog = FindRendererLog(existingLogs, startedUtc, logDirectory, positions);
                var health = readyLog is not null || !IsCrashExitCode(ExitCodeOf(process))
                    ? LaunchHealth.Ready
                    : LaunchHealth.Crashed;
                var log = readyLog ?? NewLogs(existingLogs, startedUtc, logDirectory)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                return new StartupWatchResult(health, log, ExitCodeOf(process));
            }

            if (DateTime.UtcNow >= deadline)
            {
                var log = NewLogs(existingLogs, startedUtc, logDirectory)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                return new StartupWatchResult(LaunchHealth.Ready, log, null);
            }

            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string? FindRendererLog(
        HashSet<string> existingLogs,
        DateTime startedUtc,
        string logDirectory,
        Dictionary<string, long> positions)
    {
        foreach (var file in NewLogs(existingLogs, startedUtc, logDirectory))
        {
            positions.TryGetValue(file, out var position);
            var text = ReadNewText(file, ref position);
            positions[file] = position;
            if (text.Contains(RendererReadyMarker, StringComparison.Ordinal))
                return file;
        }
        return null;
    }

    private static IEnumerable<string> NewLogs(
        HashSet<string> existingLogs, DateTime startedUtc, string logDirectory)
    {
        try
        {
            if (!Directory.Exists(logDirectory))
                return Array.Empty<string>();
            var oldest = startedUtc.AddSeconds(-3);
            return Directory.EnumerateFiles(logDirectory, "*.log")
                .Where(file => !existingLogs.Contains(file))
                .Where(file => File.GetLastWriteTimeUtc(file) >= oldest)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn("扫描 Roblox 日志失败：" + ex.Message);
            return Array.Empty<string>();
        }
    }

    private static string ReadNewText(string path, ref long position)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length <= position)
                return "";
            stream.Seek(position, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            position = stream.Position;
            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>No log appeared: only an NTSTATUS exit counts as a crash.</summary>
    private static LaunchHealth ClassifyExited(Process process) =>
        HasExitedSafely(process) && IsCrashExitCode(ExitCodeOf(process))
            ? LaunchHealth.Crashed
            : LaunchHealth.Unknown;

    private static bool HasExitedSafely(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch
        {
            return true;
        }
    }

    private static int? ExitCodeOf(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>NTSTATUS crash codes, the range Voidstrap classifies as a crash on exit.</summary>
    private static bool IsCrashExitCode(int? exitCode) =>
        exitCode is { } code && (uint)code is >= 0xC0000000u and < 0xFFFFFFFFu;
}
