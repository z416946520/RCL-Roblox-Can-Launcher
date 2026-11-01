using System.Diagnostics;
using System.IO;
using System.Text;

namespace RCL.Core;

public static class Log
{
    private static readonly object Gate = new();
    private static readonly StringBuilder Pending = new();

    private const long MaxLogBytes = 5L * 1024 * 1024;
    private const int MaxRotatedFiles = 10;
    private static int _writesSinceCheck;

    public static event Action<string>? LineWritten;

    /// <summary>
    /// Rotates an oversized log and trims old copies. The current log keeps the plain
    /// <c>rcl.log</c> name so the "open log" button never points at a stale file.
    /// </summary>
    public static void Start()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Logs);
                TrimRotated();
                if (File.Exists(AppPaths.LogFile) && new FileInfo(AppPaths.LogFile).Length > MaxLogBytes)
                    Rotate();
            }
            catch
            {
                // Logging setup must never block startup.
            }
        }
    }

    private static void Rotate()
    {
        try
        {
            if (!File.Exists(AppPaths.LogFile))
                return;
            var name = Path.Combine(AppPaths.Logs, $"rcl-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.Move(AppPaths.LogFile, name, true);
            TrimRotated();
        }
        catch
        {
        }
    }

    private static void TrimRotated()
    {
        foreach (var file in Directory.GetFiles(AppPaths.Logs, "rcl-*.log")
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Skip(MaxRotatedFiles))
        {
            try { File.Delete(file); } catch { }
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception ex) =>
        Write("ERROR", message + Environment.NewLine + ex);

    public static void Open()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Logs);
            if (!File.Exists(AppPaths.LogFile))
                File.WriteAllText(AppPaths.LogFile, "");
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{AppPaths.LogFile}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Write("ERROR", "无法打开日志：" + ex.Message);
        }
    }

    public static string Tail(int lines = 300)
    {
        try
        {
            if (!File.Exists(AppPaths.LogFile))
                return "（暂无日志）";

            using var stream = new FileStream(AppPaths.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var all = new Queue<string>();
            while (reader.ReadLine() is { } line)
            {
                all.Enqueue(line);
                if (all.Count > lines)
                    all.Dequeue();
            }
            return string.Join(Environment.NewLine, all);
        }
        catch (Exception ex)
        {
            return "读取日志失败：" + ex.Message;
        }
    }

    private static void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
        Debug.WriteLine(line);
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Logs);
                File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine, Encoding.UTF8);
                if (++_writesSinceCheck >= 200)
                {
                    _writesSinceCheck = 0;
                    if (new FileInfo(AppPaths.LogFile).Length > MaxLogBytes)
                        Rotate();
                }
            }
            catch
            {
                // Logging must never take the launcher down.
            }
        }
        try
        {
            LineWritten?.Invoke(line);
        }
        catch
        {
            // UI listeners are not allowed to break logging.
        }
    }
}
