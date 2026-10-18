using System.Globalization;
using System.Text.Json;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Scripted story scenes (Reconstructed/scripted-scenes.json; its "contract" section cites the client
/// code for every event used here). The original server's scene script is not in the client: each scene is rebuilt as
/// steps, each naming the DungeonQuestConfig quest the tracker shows, the scene actors shown during it, an optional
/// run-time story the client plays, the story lines said during it, and the client events that complete it. Nothing is
/// keyed by a task id: a step completes on any listed event (interaction, dungeon message, region, point or plot end),
/// or, when it has spawns (a fight step), once all of them are killed.
/// </summary>
public sealed partial class ReconstructedDungeons
{
    public enum TriggerKind { Interaction, Message, Region, Point, Plot }

    /// <summary>A client event that completes (or, under "ignore", deliberately does not complete) a step.</summary>
    public sealed record StepTrigger(TriggerKind Kind, long Id, float Radius = 0);

    /// <summary>A scene actor: an NPCConfig id at a scene-editor point, with the buffs (BuffConfig ids) it carries.</summary>
    public sealed record SceneActor(int NpcId, long Point, IReadOnlyList<int> Buffs);

    /// <summary>
    /// A ChatConfig story line, said <paramref name="Ms"/> after the step starts (or as it completes, for a "done" line).
    /// <paramref name="By"/> is the NPCConfig id of the speaker (a scene actor or a spawned monster), needed for a type-2
    /// bubble and otherwise 0.
    /// </summary>
    public sealed record StepLine(int Chat, int By, int Ms);

    /// <summary>
    /// One scripted step. <paramref name="Story"/> is a PlotInterpretation id the client plays at the step's start
    /// (0: none); <paramref name="TeleportAfter"/> a scene-editor point the participants are moved to when it
    /// completes (0: none). <paramref name="Goal"/> is the tracker's "(done/total)" while the step runs.
    /// <paramref name="Spawns"/> makes it a fight step: those monsters spawn at its start and it completes when all are
    /// killed (the tracker counts the kills); it has no completing client event.
    /// </summary>
    public sealed record ScriptedStep(string Stage, int Quest, (int Done, int Total) Goal, IReadOnlyList<SceneActor> Actors,
        long Story, IReadOnlyList<StepTrigger> Complete, IReadOnlyList<StepTrigger> Ignore, long TeleportAfter,
        IReadOnlyList<Spawn>? Spawns = null, IReadOnlyList<StepLine>? Lines = null, IReadOnlyList<StepLine>? DoneLines = null)
    {
        public IReadOnlyList<Spawn> Spawns { get; init; } = Spawns ?? [];
        public IReadOnlyList<StepLine> Lines { get; init; } = Lines ?? [];
        public IReadOnlyList<StepLine> DoneLines { get; init; } = DoneLines ?? [];
        public bool Fight => Spawns.Count > 0;
    }

    /// <summary>
    /// A scene. <paramref name="Unplayed"/> are DungeonQuestConfig quests of the scene that no step shows: the branch
    /// that shows them cannot be reached in the reconstruction (each has its reason in the json, "unplayed").
    /// </summary>
    public sealed record ScriptedScene(long Scene, string Name, string Confidence, IReadOnlyList<ScriptedStep> Steps,
        IReadOnlyList<int> Unplayed);

    private readonly Dictionary<long, ScriptedScene> _scripted = new();

    public IReadOnlyCollection<ScriptedScene> AllScripted => _scripted.Values;

    /// <summary>The scripted reconstruction of a scene, when the switch is on.</summary>
    public ScriptedScene? Scripted(long scene) => Enabled && _scripted.TryGetValue(scene, out var s) ? s : null;

    private void LoadScriptedScenes()
    {
        using var stream = typeof(ReconstructedDungeons).Assembly.GetManifestResourceStream("Ro3Emu.Server.Reconstructed.scripted-scenes.json")
            ?? throw new InvalidOperationException("Embedded Reconstructed/scripted-scenes.json is missing.");
        using var doc = JsonDocument.Parse(stream);
        foreach (var entry in doc.RootElement.GetProperty("scenes").EnumerateObject())
        {
            long scene = long.Parse(entry.Name, CultureInfo.InvariantCulture);
            var steps = entry.Value.GetProperty("steps").EnumerateArray().Select(Step).ToArray();
            if (steps.Length == 0) throw new InvalidOperationException($"Scripted scene {scene} has no steps.");
            var unplayed = entry.Value.TryGetProperty("unplayed", out var u)
                ? u.EnumerateArray().Select(q => q.GetProperty("quest").GetInt32()).ToArray() : [];
            _scripted[scene] = new ScriptedScene(scene, entry.Value.GetProperty("name").GetString() ?? "",
                entry.Value.GetProperty("confidence").GetString() ?? "", steps, unplayed);
        }
    }

    private static ScriptedStep Step(JsonElement s)
    {
        var goal = s.TryGetProperty("goal", out var g) ? (g[0].GetInt32(), g[1].GetInt32()) : (0, 1);
        var actors = s.TryGetProperty("actors", out var a)
            ? a.EnumerateArray().Select(x => new SceneActor(x.GetProperty("npc").GetInt32(), x.GetProperty("point").GetInt64(),
                x.TryGetProperty("buffs", out var b) ? b.EnumerateArray().Select(v => v.GetInt32()).ToArray() : [])).ToArray()
            : [];
        var complete = Triggers(s, "complete");
        var spawns = s.TryGetProperty("spawns", out var sp)
            ? sp.EnumerateArray().Select(x => new Spawn(x.GetProperty("point").GetInt64(), x.GetProperty("npc").GetInt32())).ToArray()
            : [];
        string stage = s.GetProperty("stage").GetString() ?? "";
        if (complete.Count == 0 && spawns.Length == 0) throw new InvalidOperationException($"Scripted step '{stage}' cannot complete.");
        if (complete.Count > 0 && spawns.Length > 0)
            throw new InvalidOperationException($"Scripted fight step '{stage}' completes on its kills, not on client events.");
        return new ScriptedStep(stage, s.GetProperty("quest").GetInt32(), goal, actors,
            s.TryGetProperty("story", out var story) ? story.GetInt64() : 0, complete, Triggers(s, "ignore"),
            s.TryGetProperty("after", out var after) && after.TryGetProperty("teleport", out var to) ? to.GetInt64() : 0,
            spawns, Lines(s, "lines"), Lines(s, "doneLines"));
    }

    private static StepLine[] Lines(JsonElement step, string field) => step.TryGetProperty(field, out var list)
        ? list.EnumerateArray().Select(l => new StepLine(l.GetProperty("chat").GetInt32(),
            l.TryGetProperty("by", out var by) ? by.GetInt32() : 0, l.TryGetProperty("ms", out var ms) ? ms.GetInt32() : 0)).ToArray()
        : [];

    private static IReadOnlyList<StepTrigger> Triggers(JsonElement step, string field)
    {
        if (!step.TryGetProperty(field, out var list)) return [];
        var triggers = new List<StepTrigger>();
        foreach (var t in list.EnumerateArray())
        {
            var kinds = Enum.GetValues<TriggerKind>().Where(k => t.TryGetProperty(k.ToString().ToLowerInvariant(), out _)).ToArray();
            if (kinds.Length != 1) throw new InvalidOperationException($"A scripted trigger names {kinds.Length} kinds: {t}");
            var kind = kinds[0];
            triggers.Add(new StepTrigger(kind, t.GetProperty(kind.ToString().ToLowerInvariant()).GetInt64(),
                t.TryGetProperty("radius", out var r) ? r.GetSingle() : 0));
        }
        return triggers;
    }
}
