using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Learned and buff-borne triggers beyond the proc table (CombatService.Procs). Enchant Blade, Energy Coat, Soul Drain
/// and Advanced Katar Mastery are PassiveSkills and TalentProcs.Passives:
/// <list type="bullet">
/// <item>Blitz Beat 13102: "When using a Basic Attack, the Falcon has a ${8}% Chance to cast 【Blitz Beat】 on the enemy"
/// (same-id BuffConfig _kLogicData [chance bp, 13110]: the auto-blitz row of the learned level, mode 0). The falcon has to
/// be able to cast Blitz Beat (its _iSkillCondition).</item>
/// <item>Crimson Rock 12312: "a ${5}% Chance to trigger Meteorite" (_kLogicData [6000]: the auto-learned FireStar row
/// 1235001, MATK x 108%, on the main target).</item>
/// <item>Dragon Breath (Fire) 11304: "a ${6}% Chance to Burn damaged enemies" (Burn 1138201 on each enemy hit; one roll
/// per enemy per cast, inferred).</item>
/// <item>Recoil 11416: while 【Shrink】 (group 11409, _kLogicData [11420]) and 【Auto Guard】 (group 11403) last, a hit
/// from an attacker within ${5} m has a ${2}% chance to cast Recoil Auto Counter 1142001 (Stun ${3} s) on it;
/// cooldown ${4} s.</item>
/// <item>Shield Defense 11613: its orbit buff (group 11616, _kLogicData [11621, ...]) answers a hit from an enemy in
/// front (within 90 degrees of the facing) with 【Spear Auto Counter】 of the Shield Defense level, at most once every
/// ${5} (0.5) s. Its "reduces the Cooldown of Shield Defense" part is not applied.</item>
/// </list>
/// </summary>
public sealed partial class CombatService
{
    private const int BlitzBeatGroup = 13102, CrimsonRockGroup = 12312, DragonBurnRow = 1138201, DragonBreathFireGroup = 11304,
        ShrinkGroup = 11409, AutoGuardGroup = 11403, RecoilSkill = 11416, ShieldDefenseOrbitGroup = 11616, ShieldDefenseSkill = 11613;

    /// <summary>Learned-skill procs for <see cref="OutgoingProcs"/>: Blitz Beat's auto-blitz and Crimson Rock's Meteorite.</summary>
    private IEnumerable<(int Chance, JsonElement Row)> LearnedProcs(GameSession s, JsonElement row, bool normal)
    {
        if (s.Progress is not { } progress) yield break;
        if (normal && _skills.Level(progress, BlitzBeatGroup) is var blitz and > 0
            && _data.Row("BuffConfig", _skills.RealId(BlitzBeatGroup, blitz)) is { } blitzBuff
            && Numbers(blitzBuff, "_kLogicData") is [var chance, var autoGroup, ..]
            && _data.Row("SkillConfig", _skills.RealId(BlitzBeatGroup, blitz)) is { } blitzRow && SkillConditionMet(s, blitzRow)
            && _data.Row("SkillConfig", _skills.RealId((int)autoGroup, blitz)) is { } auto)
            yield return ((int)chance, auto);
        if (GameData.Int(row, "_iSkillID") == CrimsonRockGroup && Numbers(row, "_kLogicData") is [var meteorChance, ..])
            foreach (long id in Numbers(row, "_kAutoLearnSkill"))
                if (_data.Row("SkillConfig", id) is { } meteor && Script(meteor) == "FireStar")
                    yield return ((int)meteorChance, meteor);
    }

    /// <summary>Dragon Breath (Fire)'s Burn: one roll per damaged enemy.</summary>
    private async Task BurnAsync(GameSession s, JsonElement row, List<(MonsterUnit Target, long Amount)> landed, Func<bool> current)
    {
        if (GameData.Int(row, "_iSkillID") != DragonBreathFireGroup || Numbers(row, "_kDescData") is not { Length: > 5 } desc
            || _data.Row("SkillConfig", DragonBurnRow) is not { } burn) return;
        foreach (var (target, amount) in landed.DistinctBy(l => l.Target.Uid).ToArray())
        {
            if (!current()) break;
            if (amount > 0 && !target.Dead && target.Hp > 0 && Rolls.Proc(10000) < desc[5] * 100) await ProcCastAsync(s, burn, target);
        }
    }

    /// <summary>The learned and buff-borne answers to a monster's hit (any distance or type).</summary>
    private async Task ReactWithTriggersAsync(MonsterUnit attacker, GameSession p, long now)
    {
        var progress = p.Progress!;
        if (attacker.Dead || attacker.Hp <= 0) return;
        if (LiveGroup(p, ShrinkGroup) is { } shrink && LiveGroup(p, AutoGuardGroup) != null
            && now >= p.ProcState.GetValueOrDefault(ShrinkGroup)
            && _data.Row("SkillConfig", _skills.RealId(RecoilSkill, Math.Max(1, _skills.Level(progress, RecoilSkill)))) is { } recoil
            && Numbers(recoil, "_kDescData") is { Length: > 4 } rdesc
            && MonsterService.Distance(attacker, p) <= rdesc[4]
            && _data.Row("BuffConfig", shrink.ConfigId) is { } shrinkRow && Numbers(shrinkRow, "_kLogicData") is [var answerGroup, ..]
            && _data.Row("SkillConfig", _skills.RealId((int)answerGroup, 1)) is { } answer
            && Rolls.Proc(10000) < rdesc[1] * 100)
        {
            p.ProcState[ShrinkGroup] = now + (long)(rdesc[3] * 1000);
            await ProcCastAsync(p, answer, attacker);
        }
        if (LiveGroup(p, ShieldDefenseOrbitGroup) is { } orbit && now >= p.ProcState.GetValueOrDefault(ShieldDefenseOrbitGroup)
            && _data.Row("BuffConfig", orbit.ConfigId) is { } orbitRow && Numbers(orbitRow, "_kLogicData") is [var counterGroup, ..]
            && _data.Row("SkillConfig", _skills.RealId((int)counterGroup, Math.Max(1, _skills.Level(progress, ShieldDefenseSkill)))) is { } counter
            && InFront(p, attacker))
        {
            var cdesc = Numbers(counter, "_kDescData");
            p.ProcState[ShieldDefenseOrbitGroup] = now + (long)((cdesc.Length > 4 ? cdesc[4] : 0.5) * 1000);
            await ProcCastAsync(p, counter, null, p.Face, area: true);
        }
    }

    private static bool InFront(GameSession p, MonsterUnit attacker)
    {
        float toward = MonsterService.Angle(p.Position.X, p.Position.Z, attacker.X, attacker.Z);
        float delta = Math.Abs((toward - p.Face + 540) % 360 - 180);
        return delta <= 90;
    }
}
