using System.Text.Json;
using Ro3Emu.Server.Game;
using Fighter = Ro3Emu.Server.Game.CombatService.Fighter;

namespace Ro3Emu.Tests;

/// <summary>The _kDamageFormulaExtra terms: stat resonance, ASPD scaling and Heal's Base Level bonus.</summary>
public sealed class SkillFormulaTests
{
    private static JsonElement Row(string json) => JsonDocument.Parse(json).RootElement;

    private static Fighter Player(int level = 10, params (int Id, long Value)[] stats)
    {
        var sheet = new StatSheet();
        foreach (var (id, value) in stats) sheet[id] = value;
        return new Fighter(100, 100, 0, 0, level, 1) { Stats = sheet, Race = CombatService.PlayerRace, Size = CombatService.PlayerSize };
    }

    // Jack Frost Lv5 1230805: "Dexterity Resonance 0.1%".
    private static readonly JsonElement JackFrost = Row("""{"_kDamageFormulaExtra":["T_AXadd0_etc_withlimit"],"_kDamageParamExtra":[[1,10,5,1,100]]}""");
    // Radiant Holy Light 1430305 carries no mode: [caster, coefficient, attribute, limit].
    private static readonly JsonElement RadiantHolyLight = Row("""{"_kDamageFormulaExtra":["T_AXadd0_etc_withlimit"],"_kDamageParamExtra":[[1,10,5,100]]}""");
    // Blitz Beat 1310205: "Every 20 AGI increases this DMG by 1%".
    private static readonly JsonElement BlitzBeat = Row("""{"_kDamageFormulaExtra":["T_AXadd0_etc"],"_kDamageParamExtra":[[1,5,2,1]]}""");
    // Banishing Thrust 1160105: "For every 1% ASPD, this DMG increases by 0.3%".
    private static readonly JsonElement BanishingThrust = Row("""{"_kDamageFormulaExtra":["AXaddBplus1"],"_kDamageParamExtra":[[30,115,10000]]}""");
    // Cross Ripper Slasher 1530205: "PATK * 40% * (1 + ASPD * 0.12%)", ASPD up to 500%.
    private static readonly JsonElement CrossRipper = Row("""{"_kDamageFormulaExtra":["AXaddBplus1_withlimit"],"_kDamageParamExtra":[[12,115,10000,500]]}""");
    // Heal 1140505: "Each Base Level increases Healing Volume by 0.1%".
    private static readonly JsonElement Heal = Row("""{"_kDamageFormulaExtra":["T_AXadd0_Xisskilllv"],"_kDamageParamExtra":[[1,10,0,1]]}""");
    // A shield's flat amount (mode 3) is not a damage multiplier.
    private static readonly JsonElement Shield = Row("""{"_kDamageFormulaExtra":["T_AXadd0_etc"],"_kDamageParamExtra":[[0,500,101,3]]}""");

    [Fact]
    public void ResonanceAddsATenthOfAPercentPerPointUpToItsCap()
    {
        Assert.Equal(1.05, CombatService.FormulaFactor(Player(stats: (5, 50)), JackFrost, 0, heal: false), 6);
        Assert.Equal(1.10, CombatService.FormulaFactor(Player(stats: (5, 180)), JackFrost, 0, heal: false), 6);
        Assert.Equal(1.08, CombatService.FormulaFactor(Player(stats: (5, 80)), RadiantHolyLight, 0, heal: true), 6);
        Assert.Equal(1.05, CombatService.FormulaFactor(Player(stats: (2, 100)), BlitzBeat, 0, heal: false), 6);
    }

    [Fact]
    public void AspdScalingReadsThePanelAspd()
    {
        Assert.Equal(1.3, CombatService.FormulaFactor(Player(stats: (115, 100)), BanishingThrust, 0, heal: false), 6);
        Assert.Equal(1.75, CombatService.FormulaFactor(Player(stats: (115, 250)), BanishingThrust, 0, heal: false), 6);
        Assert.Equal(1.36, CombatService.FormulaFactor(Player(stats: (115, 300)), CrossRipper, 0, heal: false), 6);
        Assert.Equal(1.6, CombatService.FormulaFactor(Player(stats: (115, 900)), CrossRipper, 0, heal: false), 6);
    }

    [Fact]
    public void HealsGainTheBaseLevelBonusAndDamageDoesNot()
    {
        Assert.Equal(1.05, CombatService.FormulaFactor(Player(level: 50), Heal, 0, heal: true), 6);
        Assert.Equal(1, CombatService.FormulaFactor(Player(level: 50), Heal, 1, heal: false));
        Assert.Equal(1, CombatService.FormulaFactor(Player(stats: (101, 10000)), Shield, 0, heal: false));
        var monster = new Fighter(100, 100, 0, 0, 10, 1) { IsMonster = true, Stats = new StatSheet { [5] = 100 } };
        Assert.Equal(1, CombatService.FormulaFactor(monster, JackFrost, 0, heal: false));
    }
}
