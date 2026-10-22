using System.Text.Json;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Profession talents' SpecialEffectConfig operations: SeasonTalentEffectConfig._kSpecialEffectList of the active plan's
/// bought nodes. The client only describes them (LC_SeasonTalentHelper.GetSpecialEffectDesc), and its
/// enum_SpecialEffectType stops at 15, so the layouts of the server-only ops are read from the exported rows and their
/// descriptions (inferred):
/// - Attr(1) [1, attr, value]: "Physical ATK +15";
/// - 16 [16, weapon, attr, value]: "When a Sword is equipped, STR +10" (ItemConfig._iSubType 2 Sword, 3 Two-Handed Sword);
/// - 19 [19, skill group, learned level, attr, value]: "When Mace Mastery reaches Lv. 10, STR +5";
/// - 22 [22, weapon, attr, per learned level, skill group]: "PATK +2 for each Learn Lv. of Sword Quicken";
/// - 21 [21, weapon or 0, BuffConfig._kSkillAttributeList tuple]: "Bowling Bash's Cooldown is changed to 8 seconds";
/// - Buff(2): the buff's _kSkillAttributeList ("Basic Attack's percentage damage multiplier increases by 3%").
/// The ops of one effect that differ only in their condition ("a dagger or katar", "Sanctuary or Magnus Exorcismus")
/// apply once. The skill tuples go through SkillParameterRules like a buff's.
/// Buff(2) procs (_kLogicData: Rapid Pierce's extra hits, Double Counter's extra cast ...) are TalentProcs'. Not executed:
/// _kBuffAttributeList, BuffDictRef(12) and 20.
/// </summary>
public sealed partial class SeasonTalentService
{
    private const long AttrOp = 1, BuffOp = 2, WeaponAttrOp = 16, SkillLevelAttrOp = 19, SkillParameterOp = 21, WeaponSkillAttrOp = 22;

    /// <summary>Observers of a changed set of talent skill modifiers in the world (CombatService republishes 31132).</summary>
    public List<Func<GameSession, Task>> EffectsChanged { get; } = new();

    private IEnumerable<(long Id, double Value)> SpecialAttributes(GameSession s, JsonElement effect)
    {
        foreach (long id in new SeasonTalentEffectConfig(effect).SpecialEffectList)
        {
            if (SpecialEffectConfig.Get(_data, id) is not { } special) continue;
            var once = new HashSet<(long Op, long Attr, long Value, long Group)>();
            foreach (var op in special.SpecialEffect)
            {
                switch (op)
                {
                    case [AttrOp, var attr, var value]:
                        yield return (attr, value);
                        break;
                    case [WeaponAttrOp, var weapon, var attr, var value]
                        when SkillParameterRules.Wields(s, _data, weapon) && once.Add((WeaponAttrOp, attr, value, 0)):
                        yield return (attr, value);
                        break;
                    case [SkillLevelAttrOp, var group, var level, var attr, var value]
                        when LearnedLevel(s, group) >= level && once.Add((SkillLevelAttrOp, attr, value, 0)):
                        yield return (attr, value);
                        break;
                    case [WeaponSkillAttrOp, var weapon, var attr, var perLevel, var group]
                        when SkillParameterRules.Wields(s, _data, weapon) && once.Add((WeaponSkillAttrOp, attr, perLevel, group)):
                        yield return (attr, perLevel * LearnedLevel(s, group));
                        break;
                    // "While [Battle Chant] is active, your Max HP increases by ${1}.": the granted buff's _kChangeAttri.
                    case [BuffOp, var buff] when TalentProcs.WhileActive.TryGetValue(buff, out long active)
                        && SkillParameterRules.HasBuffGroup(s, _data, active) && BuffConfig.Get(_data, buff)?.LogicData is [var granted, ..]
                        && BuffConfig.Get(_data, granted) is { } grantedRow:
                        foreach (var change in grantedRow.ChangeAttri)
                            if (change is [var attr, var value]) yield return (attr, value);
                        break;
                }
            }
        }
    }

    private static long LearnedLevel(GameSession s, long group) => s.Progress?.Skills.GetValueOrDefault((int)group) ?? 0;

    /// <summary>The active plan's skill parameter modifiers, each with the weapon and the buff group it requires (0 = none),
    /// and its Buff(2) rows with their effect's description values.</summary>
    private (List<(long Weapon, long Buff, SkillParameterRules.Modifier Modifier)> Modifiers, List<(long Buff, string[] Description)> Buffs)
        SkillEffects(GameSession s)
    {
        var modifiers = new List<(long, long, SkillParameterRules.Modifier)>();
        var buffs = new List<(long, string[])>();
        foreach (var effect in ActiveEffects(s))
            foreach (long id in new SeasonTalentEffectConfig(effect).SpecialEffectList)
            {
                if (SpecialEffectConfig.Get(_data, id) is not { } special) continue;
                foreach (var op in special.SpecialEffect)
                {
                    if (op is [SkillParameterOp, var weapon, 0, var group, var type, var operation, var value, ..] && group > 0)
                        modifiers.Add((weapon, 0, new SkillParameterRules.Modifier((int)group, (int)type, (int)operation, value,
                            op.Length > 7 ? (int)op[7] : 0)));
                    else if (op is [BuffOp, var buff])
                    {
                        foreach (var modifier in SkillParameterRules.BuffModifiers(_data, (int)buff)) modifiers.Add((0, 0, modifier));
                        // "While [Aura Blade] is active ...": the buff _kLogicData names applies while that group lives.
                        if (TalentProcs.WhileActive.TryGetValue(buff, out long active) && BuffConfig.Get(_data, buff)?.LogicData is [var granted, ..])
                            foreach (var modifier in SkillParameterRules.BuffModifiers(_data, (int)granted)) modifiers.Add((0, active, modifier));
                        buffs.Add((buff, special.DescData));
                    }
                }
            }
        return (modifiers, buffs);
    }

    /// <summary>Recompute the session's talent skill modifiers and procs after a talent, plan or quest change.</summary>
    internal async Task RefreshEffectsAsync(GameSession s)
    {
        var (next, buffs) = SkillEffects(s);
        s.TalentProcBuffs = buffs;
        if (next.SequenceEqual(s.TalentSkillModifiers)) return;
        s.TalentSkillModifiers = next;
        if (s.SceneLoading || s.Role == null) return; // the loaded scene publishes 31132 itself
        foreach (var observer in EffectsChanged) await observer(s);
    }
}
