using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ro3Emu.Launcher;

/// <summary>launcher.json: built into the exe (LauncherConfigFile in the csproj); a file beside the exe wins.</summary>
public sealed class LauncherConfig
{
    [JsonPropertyName("repository")] public string Repository { get; set; } = "http://localhost:8790/";
    [JsonPropertyName("channel")] public string Channel { get; set; } = "test";
    [JsonPropertyName("server")] public string? Server { get; set; }
    [JsonPropertyName("serverName")] public string ServerName { get; set; } = "RO3Emu";

    static readonly JsonSerializerOptions Options = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static LauncherConfig Load(string besideExe)
    {
        try
        {
            if (File.Exists(besideExe)) return JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllBytes(besideExe), Options) ?? new();
            using var stream = typeof(LauncherConfig).Assembly.GetManifestResourceStream("launcher.json");
            return stream is null ? new() : JsonSerializer.Deserialize<LauncherConfig>(stream, Options) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }
}

/// <summary>Per-user choices (%APPDATA%\RO3Emu\launcher-settings.json): only the client folder so far.</summary>
public sealed class UserSettings
{
    [JsonPropertyName("clientDirectory")] public string? ClientDirectory { get; set; }

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RO3Emu", "launcher-settings.json");

    public static UserSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllBytes(FilePath)) ?? new() : new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
