using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>The client's attribute pipeline on the client's own AttributeConfig.</summary>
public sealed class StatCalculatorTests
{
    private static readonly GameData Data = new(
        Options.Create(new ServerOptions { DataDir = Path.Combine(RepoRoot(), "data") }), NullLogger<GameData>.Instance);

    [ClientDataFact]
    public void PercentMembersScaleTheirRootAndTheResultRoundsUp()
    {
        // STR 5 at PATK base 17: PATK raw 17 + 5 (STR -> ATK +1) = 22, per_atk 1031 = 5 * 20 = 100 (= 1 %).
        var sheet = StatCalculator.Compute(new Dictionary<int, double> { [103] = 22, [1031] = 100 }, Data);
        Assert.Equal(23, sheet[103]);         // 22 * 1.01 = 22.22 -> 23, as the client's preview shows
        Assert.Equal(22, sheet.Base[103]);    // pre-percentage part for the client's preview
        Assert.Equal(100, sheet[1031]);       // members travel as their own properties
    }

    [ClientDataFact]
    public void AspdUsesItsDataTypeAndBuffTypes()
    {
        // baseaspd 115 = 100 (1.00, dt 2), AGI 10 -> aspd 114 = 20 (+20 %, calc 2), buff_aspd 1142 = 10 (+0.10, calc 3).
        var sheet = StatCalculator.Compute(new Dictionary<int, double> { [115] = 100, [114] = 20, [1142] = 10 }, Data);
        Assert.Equal(130, sheet[115]); // 1.00 * 1.20 + 0.10 = 1.30
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
