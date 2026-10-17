using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class PetCombatRulesTests
{
    private static readonly GameData Data = new(Options.Create(new ServerOptions { GameDataDir = Path.Combine(RepoRoot(), "data") }),
        NullLogger<GameData>.Instance);

    [ClientDataTheory]
    [InlineData(13001, 1, 1, 0, 3165, 183, 20)]
    [InlineData(13001, 12, 1, 0, 3363, 218, 240)]
    [InlineData(13001, 12, 1, 1, 3498, 223, 250)]
    [InlineData(13001, 12, 2, 0, 3902, 236, 279)]
    [InlineData(10001, 12, 1, 0, 2242, 326, 240)]
    public void BattleVariantAndStarProduceConfigDerivedStats(int id, int level, int star, int stage, long hp, long atk, long def)
    {
        // (verified) Bobo variation10140: Lv1 [3165,183,183,20,20], Lv12 [3363,218,218,240,240].
        // PetStar 64/65/68: 10000/10400/11600. Choco10110 Lv12: [2242,326,326,240,240].
        // PetAttrChangeConfig quality 3 (both pets): HP/ATK/MATK x 10000/10400/11600 and 10000/10200/10800 by the same
        // star/stage, the other attributes by PetStar (Lv12 ATK 218 x 1.02 = 222.4 -> 223, x 1.08 = 235.4 -> 236).
        // (inferred) Combat overlay and ceil follow PetCombatRules; expected constants are not computed by that method.
        var raw = PetCombatRules.Attributes(Data, id, new PetData { PetId = id, Level = level, Star = star, StarProgress = stage });
        var stats = StatCalculator.Compute(Assert.IsType<Dictionary<int, double>>(raw), Data);
        Assert.Equal(hp, stats[101]);
        Assert.Equal(atk, stats[103]); Assert.Equal(atk, stats[104]);
        Assert.Equal(def, stats[105]); Assert.Equal(def, stats[106]);
        Assert.Equal(700, stats[113]); // star scaling must not multiply NPC movement speed.
    }

    [Fact]
    public void MissingOwnershipInputsOrUnknownProgressionHaveNoCombatTemplate()
    {
        Assert.Null(PetCombatRules.Attributes(Data, -1, new PetData { Level = 12, Star = 1 }));
        Assert.Null(PetCombatRules.Attributes(Data, 13001, new PetData { Level = 12, Star = 9 }));
        Assert.Null(PetCombatRules.Attributes(Data, 13001, new PetData { Level = 9999, Star = 1 }));
    }

    [ClientDataFact]
    public void ShieldAndHealOperandsUseTheirConfiguredSourceAndPercent()
    {
        var source = new StatSheet(); source[101] = 3363;
        var target = new StatSheet(); target[101] = 10000;
        Assert.Equal(218, PetCombatRules.AttributeAmount(Data.Row("BuffConfig", 2354401)!.Value, source, target));
        Assert.Equal(1300, PetCombatRules.AttributeAmount(Data.Row("BuffConfig", 2323401)!.Value, source, target));
        Assert.Equal(200, PetCombatRules.AttributeAmount(Data.Row("SkillConfig", 2356401)!.Value, source, target));
        Assert.Equal(0, PetCombatRules.AttributeAmount(Data.Row("SkillConfig", 2354401)!.Value, source, target));
        Assert.Equal(PetCombatRules.SupportKind.Buff, PetCombatRules.SupportFor(Data.Row("SkillConfig", 2354301)!.Value)!.Kind);
        Assert.Null(PetCombatRules.SupportFor(Data.Row("SkillConfig", 2354401)!.Value));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
