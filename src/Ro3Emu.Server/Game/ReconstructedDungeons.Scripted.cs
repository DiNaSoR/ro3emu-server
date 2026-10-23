using System.Globalization;
using System.Text.Json;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Scripted story scenes (Reconstructed/scripted-scenes.json; its "contract" section cites the client
/// code for every event used here). The original server's scene script is not in the client: each scene is rebuilt as
/// steps, each naming the DungeonQuestConfig quest the tracker shows, the scene actors shown during it, an optional
/// run-time story the client plays, the story lines said during it, the guide line drawn during it, and the client events that complete it. Nothing is
/// keyed by a task id: a step completes on any listed event (interaction, dungeon message, region, point or plot end),
/// or, when it has spawns (a fight step), once all of them are killed.
/// </summary>
public sealed partial class ReconstructedDungeons
{
    public enum TriggerKind { Interaction, Message, Region, Point, Plot }

    /// <summary>A client event that completes (or, under "ignore", deliberately does not complete) a step.</summary>
    public sealed record StepTrigger(TriggerKind Kind, long Id, float Radius = 0);

    /// <summary>A scene actor: an NPCConfig id at a scene-editor point, with the buffs (BuffConfig ids) it carries.</summary>
    public sealed record SceneActor(int NpcId, long Point, IReadOnlyList<int> Buffs, float Dx = 0, float Dz = 0);

    /// <summary>
    /// A ChatConfig story line, said <paramref name="Ms"/> after the step starts (or as it completes, for a "done" line).
    /// <paramref name="By"/> is the NPCConfig id of the speaker (a scene actor or a spawned monster), needed for a type-2
    /// bubble and otherwise 0.
    /// </summary>
    public sealed record StepLine(int Chat, int By, int Ms);

    /// <summary>
    /// A slide the participants are put on while the step runs (LC_Movement_Controller_Slide): speeds in m/s, sent as
    /// the client's Player_SlideSpeed (5400003, [side, down] x 100) before Player_SlideState (5400001) 1.
    /// </summary>
    public sealed record StepSlide(float Side, float Down);

    /// <summary>
    /// What ends a step the player cannot otherwise finish: after <paramref name="Ms"/>, counted from the step's start
    /// or (<paramref name="FromMount"/>) from a participant mounting a vehicle actor, the step completes as if its first
    /// trigger had come and the participants are moved to the scene-editor point <paramref name="Teleport"/> (0: none).
    /// </summary>
    public sealed record StepFallback(int Ms, long Teleport, bool FromMount);

    /// <summary>
    /// One scripted step. <paramref name="Story"/> is a PlotInterpretation id the client plays at the step's start
    /// (0: none); <paramref name="TeleportAfter"/> a scene-editor point the participants are moved to when it
    /// completes (0: none). <paramref name="Goal"/> is the tracker's "(done/total)" while the step runs.
    /// <paramref name="Spawns"/> makes it a fight step: those monsters spawn at its start and it completes when all are
    /// killed (the tracker counts the kills), as well as any configured completing events.
    /// </summary>
    public sealed record ScriptedStep(string Stage, int Quest, (int Done, int Total) Goal, IReadOnlyList<SceneActor> Actors,
        long Story, IReadOnlyList<StepTrigger> Complete, IReadOnlyList<StepTrigger> Ignore, long TeleportAfter,
        IReadOnlyList<Spawn>? Spawns = null, IReadOnlyList<StepLine>? Lines = null, IReadOnlyList<StepLine>? DoneLines = null)
    {
        public IReadOnlyList<Spawn> Spawns { get; init; } = Spawns ?? [];
        public IReadOnlyList<StepLine> Lines { get; init; } = Lines ?? [];
        public IReadOnlyList<StepLine> DoneLines { get; init; } = DoneLines ?? [];
        public StepSlide? Slide { get; init; }
        public StepFallback? Fallback { get; init; }
        public Seek? Seek { get; init; }
        public Shelter? Shelter { get; init; }
        /// <summary>
        /// Scene-editor guide points (m_kGuidePoints) the client draws as a guide arrow line while the step runs
        /// (dungeon var 10021 PGuideWayList); empty: none.
        /// </summary>
        public IReadOnlyList<long> Guide { get; init; } = [];
        public bool Fight => Spawns.Count > 0;
        /// <summary>Every listed event must arrive, rather than any single one.</summary>
        public bool All { get; init; }
        /// <summary>Actor interaction, skill, and inferred number of strikes needed to sink a target.</summary>
        public IReadOnlyList<(long Interaction, long Skill, int Shots)> Strikes { get; init; } = [];
    }

    /// <summary>
    /// A scene. <paramref name="Unplayed"/> are DungeonQuestConfig quests of the scene that no step shows: the branch
    /// that shows them cannot be reached in the reconstruction (each has its reason in the json, "unplayed").
    /// <paramref name="Next"/> is the scene a won run goes straight on to (DungeonConfig _iSameDupLoadingType 2 on it),
    /// 0 when the players are sent back out.
    /// </summary>
    public sealed record ScriptedScene(long Scene, string Name, string Confidence, IReadOnlyList<ScriptedStep> Steps,
        IReadOnlyList<int> Unplayed, long Next = 0);

    /// <summary>
    /// Hide-and-seek rounds (inferred): warn, count down, then check every participant against unsearched Point
    /// triggers. A passed round searches nearby points; a found participant restarts the game. ChatConfig ids name
    /// the opening, warning, countdown, next-round, win and failure messages.
    /// </summary>
    public sealed record Seek(int Rounds, float SearchedRadius, IReadOnlyList<int> Opening, int Round, int Countdown, int Longer,
        int Won, int Found);

    /// <summary>
    /// Cover from a boss attack (inferred): warn, hold the boss for WarnMs, then hit participants outside the Cover
    /// actor's shadow with Skill. Width is the shadow's half-width; Depth is its length beyond the cover, in metres.
    /// </summary>
    public sealed record Shelter(int Boss, int Cover, int Tip, long Skill, int EveryMs, int WarnMs, float Width, float Depth);

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
                entry.Value.GetProperty("confidence").GetString() ?? "", steps, unplayed,
                entry.Value.TryGetProperty("next", out var next) ? next.GetProperty("scene").GetInt64() : 0);
        }
    }

    private static ScriptedStep Step(JsonElement s)
    {
        var goal = s.TryGetProperty("goal", out var g) ? (g[0].GetInt32(), g[1].GetInt32()) : (0, 1);
        var actors = s.TryGetProperty("actors", out var a)
            ? a.EnumerateArray().Select(x => new SceneActor(x.GetProperty("npc").GetInt32(), x.GetProperty("point").GetInt64(),
                x.TryGetProperty("buffs", out var b) ? b.EnumerateArray().Select(v => v.GetInt32()).ToArray() : [],
                x.TryGetProperty("offset", out var o) ? o[0].GetSingle() : 0, x.TryGetProperty("offset", out o) ? o[1].GetSingle() : 0)).ToArray()
            : [];
        var complete = Triggers(s, "complete");
        var spawns = s.TryGetProperty("spawns", out var sp)
            ? sp.EnumerateArray().Select(x => new Spawn(x.GetProperty("point").GetInt64(), x.GetProperty("npc").GetInt32())).ToArray()
            : [];
        string stage = s.GetProperty("stage").GetString() ?? "";
        if (complete.Count == 0 && spawns.Length == 0) throw new InvalidOperationException($"Scripted step '{stage}' cannot complete.");
        return new ScriptedStep(stage, s.GetProperty("quest").GetInt32(), goal, actors,
            s.TryGetProperty("story", out var story) ? story.GetInt64() : 0, complete, Triggers(s, "ignore"),
            s.TryGetProperty("after", out var after) && after.TryGetProperty("teleport", out var to) ? to.GetInt64() : 0,
            spawns, Lines(s, "lines"), Lines(s, "doneLines"))
        {
            All = s.TryGetProperty("all", out var all) && all.GetBoolean(),
            Seek = s.TryGetProperty("seek", out var seek)
                ? new Seek(seek.GetProperty("rounds").GetInt32(), seek.GetProperty("searchedRadius").GetSingle(),
                    [.. seek.GetProperty("opening").EnumerateArray().Select(v => v.GetInt32())], seek.GetProperty("round").GetInt32(),
                    seek.GetProperty("countdown").GetInt32(), seek.GetProperty("longer").GetInt32(), seek.GetProperty("won").GetInt32(),
                    seek.GetProperty("found").GetInt32())
                : null,
            Shelter = s.TryGetProperty("shelter", out var shelter)
                ? new Shelter(shelter.GetProperty("boss").GetInt32(), shelter.GetProperty("cover").GetInt32(), shelter.GetProperty("tip").GetInt32(),
                    shelter.GetProperty("skill").GetInt64(), shelter.GetProperty("everyMs").GetInt32(), shelter.GetProperty("warnMs").GetInt32(),
                    shelter.GetProperty("width").GetSingle(), shelter.GetProperty("depth").GetSingle())
                : null,
            Strikes = s.TryGetProperty("strike", out var strike)
                ? strike.EnumerateArray().Select(x => (x.GetProperty("interaction").GetInt64(), x.GetProperty("skill").GetInt64(),
                    x.TryGetProperty("shots", out var shots) ? shots.GetInt32() : 0)).ToArray() : [],
            Slide = s.TryGetProperty("slide", out var slide)
                ? new StepSlide(slide.GetProperty("side").GetSingle(), slide.GetProperty("down").GetSingle()) : null,
            Fallback = s.TryGetProperty("fallback", out var f)
                ? new StepFallback(f.GetProperty("ms").GetInt32(), f.TryGetProperty("teleport", out var t) ? t.GetInt64() : 0,
                    f.TryGetProperty("from", out var from) && from.GetString() == "mount")
                : null,
            Guide = s.TryGetProperty("guide", out var guide) ? guide.EnumerateArray().Select(v => v.GetInt64()).ToArray() : [],
        };
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
