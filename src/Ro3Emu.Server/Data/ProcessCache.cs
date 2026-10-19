using System.Collections.Concurrent;
using System.Text.Json;

namespace Ro3Emu.Server.Data;

/// <summary>
/// Work every server in one process can share: parsed exported files (config tables, scene editors, nav grids) and
/// a generated gate key. A server normally does this work itself; the test suite starts a server per test, and each
/// one parsed the same ~250 MB of tables again (2-3 s per test, and the memory that made parallel runs fail) and
/// generated a 2048-bit RSA key (0.2-0.6 s). Off by default: only the tests turn it on.
/// Files are keyed by path, size and write time, so a file rewritten in place is read again. Callers never change a
/// shared value; a table a server overlays with custom content is copied first (<see cref="GameData"/>).
/// </summary>
public static class ProcessCache
{
    /// <summary>Share parsed files and the generated gate key across servers in this process (the tests set it).</summary>
    public static bool Enabled { get; set; }

    private static readonly ConcurrentDictionary<(string Kind, string Path, long Length, DateTime Written), Lazy<object>> Files = new();

    /// <summary><paramref name="load"/>(<paramref name="path"/>), parsed once per process while sharing is on.</summary>
    public static T File<T>(string kind, string path, Func<string, T> load) where T : class
    {
        if (!Enabled) return load(path);
        var info = new FileInfo(path);
        var key = (kind, info.FullName, info.Exists ? info.Length : -1, info.Exists ? info.LastWriteTimeUtc : default);
        return (T)Files.GetOrAdd(key, _ => new Lazy<object>(() => load(path), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    /// <summary>The root of a JSON file (a scene editor). The document is never disposed: its buffers are GC memory.</summary>
    public static JsonElement Json(string path) =>
        File("json", path, p => new JsonBox(JsonDocument.Parse(System.IO.File.ReadAllBytes(p)).RootElement)).Root;

    private sealed record JsonBox(JsonElement Root);

    private static readonly ConcurrentDictionary<string, Lazy<string>> Generated = new();

    /// <summary>
    /// <paramref name="generate"/>(), or while sharing is on the value it produced earlier in this process under
    /// <paramref name="name"/> (a new gate RSA key costs 0.2-0.6 s; every test server would make one).
    /// </summary>
    public static string Generate(string name, Func<string> generate) =>
        Enabled ? Generated.GetOrAdd(name, _ => new Lazy<string>(generate)).Value : generate();
}
