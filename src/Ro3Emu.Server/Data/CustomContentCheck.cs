using System.Globalization;
using System.Text.Json;

namespace Ro3Emu.Server.Data;

/// <summary>
/// Finds mistakes in custom content (content/*.json) that loading alone does not catch: ids that point at nothing,
/// such as a quest's NPC, monster, map, dialogue, pre-task or reward, an NPC's model, or a text. Run at server start
/// (<see cref="CustomContentStartupCheck"/>, as warnings) and by "Ro3Emu.Server --check-content".
/// </summary>
public static class CustomContentCheck
{
    public static List<string> Problems(GameData data)
    {
        var custom = data.Custom;
        var problems = custom.Errors.Select(e => "skipped " + e).ToList();
        var texts = data.Table(CustomContent.TextTable);

        void Need(string what, string table, long id, string by)
        {
            if (id != 0 && data.Row(table, id) is null) problems.Add($"{by}: {what} {id} is not in {table}");
        }
        void NeedMap(long map, string by)
        {
            if (data.Row("MapConfig", map) is null && data.Row("DungeonConfig", map) is null) problems.Add($"{by}: map {map} is not in MapConfig");
        }
        void NeedText(JsonElement row, string field, string by)
        {
            if (row.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0
                && v[0].TryGetInt64(out long id) && id != 0 && !texts.ContainsKey(Key(id)))
                problems.Add($"{by}: {field} text {id} does not exist");
        }
        void NeedDialogs(JsonElement row, string field, string by)
        {
            foreach (var action in Arrays(row, field))
                if (action.Count == 2 && action[0] is "7" or "17" or "53" && long.TryParse(action[1], out long dialog))
                    Need("dialogue", "DialogueConfig", dialog, by);
        }

        foreach (var (table, ids) in custom.Rows)
            foreach (var id in ids.Keys)
            {
                string by = $"{table} {id}";
                if (!data.Table(table).TryGetValue(id, out var row)) { problems.Add($"{by}: did not load (see the log)"); continue; }
                switch (table)
                {
                    case "TaskConfig":
                        foreach (var f in new[] { "_iName", "_iTaskDescribe", "_iTargetDescribe" }) NeedText(row, f, by);
                        foreach (long pre in Longs(row, "_kPreTask")) Need("pre-task", "TaskConfig", pre, by);
                        // _iAward is a MultiverseGroupConfig id, resolved to its default AwardConfig row as the server does.
                        long award = GameData.Int(row, "_iAward");
                        Need("reward", "MultiverseGroupConfig", award, by);
                        if (award != 0 && data.Row("MultiverseGroupConfig", award) is { } group && GameData.Int(group, "_itype") == 0)
                            Need($"reward {award} item package", "AwardConfig", GameData.Int(group, "_iValue_default"), by);
                        if (row.TryGetProperty("_kProgressTarget", out var targets) && targets.ValueKind == JsonValueKind.Array)
                            foreach (var t in targets.EnumerateArray())
                            {
                                long[] n = t.ValueKind == JsonValueKind.Array ? t.EnumerateArray().Select(e => GameData.Number(e) is { } d ? (long)d : 0).ToArray() : [];
                                if (n is [104001, var npc, var map, var dialog, ..])
                                {
                                    // The NPC, or a scene task point on the map that shows an NPC (the client's own rows).
                                    if (data.Row("NPCConfig", npc) is null && data.TaskPointNpc(map, npc) is { } shown) npc = shown;
                                    Need("NPC", "NPCConfig", npc, by); NeedMap(map, by); Need("dialogue", "DialogueConfig", dialog, by);
                                }
                                else if (n is [104002 or 104014, .. var rest] && rest.Length >= 2)
                                    foreach (long monster in rest[..^1]) Need("monster", "NPCConfig", monster, by);
                            }
                        foreach (var track in Arrays(row, "_kTrack"))
                            if (track is ["142" or "143", var map, ..] && long.TryParse(map, out long m)) NeedMap(m, by);
                        break;
                    case "NPCConfig":
                        NeedText(row, "_kName", by);
                        Need("model", "UnitConfig", GameData.Int(row, "_iUnitID"), by);
                        foreach (long interact in Longs(row, "_iInteractID")) Need("interaction", "NPCInteractConfig", interact, by);
                        break;
                    case "NPCInteractConfig":
                        NeedDialogs(row, "_iEXECAction", by);
                        break;
                    case "DialogueConfig":
                        foreach (var page in row.ValueKind == JsonValueKind.Array ? row.EnumerateArray().ToList() : [row])
                        {
                            NeedText(page, "_iText", by);
                            NeedDialogs(page, "_iEXECAction", by);
                        }
                        break;
                }
            }
        foreach (var npc in custom.Npcs)
        {
            string by = $"{npc.Source}: NPC {npc.NpcId} on map {npc.Map}";
            Need("NPC", "NPCConfig", npc.NpcId, by);
            NeedMap(npc.Map, by);
        }
        problems.AddRange(custom.Warnings);
        return problems;
    }

    /// <summary>"Ro3Emu.Server --check-content [config args]": prints what <see cref="Problems"/> finds; exit code 1 if any.</summary>
    public static int RunCli(ServerOptions options, TextWriter output)
    {
        using var logs = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
        var data = new GameData(Microsoft.Extensions.Options.Options.Create(options), logs.CreateLogger<GameData>());
        var custom = data.Custom;
        output.WriteLine($"Custom content in {Path.GetFullPath(options.CustomContentDir)}: {custom.Files.Count} file(s), " +
            $"{custom.Rows.Values.Sum(r => r.Count)} row(s), {custom.Texts.Count} text(s), {custom.Npcs.Count} placed NPC(s)");
        var problems = Problems(data);
        foreach (var p in problems) output.WriteLine("  problem: " + p);
        output.WriteLine(problems.Count == 0 ? "No problems found." : $"{problems.Count} problem(s).");
        return problems.Count == 0 ? 0 : 1;
    }

    private static string Key(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static IEnumerable<long> Longs(JsonElement row, string field) =>
        row.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(e => GameData.Number(e) is { } d ? (long)d : 0).Where(n => n != 0)
            : [];

    /// <summary>A field like _kTrack or _iEXECAction: an array of string arrays.</summary>
    private static IEnumerable<List<string>> Arrays(JsonElement row, string field) =>
        row.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Array)
                .Select(a => a.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText()).ToList())
            : [];
}

/// <summary>Logs <see cref="CustomContentCheck"/>'s findings at server start.</summary>
public sealed class CustomContentStartupCheck(GameData data, ILogger<CustomContentStartupCheck> log) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (data.Custom.IsEmpty && data.Custom.Errors.Count == 0) return Task.CompletedTask;
        var problems = CustomContentCheck.Problems(data);
        foreach (var p in problems) log.LogWarning("Custom content: {Problem}", p);
        if (problems.Count == 0) log.LogInformation("Custom content checked: no problems found");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
