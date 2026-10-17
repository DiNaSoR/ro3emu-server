namespace Ro3Emu.Server.Data;

/// <summary>
/// Exported files looked up by name in any letter case. The export keeps the client's file names (MapLinkData.json,
/// sc_pm_pveSecretLair_001_sceneeditor.json) while config rows and code name them in another case
/// (_kResSceneEditorName, lowercased names). Windows matches either; a Linux host matches only the exact name, so an
/// exact lookup there silently misses the file (every Kafra teleport refused, maps without their scene editor).
/// </summary>
public static class DataFiles
{
    /// <summary>The path of <paramref name="fileName"/> in <paramref name="dir"/>: the exact name first, else the one
    /// file whose name differs only in case. Null when the directory has no such file.</summary>
    public static string? Find(string dir, string fileName)
    {
        var exact = Path.Combine(dir, fileName);
        if (File.Exists(exact)) return exact;
        if (!Directory.Exists(dir)) return null;
        foreach (var path in Directory.EnumerateFiles(dir))
            if (string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase)) return path;
        return null;
    }
}
