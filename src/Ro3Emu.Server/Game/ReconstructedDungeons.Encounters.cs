using System.Globalization;
using System.Text.Json;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Story boss encounters (Reconstructed/story-encounters.json): the boss's own skill kit with chosen hit areas, allied
/// companions with their kits, stages that move the quest tracker, and the scene's dialogue. Run by
/// DungeonService.Encounter on top of the scene's ordinary plan (the boss still comes from the plan's wave).
/// </summary>
public sealed partial class ReconstructedDungeons
{
    public enum HitShape { Circle, Cone, Rect }

    /// <summary>One hit of a boss skill, <paramref name="Ms"/> after the cast starts, in an area fixed at the cast start.
    /// <paramref name="Forward"/> moves a circle's centre in front of the boss; <paramref name="ShareBp"/> is this hit's
    /// part of the row's damage.</summary>
    public sealed record EncounterHit(int Ms, HitShape Shape, float Radius, float Angle, float Length, float Width,
        float Forward, int ShareBp);

    public sealed record EncounterSkill(int Skill, IReadOnlyList<EncounterHit> Hits, int Line);

    /// <summary>A line of ChatConfig said <paramref name="Ms"/> into its stage (or its stage's end); <paramref name="By"/>
    /// names the NPC whose bubble shows a type-2 line.</summary>
    public sealed record EncounterLine(int Chat, int By, int Ms);

    public sealed record EncounterAlly(int NpcId, long Point);

    public sealed record EncounterKit(IReadOnlyList<int> Skills, bool Support);

    /// <summary>A stage: its tracker quest, the companions that join, its lines, and (all but the last) when it ends.</summary>
    public sealed record EncounterStage(int Quest, IReadOnlyList<EncounterAlly> Allies, int FirstTarget,
        IReadOnlyList<EncounterLine> Lines, int EndBossHpPct, int EndMs, IReadOnlyList<EncounterLine> EndLines, int EndDelayMs);

    public sealed record Encounter(long Scene, string Name, string Confidence, int Boss, int BossSkillGapMs, int RetargetMs,
        IReadOnlyList<EncounterSkill> BossSkills, IReadOnlyDictionary<int, EncounterKit> Kits,
        IReadOnlyList<EncounterStage> Stages, IReadOnlyList<EncounterLine> WinLines);

    private readonly Dictionary<long, Encounter> _encounters = new();

    public IReadOnlyCollection<Encounter> AllEncounters => _encounters.Values;

    /// <summary>The scene's story encounter, when the switch is on.</summary>
    public Encounter? EncounterFor(long scene) => Enabled && _encounters.TryGetValue(scene, out var e) ? e : null;

    private void LoadEncounters()
    {
        using var stream = typeof(ReconstructedDungeons).Assembly.GetManifestResourceStream("Ro3Emu.Server.Reconstructed.story-encounters.json")
            ?? throw new InvalidOperationException("Embedded Reconstructed/story-encounters.json is missing.");
        using var doc = JsonDocument.Parse(stream);
        static int Int(JsonElement e, string name, int fallback = 0) => e.TryGetProperty(name, out var v) ? v.GetInt32() : fallback;
        static float Float(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetSingle() : 0;
        static EncounterLine[] Lines(JsonElement e, string name) => e.TryGetProperty(name, out var list)
            ? list.EnumerateArray().Select(l => new EncounterLine(Int(l, "chat"), Int(l, "by"), Int(l, "ms"))).ToArray() : [];
        foreach (var entry in doc.RootElement.GetProperty("encounters").EnumerateObject())
        {
            long scene = long.Parse(entry.Name, CultureInfo.InvariantCulture);
            var e = entry.Value;
            var skills = e.GetProperty("bossSkills").EnumerateArray().Select(s => new EncounterSkill(Int(s, "skill"),
                s.GetProperty("hits").EnumerateArray().Select(h => new EncounterHit(Int(h, "ms"),
                    Enum.Parse<HitShape>(h.GetProperty("shape").GetString()!, ignoreCase: true), Float(h, "radius"), Float(h, "angle"),
                    Float(h, "length"), Float(h, "width"), Float(h, "forward"), Int(h, "share", 10000))).ToArray(),
                Int(s, "line"))).ToArray();
            var kits = e.GetProperty("kits").EnumerateObject().ToDictionary(k => int.Parse(k.Name, CultureInfo.InvariantCulture),
                k => new EncounterKit(k.Value.GetProperty("skills").EnumerateArray().Select(v => v.GetInt32()).ToArray(),
                    k.Value.TryGetProperty("support", out var support) && support.GetBoolean()));
            var stages = e.GetProperty("stages").EnumerateArray().Select(s =>
            {
                var end = s.TryGetProperty("end", out var x) ? x : default;
                return new EncounterStage(Int(s, "quest"),
                    s.GetProperty("allies").EnumerateArray().Select(a => new EncounterAlly(Int(a, "npc"), a.GetProperty("point").GetInt64())).ToArray(),
                    Int(s, "firstTarget"), Lines(s, "lines"),
                    end.ValueKind == JsonValueKind.Object ? Int(end, "bossHpPct") : 0, end.ValueKind == JsonValueKind.Object ? Int(end, "ms") : 0,
                    Lines(s, "endLines"), Int(s, "endDelayMs"));
            }).ToArray();
            if (stages.Length == 0) throw new InvalidOperationException($"Story encounter {scene} has no stages.");
            _encounters[scene] = new Encounter(scene, e.GetProperty("name").GetString() ?? "", e.GetProperty("confidence").GetString() ?? "",
                Int(e, "boss"), Int(e, "bossSkillGapMs", 6000), Int(e, "retargetMs", 8000), skills, kits, stages, Lines(e, "winLines"));
        }
    }
}
