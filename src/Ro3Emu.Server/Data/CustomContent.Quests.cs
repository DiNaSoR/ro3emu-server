using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ro3Emu.Server.Data;

/// <summary>
/// The plain-text quest format (docs/guide/custom-content.md, "Writing quests"): whole NPCs ("npcs" entries with a
/// "name") and quest chains ("quests"), turned into the same NPCConfig, NPCInteractConfig, DialogueConfig, TaskConfig
/// rows and texts a hand-written file would hold. Only the NPC and quest ids are chosen by the author, because saved
/// characters keep them; text and dialogue ids are derived from them (they are never saved, so a change only needs a
/// relog). The rows copy the client rows the prototype quest was built from: NPC 200014 (Alphonse), talk quest
/// 10101130 and kill quest 10101050.
/// </summary>
public sealed partial class CustomContent
{
    /// <summary>Text ids made for NPCs and quest steps: base + (id % 1,000,000) * 100 + n.</summary>
    public const long NpcTextBase = 97_000_000_000, QuestTextBase = 98_000_000_000;
    /// <summary>Dialogue ids made for NPCs and quest steps: base + (id % 1,000,000) * 100 + page. They must fit a
    /// 32-bit int, because the dialogue id travels in 31208.</summary>
    public const long NpcDialogBase = 1_000_000_000, QuestDialogBase = 900_000_000;

    private const long NpcBase = 200014, InteractBase = 20001401, TalkQuestBase = 10101130, KillQuestBase = 10101050;
    private const long BaseLevelOneCondition = 2000008;
    private const int TalkTarget = 104001, KillTarget = 104002, PartyKillTarget = 104014;

    /// <summary>
    /// "type" of a quest: the client's enum_TaskType, the quest log tag (TaskConfig._iShowTag, as the client's own
    /// quests of that type carry it) and the bubble over the NPC a talk step names (_kNPCProgressMark, shown by
    /// LC_System_TaskTag while the step runs; the client's main quests use the orange one, side quests the blue one,
    /// and the green one is the same bubble in the Guide tag's colour).
    /// </summary>
    private static readonly Dictionary<string, (int Type, long Tag, string Colour, string Mark)> QuestTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["main"] = (1, 13150300005, "#ec8c2f", "task_icon_npc_yellew02.png"),
            ["side"] = (2, 13150300008, "#6890d2", "task_icon_npc_blue02.png"),
            ["guide"] = (3, 13150300010, "#62af51", "task_icon_npc_green02.png"),
        };

    private readonly List<(JsonObject Quest, string File)> _quests = new();
    /// <summary>Custom NPC id -> the map it is placed on, for talk steps that name no map.</summary>
    private readonly Dictionary<long, long> _npcMaps = new();
    /// <summary>Kill steps written with a "map": TaskConfig id -> where the tracker should walk, found at overlay.</summary>
    private readonly Dictionary<string, (long Map, long[] Monsters)> _killTracks = new(StringComparer.Ordinal);
    /// <summary>Text and dialogue ids made so far -> what made them, to report two owners sharing an id.</summary>
    private readonly Dictionary<long, string> _madeIds = new();

    /// <summary>
    /// { "id", "name", "title"?, "model"?, "icon"?, "copy"?, "greeting", "goodbye"?, "map"?, "x"?, "z"?, "y"?, "face"? }
    /// </summary>
    private void AddNpcDefinition(JsonObject o, string file)
    {
        long id = Id(o, "id", "npcs");
        string where = $"npcs {id}";
        var texts = new TextMaker(this, NpcTextBase, id, where);
        long dialog = DialogId(NpcDialogBase, id, 1, where);

        var npc = new JsonObject
        {
            ["_base"] = Opt(o, "copy") ?? NpcBase,
            ["_kName"] = texts.Field(Need(o, "name", where)),
            ["_kTitle"] = texts.Field(Str(o, "title") ?? ""),
            ["_iInteractID"] = new JsonArray(id),
        };
        if (Opt(o, "model") is { } model)
        {
            npc["_iUnitID"] = model;
            npc["_kHeadIcon"] = Str(o, "icon") ?? $"temphead_npc_{model}.png";
            npc["_kDialoguePhoto"] = "";
        }
        else if (Str(o, "icon") is { } icon) npc["_kHeadIcon"] = icon;
        Row("NPCConfig", id, npc);
        Row("NPCInteractConfig", id, new JsonObject
        {
            ["_base"] = InteractBase,
            ["_iEXECAction"] = new JsonArray(new JsonArray("53", Str(dialog))),
        });
        Row("DialogueConfig", dialog, new JsonArray(
            DialogRow(dialog, id, texts.Field(Need(o, "greeting", where))),
            ReplyRow(dialog, id, texts.Field(Str(o, "goodbye") ?? "Goodbye"), new JsonArray("8", "TOPBAR", "UI_Sys_NpcDialogPanel"))));

        if (o.ContainsKey("map"))
        {
            long map = Id(o, "map", where);
            if (Num(o, "x") is not { } x || Num(o, "z") is not { } z) throw new InvalidDataException($"{where}: a placed NPC needs x and z");
            _npcs.Add(new Npc(map, checked((int)id), (float)x, Num(o, "y") is { } y ? (float)y : null, (float)z, (float)(Num(o, "face") ?? 0), file));
            _npcMaps[id] = map;
        }
    }

    /// <summary>Turns every "quests" entry into rows, once all files are read (a step may talk to an NPC of
    /// another file). A broken quest is skipped whole.</summary>
    private void ExpandQuests(ILogger log)
    {
        foreach (var (quest, file) in _quests)
        {
            var scratch = new CustomContent();
            foreach (var (k, v) in _npcMaps) scratch._npcMaps[k] = v;
            foreach (var (k, v) in _madeIds) scratch._madeIds[k] = v;
            try
            {
                scratch.AddQuest(quest);
            }
            catch (Exception e)
            {
                string msg = $"{file}: quest {quest["id"]?.ToJsonString() ?? "?"}: {e.Message}";
                _errors.Add(msg);
                log.LogError("Custom content {Message} (quest skipped)", msg);
                continue;
            }
            foreach (var (table, rows) in scratch._rows)
            {
                if (!_rows.TryGetValue(table, out var target)) _rows[table] = target = new(StringComparer.Ordinal);
                foreach (var (id, row) in rows) target[id] = row;
            }
            foreach (var (k, v) in scratch._texts) _texts[k] = v;
            foreach (var (k, v) in scratch._killTracks) _killTracks[k] = v;
            foreach (var (k, v) in scratch._madeIds) _madeIds[k] = v;
        }
        _quests.Clear();
    }

    /// <summary>
    /// { "id", "after"?, "type"? ("main", "side" (default) or "guide"), "steps": [ { "name", "description", "objective", and either "talk" (NPC id) + "dialog"
    /// (+ "map" for a client NPC) or "kill" (monster id or ids) + "count" (+ "map", "solo"); "exp"?, "award"?,
    /// "track"?, "fields"? } ] }. Step n takes TaskConfig id + n and follows step n - 1.
    /// </summary>
    private void AddQuest(JsonObject q)
    {
        long first = Id(q, "id", "quests");
        if (q["steps"] is not JsonArray { Count: > 0 } steps) throw new InvalidDataException("needs \"steps\": [ ... ]");
        long? after = q.ContainsKey("after") ? Id(q, "after", "quests") : null;
        string typeName = Str(q, "type") ?? "side";
        if (!QuestTypes.TryGetValue(typeName, out var type))
            throw new InvalidDataException($"\"type\" is \"{typeName}\"; use \"main\", \"side\" or \"guide\"");
        for (int i = 0; i < steps.Count; i++)
        {
            long id = first + i;
            string where = $"step {i + 1} ({id})";
            if (steps[i] is not JsonObject step) throw new InvalidDataException($"{where}: needs to be an object");
            var texts = new TextMaker(this, QuestTextBase, id, where);
            bool talk = step.ContainsKey("talk"), kill = step.ContainsKey("kill");
            if (talk == kill) throw new InvalidDataException($"{where}: needs either \"talk\" or \"kill\"");

            string description = Need(step, "description", where);
            string objective = Need(step, "objective", where);
            var row = new JsonObject
            {
                ["_base"] = talk ? TalkQuestBase : KillQuestBase,
                ["_iTaskType"] = type.Type,
                ["_iShowTag"] = new JsonArray(type.Tag, $"<color={type.Colour}>", "</color>"),
                ["_kNPCProgressMark"] = type.Mark,
                ["_iTaskRank"] = id,
                ["_iName"] = texts.Field(Need(step, "name", where)),
                ["_iTaskDescribe"] = texts.Field(description),
                ["_iBeforeDescribe"] = texts.Field(description),
                // A line ending in * shows the progress counter (5/5) in the quest log; kills want one.
                ["_iTargetDescribe"] = texts.Field(kill && !objective.EndsWith('*') ? objective + "*" : objective),
                ["_iStageName"] = new JsonArray(0),
                ["_iAward"] = Opt(step, "award") ?? 0,
                ["_kPreTask"] = i > 0 ? new JsonArray(id - 1) : after is { } pre ? new JsonArray(pre) : new JsonObject(),
                ["_kEXP"] = Opt(step, "exp") is { } exp ? new JsonArray(1, exp) : new JsonObject(),
            };
            // No pre-task: offered to every character from base level 1 (TaskService.AcceptReady).
            if (i == 0 && after is null) row["_iCondition"] = BaseLevelOneCondition;

            if (talk)
            {
                long npc = Id(step, "talk", where);
                long map = step.ContainsKey("map") ? Id(step, "map", where)
                    : _npcMaps.TryGetValue(npc, out long placed) ? placed
                    : throw new InvalidDataException($"{where}: NPC {npc} is not placed by a custom file, so the step needs a \"map\"");
                long dialog = AddDialog(step, id, npc, texts, where);
                row["_kProgressTarget"] = new JsonArray(new JsonArray(TalkTarget, npc, map, dialog));
                row["_kTrack"] = new JsonArray(new JsonArray("143", Str(map), Str(npc), "9", Str(dialog)));
            }
            else
            {
                long[] monsters = step["kill"] switch
                {
                    JsonArray list when list.Count > 0 => list.Select(m => m is JsonValue v && v.TryGetValue<long>(out long n) ? n
                        : throw new InvalidDataException($"{where}: \"kill\" holds monster ids")).ToArray(),
                    JsonValue v when v.TryGetValue<long>(out long n) => [n],
                    _ => throw new InvalidDataException($"{where}: \"kill\" needs a monster id or a list of them"),
                };
                long count = Id(step, "count", where);
                var target = new JsonArray(step["solo"]?.GetValue<bool>() == true ? KillTarget : PartyKillTarget);
                foreach (long m in monsters) target.Add(m);
                target.Add(count);
                row["_kProgressTarget"] = new JsonArray(target);
                // The copied quest tracks its own spawn; with no "map" the tracker has nowhere to walk.
                row["_kTrack"] = new JsonArray(new JsonArray(""));
                if (step.ContainsKey("map")) _killTracks[Str(id)] = (Id(step, "map", where), monsters);
            }
            if (step["track"] is JsonArray track) { row["_kTrack"] = new JsonArray(track.DeepClone()); _killTracks.Remove(Str(id)); }
            if (step["fields"] is JsonObject fields)
                foreach (var (key, value) in fields) row[key] = value?.DeepClone();
            Row("TaskConfig", id, row);
        }
    }

    /// <summary>
    /// "dialog": [ page, ... ]: a page is the NPC's line, as a string or { "say", "reply"? }. Every page but the last
    /// gets a reply button ("Continue" unless written) that opens the next page; closing the last page finishes the
    /// step (action 17 names the step's first dialogue, which is what the server matches).
    /// </summary>
    private long AddDialog(JsonObject step, long id, long npc, TextMaker texts, string where)
    {
        if (step["dialog"] is not JsonArray { Count: > 0 } pages) throw new InvalidDataException($"{where}: a talk step needs \"dialog\": [ ... ]");
        long first = DialogId(QuestDialogBase, id, 1, where);
        for (int p = 0; p < pages.Count; p++)
        {
            long dialog = DialogId(QuestDialogBase, id, p + 1, where);
            (string say, string? reply) = pages[p] switch
            {
                JsonValue v when v.GetValueKind() == JsonValueKind.String => (v.GetValue<string>(), null),
                JsonObject o => (Need(o, "say", $"{where} dialog page {p + 1}"), Str(o, "reply")),
                _ => throw new InvalidDataException($"{where}: each dialog page is a string or {{ \"say\", \"reply\" }}"),
            };
            bool last = p == pages.Count - 1;
            if (last && reply is not null) throw new InvalidDataException($"{where}: the last dialog page cannot have a reply (closing it finishes the step)");
            var text = DialogRow(dialog, npc, texts.Field(say));
            if (last)
            {
                text["_iEXECAction"] = new JsonArray(new JsonArray("17", Str(first)));
                Row("DialogueConfig", dialog, new JsonArray(text));
            }
            else
                Row("DialogueConfig", dialog, new JsonArray(text,
                    ReplyRow(dialog, npc, texts.Field(reply ?? "Continue"), new JsonArray("7", Str(dialog + 1)))));
        }
        return first;
    }

    private static JsonObject DialogRow(long dialog, long npc, JsonArray text) => new()
    {
        ["_iID"] = dialog, ["_iNPCID"] = npc, ["_iTalkNPCID"] = npc, ["_iText"] = text,
    };

    private static JsonObject ReplyRow(long dialog, long npc, JsonArray text, JsonArray action) => new()
    {
        ["_iID"] = dialog, ["_iNPCID"] = npc, ["_iTalkNPCID"] = npc, ["_iSortID"] = 2, ["_iTextType"] = 2,
        ["_iIcon"] = "npcdialog_icon_chat.png", ["_iText"] = text, ["_iEXECAction"] = new JsonArray(action),
    };

    private long DialogId(long @base, long owner, int page, string where)
    {
        if (page > 99) throw new InvalidDataException($"{where}: at most 99 dialog pages");
        long id = @base + owner % 1_000_000 * 100 + page;
        Claim(id, where);
        return id;
    }

    private void Claim(long id, string where)
    {
        if (_madeIds.TryGetValue(id, out var other) && other != where)
            throw new InvalidDataException($"{where} would reuse id {id} of {other}: ids that end in the same six digits clash");
        _madeIds[id] = where;
    }

    private void Row(string table, long id, JsonNode row)
    {
        if (!_rows.TryGetValue(table, out var rows)) _rows[table] = rows = new(StringComparer.Ordinal);
        rows[Str(id)] = row;
    }

    /// <summary>Makes the texts of one NPC or quest step, and the field arrays that show them.</summary>
    private sealed class TextMaker(CustomContent content, long @base, long owner, string where)
    {
        private int _next = 1;
        private readonly Dictionary<string, JsonArray> _made = new(StringComparer.Ordinal);

        /// <summary>
        /// [textId, values...]. Tags in the text (&lt;color=#CC762A&gt;…&lt;/color&gt;) become ^{n} placeholders with the
        /// tag as value n, the way the client's own strings carry them; a raw tag in the text shows as written.
        /// </summary>
        public JsonArray Field(string text)
        {
            if (_made.TryGetValue(text, out var same)) return (JsonArray)same.DeepClone();
            if (_next > 99) throw new InvalidDataException($"{where}: too many texts");
            long id = @base + owner % 1_000_000 * 100 + _next++;
            content.Claim(id, where);
            var field = new JsonArray(id);
            var sb = new StringBuilder();
            int last = 0;
            foreach (Match m in Tag().Matches(text))
            {
                sb.Append(text, last, m.Index - last);
                field.Add(m.Value);
                sb.Append("^{").Append(field.Count - 1).Append('}');
                last = m.Index + m.Length;
            }
            sb.Append(text, last, text.Length - last);
            content._texts[id] = sb.ToString();
            _made[text] = field;
            return (JsonArray)field.DeepClone();
        }
    }

    [GeneratedRegex(@"</?color(=[^>]*)?>", RegexOptions.IgnoreCase)]
    private static partial Regex Tag();

    private static string Str(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static string Need(JsonObject o, string key, string where) =>
        Str(o, key) is { Length: > 0 } s ? s : throw new InvalidDataException($"{where}: needs \"{key}\" (text)");

    private static long? Opt(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<long>(out long n) ? n
        : o.ContainsKey(key) ? throw new InvalidDataException($"\"{key}\" needs a whole number") : null;

    private static long Id(JsonObject o, string key, string where) =>
        Opt(o, key) is { } n && n > 0 ? n : throw new InvalidDataException($"{where}: needs \"{key}\" (a number)");
}
