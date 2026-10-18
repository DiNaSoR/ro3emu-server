using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Learned passive skills that change other skills. A passive SkillConfig level row (_iBasicType 0) often has a
/// BuffConfig row with the same id (_iBuffID = the skill group, _iLevel = the skill level; 703 of the export's 1,042
/// passive rows). Its _kSkillAttributeList is that passive's effect on other skills: Fatal Blow 1100301
/// [0,11001,29,5,500] raises Bash's damage multiplier, Graffiti Mastery switches Cross Ripper Slasher to its mode-1 row.
/// The server never received these as buffs, so they join SkillParameterRules as non-buff modifiers while the level
/// is learned (inferred link from the matching ids and each row's description). These rows' _kChangeAttri are mostly
/// stale (Riding's Holy damage reduction, Emergency Treatment's PATK); the stat bonus of a passive is its SkillConfig
/// _kAttri (PassiveAttributes), so only <see cref="BuffStatGroups"/>, checked against their descriptions, add theirs.
/// </summary>
public sealed partial class CombatService
{
    /// <summary>Passives whose skill changes hold only under a condition the server does not track, or whose data
    /// would apply only half of the described effect.</summary>
    private static readonly HashSet<int> ConditionalPassiveGroups =
    [
        13310, // Wolf Taming: Warg Strike and the speed only while mounted on the wolf
        15220, // Advanced Katar Mastery: the -20% multiplier is paired with +85% crit (type 10, not applied)
        11317, 11318, // Hundred Spears Stack / Slash monitors: stack trackers, not learned bonuses
    ];

    /// <summary>Passives whose same-id buff _kChangeAttri matches the description: Meditatio (Max MP% and MP
    /// recovery%), Mana Recharge (MP cost -% and MATK), Create Deadly Poison (Poison damage%).</summary>
    private static readonly HashSet<int> BuffStatGroups = [14203, 14204, 15212];

    /// <summary>The learned passive level rows (group, SkillConfig row), the same groups PassiveAttributes reads.</summary>
    private IEnumerable<(int Group, System.Text.Json.JsonElement Row)> LearnedPassiveRows(GameSession session)
    {
        if (session.Progress == null) yield break;
        _passiveGroups ??= data.Table("SkillConfig").Values
            .Where(r => GameData.Int(r, "_iBasicType") == 0 && GameData.Int(r, "_iMode") == 0
                && GameData.Int(r, "_iInitialLevel") > 0)
            .Select(r => (int)GameData.Int(r, "_iSkillID")).Where(g => g > 0).Distinct().ToArray();
        foreach (int group in session.Progress.Skills.Keys.Concat(_passiveGroups).Distinct())
        {
            int level = skills.Level(session.Progress, group);
            if (level <= 0 || data.Row("SkillConfig", skills.RealId(group, level)) is not { } row
                || GameData.Int(row, "_iBasicType") != 0) continue;
            yield return (group, row);
        }
    }

    /// <summary>Recompute the passive skills' modifiers when the learned levels, job or base level changed.</summary>
    internal void SyncPassiveSkillModifiers(GameSession session)
    {
        var p = session.Progress;
        if (p == null) return;
        long signature = unchecked(p.Career * 1_000_003L + p.Level);
        foreach (var (group, level) in p.Skills) signature = unchecked(signature + (group * 7919L + level) * 104_729L);
        if (session.PassiveSkillSignature == signature) return;
        var result = new List<SkillParameterRules.Modifier>();
        foreach (var (group, row) in LearnedPassiveRows(session))
            if (!ConditionalPassiveGroups.Contains(group))
                result.AddRange(SkillParameterRules.BuffModifiers(data, (int)GameData.Int(row, "_iID")));
        session.PassiveSkillSignature = signature;
        if (result.SequenceEqual(session.PassiveSkillModifiers)) return;
        session.PassiveSkillModifiers = result;
        session.PassiveSkillModifierVersion++;
    }

    /// <summary>The <see cref="BuffStatGroups"/> passives' same-id buff attributes.</summary>
    private IEnumerable<(long Id, double Value)> PassiveBuffAttributes(GameSession session)
    {
        foreach (var (group, row) in LearnedPassiveRows(session))
            if (BuffStatGroups.Contains(group) && data.Row("BuffConfig", GameData.Int(row, "_iID")) is { } buff)
                foreach (var pair in GameData.Pairs(buff, "_kChangeAttri")) yield return pair;
    }
}
