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
        11317, 11318, // Hundred Spears Stack / Slash monitors: stack trackers, not learned bonuses
    ];

    /// <summary>
    /// Passives whose skill changes sit in another group's BuffConfig rows (group, owner group). Steel Crow 13105
    /// ("Blitz Beat damage coefficient +X%", Lv5 = 5%) has no rows of its own; its +100 bp per level on Blitz Beat
    /// 13102/13110 is stored under Beastbane's group 13104 (rows 1310401-1310410 are named Steel Crow). Beastbane's
    /// own effect, Brute and Insect damage %, is its SkillConfig _kAttri 401/409, so it reads no buff rows (inferred
    /// client id swap; Steven chose Steel Crow as the owner, 2026-10-18).
    /// </summary>
    private static readonly IReadOnlyDictionary<int, int> PassiveBuffOwners = new Dictionary<int, int>
    {
        [13105] = 13104,
        [13104] = 0,
    };

    /// <summary>The BuffConfig row holding a learned passive's skill changes: its own id, or the owner group's row of
    /// the same level; 0 for none.</summary>
    internal static int PassiveBuffRow(int group, System.Text.Json.JsonElement row)
    {
        if (!PassiveBuffOwners.TryGetValue(group, out int owner)) return (int)GameData.Int(row, "_iID");
        return owner == 0 ? 0 : owner * 100 + (int)GameData.Int(row, "_iLevel");
    }

    /// <summary>Passives whose same-id buff _kChangeAttri matches the description: Meditatio (Max MP% and MP
    /// recovery%), Mana Recharge (MP cost -% and MATK), Create Deadly Poison (Poison damage%).</summary>
    private static readonly HashSet<int> BuffStatGroups = [14203, 14204, 15212];

    /// <summary>The learned passive level rows (group, SkillConfig row), the same groups PassiveAttributes reads.</summary>
    private IEnumerable<(int Group, System.Text.Json.JsonElement Row)> LearnedPassiveRows(GameSession session)
    {
        if (session.Progress == null) yield break;
        _passiveGroups ??= _data.Table("SkillConfig").Values
            .Where(r => GameData.Int(r, "_iBasicType") == 0 && GameData.Int(r, "_iMode") == 0
                && GameData.Int(r, "_iInitialLevel") > 0)
            .Select(r => (int)GameData.Int(r, "_iSkillID")).Where(g => g > 0).Distinct().ToArray();
        foreach (int group in session.Progress.Skills.Keys.Concat(_passiveGroups).Distinct())
        {
            int level = _skills.Level(session.Progress, group);
            if (level <= 0 || _data.Row("SkillConfig", _skills.RealId(group, level)) is not { } row
                || GameData.Int(row, "_iBasicType") != 0) continue;
            yield return (group, row);
        }
    }

    /// <summary>Recompute the passive skills' modifiers when the learned levels, job or base level changed.</summary>
    internal void SyncPassiveSkillModifiers(GameSession session)
    {
        var p = session.Progress;
        if (p == null) return;
        long signature = unchecked(p.Career * 1_000_003L + p.Level + _skills.Weapon(session).SubType * 7_368_787L);
        foreach (var (group, level) in p.Skills) signature = unchecked(signature + (group * 7919L + level) * 104_729L);
        if (session.PassiveSkillSignature == signature) return;
        var result = new List<SkillParameterRules.Modifier>();
        foreach (var (group, row) in LearnedPassiveRows(session))
        {
            if (group == KatarMasteryGroup)
            {
                if (_skills.Weapon(session).SubType == KatarSubType) result.AddRange(KatarMasteryModifiers(row));
            }
            else if (!ConditionalPassiveGroups.Contains(group))
                if (PassiveBuffRow(group, row) is var buff and > 0) result.AddRange(SkillParameterRules.BuffModifiers(_data, buff));
            // [21, weapon subtype or 0, scope 0, group, type, operation, value]: a skill change while that weapon is held
            // (Mace Mastery's Normal Attack crit with a mace), read like a talent's opcode 21 (SeasonTalentEffects).
            foreach (var op in SpecialEffectOps(row))
                if (op.Length >= 7 && op[0] == 21 && op[2] == 0 && op[3] > 0 && (op[1] == 0 || op[1] == _skills.Weapon(session).SubType))
                    result.Add(new SkillParameterRules.Modifier((int)op[3], (int)op[4], (int)op[5], op[6], op.Length > 7 ? (int)op[7] : 0));
        }
        session.PassiveSkillSignature = signature;
        if (result.SequenceEqual(session.PassiveSkillModifiers)) return;
        session.PassiveSkillModifiers = result;
        session.PassiveSkillModifierVersion++;
    }

    private const int KatarMasteryGroup = 15101, KatarMasteryBuffGroup = 15112, KatarSubType = 12;

    /// <summary>
    /// Katar Mastery 15101: "While equipped with a Katar-type weapon, increases Physical ATK by ${1} and increases the
    /// damage multiplier of [Assassin Normal Attack] by ${2}%". Its data sits in BuffConfig 1511201-1511210 (named
    /// "Katar Mastery", in Disguise's group 15112): _kChangeAttri [1033, level] and, from Lv5, the +10% normal-attack
    /// modifiers. The description gives 10% at every level, so lower levels use the Lv5 row's modifiers (inferred).
    /// </summary>
    private IEnumerable<SkillParameterRules.Modifier> KatarMasteryModifiers(System.Text.Json.JsonElement row)
    {
        int level = (int)Math.Max(1, GameData.Int(row, "_iLevel"));
        var own = SkillParameterRules.BuffModifiers(_data, KatarMasteryBuffGroup * 100 + level);
        return own.Count > 0 ? own : SkillParameterRules.BuffModifiers(_data, KatarMasteryBuffGroup * 100 + 5);
    }

    /// <summary>Katar Mastery's PATK (its BuffConfig row's _kChangeAttri) while a katar is held.</summary>
    private IEnumerable<(long Id, double Value)> KatarMasteryAttributes(GameSession session)
    {
        if (session.Progress is not { } progress || _skills.Level(progress, KatarMasteryGroup) is not (var level and > 0)
            || _skills.Weapon(session).SubType != KatarSubType
            || _data.Row("BuffConfig", KatarMasteryBuffGroup * 100 + level) is not { } row) yield break;
        foreach (var (id, value) in GameData.Pairs(row, "_kChangeAttri")) yield return (id, value);
    }

    /// <summary>The SpecialEffectConfig tuples a passive level row names in _iSpecialEffect (one id or a list).</summary>
    private IEnumerable<long[]> SpecialEffectOps(System.Text.Json.JsonElement row)
    {
        if (!row.TryGetProperty("_iSpecialEffect", out var field)) yield break;
        var ids = field.ValueKind == System.Text.Json.JsonValueKind.Array
            ? field.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray()
            : [(long)(GameData.Number(field) ?? 0)];
        foreach (long id in ids)
            if (id > 0 && _data.Row("SpecialEffectConfig", id) is { } effect)
                foreach (var op in EquipmentAffixes.Rows(effect, "_kSpecialEffect")) yield return op;
    }

    /// <summary>
    /// Weapon and mount masteries: [16, weapon subtype, attribute, value] while the main hand holds that weapon type
    /// (Sword Mastery [16,2,103,10] and [16,3,103,10]: PATK +10 with a one- or two-handed sword) and
    /// [17, mount type, attribute, value] while riding that mount (Riding [17,2,1131,500]: +5% move speed on a Peco).
    /// The mount type is the ridden Battle mount's CartRemouldTable _iSubType (2 Peco Peco, 4 wolf, 5 Madogear,
    /// 6 dragon, 7 gryphon); the same reading as the talents' opcode 16 (SeasonTalentService.Attributes), inferred.
    /// </summary>
    private IEnumerable<(long Id, double Value)> PassiveConditionalAttributes(GameSession session)
    {
        foreach (var katar in KatarMasteryAttributes(session)) yield return katar;
        int? weapon = null, mount = null;
        foreach (var (_, row) in LearnedPassiveRows(session))
            foreach (var op in SpecialEffectOps(row))
                switch (op)
                {
                    case [16, var sub, var attr, var value]:
                        if ((weapon ??= _skills.Weapon(session).SubType) == sub) yield return (attr, value);
                        break;
                    case [17, var type, var attr, var value]:
                        if ((mount ??= RiddenMountType(session)) == type) yield return (attr, value);
                        break;
                }
    }

    /// <summary>The ridden Battle mount's CartRemouldTable _iSubType; 0 on foot or on an ordinary mount.</summary>
    internal int RiddenMountType(GameSession session) =>
        MountService.Rider(session) is var id and not 0 && _data.Row("CartRemouldTable", id) is { } row
            && GameData.Int(row, "_iType") == 2 ? (int)GameData.Int(row, "_iSubType") : 0;

    /// <summary>Weapon subtype 5 (Knight Spear, ItemConfig _iEquipPart 1 two-handed; subtype 4 Halberd is one-handed).</summary>
    internal const int TwoHandedSpear = 5;

    /// <summary>Shields are off-hand ItemConfig subtype 50 (_iEquipPart 3).</summary>
    internal const int ShieldSubType = 50;

    /// <summary>
    /// Monster Resistance 11421 (30%), Advanced Monster Resistance 11508 (40%) and 11637 (50%): "damage reduction from
    /// monsters +X% while a shield is equipped. Does not stack with other monster damage reduction effects". The rows
    /// carry the percentage only in _kDescData; the best learned one applies, in basis points (inferred: not stacking).
    /// </summary>
    private static readonly int[] MonsterResistanceGroups = [11421, 11508, 11637];

    internal int MonsterResistanceBp(GameSession session)
    {
        if (session.Progress == null || !_skills.HoldsShield(session)) return 0;
        int best = 0;
        foreach (int group in MonsterResistanceGroups)
            if (_skills.Level(session.Progress, group) is var level and > 0
                && _data.Row("SkillConfig", _skills.RealId(group, level)) is { } row
                && row.TryGetProperty("_kDescData", out var desc) && desc.ValueKind == System.Text.Json.JsonValueKind.Array
                && desc.GetArrayLength() > 0 && GameData.Number(desc[0]) is { } pct)
                best = Math.Max(best, (int)Math.Round(pct * 100));
        return best;
    }

    /// <summary>The <see cref="BuffStatGroups"/> passives' same-id buff attributes.</summary>
    private IEnumerable<(long Id, double Value)> PassiveBuffAttributes(GameSession session)
    {
        foreach (var (group, row) in LearnedPassiveRows(session))
            if (BuffStatGroups.Contains(group) && _data.Row("BuffConfig", GameData.Int(row, "_iID")) is { } buff)
                foreach (var pair in GameData.Pairs(buff, "_kChangeAttri")) yield return pair;
    }
}
