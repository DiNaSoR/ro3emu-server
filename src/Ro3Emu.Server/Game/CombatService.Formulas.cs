using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// One packet of a skill that lands in several parts (Tetra Vortex's four elements, Gravitational Impact's pulls and
/// burst, Axe Tornado's spins). <see cref="Branch"/> picks the _kDamageParam1/2 entry; <see cref="Element"/> replaces
/// the row's element (0 keeps it); <see cref="Split"/> is how many numbers the client shows (0: the row's _kShowDmg).
/// Only the <see cref="First"/> packet grants the caster's buffs, pushes targets, recovers energy and rolls procs.
/// <see cref="Buff2"/> rolls only that _kBuff2 tuple (-1: all of them); on the <see cref="Last"/> packet a target that
/// none of them reached gets the <see cref="Fallback"/> tuple.
/// </summary>
internal sealed record HitStage(int Branch, int Element = 0, int Split = 0, bool First = true, int Buff2 = -1, bool Last = true, int Fallback = -1);

public sealed partial class CombatService
{
    private const int UndeadRace = 5, DemonRace = 3, UndeadElement = 10, ShadowElement = 8;
    private const int SupersonicBlowGroup = 15103, PreyOnTheWeakGroup = 15316;

    /// <summary>
    /// A skill's _kDamageFormulaExtra multiplier for a player (none for monsters). Every name reads its
    /// _kDamageParamExtra tuple; the entry with the hit's branch is used when the row lists one per branch (Fearless
    /// Charge). The client only shows these terms in descriptions; how they combine is inferred from them:
    /// <list type="bullet">
    /// <item>T_AXadd0_etc [1 caster, A, attribute, 1]: x (1 + A x attribute / 10000). Blitz Beat [1,5,2,1] "every 20 AGI
    /// increases this DMG by 1%". Mode 3 rows are flat amounts (shields), which their owners apply.</item>
    /// <item>T_AXadd0_etc_withlimit [1, A, attribute, 1, limit] (or [1, A, attribute, limit]): the same with the
    /// attribute capped at limit. The "Resonance 0.1%" skills: Jack Frost DEX [1,10,5,1,100], Arrow Storm AGI, Axe
    /// Tornado VIT, Sonic Burst LUK (inferred: the cap counts attribute points, as the ASPD cap below does).</item>
    /// <item>AXaddBplus1 [A, 115, B] and AXaddBplus1_withlimit [A, 115, B, limit]: x (B + A x ASPD) / 10000 with ASPD
    /// the panel value (attribute 115, 100 = 100%) capped at limit (500 = the 5x ASPD cap SkillMs uses). Cross Ripper
    /// Slasher [12,115,10000,500] "PATK x 40% x (1 + ASPD x 0.12%)"; Banishing Thrust [30,115,10000] "for every 1% ASPD
    /// this DMG increases by 0.3%".</item>
    /// <item>T_AXadd0_Xisskilllv [1, A, 0, 1]: heals only, x (1 + A x Base Level / 10000). Heal "each Base Level
    /// increases Healing Volume by 0.1%" (the name says skill level; the description says Base Level).</item>
    /// </list>
    /// AXadd_HitCount_ExtraDmg (Lord of Vermilion, Napalm Vulcan, Hammer Fall) is in no description and is not applied.
    /// </summary>
    internal static double FormulaFactor(Fighter a, JsonElement row, int branch, bool heal)
    {
        if (a.IsMonster || a.Stats is not { } stats) return 1;
        if (!row.TryGetProperty("_kDamageFormulaExtra", out var list) || list.ValueKind != JsonValueKind.Array) return 1;
        var names = list.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "").ToArray();
        var tuples = Tuples(row, "_kDamageParamExtra").Select(t => t.EnumerateArray().Select(v => GameData.Number(v) ?? 0).ToArray()).ToArray();
        int count = Math.Min(names.Length, tuples.Length);
        if (count == 0) return 1;
        int i = Math.Clamp(branch, 0, count - 1);
        var p = tuples[i];
        double Attribute(double id) => id is >= 1 and < 100000 ? stats[(int)id] : 0;
        switch (names[i])
        {
            case "T_AXadd0_etc" when !heal && p is [1, var coef, var attr, 1, ..]:
                return Math.Max(0, 1 + coef * Attribute(attr) / 10000);
            case "T_AXadd0_etc_withlimit" when p is [1, var coef, var attr, 1, var limit]:
                return Math.Max(0, 1 + coef * Math.Min(Attribute(attr), limit) / 10000);
            case "T_AXadd0_etc_withlimit" when p is [1, var coef, var attr, var limit]:
                return Math.Max(0, 1 + coef * Math.Min(Attribute(attr), limit) / 10000);
            case "AXaddBplus1" when !heal && p is [var coef, var attr, var b and > 0, ..]:
                return Math.Max(0, (b + coef * Attribute(attr)) / 10000);
            case "AXaddBplus1_withlimit" when !heal && p is [var coef, var attr, var b and > 0, var limit, ..]:
                return Math.Max(0, (b + coef * Math.Min(Attribute(attr), limit)) / 10000);
            case "T_AXadd0_Xisskilllv" when heal && p is [1, var coef, _, 1, ..]:
                return Math.Max(0, 1 + coef * a.Level / 10000);
            default:
                return 1;
        }
    }

    /// <summary>
    /// The attack power a hit scales from. Magic rows use MATK and physical rows PATK, except physical hits described
    /// as MATK-based: Magic Crasher ("Neutral Ranged Physical damage equal to MATK * 60%"), Double Holy Light's
    /// physical follow-up JudexMix1 ("Physical damage equal to your Magic ATK * 30%"), and Soul Destroyer, whose
    /// one ratio covers both ("Physical ATK * 45% + Magic ATK * 45% + 50").
    /// </summary>
    private double AttackPower(Fighter a, JsonElement skill, bool magic) => magic ? a.Matk : Script(skill) switch
    {
        _ when AdaptiveGuardian(skill) => Math.Max(a.Atk, a.Matk),
        "MoJiShu" or "MoJiPao" or "JudexMix1" => a.Matk,
        "SoulDestroyer" => a.Atk + a.Matk,
        _ => a.Atk,
    };

    /// <summary>
    /// Which _kDamageParam1/2 entry a hit on this monster uses:
    /// <list type="bullet">
    /// <item>Heal's enemy branch is [1]; Holy Cross's [1] is its two-handed spear branch ([5500, 5775] at Lv5).</item>
    /// <item>Soul Strike [1] "an Extra 50% ... to Undead enemies" (Undead element or race, inferred); Magnus Exorcismus
    /// [1] against "the Undead Race or Demon Race, or with the Shadow property or Undead property".</item>
    /// <item>Shield Press, Earth Drive, Powerful/Ultra Shield Bash [1] "against Monster targets" (every target here is
    /// a monster; player targets use [0]).</item>
    /// <item>Sonic Blow [1] when the target's HP is at or below 50% (_kLogicData [2000, 101, 5000]: +20%, attribute
    /// 101 = HP, 5000 = 50%); with Supersonic Blow learned, [2] at or below 80% (its _kDescData [80, 25]).</item>
    /// </list>
    /// A branch the row does not list falls back to [0].
    /// </summary>
    internal int DamageBranch(GameSession s, JsonElement row, MonsterUnit? target)
    {
        int branch = Script(row) switch
        {
            "Heal" => 1,
            "HolyCross" => _skills.Weapon(s).SubType == TwoHandedSpear ? 1 : 0,
            "Soul_Strike" => target is { } t && (t.Template.Element == UndeadElement || t.Template.Race == UndeadRace) ? 1 : 0,
            "Magnus_Exorcismus" => target is { } m && (m.Template.Race is UndeadRace or DemonRace
                || m.Template.Element is ShadowElement or UndeadElement) ? 1 : 0,
            "ShieldPress" or "ShieldPresss1" or "EarthDrive" or "Smite3" or "Smite2" => target != null ? 1 : 0,
            "SonicBlow" => SonicBlowBranch(s, row, target),
            _ => 0,
        };
        return branch < Numbers(row, "_kDamageParam1").Length ? branch : 0;
    }

    private int SonicBlowBranch(GameSession s, JsonElement row, MonsterUnit? target)
    {
        if (target == null || target.Template.MaxHp <= 0) return 0;
        double hp = target.Hp * 10000.0 / target.Template.MaxHp;
        if (s.Progress is { } p && _skills.Level(p, SupersonicBlowGroup) > 0
            && _data.Row("SkillConfig", _skills.RealId(SupersonicBlowGroup, _skills.Level(p, SupersonicBlowGroup))) is { } super
            && Numbers(super, "_kDescData") is [var threshold and > 0, ..] && hp <= threshold * 100) return 2;
        var logic = Numbers(row, "_kLogicData");
        return logic.Length >= 3 && logic[1] == 101 && hp <= logic[2] ? 1 : 0;
    }

    /// <summary>
    /// Prey on the Weak 15316: "Damage dealt to enemies afflicted with status effects is increased by 0.5%" per level
    /// (its same-id buff's _kLogicData [50] in basis points). A status effect is any live debuff (_iType 2) on the
    /// target.
    /// </summary>
    private long PreyOnTheWeak(GameSession s, MonsterUnit target, JsonElement row, DamageHitResult result, long amount)
    {
        if (amount <= 0 || s.Progress is not { } p || _skills.Level(p, PreyOnTheWeakGroup) is not (var level and > 0)
            || _data.Row("BuffConfig", _skills.RealId(PreyOnTheWeakGroup, level)) is not { } buff
            || Numbers(buff, "_kLogicData") is not [var bp and > 0, ..]) return amount;
        long now = Now;
        bool afflicted = _buffs.UnitBuffs(target).Any(b => b.ExpireMs > now
            && _data.Row("BuffConfig", b.ConfigId) is { } config && GameData.Int(config, "_iType") == 2);
        return afflicted ? (long)Math.Round(amount * (1 + bp / 10000)) : amount;
    }

    /// <summary>
    /// The packets of a skill that lands in parts, with the template time (m_kAtkPoints scale) of each. The packets are
    /// spread evenly from the first listed attack point (the cast's impact) to the last one (inferred timing):
    /// <list type="bullet">
    /// <item>Soul Expansion: "@{1} instances" of [8000, 8000], one per entry.</item>
    /// <item>Tetra Vortex: "Sequentially deals Water, Fire, Earth, and Wind" damage, one entry each; each rolls its own
    /// status (_kBuff2 [Stun, Burn, Petrify, Frozen, Frozen 100%]: Water Frozen, Fire Burn, Earth Petrify, Wind Stun),
    /// and "is guaranteed to inflict at least 1": the last tuple goes to a target none of them reached.</item>
    /// <item>Gravitational Impact: "@{1} hits ... equal to MATK * 158.5%" then "@{2} hits ... equal to MATK * 317%":
    /// _kShowDmg [3, 1] hits of each entry.</item>
    /// <item>Axe Tornado: "total ... PATK * 1252%" = 6 spins of [0] + one final [1] (Lv5 [16700, 25000]); the
    /// Enhanced row's 1438% = 10 spins of 115% + 288%. The spin count comes from that described total.</item>
    /// <item>Piercing Shadow: "a total of PATK * 300%" = [15000, 15000] in two slashes (_kShowDmg [8, 1]).</item>
    /// <item>Myriad Stars Chase the Moon: "continuously dealing PATK * 200%", described as 1400% in all = one hit per
    /// attack point after the first (7).</item>
    /// </list>
    /// </summary>
    private IReadOnlyList<(double Point, HitStage Stage)>? HitPlan(JsonElement row)
    {
        var ratios = Numbers(row, "_kDamageParam1");
        var shown = Numbers(row, "_kShowDmg");
        int Shown(int i) => i < shown.Length && shown[i] >= 1 ? (int)shown[i] : 1;
        var stages = new List<HitStage>();
        string script = Script(row);
        switch (script)
        {
            case "SoulExpansion" or "SoulExpansion1" or "SoulExpansion_Copy" or "SoulExpansion1_Copy" when ratios.Length > 1:
                for (int i = 0; i < ratios.Length; i++) stages.Add(new HitStage(i));
                break;
            case "TetraVortex" or "TetraVortex2" or "TetraVortexcopy" or "TetraVortex2copy" when ratios.Length >= 4:
                int[] elements = [5, 4, 3, 2], statuses = [3, 1, 2, 0];
                int fallback = Tuples(row, "_kBuff2").Count() > 4 ? 4 : -1;
                for (int i = 0; i < 4; i++) stages.Add(new HitStage(i, elements[i], Buff2: statuses[i], Fallback: fallback));
                break;
            case "VectorShock" or "VectorShock1" or "VectorShock_Copy" or "VectorShock1_Copy" when ratios.Length > 1:
                for (int i = 0; i < ratios.Length; i++)
                    for (int n = 0; n < Shown(i); n++) stages.Add(new HitStage(i, Split: 1));
                break;
            case var axe when axe.StartsWith("AxeTornado", StringComparison.Ordinal) && ratios.Length > 1 && ratios[0] > 0:
                var desc = Numbers(row, "_kDescData");
                int spins = desc.Length > 2 && desc[2] > 0 ? (int)Math.Round((desc[2] * 100 - ratios[1]) / ratios[0]) : 0;
                if (spins is < 1 or > 20) spins = axe.Contains("Pro", StringComparison.Ordinal) ? 10 : 6;
                for (int n = 0; n < spins; n++) stages.Add(new HitStage(0, Split: 1));
                stages.Add(new HitStage(1, Split: 1));
                break;
            case "wudizhan" or "wudizhanS1" when ratios.Length > 1:
                for (int i = 0; i < ratios.Length; i++) stages.Add(new HitStage(i, Split: Shown(i)));
                break;
            case "VolleyArrows":
                int arrows = AttackPoints(row).Count(p => p > 0);
                if (arrows < 2) return null;
                for (int n = 0; n < arrows; n++) stages.Add(new HitStage(0, Split: Shown(0)));
                break;
            default:
                return null;
        }
        if (stages.Count < 2) return null;
        var points = AttackPoints(row);
        double first = points.FirstOrDefault(0.2), last = points.Length > 0 ? Math.Max(first, points.Max()) : first;
        return stages.Select((stage, i) => (first + (last - first) * i / (stages.Count - 1),
            stage with { First = i == 0, Last = i == stages.Count - 1 })).ToList();
    }

    private double[] AttackPoints(JsonElement row) =>
        _data.SkillTemplate(GameData.Int(row, "_iScriptShow")) is { } t ? Numbers(t, "m_kAtkPoints") : [];

    /// <summary>Lands a cast in its <see cref="HitPlan"/> packets: the first now (the impact), the rest at their offsets
    /// on the cast timeline. A packet finds its targets again when it lands.</summary>
    private async Task ApplyStagedEffectsAsync(GameSession s, ActiveSkillCast action, Vec3 center, IReadOnlyList<(double Point, HitStage Stage)> plan)
    {
        var template = _data.SkillTemplate(GameData.Int(action.Row, "_iScriptShow"));
        int start = ImpactOffset(template, plan[0].Point, action.ChantMs, action.ActionMs);
        for (int i = 1; i < plan.Count; i++)
        {
            var stage = plan[i].Stage;
            int delay = Math.Max(0, ImpactOffset(template, plan[i].Point, action.ChantMs, action.ActionMs) - start);
            ScheduleRuntime(action.WorldScene, TimeSpan.FromMilliseconds(delay), async () =>
            {
                if (CurrentCastMap(s, action)) await ApplyEffectsAsync(s, action, center, stage: stage);
            });
        }
        await ApplyEffectsAsync(s, action, center, stage: plan[0].Stage);
    }

    /// <summary>
    /// Envenomed Blade 15105: "Then Launches a poisoned blade at a random enemy within ${4} = 3 meters of the target ...
    /// up to ${7} = 3 times" (_kLogicData [4] = the throws with the first). Each blade leaves from the enemy the last one
    /// hit, 200 ms apart (inferred), and does not return to an enemy already hit (inferred).
    /// </summary>
    private void ScheduleBounces(GameSession s, ActiveSkillCast action, MonsterUnit first)
    {
        var logic = Numbers(action.Row, "_kLogicData");
        var desc = Numbers(action.Row, "_kDescData");
        int bounces = logic.Length > 0 ? (int)logic[0] - 1 : 0;
        float radius = desc.Length > 3 && desc[3] > 0 ? (float)desc[3] : 3;
        if (bounces <= 0) return;
        var hit = new HashSet<long> { first.Uid };
        var from = first.Pos.Clone();
        var bounce = new HitStage(0, First: false);
        async Task NextAsync(int left)
        {
            if (!CurrentCastMap(s, action)) return;
            var candidates = _world.Map(s.WorldScene).Units.OfType<MonsterUnit>()
                .Where(m => m.Hp > 0 && !m.Dead && !hit.Contains(m.Uid) && CanHitMonster(s, m) && Hostile(action, m)
                    && Distance(from, m.Pos) <= radius + 0.1f)
                .OrderBy(m => m.Uid).ToList();
            if (candidates.Count == 0) return;
            var next = candidates[Rolls.Proc(candidates.Count)];
            hit.Add(next.Uid);
            from = next.Pos.Clone();
            await ApplyEffectsAsync(s, action, from, stage: bounce, only: next);
            if (left > 1) ScheduleRuntime(action.WorldScene, TimeSpan.FromMilliseconds(200), () => NextAsync(left - 1));
        }
        ScheduleRuntime(action.WorldScene, TimeSpan.FromMilliseconds(200), () => NextAsync(bounces));
    }

    /// <summary>
    /// Skills that name one target (_iRangeType 1) but hit more around it, or the whole area:
    /// <list type="bullet">
    /// <item>A class skill row with a radius and room for several targets (_kRange, _iTargetMax > 1): Virus Spread "up to 5 enemies
    /// within a 3-meter radius around a target", Evil Spirit Threat and Rolling Cutter around the caster. Around the
    /// locked target (release 1, 7, 8) the target comes first.</item>
    /// <item>Blitz Beat _kLogicData [300, 3, 0]: "the designated ST enemy ... and up to 2 Other enemies within 3 meters
    /// of the target" (radius cm, targets in all).</item>
    /// <item>Sharp Shooting _kLogicData [3, 150] and Focused Arrow Strike _kDescData [18, 5, 60]: "the target and up to
    /// N enemies in a line within Attack Range": a 1.5 m wide line from the caster through the target, as long as the
    /// skill's range (inferred width for Focused Arrow Strike, which carries none).</item>
    /// </list>
    /// Null: an ordinary single-target row.
    /// </summary>
    private List<MonsterUnit>? SelectAroundTarget(GameSession s, ActiveSkillCast action)
    {
        var row = action.Row;
        string script = Script(row);
        var target = FindMonster(s.WorldScene, action.Request.TargetId);
        bool TargetOk() => target != null && target.Hp > 0 && CanHitMonster(s, target) && Hostile(action, target);
        IEnumerable<MonsterUnit> Others() => _world.Map(s.WorldScene).Units.OfType<MonsterUnit>()
            .Where(m => m != target && m.Hp > 0 && CanHitMonster(s, m) && Hostile(action, m));
        var logic = Numbers(row, "_kLogicData");
        if (script == "BlitzBeat" && logic is [var cm and > 0, var total and > 1, ..])
        {
            if (!TargetOk()) return [];
            float radius = (float)(cm / 100);
            return Others().Where(m => Distance(target!.Pos, m.Pos) <= radius + 0.1f).OrderBy(m => Distance(target!.Pos, m.Pos)).ThenBy(m => m.Uid)
                .Take((int)total - 1).Prepend(target!).ToList();
        }
        bool sharp = script.StartsWith("RuiLiSheJi", StringComparison.Ordinal), aimed = script.StartsWith("AimedBolt", StringComparison.Ordinal);
        if (sharp || aimed)
        {
            int extra = sharp ? (logic.Length > 0 ? (int)logic[0] : 0) : Numbers(row, "_kDescData") is [_, var n and > 0, ..] ? (int)n : 0;
            float width = sharp && logic.Length > 1 && logic[1] > 0 ? (float)(logic[1] / 100) : 1.5f;
            if (!TargetOk()) return [];
            if (extra <= 0) return [target!];
            var origin = action.Origin;
            float dx = target!.X - origin.X, dz = target.Z - origin.Z, length = MathF.Sqrt(dx * dx + dz * dz);
            if (length < 0.01f) return [target];
            float ux = dx / length, uz = dz / length, reach = Math.Max(length, Range(row));
            return Others().Where(m =>
            {
                float mx = m.X - origin.X, mz = m.Z - origin.Z, along = mx * ux + mz * uz, side = MathF.Abs(mx * uz - mz * ux);
                return along >= 0 && along <= reach + 0.1f && side <= width / 2;
            }).OrderBy(m => Distance(origin, m.Pos)).ThenBy(m => m.Uid).Take(extra).Prepend(target).ToList();
        }
        var range = Numbers(row, "_kRange");
        // Only class skills (_iJob): story, guild-league and pet rows share the shape but are not player area hits.
        if (range is not [var r0 and > 0, ..] || GameData.Int(row, "_iTargetMax") <= 1 || GameData.Int(row, "_iJob") <= 0) return null;
        int release = (int)GameData.Int(row, "_iRelease");
        int max = (int)GameData.Int(row, "_iTargetMax");
        if (release is 1 or 7 or 8)
        {
            if (!TargetOk()) return [];
            float radius = (float)(r0 / 100);
            return Others().Where(m => Distance(target!.Pos, m.Pos) <= radius + 0.1f).OrderBy(m => Distance(target!.Pos, m.Pos)).ThenBy(m => m.Uid)
                .Take(max - 1).Prepend(target!).ToList();
        }
        return SelectEnemies(s, action, action.Origin, area: true);
    }
}
