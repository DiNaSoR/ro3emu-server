using System.Text.Json;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Per-instance equipment score for ItemBaseInfo.itemInfos[2]. The client reads this value rather than calculating it
/// (LC_ItemProtoParse.ParseEquipInfo, clientB:271-293,471-487; LC_EquipmentHelper.GetEquipScore:965-975).
/// The weighted construction below is inferred: the client does not establish the original server formula.
/// It is not an emblem or overall player-power formula; PowerService counts it as one term of equipRankScore.
/// </summary>
public static class EquipmentRating
{
    public const int ScoreKey = 2;
    private const int AppraiseKey = 3;

    /// <summary>
    /// LC_ScoreHelper.CalcAttrScore:16-27 proves value times AttributeConfig._iPower, and CalcSpecialScore:29-39
    /// proves the special-power lookup. Using their sum for equipment intrinsic pairs is (inferred), as are the
    /// nonnegative clamp and nearest-integer rounding, halves away from zero.
    /// The pair ranges and appraisal marker are clientB PBEnumDefine.lua:7476-7513 and LC_ItemProtoParse.lua:271-293,
    /// 356-450. No equipment-base _iPower is available in the exported EquipConfig.
    /// Grid strengthen/refine, cards, and enchantments are deliberately outside this item-instance calculation.
    /// </summary>
    public static long Calculate(InvItem item, GameData data)
    {
        if (item.Attrs is not { } attrs || attrs.GetValueOrDefault(AppraiseKey) != 1
            || data.Row("ItemConfig", item.Entry) is not { } config || GameData.Int(config, "_iType") != 5)
            return 0;

        decimal score = AttributePairs(attrs, data, 30, 60);
        score += AttributePairs(attrs, data, 100, 140);

        // The client parses the special slots as IDs, not attribute/value pairs (LC_ItemProtoParse.lua:430-450).
        for (int slot = 0; slot < 10 && attrs.TryGetValue(80 + slot, out long id); slot++)
            if (data.Row("SpecialEffectConfig", id) is { } special)
                score += GameData.Int(special, "_iPower");

        score = decimal.Round(Math.Max(0m, score), 0, MidpointRounding.AwayFromZero);
        return score >= long.MaxValue ? long.MaxValue : (long)score;
    }

    private static decimal AttributePairs(Dictionary<int, long> attrs, GameData data, int keyStart, int valueStart)
    {
        decimal score = 0;
        for (int slot = 0; slot < 20; slot++)
        {
            if (!attrs.TryGetValue(keyStart + slot, out long id)
                || !attrs.TryGetValue(valueStart + slot, out long value)) break;
            if (data.Row("AttributeConfig", id) is { } attribute
                && attribute.TryGetProperty("_iPower", out JsonElement power)
                && power.ValueKind == JsonValueKind.Number && power.TryGetDecimal(out decimal weight))
                score += value * weight;
        }
        return score;
    }
}
