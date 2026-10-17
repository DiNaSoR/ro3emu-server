using System.Collections.Concurrent;

namespace Ro3Emu.Server.Data;

/// <summary>
/// Finds an exported data file whatever the case of its name. The export keeps the client's own file names
/// (sceneeditor/MapLinkData.json, sc_pm_pveSecretLair_001_sceneeditor.json) while callers ask for them in another case;
/// Windows does not mind, but on Linux the exact path is missing and the file silently looked absent.
/// </summary>
public static class DataFile
{
    private static readonly ConcurrentDictionary<string, Dictionary<string, string>> listings = new();

    /// <summary>The path of <paramref name="name"/> in <paramref name="dir"/>, or that path unchanged when no file matches.</summary>
    public static string Resolve(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        if (File.Exists(path)) return path;
        var byName = listings.GetOrAdd(Path.GetFullPath(dir), List);
        // A file exported after the listing was taken (tests write their own) is picked up by listing again once.
        if (!byName.TryGetValue(name, out var found))
            byName = listings[Path.GetFullPath(dir)] = List(Path.GetFullPath(dir));
        return byName.TryGetValue(name, out found) ? found : path;
    }

    /// <summary><see cref="Resolve"/> for <paramref name="dir"/>/<paramref name="sub"/>.</summary>
    public static string Resolve(string dir, string sub, string name) => Resolve(Path.Combine(dir, sub), name);

    private static Dictionary<string, string> List(string dir)
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(dir))
            foreach (var file in Directory.EnumerateFiles(dir))
                byName.TryAdd(Path.GetFileName(file), file);
        return byName;
    }
}
