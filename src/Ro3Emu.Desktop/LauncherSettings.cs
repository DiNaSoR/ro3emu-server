using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ro3Emu.Desktop;

/// <summary>A server in the list: what the player calls it and its address (host, host:port or http://host:port).</summary>
public sealed class ServerEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("address")] public string Address { get; set; } = "";

    public override string ToString() => Name.Length > 0 ? $"{Name}  ({Address})" : Address;
}

/// <summary>RO3EmuDesktop.json next to the exe: the server list, the last choices and (when asked) the password.</summary>
public sealed class LauncherSettings
{
    public const string FileName = "RO3EmuDesktop.json";

    [JsonPropertyName("servers")] public List<ServerEntry> Servers { get; set; } = [];
    [JsonPropertyName("selected")] public string? Selected { get; set; }
    [JsonPropertyName("account")] public string? Account { get; set; }
    /// <summary>Base64 of the password protected with Windows DPAPI for this user (unreadable on another PC or account).</summary>
    [JsonPropertyName("password")] public string? ProtectedPassword { get; set; }
    /// <summary>The game folder for RO3Emu.exe --client; empty = the one RO3Emu.exe remembers or finds.</summary>
    [JsonPropertyName("client")] public string? ClientDir { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static LauncherSettings Load(string path)
    {
        LauncherSettings? settings = null;
        try { if (File.Exists(path)) settings = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(path)); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        settings ??= new LauncherSettings();
        if (settings.Servers.Count == 0) settings.Servers.Add(new ServerEntry { Name = "This PC", Address = "127.0.0.1" });
        return settings;
    }

    public void Save(string path)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, overwrite: true);
    }

    public string? ReadPassword()
    {
        if (string.IsNullOrEmpty(ProtectedPassword) || !OperatingSystem.IsWindows()) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedPassword), Entropy, DataProtectionScope.CurrentUser)); }
        catch (Exception e) when (e is CryptographicException or FormatException or PlatformNotSupportedException) { return null; }
    }

    public void WritePassword(string? password)
    {
        ProtectedPassword = null;
        if (string.IsNullOrEmpty(password) || !OperatingSystem.IsWindows()) return;
        try { ProtectedPassword = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser)); }
        catch (Exception e) when (e is CryptographicException or PlatformNotSupportedException) { }
    }

    private static readonly byte[] Entropy = "RO3EmuDesktop password v1"u8.ToArray();
}
