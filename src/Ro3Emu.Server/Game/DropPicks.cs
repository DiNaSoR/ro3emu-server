using System.Text.Json;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The "picks" of a DropPoolConfig slot [group, chance, rolls, picks] (inferred): one successful roll draws up to
/// "picks" distinct DropGroupConfig rows by _iWeights, without replacement. Pools commonly set picks to the group's
/// row count (pool 34 = 10 of 10 rows, 128 = 11 of 11, 133 = 5 of 5), which only makes sense as "each row once";
/// drawing with replacement turned quest 10101210's one-weapon-per-class pool into 10 copies of the same weapon.
/// </summary>
internal static class DropPicks
{
    public static List<JsonElement> Distinct(IReadOnlyList<JsonElement> candidates, int picks, Random random)
    {
        var left = candidates.Where(r => GameData.Int(r, "_iWeights") > 0).ToList();
        var chosen = new List<JsonElement>(Math.Min(picks, left.Count));
        while (chosen.Count < picks && left.Count > 0)
        {
            long choice = random.NextInt64(left.Sum(r => GameData.Int(r, "_iWeights")));
            for (int i = 0; i < left.Count; i++)
            {
                choice -= GameData.Int(left[i], "_iWeights");
                if (choice >= 0) continue;
                chosen.Add(left[i]);
                left.RemoveAt(i);
                break;
            }
        }
        return chosen;
    }
}
