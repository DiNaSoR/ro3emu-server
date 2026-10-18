using System.Text.Json;
using Ro3Emu.Server.Game;
using Fighter = Ro3Emu.Server.Game.CombatService.Fighter;

namespace Ro3Emu.Tests;

/// <summary>
/// The secondary stats in the damage and heal formulas: each "+X% damage" stat against its "-X% damage taken"
/// partner is one factor, and the factors multiply.
/// </summary>
public sealed class SecondaryStatTests
{
    private static JsonElement Skill(string json) => JsonDocument.Parse(json).RootElement;
    private static readonly JsonElement Normal = Skill("""{"_iIsNormalAtk":1,"_iDistanceType":1}""");
    private static readonly JsonElement RangedSkill = Skill("""{"_iIsNormalAtk":0,"_iDistanceType":2}""");

    private static Fighter Player(params (int Id, long Value)[] stats)
    {
        var sheet = new StatSheet();
        foreach (var (id, value) in stats) sheet[id] = value;
        return new Fighter(100, 100, 0, 0, 10, 1) { Stats = sheet, Race = CombatService.PlayerRace, Size = CombatService.PlayerSize };
    }

    private static Fighter Monster(int race, int size, int element, bool boss = false, params (int Id, long Value)[] stats)
    {
        StatSheet? sheet = null;
        foreach (var (id, value) in stats) (sheet ??= new StatSheet())[id] = value;
        return new Fighter(100, 100, 0, 0, 10, element) { Race = race, Size = size, IsMonster = true, Boss = boss, Stats = sheet };
    }

    [Fact]
    public void NoStatsLeaveTheHitAlone()
    {
        Assert.Equal(1, CombatService.BonusFactor(Player(), Monster(4, 2, 5), Normal, false, 1));
        Assert.Equal(1.5, CombatService.CritFactor(Player(), Monster(4, 2, 5)));
    }

    [Fact]
    public void PlayerBonusesAgainstAMonsterMultiply()
    {
        // +20% vs Plant (404), +10% vs Medium (503), +10% Physical (211): 1.2 x 1.1 x 1.1
        var a = Player((404, 2000), (503, 1000), (211, 1000));
        Assert.Equal(1.452, CombatService.BonusFactor(a, Monster(4, 2, 5), Normal, false, 1), 6);
        // The same player against a Large Brute only keeps Physical.
        Assert.Equal(1.1, CombatService.BonusFactor(a, Monster(1, 3, 5), Normal, false, 1), 6);
        // Magic damage ignores the Physical bonus.
        Assert.Equal(1.32, CombatService.BonusFactor(a, Monster(4, 2, 5), Normal, true, 1), 6);
    }

    [Fact]
    public void ElementBossSkillAndRangeTerms()
    {
        // Fire damage +15% (304) on a Water monster (+10% vs Water monsters, 325), +30% vs boss (601).
        var a = Player((304, 1500), (325, 1000), (601, 3000), (603, 5000));
        Assert.Equal(1.15 * 1.1 * 1.3, CombatService.BonusFactor(a, Monster(4, 2, 5, boss: true), Normal, false, 4), 6);
        Assert.Equal(1.1 * 1.5, CombatService.BonusFactor(a, Monster(4, 2, 5), Normal, false, 1), 6); // neutral, non-boss
        // Skill Damage% (225) skips normal attacks; ranged Physical (229) only counts for ranged skills.
        var b = Player((225, 2000), (229, 1000), (227, 4000));
        Assert.Equal(1.4, CombatService.BonusFactor(b, Monster(4, 2, 5), Normal, false, 1), 6);
        Assert.Equal(1.2 * 1.1, CombatService.BonusFactor(b, Monster(4, 2, 5), RangedSkill, false, 1), 6);
    }

    [Fact]
    public void ReductionsCutAMonstersHitAndFloorAtZero()
    {
        // A Brute (race 1), Large, Fire monster hits a player with -10% from Brute (411), -20% from Large (506),
        // -25% from Fire monsters (334) and -50% Damage (216).
        var d = Player((411, 1000), (506, 2000), (334, 2500), (216, 5000));
        Assert.Equal(0.9 * 0.8 * 0.75 * 0.5, CombatService.BonusFactor(Monster(1, 3, 4), d, Normal, false, 1), 6);
        Assert.Equal(0, CombatService.BonusFactor(Monster(1, 3, 4), Player((216, 12000)), Normal, false, 1));
        // A debuffed monster (a live -20% Damage Reduction, i.e. 216 = -2000) takes more.
        Assert.Equal(1.2, CombatService.BonusFactor(Player(), Monster(4, 2, 5, false, (216, -2000)), Normal, false, 1), 6);
    }

    [Fact]
    public void PvPUsesTheDemiHumanRaceTerms()
    {
        var a = Player((402, 1000)); var d = Player((412, 500));
        Assert.Equal(1.05, CombatService.BonusFactor(a, d, Normal, false, 1), 6);
        // Size and boss terms are monster-only.
        Assert.Equal(1, CombatService.BonusFactor(Player((503, 3000), (603, 3000)), Player(), Normal, false, 1));
    }

    [Fact]
    public void CritAndHealTerms()
    {
        Assert.Equal(1.8, CombatService.CritFactor(Player((209, 3000)), Monster(4, 2, 5)), 6);
        Assert.Equal(1.6, CombatService.CritFactor(Player((209, 3000)), Player((210, 2000))), 6);
        Assert.Equal(1.2 * 1.1, CombatService.HealFactor(Player((207, 1500), (241, 500)), Player((208, 1000))), 6);
    }

    [Fact]
    public void The12xxCopiesAreTheirOwnFactors()
    {
        // Lex Aeterna (1216 -2000) on the monster, True Sight (1211 +5%) and Damage% (215 +10%) on the player.
        var a = Player((1211, 500), (215, 1000));
        Assert.Equal(1.2 * 1.05 * 1.1, CombatService.BonusFactor(a, Monster(4, 2, 5, false, (1216, -2000)), Normal, false, 1), 6);
        // Magic ignores the Physical copy; ranged Physical 1229 against 1230 only for ranged skills.
        Assert.Equal(1.1, CombatService.BonusFactor(a, Monster(4, 2, 5), Normal, true, 1), 6);
        Assert.Equal(0.9, CombatService.BonusFactor(Player((1229, 1000)), Monster(4, 2, 5, false, (1230, 2000)), RangedSkill, false, 1), 6);
        Assert.Equal(1, CombatService.BonusFactor(Player((1229, 1000)), Monster(4, 2, 5), Normal, false, 1), 6);
    }

    [Fact]
    public void TotemCoveredPartsAreNotCountedTwice()
    {
        var a = Player((211, 3000)) with { Covered = id => id == 211 ? 1000 : 0 };
        Assert.Equal(1.2, CombatService.BonusFactor(a, Monster(4, 2, 5), Normal, false, 1), 6);
    }
}
