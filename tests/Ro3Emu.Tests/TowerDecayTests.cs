using System.Text.Json;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Captured playbook 101 floor 2: UI decay and the first monster AOI HP (traffic-floor2.jsonl).</summary>
public sealed class TowerDecayTests
{
    [ClientDataFact]
    public void FloorTwoRatioMatchesTheCapturedNinetyNinePercentAnd1188Hp()
    {
        // The client floors both timestamps after its UTC+8 offset and the configured 05:00 refresh.
        var beforeRefresh = new DateTimeOffset(2026, 10, 8, 20, 59, 0, TimeSpan.Zero);
        long challengeMs = beforeRefresh.ToUnixTimeMilliseconds();
        using var config = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(RepoRoot(), "data", "config", "PetFloorConfig.json")));
        var floor = config.RootElement.EnumerateArray().Single(r => r.GetProperty("_iID").GetInt32() == 2);

        Assert.Equal(33, floor.GetProperty("_iLevelLimit").GetInt32());
        Assert.Equal(10000, TowerService.DifficultyRatio(floor, 1011, challengeMs, beforeRefresh));
        Assert.Equal(9900, TowerService.DifficultyRatio(floor, 1011, challengeMs, beforeRefresh.AddMinutes(1)));
        Assert.Equal(9900, TowerService.DifficultyRatio(floor, 215, challengeMs, beforeRefresh.AddMinutes(1)));
        Assert.Equal(9000, TowerService.DifficultyRatio(floor, 1011, challengeMs, beforeRefresh.AddDays(11)));

        // AttriMonsterConfig[310002] starts at 1200 HP. The capture's first AOI has 1188;
        // its later 1187 is after Bobo's one-damage hit.
        Assert.Equal(1188, 1200 * TowerService.DifficultyRatio(floor, 1011, challengeMs, beforeRefresh.AddMinutes(1)) / 10000);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ro3Emu.sln"))
            && !File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
    }
}
