using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RCL.Core;

public sealed class AppSettings
{
    public string AccentHex { get; set; } = "#1370F3";
    public string BackgroundImage { get; set; } = "";
    public double BackgroundOpacity { get; set; } = 0.22;
    public bool ConfirmLaunch { get; set; } = true;
    public bool CloseAfterLaunch { get; set; }
    public bool DefaultMultiInstance { get; set; }
    public int MaxSlots { get; set; } = 8;
    public bool TrackPlaytime { get; set; } = true;
    public double WindowWidth { get; set; } = 1140;
    public double WindowHeight { get; set; } = 730;
    public bool WindowMaximized { get; set; }
    public string LastInstanceId { get; set; } = "";
    public bool FirstRun { get; set; } = true;
    public bool CheckUpdatesOnStartup { get; set; } = true;
    public string UpdateManifestUrl { get; set; } = "";
    public bool DiscordRpcEnabled { get; set; }
    public string DiscordClientId { get; set; } = "";
    public bool DiscordShowButtons { get; set; } = true;

    [JsonIgnore]
    public static AppSettings Current { get; private set; } = new();

    public static void Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var text = File.ReadAllText(AppPaths.SettingsFile);
                Current = JsonSerializer.Deserialize<AppSettings>(text) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("设置读取失败，使用默认值：" + ex.Message);
            Current = new AppSettings();
        }
        Current.MaxSlots = Math.Clamp(Current.MaxSlots, 1, 16);
        Current.BackgroundOpacity = Math.Clamp(Current.BackgroundOpacity, 0, 0.85);
        Current.WindowWidth = Math.Clamp(Current.WindowWidth, 1000, 5000);
        Current.WindowHeight = Math.Clamp(Current.WindowHeight, 640, 4000);
    }

    public static void Save()
    {
        try
        {
            AppPaths.Ensure();
            var text = JsonSerializer.Serialize(Current, JsonOpts.Write);
            File.WriteAllText(AppPaths.SettingsFile, text);
        }
        catch (Exception ex)
        {
            Log.Error("设置保存失败", ex);
        }
    }

    public static void ResetToDefaults()
    {
        Current = new AppSettings { FirstRun = false };
        Save();
    }
}

public static class JsonOpts
{
    public static readonly JsonSerializerOptions Write = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new GuidJsonConverter() }
    };

    public static readonly JsonSerializerOptions Read = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new GuidJsonConverter() }
    };
}

public sealed class GuidJsonConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString();
            if (Guid.TryParse(text, out var guid))
                return guid;
        }
        throw new JsonException("无法识别的 GUID 值");
    }

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString("N"));
    }
}
