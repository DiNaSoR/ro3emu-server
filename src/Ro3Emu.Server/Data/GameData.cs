using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Ro3Emu.Server.Data;

/// <summary>
/// The client's own config tables, exported to data/config/*.json by tools/ConfigExport. <see cref="Preload"/> parses
/// every table and skill template before the gates open (a cold table cost seconds inside a player's request); after
/// that, lookups are lock-free reads of a frozen snapshot. Without a preload (tests, tools) tables load on first use.
/// A missing table is empty (the server still runs, it just knows less).
/// </summary>
public sealed class GameData(IOptions<ServerOptions> options, ILogger<GameData> log)
{
    private readonly Dictionary<string, Dictionary<string, JsonElement>> _tables = new();
    private readonly object _sync = new();
    private volatile Dictionary<long, string>? _texts;
    private volatile FrozenDictionary<string, Dictionary<string, JsonElement>>? _frozen;

    private string Root => options.Value.GameDataDir ?? options.Value.DataDir;
    private CustomContent? _custom;

    /// <summary>Content written for this server (content/*.json), overlaid on the tables as they load.</summary>
    public CustomContent Custom
    {
        get
        {
            if (_custom is { } loaded) return loaded;
            lock (_sync) return _custom ??= CustomContent.Load(options.Value.CustomContentDir, log);
        }
    }

    /// <summary>Rows of a table keyed as in the client (usually the id as a string).</summary>
    public IReadOnlyDictionary<string, JsonElement> Table(string name)
    {
        if (_frozen is { } frozen && frozen.TryGetValue(name, out var hit)) return hit;
        lock (_sync)
        {
            if (_tables.TryGetValue(name, out var table)) return table;
            var path = Path.Combine(Root, "config", name + ".json");
            if (File.Exists(path)) table = new(ProcessCache.File("config", path, LoadConfig));
            else
            {
                table = new();
                if (!Custom.Rows.ContainsKey(name)) log.LogWarning("No {Path}; run tools/ConfigExport", path);
            }
            Custom.Overlay(name, table, log, KillTrackTarget);
            _tables[name] = table;
            return table;
        }
    }

    // The rows reference one document per table, which lives as long as the table (never disposed: its buffers are
    // ordinary GC memory then). Cloning every row made one document per row: ~2x the heap for the same data.
    private static Dictionary<string, JsonElement> LoadConfig(string path)
    {
        var table = new Dictionary<string, JsonElement>();
        var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        if (doc.RootElement.ValueKind == JsonValueKind.Object)
            foreach (var p in doc.RootElement.EnumerateObject()) table[p.Name] = p.Value;
        else if (doc.RootElement.ValueKind == JsonValueKind.Array)
            foreach (var (row, i) in doc.RootElement.EnumerateArray().Select((r, i) => (r, i)))
                table[row.ValueKind == JsonValueKind.Object && row.TryGetProperty("_iID", out var rid) ? rid.ToString() : (i + 1).ToString()] = row;
        return table;
    }

    private static Dictionary<string, JsonElement> LoadObject(string path)
    {
        var table = new Dictionary<string, JsonElement>();
        var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var p in doc.RootElement.EnumerateObject()) table[p.Name] = p.Value;
        return table;
    }

    /// <summary>
    /// Parses every config table and skill template in parallel, builds the text index and freezes the snapshot that
    /// lookups then read without a lock. Run once at startup, before players are admitted.
    /// </summary>
    public void Preload()
    {
        var watch = Stopwatch.StartNew();
        var loaded = new ConcurrentDictionary<string, Dictionary<string, JsonElement>>();
        var config = Path.Combine(Root, "config");
        var skills = Path.Combine(Root, "skills");
        var files = (Directory.Exists(config) ? Directory.GetFiles(config, "*.json").Select(p => (Key: Path.GetFileNameWithoutExtension(p), Path: p, Config: true)) : [])
            .Concat(Directory.Exists(skills) ? Directory.GetFiles(skills, "*.json").Select(p => (Key: "skills/" + Path.GetFileNameWithoutExtension(p), Path: p, Config: false)) : [])
            .ToArray();
        var custom = Custom;
        Parallel.ForEach(files, f =>
        {
            var table = f.Config ? new(ProcessCache.File("config", f.Path, LoadConfig)) : ProcessCache.File("object", f.Path, LoadObject);
            // TaskConfig waits for the other tables: its kill steps look up MapConfig and the scene editor
            // (KillTrackTarget) to point the tracker at the monsters.
            if (f.Config && f.Key != "TaskConfig") custom.Overlay(f.Key, table, log);
            loaded[f.Key] = table;
        });
        lock (_sync)
        {
            foreach (var (key, table) in loaded) _tables.TryAdd(key, table);
            if (loaded.TryGetValue("TaskConfig", out var tasks) && _tables["TaskConfig"] == tasks)
                custom.Overlay("TaskConfig", tasks, log, KillTrackTarget);
            _frozen = _tables.ToFrozenDictionary();
        }
        Text(0); // builds the text index
        log.LogInformation("Game data preloaded: {Tables} tables in {Ms} ms, managed heap {Mb} MB",
            files.Length, watch.ElapsedMilliseconds, GC.GetTotalMemory(false) >> 20);
    }

    public JsonElement? Row(string table, long id) => Table(table).TryGetValue(id.ToString(), out var row) ? row : null;

    private readonly ConcurrentDictionary<string, object> _indexes = new(StringComparer.Ordinal);

    /// <summary>
    /// The rows of <paramref name="table"/> grouped by <paramref name="key"/>, for lookups by a field other than the row
    /// id: a dictionary hit instead of a scan (a SkillConfig scan costs ~6 ms, an eighth of a game tick). Built on first
    /// use and kept; <paramref name="name"/> names the key in the cache, so one name always means one key function.
    /// </summary>
    public TableIndex<TKey> Index<TKey>(string table, string name, Func<JsonElement, TKey> key) where TKey : notnull
    {
        var rows = Table(table);
        string cacheKey = table + "/" + name;
        if (_indexes.TryGetValue(cacheKey, out var cached) && cached is TableIndex<TKey> index && index.Fresh(rows)) return index;
        index = new TableIndex<TKey>(rows, key);
        _indexes[cacheKey] = index;
        return index;
    }

    /// <summary>The first SkillConfig row of a skill group at a level, any mode (1600105 = BuyLow 16001 at Lv5).</summary>
    public JsonElement? SkillLevelRow(long skill, long level) =>
        Index("SkillConfig", "_iSkillID,_iLevel", r => (Int(r, "_iSkillID"), Int(r, "_iLevel"))).First((skill, level));

    /// <summary>Localized text (Localization_en: id -> {_kDes}); config "names" are arrays of text ids.</summary>
    public string Text(long id)
    {
        var texts = _texts;
        if (texts == null)
            lock (_sync)
            {
                texts = _texts;
                if (texts == null)
                {
                    texts = new();
                    foreach (var (_, row) in Table("Localization_en"))
                    {
                        if (row.TryGetProperty("_iID", out var tid) && row.TryGetProperty("_kDes", out var des) && des.ValueKind == JsonValueKind.String)
                            texts[tid.GetInt64()] = des.GetString()!;
                    }
                    _texts = texts; // published only when complete; readers never see a half-built index
                }
            }
        return texts.TryGetValue(id, out var text) ? text : "";
    }

    /// <summary>Text of a field holding [textId, ...].</summary>
    /// <summary>
    /// A localized text field [textId, param1, param2, ...]: the text with @{n} replaced by the n-th parameter, as the
    /// client's GetWordNew does (NPCConfig 7010001 _kName [10470000361, "1"] is "Lv. 1 Ore Vein").
    /// </summary>
    public string TextOf(JsonElement row, string field)
    {
        if (!row.TryGetProperty(field, out var v) || v.ValueKind != JsonValueKind.Array || v.GetArrayLength() == 0 || !v[0].TryGetInt64(out var id))
            return "";
        string text = Text(id);
        for (int i = 1; i < v.GetArrayLength(); i++)
        {
            string placeholder = "@{" + i + "}";
            if (text.Contains(placeholder))
                text = text.Replace(placeholder, v[i].ValueKind == JsonValueKind.String ? v[i].GetString() : v[i].GetRawText());
        }
        return text;
    }

    public string ItemName(long itemId) => Row("ItemConfig", itemId) is { } row ? TextOf(row, "_iName") : "";

    /// <summary>Items whose English name contains <paramref name="text"/> (case-insensitive), best match first.</summary>
    public IEnumerable<(long Id, string Name)> FindItems(string text) =>
        Table("ItemConfig").Values
            .Select(r => (Id: r.GetProperty("_iID").GetInt64(), Name: TextOf(r, "_iName")))
            .Where(i => i.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Name.Length);

    /// <summary>JobConfig _kAttri: [[attrIdx, value], ...] base attributes of a career (Novice: Speed 113 = 700).</summary>
    public IReadOnlyDictionary<long, long> JobAttributes(int career)
    {
        var result = new Dictionary<long, long>();
        if (Row("JobConfig", career) is { } job && job.TryGetProperty("_kAttri", out var attri) && attri.ValueKind == JsonValueKind.Array)
        {
            foreach (var pair in attri.EnumerateArray())
                if (pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() == 2) result[pair[0].GetInt64()] = pair[1].GetInt64();
        }
        return result;
    }

    /// <summary>An integer field of a row (0 when absent or not a number).</summary>
    public static long Int(JsonElement row, string field) =>
        row.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : 0;

    /// <summary>A field holding [[id, value, ...], ...] (attribute lists): (id, first value) pairs.</summary>
    public static IEnumerable<(long Id, double Value)> Pairs(JsonElement row, string field)
    {
        if (!row.TryGetProperty(field, out var list) || list.ValueKind != JsonValueKind.Array) yield break;
        foreach (var pair in list.EnumerateArray())
        {
            if (pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() >= 2 && Number(pair[0]) is { } id && Number(pair[1]) is { } value)
                yield return ((long)id, value);
        }
    }

    /// <summary>A number, also when the Lua table stored it as a string ("2000").</summary>
    public static double? Number(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.GetDouble(),
        JsonValueKind.String when double.TryParse(e.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    /// <summary>
    /// A skill template (Config/Skill/skill_<scriptShow>.lua exported to data/skills): m_fAtkRange (m), m_fTotalTime (s),
    /// m_kAtkPoints (hit moments in s on the template timeline).
    /// </summary>
    public JsonElement? SkillTemplate(long scriptShow)
    {
        string key = "skills/skill_" + scriptShow;
        if (_frozen is { } frozen && frozen.TryGetValue(key, out var hit)) return hit.Values.Cast<JsonElement?>().FirstOrDefault();
        lock (_sync)
        {
            if (!_tables.TryGetValue(key, out var table))
            {
                var path = Path.Combine(Root, "skills", "skill_" + scriptShow + ".json");
                table = File.Exists(path) ? ProcessCache.File("object", path, LoadObject) : new();
                _tables[key] = table;
            }
            return table.Values.Cast<JsonElement?>().FirstOrDefault();
        }
    }

    /// <summary>A whole table exported to data/skills (Lua_Config_EffectData: effect id -> definition).</summary>
    public IReadOnlyDictionary<string, JsonElement> SkillTemplateTable(string name)
    {
        string key = "skills/" + name;
        if (_frozen is { } frozen && frozen.TryGetValue(key, out var hit)) return hit;
        lock (_sync)
        {
            if (_tables.TryGetValue(key, out var table)) return table;
            var path = Path.Combine(Root, "skills", name + ".json");
            table = File.Exists(path) ? ProcessCache.File("object", path, LoadObject) : new();
            _tables[key] = table;
            return table;
        }
    }

    public string MapName(long sceneEntry) => Row("MapConfig", sceneEntry) is { } row ? TextOf(row, "_iName") : "";

    /// <summary>
    /// The NPCConfig id a scene-editor task point (m_kTaskPoints) on <paramref name="map"/> shows, or null when the map
    /// has no such point. The client's own talk targets often name one (TaskConfig 201010: point 2010101, NPC 400049).
    /// </summary>
    public long? TaskPointNpc(long map, long point)
    {
        if ((Row("MapConfig", map) ?? Row("DungeonConfig", map)) is not { } config
            || !config.TryGetProperty("_kResSceneEditorName", out var name) || name.GetString() is not { Length: > 0 } module
            || DataFiles.Find(Path.Combine(Root, "sceneeditor"), module + ".json") is not { } path) return null;
        return ProcessCache.Json(path).TryGetProperty("m_kTaskPoints", out var points) && points.ValueKind == JsonValueKind.Object
            && points.TryGetProperty(point.ToString(System.Globalization.CultureInfo.InvariantCulture), out var entry)
            ? Int(entry, "npcId") : null;
    }

    /// <summary>
    /// Where the quest tracker should walk for a custom kill step on <paramref name="map"/>: a scene-editor id that
    /// LC_InteractActions.GetPosByTargetID finds (action 142). That function looks in m_kRegins first and in
    /// m_kWorldMonsterSpawnPoints last, so the id is a region next to a spawn point of one of the monsters (the
    /// client's own kill quests point at such regions, e.g. 1010105001 by the South Gate Porings), else the spawn
    /// point itself when no earlier list uses its id. Null when the map has no spawn of these monsters.
    /// </summary>
    public long? KillTrackTarget(long map, long[] monsters)
    {
        if ((Row("MapConfig", map) ?? Row("DungeonConfig", map)) is not { } config
            || !config.TryGetProperty("_kResSceneEditorName", out var name) || name.GetString() is not { Length: > 0 } module
            || DataFiles.Find(Path.Combine(Root, "sceneeditor"), module + ".json") is not { } path) return null;
        var se = ProcessCache.Json(path);
        if (!se.TryGetProperty("m_kWorldMonsterSpawnPoints", out var points) || points.ValueKind != JsonValueKind.Object) return null;
        bool Spawns(JsonElement list, string? field) => list.ValueKind == JsonValueKind.Array && list.EnumerateArray().Any(e =>
            (field is null ? Number(e) : e.ValueKind == JsonValueKind.Object && e.TryGetProperty(field, out var f) ? Number(f) : null) is { } n
            && monsters.Contains((long)n));
        // Points that spawn only these monsters first (m_kConfigIds), then pooled points that may spawn them.
        var spawn = points.EnumerateObject().Select(p => p.Value)
            .Select(p => (Point: p, Rank: p.TryGetProperty("m_kConfigIds", out var ids) && Spawns(ids, null) ? 0
                : p.TryGetProperty("m_kMonsterRefreshLimitDatas", out var limits) && Spawns(limits, "m_kConfigId") ? 1 : 2))
            .Where(p => p.Rank < 2 && p.Point.TryGetProperty("position", out _))
            .OrderBy(p => p.Rank).ThenBy(p => Int(p.Point, "Id")).Select(p => p.Point).FirstOrDefault();
        if (spawn.ValueKind != JsonValueKind.Object) return null;
        var at = spawn.GetProperty("position");
        double x = Number(at.GetProperty("x")) ?? 0, z = Number(at.GetProperty("z")) ?? 0;
        if (se.TryGetProperty("m_kRegins", out var regions) && regions.ValueKind == JsonValueKind.Object)
        {
            var near = regions.EnumerateObject()
                .Where(r => long.TryParse(r.Name, out _) && r.Value.TryGetProperty("center", out _))
                .Select(r => (Id: long.Parse(r.Name, System.Globalization.CultureInfo.InvariantCulture), C: r.Value.GetProperty("center")))
                .Select(r => (r.Id, D: Math.Pow((Number(r.C.GetProperty("x")) ?? 0) - x, 2) + Math.Pow((Number(r.C.GetProperty("z")) ?? 0) - z, 2)))
                .Where(r => r.D <= 25 * 25).OrderBy(r => r.D).ToList();
            if (near.Count > 0) return near[0].Id;
        }
        string key = Int(spawn, "Id").ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var earlier in new[] { "m_kRegins", "m_kMonsterSpawnPoints", "m_kTaskPoints", "kNpcList", "m_kAllPoints" })
            if (se.TryGetProperty(earlier, out var list) && list.ValueKind == JsonValueKind.Object && list.TryGetProperty(key, out _)) return null;
        return Int(spawn, "Id");
    }
}

/// <summary>Runs <see cref="GameData.Preload"/> at startup, before the game loop and the gates start.</summary>
public sealed class GameDataPreload(GameData data) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) { data.Preload(); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
