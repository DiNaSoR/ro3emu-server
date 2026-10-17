using System.Text.Json;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// DropAttenuationConfig (12 rows, 2 plans): a DropGroupConfig row with _iAttenuationPlanID scales its drop by the
/// level gap between the player and the slain monster. Each row's _kLvDiffLowerlimit/_kLvDiffUpLimit are the two ends
/// of an inclusive gap range (the export names them the other way round for the positive side: [10,-9], [20,11]...),
/// _iAttenuationRatio is per 10000. Plan 1 is symmetric (100% within 10 levels, 70% / 30% / 0 beyond); plan 2 is
/// 100% from -9 to 10, 50% from -19..-10 and 11..30, 0 beyond. Groups 105, 106 and 107 (NPC 5099017's drop 22) use them.
/// *(inferred)* The gap is player level minus monster level, as LvExpCorrectionConfig._iBaseLvGap; the ratio scales the
/// count of the picked row (a fraction rounds up with its own probability, so the expected count is exact) and a
/// count of 0 drops nothing; an award without a slain monster (AwardService) has no gap and drops in full.
/// </summary>
public sealed class DropAttenuation(GameData data)
{
    public const int NoMonster = int.MinValue;

    private Dictionary<long, (long Low, long High, long Ratio)[]>? _plans;

    /// <summary>The plan's ratio per 10000 for a player-minus-monster level gap; 10000 for no plan or an unlisted gap.</summary>
    public long Ratio(long planId, long gap)
    {
        if (planId <= 0) return 10000;
        var plans = _plans ??= Build();
        if (!plans.TryGetValue(planId, out var ranges)) return 10000;
        foreach (var (low, high, ratio) in ranges)
            if (gap >= low && gap <= high) return ratio;
        return 10000;
    }

    /// <summary>A picked row's count after its plan; <paramref name="monsterLevel"/> = <see cref="NoMonster"/> keeps it.</summary>
    public int Apply(JsonElement row, int count, int playerLevel, int monsterLevel, Random random)
    {
        if (monsterLevel == NoMonster) return count;
        long ratio = Ratio(GameData.Int(row, "_iAttenuationPlanID"), (long)playerLevel - monsterLevel);
        if (ratio >= 10000) return count;
        if (ratio <= 0) return 0;
        long scaled = (long)count * ratio;
        int whole = (int)(scaled / 10000);
        return whole + (random.NextInt64(10000) < scaled % 10000 ? 1 : 0);
    }

    private Dictionary<long, (long Low, long High, long Ratio)[]> Build()
    {
        static long First(JsonElement row, string field) => row.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Array
            && v.GetArrayLength() > 0 ? (long)(GameData.Number(v[0]) ?? 0) : (long)(GameData.Number(v) ?? 0);
        return data.Table("DropAttenuationConfig").Values
            .GroupBy(r => GameData.Int(r, "_iPlanID"))
            .ToDictionary(g => g.Key, g => g.Select(r =>
            {
                long a = First(r, "_kLvDiffLowerlimit"), b = First(r, "_kLvDiffUpLimit");
                return (Math.Min(a, b), Math.Max(a, b), GameData.Int(r, "_iAttenuationRatio"));
            }).ToArray());
    }
}
