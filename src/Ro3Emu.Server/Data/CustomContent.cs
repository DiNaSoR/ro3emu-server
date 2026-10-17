using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ro3Emu.Server.Data;

/// <summary>
/// Content the client does not ship: quests, NPCs, dialogue and text written for this server (docs/guide/custom-content.md).
/// Every *.json under <see cref="ServerOptions.CustomContentDir"/> (default "content") may hold:
/// <list type="bullet">
/// <item>"texts": { "textId": "English text" }, which become Localization_en rows {_iID, _kDes};</item>
/// <item>"rows": { "Table": { "id": row } }, rows added to (or replacing rows of) the client's config tables. A row object
/// may name "_base": an existing row id of the same table, copied first; its own fields then replace the copy's
/// top-level fields, and the base's id fields (_iID, _i*ID equal to the base id) take the new id. A row may also be an
/// array of row objects (DialogueConfig keeps one array per dialogue id);</item>
/// <item>"npcs": [ { "map", "npc", "x", "z", "y"?, "face"? } ], server NPCs placed on a map (WorldData).</item>
/// </list>
/// GameData overlays the rows on the tables it loads, so the server reads them like client data; the client gets the
/// same rows and texts at role login (CustomContentService, game message 77020) and Ro3Emu.ClientHooks.CustomContent
/// adds them to the client's own tables. Files load once, at the first table load; a broken file is skipped with an error.
/// </summary>
public sealed class CustomContent
{
    public sealed record Npc(long Map, int NpcId, float X, float? Y, float Z, float Face, string Source);

    /// <summary>Table name -> id -> the row as written (before its _base is applied).</summary>
    public IReadOnlyDictionary<string, Dictionary<string, JsonNode>> Rows => _rows;
    public IReadOnlyDictionary<long, string> Texts => _texts;
    public IReadOnlyList<Npc> Npcs => _npcs;
    public bool IsEmpty => _rows.Count == 0 && _texts.Count == 0 && _npcs.Count == 0;

    private readonly Dictionary<string, Dictionary<string, JsonNode>> _rows = new(StringComparer.Ordinal);
    private readonly Dictionary<long, string> _texts = new();
    private readonly List<Npc> _npcs = new();

    public const string TextTable = "Localization_en";

    public static CustomContent Load(string dir, ILogger log)
    {
        var content = new CustomContent();
        if (!Directory.Exists(dir)) return content;
        foreach (var file in Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                content.Add(file, JsonNode.Parse(File.ReadAllText(file), documentOptions: new JsonDocumentOptions
                    { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                    ?? throw new InvalidDataException("not a JSON object"));
            }
            catch (Exception e)
            {
                log.LogError("Custom content {File} skipped: {Message}", file, e.Message);
            }
        }
        if (!content.IsEmpty)
            log.LogInformation("Custom content from {Dir}: {Rows} row(s) in {Tables} table(s), {Texts} text(s), {Npcs} NPC(s)",
                dir, content._rows.Values.Sum(t => t.Count), content._rows.Count, content._texts.Count, content._npcs.Count);
        return content;
    }

    private void Add(string file, JsonObject root)
    {
        var name = Path.GetFileName(file);
        if (root["texts"] is JsonObject texts)
            foreach (var (key, value) in texts)
            {
                if (!long.TryParse(key, out long id) || value?.GetValueKind() != JsonValueKind.String)
                    throw new InvalidDataException($"texts.{key}: needs a numeric id and a string");
                _texts[id] = value.GetValue<string>();
            }
        if (root["rows"] is JsonObject tables)
            foreach (var (table, rows) in tables)
            {
                if (rows is not JsonObject byId) throw new InvalidDataException($"rows.{table}: needs an object of id -> row");
                if (!_rows.TryGetValue(table, out var target)) _rows[table] = target = new(StringComparer.Ordinal);
                foreach (var (id, row) in byId)
                {
                    if (!long.TryParse(id, out _) || row is not (JsonObject or JsonArray))
                        throw new InvalidDataException($"rows.{table}.{id}: needs a numeric id and an object (or an array of objects)");
                    target[id] = row.DeepClone();
                }
            }
        if (root["npcs"] is JsonArray npcs)
            foreach (var n in npcs)
            {
                if (n is not JsonObject o || Num(o, "map") is not { } map || Num(o, "npc") is not { } npc
                    || Num(o, "x") is not { } x || Num(o, "z") is not { } z)
                    throw new InvalidDataException("npcs: each needs map, npc, x and z");
                _npcs.Add(new Npc((long)map, (int)npc, (float)x, Num(o, "y") is { } y ? (float)y : null, (float)z,
                    (float)(Num(o, "face") ?? 0), name));
            }
    }

    private static double? Num(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    /// <summary>
    /// Adds this content's rows of <paramref name="name"/> to a freshly loaded table (and the texts to Localization_en).
    /// <paramref name="table"/>'s own rows serve as the _base rows.
    /// </summary>
    public void Overlay(string name, Dictionary<string, JsonElement> table, ILogger log)
    {
        if (name == TextTable)
            foreach (var (id, text) in _texts)
                table[id.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                    JsonSerializer.SerializeToElement(new JsonObject { ["_iID"] = id, ["_kDes"] = text });
        if (!_rows.TryGetValue(name, out var rows)) return;
        foreach (var (id, row) in rows)
        {
            try
            {
                table[id] = JsonSerializer.SerializeToElement(Resolve(name, id, row, table));
            }
            catch (Exception e)
            {
                log.LogError("Custom content {Table} {Id} skipped: {Message}", name, id, e.Message);
            }
        }
    }

    private static JsonNode Resolve(string table, string id, JsonNode row, Dictionary<string, JsonElement> rows)
    {
        if (row is JsonArray array)
        {
            var list = new JsonArray();
            foreach (var item in array) list.Add(item is JsonObject o ? Resolve(table, id, o, rows) : throw new InvalidDataException("array rows hold objects"));
            return list;
        }
        var obj = (JsonObject)row.DeepClone();
        if (obj["_base"] is not JsonNode baseNode) return obj;
        obj.Remove("_base");
        string baseId = baseNode.ToString();
        if (!rows.TryGetValue(baseId, out var baseRow) || baseRow.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"_base {baseId} is not a row of {table}");
        var merged = JsonNode.Parse(baseRow.GetRawText())!.AsObject();
        long newId = long.Parse(id, System.Globalization.CultureInfo.InvariantCulture);
        long oldId = long.Parse(baseId, System.Globalization.CultureInfo.InvariantCulture);
        foreach (var key in merged.Select(p => p.Key).ToList())
            if (key.StartsWith("_i", StringComparison.Ordinal) && key.EndsWith("ID", StringComparison.Ordinal)
                && merged[key] is JsonValue v && v.TryGetValue<long>(out long value) && value == oldId)
                merged[key] = newId;
        foreach (var (key, value) in obj) merged[key] = value?.DeepClone();
        return merged;
    }

    /// <summary>
    /// What the client needs, as UTF-8 JSON: {"texts": {id: text}, "rows": {Table: {id: row}}} with every row resolved
    /// (rows come from <paramref name="table"/>, i.e. GameData after the overlay).
    /// </summary>
    public byte[] ClientPayload(Func<string, IReadOnlyDictionary<string, JsonElement>> table)
    {
        var texts = new JsonObject();
        foreach (var (id, text) in _texts.OrderBy(t => t.Key)) texts[id.ToString(System.Globalization.CultureInfo.InvariantCulture)] = text;
        var rows = new JsonObject();
        foreach (var (name, ids) in _rows.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            var loaded = table(name);
            var outRows = new JsonObject();
            foreach (var id in ids.Keys.OrderBy(k => k, StringComparer.Ordinal))
                if (loaded.TryGetValue(id, out var row)) outRows[id] = JsonNode.Parse(row.GetRawText());
            rows[name] = outRows;
        }
        return Encoding.UTF8.GetBytes(new JsonObject { ["texts"] = texts, ["rows"] = rows }.ToJsonString());
    }
}
