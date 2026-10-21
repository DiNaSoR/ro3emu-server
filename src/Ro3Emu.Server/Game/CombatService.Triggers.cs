using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Learned and buff-borne triggers beyond the proc table (CombatService.Procs):
/// <list type="bullet">
/// <item>Blitz Beat 13102: "When using a Basic Attack, the Falcon has a ${8}% Chance to cast 【Blitz Beat】 on the enemy"
/// (same-id BuffConfig _kLogicData [chance bp, 13110]: the auto-blitz row of the learned level, mode 0). The falcon has to
/// be able to cast Blitz Beat (its _iSkillCondition).</item>
/// <item>Crimson Rock 12312: "a ${5}% Chance to trigger Meteorite" (_kLogicData [6000]: the auto-learned FireStar row
/// 1235001, MATK x 108%, on the main target).</item>
/// <item>Dragon Breath (Fire) 11304: "a ${6}% Chance to Burn damaged enemies" (Burn 1138201 on each enemy hit; one roll
/// per enemy per cast, inferred).</item>
/// <item>Enchant Blade 11313: "After dealing damage, increases your DEF by ${2}%" (the buff's _kLogicData row, 1131501-
/// 1131503) for 5 s, refreshed on each hit (inferred: the duration is @{1} with no source).</item>
/// <item>Advanced Katar Mastery 15220: "When a Basic Attack crits (internal cooldown: ${1}s), your ${2} Crit increases for
/// ${3}s, stacking up to ${4} times" (same-id buff _kLogicData [crit buff row, ...]: 1522101-1522110, +10 to +100 Crit
/// a pile).</item>
/// <item>Soul Drain 12209: "Each Kill restores ${2}% of Max MP as MP".</item>
/// <item>Energy Coat 12003: "When you take damage, gain 【Energy Coat】 for ${1} sec. ... Cooldown: ${3} sec." (buff
/// 1201401 _kLogicData [1201501, ms, 1]).</item>
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
    private const int BlitzBeatGroup = 13102, CrimsonRockGroup = 12312, DragonBurnRow = 1138201,
        EnchantBladeGroup = 11313, SoulDrainGroup = 12209, EnergyCoatGroup = 12003, EnergyCoatBuffRow = 1201401,
        ShrinkGroup = 11409, AutoGuardGroup = 11403, RecoilSkill = 11416, ShieldDefenseOrbitGroup = 11616, ShieldDefenseSkill = 11613,
        EnchantBladeMs = 5000;

    private void WireTriggerSkills()
    {
        DealtAsync.Add(EnchantBladeAsync);
        DamageAdjusters.Add(AdvancedKatarCrit);
        Killed.Add(SoulDrainAsync);
    }

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

    private async Task EnchantBladeAsync(GameSession s, MonsterUnit target, JsonElement row, long amount)
    {
        if (amount <= 0 || LiveGroup(s, EnchantBladeGroup) is not { } blade || _data.Row("BuffConfig", blade.ConfigId) is not { } bladeRow
            || Numbers(bladeRow, "_kLogicData") is not [var defId, ..]) return;
        await _buffs.AddAsync(s, (int)defId, EnchantBladeMs, s.Role!.Id);
    }

    private const int AdvancedKatarGroup = 15220;

    private long AdvancedKatarCrit(GameSession s, MonsterUnit target, JsonElement row, Romsg.DamageHitResult result, long damage)
    {
        if (result != Romsg.DamageHitResult.Critical || GameData.Int(row, "_iIsNormalAtk") != 1 || s.Progress is not { } progress
            || _skills.Level(progress, AdvancedKatarGroup) is not (var level and > 0)) return damage;
        long now = MonsterService.NowMs;
        if (now < s.ProcState.GetValueOrDefault(AdvancedKatarGroup)
            || _data.Row("SkillConfig", _skills.RealId(AdvancedKatarGroup, level)) is not { } skill
            || Numbers(skill, "_kDescData") is not { Length: > 2 } desc
            || _data.Row("BuffConfig", _skills.RealId(AdvancedKatarGroup, level)) is not { } buff
            || Numbers(buff, "_kLogicData") is not [var critId, ..]) return damage;
        s.ProcState[AdvancedKatarGroup] = now + (long)(desc[0] * 1000);
        var role = s.Role;
        ScheduleRuntime(s.WorldScene, TimeSpan.Zero, async () =>
        {
            if (ReferenceEquals(s.Role, role) && !s.Dead) await _buffs.AddAsync(s, (int)critId, (int)(desc[2] * 1000), role!.Id);
        });
        return damage;
    }

    private async Task SoulDrainAsync(GameSession s, MonsterUnit monster)
    {
        if (s.Progress is not { } progress || s.Dead || _skills.Level(progress, SoulDrainGroup) is not (var level and > 0)
            || _data.Row("SkillConfig", _skills.RealId(SoulDrainGroup, level)) is not { } row
            || Numbers(row, "_kDescData") is not { Length: > 1 } desc) return;
        var stats = _character.Stats(s);
        long restore = (long)Math.Ceiling(stats[102] * desc[1] / 100);
        if (restore > 0 && stats[2001] < stats[102]) await _character.SetHpSpAsync(s, null, stats[2001] + restore);
    }

    /// <summary>The learned and buff-borne answers to a monster's hit (any distance or type).</summary>
    private async Task ReactWithTriggersAsync(MonsterUnit attacker, GameSession p, long now)
    {
        var progress = p.Progress!;
        if (_skills.Level(progress, EnergyCoatGroup) is var coat and > 0 && now >= p.ProcState.GetValueOrDefault(EnergyCoatGroup)
            && _data.Row("SkillConfig", _skills.RealId(EnergyCoatGroup, coat)) is { } coatRow
            && Numbers(coatRow, "_kDescData") is { Length: > 2 } coatDesc
            && _data.Row("BuffConfig", EnergyCoatBuffRow) is { } coatBuff && Numbers(coatBuff, "_kLogicData") is [var coatId, ..])
        {
            p.ProcState[EnergyCoatGroup] = now + (long)(coatDesc[2] * 1000);
            await _buffs.AddAsync(p, (int)coatId, (int)(coatDesc[0] * 1000), p.Role!.Id);
        }
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
