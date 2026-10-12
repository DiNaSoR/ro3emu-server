using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Equipment-owned SpecialEffectConfig operations. The gear pools contain Attr(1), EffectCountAddAttr(4),
/// RefineCountAddAttr(5), Attr1CountAddAttr2(8), InstantPotionEnhance(13), TwoAttrAddOneAttr(14).
/// Formulas follow LC_SpecialEffectHelper's contextual previews.
/// Applying these previews to authoritative stats is (inferred); no generic buff/skill interpreter is introduced.
/// </summary>
internal static class EquipmentStunts
{
    /// <summary>
    /// LC_DAL_Equipment.RefreshSpecailEffectNum and the equipment tooltip exclude a main-hand weapon carried in the
    /// off-hand grid; each equipped GUID contributes once. Bag items and inactive plans contribute nothing.
    /// </summary>
    private static IEnumerable<InvItem> Active(GameSession session, GameData data) => session.Items
        .Where(i => i.Grid > 0 && CharacterService.IsAppraised(i)
            && !(i.Grid == 2 && data.Row("ItemConfig", i.Entry) is { } config && GameData.Int(config, "_iEquipPart") == 2))
        .DistinctBy(i => i.Guid);

    private static IEnumerable<(InvItem Item, long[] Op)> Operations(GameSession session, GameData data)
    {
        foreach (var item in Active(session, data))
            foreach (long id in EquipmentAffixes.Specials(item))
                if (data.Row("SpecialEffectConfig", id) is { } effect)
                    foreach (var op in EquipmentAffixes.Rows(effect, "_kSpecialEffect")) yield return (item, op);
    }

    /// <summary>Flat/stat-count/refine bonuses enter before STR..LUK conversion, like ordinary affixes.</summary>
    internal static IEnumerable<(long Id, double Value)> FlatAttributes(GameSession session, GameData data)
    {
        int count = Active(session, data).Sum(i => EquipmentAffixes.Specials(i).Count());
        foreach (var (item, op) in Operations(session, data))
        {
            if (op.Length == 3 && op[0] == 1) yield return (op[1], op[2]);
            else if (op.Length == 5 && op[0] == 4 && op[1] > 0)
                yield return (op[3], Math.Min(count, op[2]) / op[1] * op[4]);
            else if (op.Length == 4 && op[0] == 5 && op[1] > 0)
                yield return (op[2], session.EquipLevels.Refine.GetValueOrDefault(item.Grid) / op[1] * op[3]);
        }
    }

    /// <summary>
    /// LC_SpecialEffectHelper:2099-2150,2612-2672 uses floor before multiplying, then caps the bonus.
    /// (inferred) All conversions read one pre-conversion stat snapshot; reciprocal PATK/MATK conversions cannot
    /// amplify themselves or depend on inventory iteration order. Modifiers enter their own AttributeConfig roots.
    /// </summary>
    internal static StatSheet ApplyConversions(GameSession session, Dictionary<int, double> raw, GameData data)
    {
        var before = StatCalculator.Compute(raw, data);
        bool changed = false;
        foreach (var (_, op) in Operations(session, data))
        {
            long source, step, target, perStep, cap;
            if (op.Length == 6 && op[0] == 8)
                (source, step, target, perStep, cap) = (before[(int)op[1]], op[2], op[3], op[4], op[5]);
            else if (op.Length == 7 && op[0] == 14)
                (source, step, target, perStep, cap) = (before[(int)op[1]] + before[(int)op[2]], op[3], op[4], op[5], op[6]);
            else continue;
            double value = step <= 0 ? 0 : Math.Min(Math.Floor((double)source / step) * perStep, cap);
            raw[(int)target] = raw.GetValueOrDefault((int)target) + value;
            changed = true;
        }
        return changed ? StatCalculator.Compute(raw, data) : before;
    }

    /// <summary>
    /// Opcode 13's text describes chance/10000 and recovery bonus/10000. (inferred) Each consumed instant-recovery
    /// item rolls each active stunt independently; bonuses add on that item's HP or SP recovery, with the batch
    /// rounded down once as in the existing item-use path.
    /// Other item actions, periodic buffs and skills do not acquire this bonus.
    /// </summary>
    internal static long PotionRecovery(GameSession session, double perItem, int count, GameData data)
    {
        var effects = Operations(session, data).Select(x => x.Op).Where(op => op.Length == 3 && op[0] == 13).ToArray();
        double total = perItem * count;
        for (int i = 0; i < count; i++)
        {
            long bonus = 0;
            foreach (var op in effects)
                if (Random.Shared.Next(10000) < op[1]) bonus += op[2];
            total += perItem * bonus / 10000;
        }
        return (long)total;
    }
}
