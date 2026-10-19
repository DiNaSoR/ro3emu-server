using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Protection and detection skills (CombatService.Guard, BuffService.Immunity).</summary>
public sealed partial class SkillCombatTests
{
    private static async Task<int> CastSelf(BotClient bot, int skill, long self)
    {
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skill, TargetId = self });
        return (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result;
    }

    private async Task WaitFor(Func<Task<bool>> condition, string what, int seconds = 8)
    {
        for (var until = DateTime.UtcNow.AddSeconds(seconds); DateTime.UtcNow < until; await Task.Delay(50))
            if (await condition()) return;
        Assert.Fail(what);
    }

    [ClientDataFact]
    public async Task DevotionPassesItsShareToTheCrusader()
    {
        await using var alice = await InWorld("Alice"); await using var bob = await InWorld("Bob");
        await Gm("alice", "!job 1210"); await Teach("alice", 11413); await Seasoned("alice");
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1141301, TargetId = Session("bob").Role!.Id });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Until(s => s.Buffs.Any(b => b.ConfigId == 1150401), "bob"); // "The Crusader takes 12% of the damage you receive"
        Assert.False(await HasBuff("bob", 1150901));
        Assert.True(await HasBuff("alice", 1150901)); // Devotion Self-Link on the Crusader
        long before = await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(Session("alice"))[2000]));
        long kept = await Loop.InvokeAsync(() => Combat.ShareDamageAsync(Session("bob"), 1000));
        Assert.Equal(880, kept);
        await Until(s => Character.Stats(s)[2000] == before - 120, "alice");
    }

    [ClientDataFact]
    public async Task KingsGraceLinksAlliesAndGuardsThem()
    {
        await using var alice = await InWorld("Alice"); await using var bob = await InWorld("Bob");
        await Gm("alice", "!job 1410"); await Teach("alice", 11612); await Seasoned("alice"); await Wield("alice", 2500120, 2);
        var monster = await Spawn("alice", t => t);
        Assert.Equal(0, await CastSelf(alice, 1161201, Session("alice").Role!.Id));
        await Until(s => s.Buffs.Any(b => b.ConfigId == 1161401), "alice");
        Assert.False(await HasBuff("bob", 1161401)); // the auras are the caster's own
        await Until(s => s.Buffs.Any(b => b.ConfigId == 1161501), "bob"); // "The Crusader takes 50% of the damage you receive"
        Assert.Equal(200, await Loop.InvokeAsync(() => Task.FromResult(Combat.KingsGraceRecovery(Session("alice"), 1000)))); // 20%
        Assert.Equal(500, await Loop.InvokeAsync(() => Combat.ShareDamageAsync(Session("bob"), 1000)));
        await Loop.InvokeAsync(() => Buffs.AddAsync(Session("bob"), 902501, 5000, monster.Uid)); // Debuff Immunity
        Assert.False(await HasBuff("bob", 902501));
        // Enemies in the orbit gain Slow every 0.5 s; five piles become Stun.
        await WaitFor(() => Loop.InvokeAsync(() => Task.FromResult(Buffs.UnitBuffs(monster).Any(b => b.ConfigId == 901601))), "no stun");
    }

    [ClientDataFact]
    public async Task HymnOfTheLambStopsThreeStatusEffects()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 4400"); await Teach("alice", 14302); await Seasoned("alice");
        var monster = await Spawn("alice", t => t);
        Assert.Equal(0, await CastSelf(alice, 1430201, Session("alice").Role!.Id));
        await Until(s => s.Buffs.Any(b => b.ConfigId == 1430201), "alice");
        int[] statuses = [902501, 902301, 901601]; // Blind, Silence, Stun
        foreach (int status in statuses)
        {
            await Loop.InvokeAsync(() => Buffs.AddAsync(Session("alice"), status, 5000, monster.Uid));
            Assert.False(await HasBuff("alice", status));
        }
        Assert.False(await HasBuff("alice", 1430201)); // "up to 3": the last chance ends it
        await Loop.InvokeAsync(() => Buffs.AddAsync(Session("alice"), 902501, 5000, monster.Uid));
        Assert.True(await HasBuff("alice", 902501));
    }

    [ClientDataFact]
    public async Task UnionCountsTheCrusaders()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1410"); await Teach("alice", 11614);
        await Until(s => s.Buffs.Any(b => b.ConfigId == 1162701 && b.PileCount == 1), "alice"); // PATK/PDEF/MDEF +10
    }

    [ClientDataFact]
    public async Task MeltDownWeakensMonsters()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 6300"); await Teach("alice", 16203); await Seasoned("alice");
        Combat.Rolls = new FixedRolls(0);
        var monster = await Spawn("alice", t => t);
        Assert.Equal(0, await CastSelf(alice, 1620301, Session("alice").Role!.Id));
        await Until(s => s.Buffs.Any(b => b.ConfigId == 1620301), "alice");
        var row = Data.Row("SkillConfig", 1620301)!.Value;
        await Loop.InvokeAsync(async () => { foreach (var handler in Combat.DealtAsync) await handler(Session("alice"), monster, row, 100); });
        await Loop.InvokeAsync(() =>
        {
            Assert.Contains(Buffs.UnitBuffs(monster), b => b.ConfigId == 1620501);
            long hits = Combat.MonsterDamageAdjusters.Aggregate(1000L, (d, f) => f(monster, d, true));
            long taken = Combat.MonsterDamageAdjusters.Aggregate(1000L, (d, f) => f(monster, d, false));
            Assert.Equal(950, hits); // "reduce its PATK and DEF by 5%"
            Assert.Equal(1050, taken);
            return Task.CompletedTask;
        });
    }

    [ClientDataFact]
    public async Task MeltDownDamagesAnEnemyPlayersWeapon()
    {
        await using var alice = await InWorld("Alice"); await using var bob = await InWorld("Bob");
        await Gm("alice", "!job 6300"); await Teach("alice", 16203); await Seasoned("alice");
        await Wield("bob", 2050120);
        Combat.Rolls = new FixedRolls(0);
        Assert.Equal(0, await CastSelf(alice, 1620301, Session("alice").Role!.Id));
        await Until(s => s.Buffs.Any(b => b.ConfigId == 1620301), "alice");
        long armed = await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(Session("bob"))[101]));
        await Loop.InvokeAsync(async () => { foreach (var handler in Combat.PvpDealtAsync) await handler(Session("alice"), Session("bob"), 100, false); });
        Assert.True(await HasBuff("bob", 1620401)); // "Weapon damaged. All Main/Off-Hand Weapon stats and Affixes are not Active."
        long damaged = await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(Session("bob"))[101]));
        Assert.True(damaged < armed, $"{damaged} < {armed}"); // the spear's Max HP is gone
        await Loop.InvokeAsync(() => Buffs.RemoveAsync(Session("bob"), Session("bob").Buffs.First(b => b.ConfigId == 1620401)));
        Assert.Equal(armed, await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(Session("bob"))[101])));
    }

    [ClientDataFact]
    public async Task HymnOfTheLambCannotBeCastWhileControlled()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 4400"); await Teach("alice", 14302); await Seasoned("alice");
        var monster = await Spawn("alice", t => t);
        await Loop.InvokeAsync(() => Buffs.AddAsync(Session("alice"), 901901, 5000, monster.Uid)); // Fear: control (25), casts allowed
        Assert.True(await HasBuff("alice", 901901));
        Assert.Equal(ErrorCodes.SkillBadState, await CastSelf(alice, 1430201, Session("alice").Role!.Id));
    }

    [ClientDataFact]
    public async Task SightAndRuwachUncoverHiddenEnemies()
    {
        await using var alice = await InWorld("Alice"); await using var bob = await InWorld("Bob");
        Combat.PlayerHostile = (_, _) => true;
        await Gm("alice", "!job 2100"); await Teach("alice", 12002); await Seasoned("alice");
        await Loop.InvokeAsync(() => Buffs.AddAsync(Session("bob"), 5000901, 30000, Session("bob").Role!.Id)); // Hiding
        await Until(s => s.Stealthed, "bob");
        Assert.Equal(0, await CastSelf(alice, 1200201, Session("alice").Role!.Id));
        await Until(s => !s.Stealthed, "bob");

        await Until(s => s.SkillCast == null, "alice"); // Sight's action time, else the next cast is busy (3005)
        await Gm("alice", "!job 4100"); await Teach("alice", 14005);
        await Loop.InvokeAsync(() => Buffs.AddAsync(Session("bob"), 5000901, 30000, Session("bob").Role!.Id));
        await Until(s => s.Stealthed, "bob");
        await Loop.InvokeAsync(() =>
        {
            foreach (var b in Session("alice").Buffs.Where(b => b.ConfigId == 1201201).ToArray()) Session("alice").Buffs.Remove(b);
            Session("alice").SkillCds.Clear(); Session("alice").SkillModeCds.Clear();
            return Task.CompletedTask;
        });
        Assert.Equal(0, await CastSelf(alice, 1400501, Session("alice").Role!.Id));
        await Until(s => !s.Stealthed, "bob");
        await Until(s => !s.Buffs.Any(b => b.ConfigId == 1400501), "alice"); // "disappears after Discovering a target"
    }

    [ClientDataFact]
    public void HigherLevelAurasReuseTheirFirstLevelHalo()
    {
        var halos = BuffRuleTables.For(Data).Halos;
        Assert.Equal([1161505], halos[1161405].Applied);
        Assert.Equal(halos[1161401].Radius, halos[1161405].Radius);
        Assert.True(halos[1162005].Hostile);
    }
}
