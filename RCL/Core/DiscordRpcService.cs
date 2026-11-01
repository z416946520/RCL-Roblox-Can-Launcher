using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RCL.Core;

/// <summary>
/// Minimal Discord Rich Presence client over the local discord-ipc-N named pipes. It only needs
/// an Application ID; the Discord app name and icon are what users see in the profile card.
/// </summary>
public static class DiscordRpcService
{
    private static readonly object StateLock = new();
    private static readonly SemaphoreSlim SendLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static CancellationTokenSource? _cts;
    private static Task? _loop;
    private static NamedPipeClientStream? _pipe;
    private static string _clientId = "";
    private static volatile bool _connected;
    private static string? _activityJson;
    private static bool _hasActivity;

    public static bool IsConnected => _connected;
    public static event Action? StateChanged;

    public static bool IsValidClientId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length >= 17 && value.Trim().All(char.IsAsciiDigit);

    public static void ApplySettings()
    {
        var settings = AppSettings.Current;
        var clientId = (settings.DiscordClientId ?? "").Trim();
        var enabled = settings.DiscordRpcEnabled && IsValidClientId(clientId);

        lock (StateLock)
        {
            if (!enabled)
            {
                StopLocked();
                return;
            }
            if (string.Equals(_clientId, clientId, StringComparison.Ordinal) && _loop is { IsCompleted: false })
            {
                RefreshFromSessions();
                return;
            }

            StopLocked();
            _clientId = clientId;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => ConnectionLoopAsync(token), token);
            Log.Info("Discord RPC 已启用，Application ID " + clientId);
        }
    }

    public static void Stop()
    {
        lock (StateLock)
            StopLocked();
    }

    private static void StopLocked()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
        }
        try
        {
            _pipe?.Dispose();
        }
        catch
        {
        }
        _pipe = null;
        _cts = null;
        _loop = null;
        _clientId = "";
        _hasActivity = false;
        _activityJson = null;
        SetConnected(false);
    }

    /// <summary>Rebuilds the activity from the running session list. Call on the UI thread.</summary>
    public static void RefreshFromSessions()
    {
        var sessions = SessionManager.Sessions;
        var latest = sessions.OrderByDescending(s => s.StartedAt).FirstOrDefault();
        if (latest is null)
        {
            _hasActivity = false;
            _activityJson = null;
        }
        else
        {
            var title = string.IsNullOrWhiteSpace(latest.PlaceName) ? "" : latest.PlaceName.Trim();
            var details = title.Length > 0 ? "正在玩 " + title : "在 Roblox 中";
            var state = "实例：" + latest.InstanceName;
            if (sessions.Count > 1)
                state += $" · 多开 {sessions.Count}";

            var activity = new JsonObject
            {
                ["details"] = Clip(details, 128),
                ["state"] = Clip(state, 128),
                ["timestamps"] = new JsonObject
                {
                    ["start"] = new DateTimeOffset(latest.StartedAt).ToUnixTimeSeconds()
                }
            };

            if (AppSettings.Current.DiscordShowButtons && latest.PlaceId is > 0)
            {
                activity["buttons"] = new JsonArray(new JsonObject
                {
                    ["label"] = "加入游戏",
                    ["url"] = $"https://www.roblox.com/games/{latest.PlaceId.Value}"
                });
            }

            _hasActivity = true;
            _activityJson = activity.ToJsonString(JsonOptions);
        }

        if (_connected)
            _ = SendCurrentActivityAsync();
    }

    public static async Task<string> TestAsync()
    {
        if (!AppSettings.Current.DiscordRpcEnabled)
            return "请先勾选「启用 Discord 状态」并点应用。";
        if (!IsValidClientId(AppSettings.Current.DiscordClientId))
            return "Application ID 需要是 17 位以上的数字。";

        for (var i = 0; i < 16 && !_connected; i++)
            await Task.Delay(350);

        if (!_connected)
            return "未连接到 Discord。请确认 Discord 正在运行，且已登录。";

        var start = DateTimeOffset.Now.ToUnixTimeSeconds();
        var test = new JsonObject
        {
            ["details"] = "RCL 集成测试",
            ["state"] = "Discord 状态连接正常",
            ["timestamps"] = new JsonObject { ["start"] = start }
        };
        await SendActivityJsonAsync(test.ToJsonString(JsonOptions));
        _ = ClearTestActivityAsync();
        return "已连接 Discord，并发送了一条测试状态。";
    }

    private static async Task ClearTestActivityAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
            if (_connected)
                await SendActivityJsonAsync("null");
        }
        catch
        {
        }
    }

    private static async Task ConnectionLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ServeConnectionAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn("Discord RPC 连接中断：" + ex.Message);
            }
            finally
            {
                try
                {
                    _pipe?.Dispose();
                }
                catch
                {
                }
                _pipe = null;
                SetConnected(false);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
            catch
            {
                break;
            }
        }
    }

    private static async Task ServeConnectionAsync(CancellationToken ct)
    {
        var pipe = await ConnectAsync(ct);
        if (pipe is null)
            return;

        _pipe = pipe;
        var handshake = JsonSerializer.Serialize(new { v = 1, client_id = _clientId });
        await WriteFrameAsync(pipe, 0, handshake, ct);

        using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        var response = await ReadFrameAsync(pipe, handshakeTimeout.Token);
        if (response is null)
        {
            Log.Warn("Discord RPC 没有返回握手响应");
            return;
        }

        SetConnected(true);
        _ = Task.Run(() => DrainResponsesAsync(pipe, ct), ct);
        await SendCurrentActivityAsync();

        while (!ct.IsCancellationRequested && _connected)
        {
            await Task.Delay(TimeSpan.FromSeconds(25), ct);
            await SendCurrentActivityAsync();
        }
    }

    private static async Task DrainResponsesAsync(Stream pipe, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await ReadFrameAsync(pipe, ct);
                if (frame is null)
                    break;
            }
        }
        catch
        {
        }
        SetConnected(false);
        try
        {
            pipe.Dispose();
        }
        catch
        {
        }
    }

    private static async Task<NamedPipeClientStream?> ConnectAsync(CancellationToken ct)
    {
        for (var i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(
                ".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromMilliseconds(250));
                await pipe.ConnectAsync(attempt.Token);
                return pipe;
            }
            catch
            {
                pipe.Dispose();
            }
        }
        return null;
    }

    private static async Task SendCurrentActivityAsync()
    {
        if (!_connected)
            return;
        await SendActivityJsonAsync(_hasActivity ? _activityJson ?? "null" : "null");
    }

    private static async Task SendActivityJsonAsync(string activityJson)
    {
        var pipe = _pipe;
        if (pipe is null)
            return;

        var payload =
            $"{{\"cmd\":\"SET_ACTIVITY\",\"args\":{{\"pid\":{Environment.ProcessId},\"activity\":{activityJson}}},\"nonce\":\"{Guid.NewGuid():N}\"}}";
        await SendLock.WaitAsync();
        try
        {
            await WriteFrameAsync(pipe, 1, payload, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Warn("Discord RPC 发送失败：" + ex.Message);
            SetConnected(false);
            try
            {
                pipe.Dispose();
            }
            catch
            {
            }
        }
        finally
        {
            SendLock.Release();
        }
    }

    private static async Task WriteFrameAsync(
        Stream pipe, int op, string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var header = new byte[8];
        BitConverter.TryWriteBytes(header.AsSpan(0, 4), op);
        BitConverter.TryWriteBytes(header.AsSpan(4, 4), bytes.Length);
        await pipe.WriteAsync(header, ct);
        await pipe.WriteAsync(bytes, ct);
        await pipe.FlushAsync(ct);
    }

    private static async Task<string?> ReadFrameAsync(Stream pipe, CancellationToken ct)
    {
        var header = new byte[8];
        await pipe.ReadExactlyAsync(header, ct);
        var length = BitConverter.ToInt32(header, 4);
        if (length <= 0 || length > 64 * 1024)
            return null;
        var payload = new byte[length];
        await pipe.ReadExactlyAsync(payload, ct);
        return Encoding.UTF8.GetString(payload);
    }

    private static void SetConnected(bool value)
    {
        if (_connected == value)
            return;
        _connected = value;
        Log.Info(value ? "Discord RPC 已连接" : "Discord RPC 已断开");
        try
        {
            StateChanged?.Invoke();
        }
        catch
        {
        }
    }

    private static string Clip(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
