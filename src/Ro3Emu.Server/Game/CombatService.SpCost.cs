using System.Text.Json;
using Ro3Emu.Server.Data;
using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>
/// SP cost changes. AttributeConfig 1231 sp_dedution (flat) and 1232 per_sp_dedution ("SP consumption", basis points)
/// come from cards (CardConfig, -20%/+10%/+15%) and buffs (Mana Recharge 14204 -1..-10%, 23392 +10..+20%, Marionette).
/// No client code reads them, so the server applies them to every skill's SP cost (CombatService.ModifiedSpCost,
/// inferred). BuffConfig type 35 (SkillCost) edits a skill's _kCost first (SkillParameterRules). The client checks SP
/// against the configured _kCost unless 31132 sends a type-35 cost list (LC_DynamicParamMgr.GetDynamicSkillCost,
/// LC_Skill_Function.CheckSkillCost), so the effective list is sent for every learned skill whose cost differs.
/// </summary>
public sealed partial class CombatService
{
    /// <summary>The cost list the client should check for this effective row, or null when it equals the configured one.</summary>
    private BeAffectedValueEx? ClientCostList(StatSheet stats, JsonElement configuredRow, JsonElement row)
    {
        if (!row.TryGetProperty("_kCost", out var costs) || costs.ValueKind != JsonValueKind.Array) return null;
        var value = new BeAffectedValueEx();
        foreach (var tuple in costs.EnumerateArray())
        {
            if (tuple.ValueKind != JsonValueKind.Array) return null;
            var list = new BeAffectedValueList();
            foreach (var v in tuple.EnumerateArray()) list.AffectValueList.Add((long)(GameData.Number(v) ?? 0));
            // [1, 2001, ratio, flat]: the client compares only the flat amount with current SP.
            if (list.AffectValueList.Count >= 4 && list.AffectValueList[0] == 1 && list.AffectValueList[1] == 2001)
                list.AffectValueList[3] = ModifiedSpCost(stats, list.AffectValueList[3]);
            value.AffectValueListList.Add(list);
        }
        return Numbers(value.AffectValueListList.Select(l => l.AffectValueList.AsEnumerable())) == Numbers(Tuples(configuredRow))
            ? null : value;

        static IEnumerable<IEnumerable<long>> Tuples(JsonElement r) => r.TryGetProperty("_kCost", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.Array)
                .Select(t => t.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)))
            : [];
        static string Numbers(IEnumerable<IEnumerable<long>> tuples) => string.Join(";", tuples.Select(t => string.Join(",", t)));
    }

    /// <summary>Does this row carry an SP cost that sp_dedution and per_sp_dedution change.</summary>
    private static bool HasSpCost(JsonElement row) => row.TryGetProperty("_kCost", out var costs) && costs.ValueKind == JsonValueKind.Array
        && costs.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.Array && t.GetArrayLength() >= 4
            && GameData.Number(t[0]) == 1 && GameData.Number(t[1]) == 2001);
}
