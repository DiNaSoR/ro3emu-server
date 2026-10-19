using Romsg;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Delayed hits and the Madogear's Firepower (CombatService.Delayed).</summary>
public sealed partial class SkillCombatTests
{
    [ClientDataFact]
    public async Task VenomSplasherBlowsUpWhenItsTimerEnds()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5100"); await Teach("alice", 15110); await Seasoned("alice");
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000; return Task.CompletedTask; });
        Assert.Equal(0, await CastOn(alice, 1511001, target));
        await WaitFor(() => Loop.InvokeAsync(() => Task.FromResult(Buffs.UnitBuffs(target).Any(b => b.ConfigId == 1511901))), "no dynamite");
        Assert.Equal(1_000_000, target.Hp); // planting deals nothing
        var bomb = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1511301);
        Assert.Contains(bomb.Dsts, d => d.DstID == target.Uid && d.Damage > 0);
    }

    [ClientDataFact]
    public async Task EnergyReleaseCountsDownThenBlasts()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 6400"); await Teach("alice", 16309); await Teach("alice", 16317); await Seasoned("alice");
        await Lease(alice, MadogearLease); await RideBattle(alice, true);
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 100_000_000; return Task.CompletedTask; });
        long maxHp = await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(Session("alice"))[101]));
        Assert.Equal(0, await CastOn(alice, 1631701, target));
        await Until(s => s.Buffs.Any(b => b.ConfigId == 1634401 && b.ShieldRemaining > 0), "alice"); // HP turned into a shield
        Assert.True(await HasBuff("alice", 903201)); // Immunity during the countdown
        Assert.Equal(100_000_000, target.Hp);
        long hp = await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(Session("alice"))[2000]));
        Assert.True(hp <= maxHp / 2 + 1, $"{hp} of {maxHp}");
        var blast = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1635201);
        Assert.Contains(blast.Dsts, d => d.DstID == target.Uid && d.Damage > 0);
        await Until(s => Character.Stats(s)[2000] < hp, "alice"); // "You then lose 50% of your current Max HP"
    }

    [ClientDataFact]
    public async Task FifteenVulcanArmsLoadThreeEightShellArmCannons()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 6400"); await Teach("alice", 16309); await Teach("alice", 16313); await Teach("alice", 16312); await Seasoned("alice");
        await Lease(alice, MadogearLease); await RideBattle(alice, true);
        for (int i = 0; i < 15; i++) await Buff("alice", 1631301, 15000);
        await Until(s => s.Buffs.Any(b => b.ConfigId == 1632001), "alice");
        Assert.False(await HasBuff("alice", 1631301));
        Assert.Equal(1631211, await Loop.InvokeAsync(() => Task.FromResult((int)Ro3Emu.Server.Data.GameData.Int(Combat.EffectiveSkillRow(Session("alice"), 1631201)!.Value, "_iID"))));
        var cannon = Data.Row("SkillConfig", 1631211)!.Value;
        for (int i = 0; i < 3; i++)
            await Loop.InvokeAsync(async () => { foreach (var handler in Combat.CastLanded) await handler(Session("alice"), cannon); });
        Assert.False(await HasBuff("alice", 1632001));
        Assert.False(await HasBuff("alice", 1631701));
    }
}
