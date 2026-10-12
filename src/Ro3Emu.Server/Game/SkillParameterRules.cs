using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The scalar part of BuffConfig._kSkillAttributeList. The client export carries
/// [scope, group, SkillAffectValueType, operation, value, optional index]. Scope 0 names a
/// skill group; scope 1 has a different selector and is not treated as a group.
/// The operation arithmetic (1 add, 2 set, 5 basis-point multiplier) is inferred from
/// the exported buff pairs, not recovered original-server code.
/// </summary>
internal static class SkillParameterRules
{
    internal readonly record struct Modifier(int Group, int Type, int Operation, long Value, int Index);

    internal static IReadOnlyList<Modifier> Active(GameSession session, GameData data)
    {
        var result = new List<Modifier>();
        foreach (var buff in session.Buffs)
        {
            if (buff.ExpireMs <= MonsterService.NowMs || data.Row("BuffConfig", buff.ConfigId) is not { } row
                || !row.TryGetProperty("_kSkillAttributeList", out var list) || list.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 5
                    || GameData.Number(item[0]) != 0 || GameData.Number(item[1]) is not { } group
                    || GameData.Number(item[2]) is not { } type || GameData.Number(item[3]) is not { } op
                    || GameData.Number(item[4]) is not { } value || group <= 0) continue;
                int index = item.GetArrayLength() > 5 && GameData.Number(item[5]) is { } n ? (int)n : 0;
                for (int i = 0; i < buff.PileCount; i++)
                    result.Add(new((int)group, (int)type, (int)op, (long)value, index));
            }
        }
        return result;
    }

    internal static long Apply(long value, Modifier modifier) => modifier.Operation switch
    {
        1 => value + modifier.Value,
        2 => modifier.Value,
        5 => (long)Math.Round(value * (1 + modifier.Value / 10000.0), MidpointRounding.AwayFromZero),
        _ => value,
    };

    internal static int Mode(int group, IReadOnlyList<Modifier> modifiers)
    {
        long mode = 0;
        foreach (var mod in modifiers)
            if (mod.Group == group && mod.Type == 15) mode = Apply(mode, mod);
        return (int)Math.Clamp(mode, 0, 99);
    }

    // The supported scalar/list fields are shared by server validation and client 31132.
    internal static string? Field(int type) => type switch
    {
        0 => "_iCD", 21 => "_iActionTime", 23 => "_iChantFix", 24 => "_iChantVar",
        25 => "_iDistanceMin", 26 => "_iDistanceMax", 27 => "_iTargetMax",
        29 => "_kDamageParam1", 30 => "_kDamageParam2",
        _ => null,
    };

    internal static JsonElement Effective(JsonElement row, IReadOnlyList<Modifier> modifiers)
    {
        int group = (int)GameData.Int(row, "_iSkillID");
        var relevant = modifiers.Where(m => m.Group == group && Field(m.Type) != null && m.Operation is 1 or 2 or 5).ToList();
        if (relevant.Count == 0) return row;
        var json = JsonNode.Parse(row.GetRawText())!.AsObject();
        foreach (var mod in relevant)
        {
            string field = Field(mod.Type)!;
            if (mod.Type is 29 or 30)
            {
                if (json[field] is not JsonArray arr) continue;
                // Index -1 in exported damage modifiers targets the full vector (inferred).
                int first = mod.Index < 0 ? 0 : mod.Index;
                int end = mod.Index < 0 ? arr.Count : Math.Min(arr.Count, first + 1);
                for (int i = first; i < end; i++)
                {
                    if (!long.TryParse(arr[i]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long old)) continue;
                    arr[i] = Apply(old, mod);
                }
            }
            else
            {
                if (!long.TryParse(json[field]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long old)) continue;
                json[field] = Apply(old, mod);
            }
        }
        return JsonSerializer.SerializeToElement(json);
    }

}
