using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Skills that cast other skills: chance procs, follow-up hits, Lex Aeterna and the parry counters.</summary>
public sealed partial class SkillCombatTests
{
    private BuffService Buffs => _app.Services.GetRequiredService<BuffService>();

    private async Task<(MonsterUnit Target, CastSkillRsp Reply)> SwingAt(BotClient bot, string name)
    {
        var target = await Target(name, stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000; return Task.CompletedTask; });
        int attack = _app.Services.GetRequiredService<SkillService>().NormalAttack(Session(name));
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = attack, TargetId = target.Uid });
        var reply = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, reply.Result);
        return (target, reply);
    }

    private Task Buff(string name, int id, int ms = 60000) =>
        Loop.InvokeAsync(() => Buffs.AddAsync(Session(name), id, ms, Session(name).Role!.Id));

    [ClientDataFact]
    public async Task EnvenomBuffProcsPoisonedOnBasicAttacks()
    {
        await using var alice = await InWorld("Alice");
        Combat.Rolls = new FixedRolls(0);
        await Buff("alice", 1512010); // Envenom Lv10: 30% on a basic attack, cast Poisoned 1511610
        var (target, _) = await SwingAt(alice, "alice");
        var poison = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1511610);
        Assert.Contains(poison.Dsts, d => d.DstID == target.Uid && d.Damage > 0);
        await Next(alice, CombatService.ExtraUseInSkill, UseInSkillInfo.Parser, u => u.SkillID == 1511610 && u.SessionId == poison.SessionId);
    }

    [ClientDataFact]
    public async Task AFailedRollCastsNothing()
    {
        await using var alice = await InWorld("Alice");
        Combat.Rolls = new FixedRolls(9999);
        await Buff("alice", 1512010);
        var (target, reply) = await SwingAt(alice, "alice");
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == reply.SessionId);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId != reply.SessionId && h.Dsts.Any(d => d.DstID == target.Uid));
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).SkillId == 1511610);
    }

    [ClientDataFact]
    public async Task EnvenomHitsAgainWithPoisoned()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5100"); await Teach("alice", 15005);
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1500501, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1500501);
        var poison = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1511601);
        Assert.Contains(poison.Dsts, d => d.DstID == target.Uid && d.Damage > 0);
    }

    [ClientDataFact]
    public async Task DoubleHolyLightFollowsJudexAndHolySpiritSurgeMakesItCertain()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 4400"); await Teach("alice", 14309); await Teach("alice", 14311);
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000; return Task.CompletedTask; });
        // Lv1 rolls 30% each: a roll of 40% misses both...
        Combat.Rolls = new FixedRolls(4000);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1430901, TargetId = target.Uid });
        var first = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, first.Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == first.SessionId);
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).SkillId is 1431601 or 1431701);
        // ...until Holy Spirit Surge sets both to 100%.
        await Buff("alice", 1431201);
        await Loop.InvokeAsync(() => { Session("alice").SkillCds.Clear(); Session("alice").SkillModeCds.Clear(); return Task.CompletedTask; });
        await Task.Delay(1500);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1430901, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1431601);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1431701);
    }

    [ClientDataFact]
    public async Task LexAeternaRaisesTheNextHitAndIsConsumed()
    {
        await using var alice = await InWorld("Alice");
        Combat.Rolls = new FixedRolls(9999);
        var (target, reply) = await SwingAt(alice, "alice");
        var plain = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == reply.SessionId);
        long before = plain.Dsts.Single(d => d.DstID == target.Uid).Damage;
        await Loop.InvokeAsync(() => Buffs.ShowOnUnitAsync(target, 1410201, 10000, Session("alice").Role!.Id));
        var lex = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SessionId > reply.SessionId && h.Dsts.Any(d => d.DstID == target.Uid));
        long boosted = lex.Dsts.Single(d => d.DstID == target.Uid).Damage;
        Assert.InRange(boosted, (long)Math.Floor(before * 1.2) - 1, (long)Math.Ceiling(before * 1.2) + 1);
        await Loop.InvokeAsync(() => { Assert.Empty(Buffs.UnitBuffs(target)); return Task.CompletedTask; });
    }

    [ClientDataFact]
    public async Task ParryCountersAMeleeHitOncePerSecond()
    {
        await using var alice = await InWorld("Alice");
        Combat.Rolls = new FixedRolls(9999);
        await Buff("alice", 1121010); // Parry Lv10: Auto Counter 1121110, 1 s internal cooldown
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(async () =>
        {
            target.Hp = 1_000_000;
            await Combat.ReactToMonsterHitAsync(target, Session("alice"), null, 100);
            await Combat.ReactToMonsterHitAsync(target, Session("alice"), null, 100); // inside the cooldown
        });
        var counter = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1121110);
        Assert.Contains(counter.Dsts, d => d.DstID == target.Uid && d.Damage > 0);
        await Task.Delay(300);
        Assert.Single(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).SkillId == 1121110);
        // A ranged hit does not count.
        await Loop.InvokeAsync(async () =>
        {
            Session("alice").ProcState.Clear();
            var ranged = System.Text.Json.JsonDocument.Parse("""{"_iDistanceType":2,"_iDmgType":1}""").RootElement;
            await Combat.ReactToMonsterHitAsync(target, Session("alice"), ranged, 100);
        });
        await Task.Delay(300);
        Assert.Single(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).SkillId == 1121110);
    }

    [ClientDataFact]
    public async Task DeathBoundReflectsTheDamageTaken()
    {
        await using var alice = await InWorld("Alice");
        Combat.Rolls = new FixedRolls(9999);
        await Buff("alice", 1131105); // Death Bound Lv5: 100% of the melee damage taken
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(async () =>
        {
            target.Hp = 1_000_000;
            await Combat.ReactToMonsterHitAsync(target, Session("alice"), null, 777);
        });
        var reflect = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1138105);
        Assert.Equal(777, reflect.Dsts.Single(d => d.DstID == target.Uid).Damage);
    }

    [ClientDataFact]
    public async Task AutoCounterStrikesWhenTheParryEndsHarderAfterAHit()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1100"); await Teach("alice", 11105);
        Combat.Rolls = new FixedRolls(9999);
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1110501, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var plain = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1110601);
        long first = plain.Dsts.Single(d => d.DstID == target.Uid).Damage;

        for (int i = 0; i < 100 && await Loop.InvokeAsync(() => Task.FromResult(Session("alice").SkillCast != null)); i++) await Task.Delay(20);
        await Loop.InvokeAsync(() => { Session("alice").SkillCds.Clear(); Session("alice").SkillModeCds.Clear(); return Task.CompletedTask; });
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(Session("alice"), null, Character.Stats(Session("alice"))[102]));
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1110501, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        for (int i = 0; i < 100 && !await Loop.InvokeAsync(() => Task.FromResult(Session("alice").Buffs.Any(b => b.ConfigId == 1110501))); i++)
            await Task.Delay(20);
        await Loop.InvokeAsync(() => Combat.ReactToMonsterHitAsync(target, Session("alice"), null, 50));
        var parried = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1110601 && h.SessionId != plain.SessionId);
        long second = parried.Dsts.Single(d => d.DstID == target.Uid).Damage;
        Assert.InRange(second, (long)Math.Floor(first * 1.5) - 1, (long)Math.Ceiling(first * 1.5) + 1);
    }
    private async Task<MonsterUnit> CastAt(BotClient bot, string name, int skill)
    {
        var target = await Target(name, stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000; return Task.CompletedTask; });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skill, TargetId = target.Uid });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        return target;
    }

    [ClientDataFact]
    public async Task FearBreezeProcsOnATargetedBowSkill()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 3400"); await Teach("alice", 13305);
        Combat.Rolls = new FixedRolls(0);
        await Loop.InvokeAsync(() =>
        {
            var s = Session("alice");
            s.Items.RemoveAll(i => i.Grid == 1);
            s.Items.Add(new InvItem { Guid = 9_000_001, Entry = 2110120, Stack = 1, Pack = 1, Grid = 1 }); // a bow
            return Task.CompletedTask;
        });
        await Buff("alice", 1330105); // Fear Breeze Lv5: 30%, cast Fear Breeze 1330405
        var target = await CastAt(alice, "alice", 1330501);
        var arrows = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1330405);
        Assert.Contains(arrows.Dsts, d => d.DstID == target.Uid && d.Damage > 0);
    }

    [ClientDataFact]
    public async Task FocusedArrowStrikeSendsTheWargEveryFewSeconds()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 3400"); await Teach("alice", 13305); await Teach("alice", 13312); await Wield("alice", 2110120);
        var target = await CastAt(alice, "alice", 1330501);
        var warg = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1331201);
        Assert.Contains(warg.Dsts, d => d.DstID == target.Uid);
        // Warg Strike Lv1's "every 2.5 seconds".
        await Loop.InvokeAsync(() =>
        {
            Assert.InRange(Session("alice").ProcState[13312] - MonsterService.NowMs, 1000, 2500);
            return Task.CompletedTask;
        });
    }
    [ClientDataFact]
    public async Task JupitelThunderLandsEachBoltAsItsOwnHit()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 2200"); await Teach("alice", 12102);
        Combat.Rolls = new FixedRolls(9999);
        // Lv1 shows 10 bolts 250 ms apart (_kShowDmg [10], bullet 1210202 m_fBulletHatchInterval 250), and its hit
        // effect 1210203 does not spread one hit's numbers, so each bolt is a hit of its own.
        var target = await CastAt(alice, "alice", 1210201);
        var first = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1210201);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long total = first.Dsts.Single(d => d.DstID == target.Uid).Damage;
        for (int i = 1; i < 10; i++)
            total += (await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == first.SessionId))
                .Dsts.Single(d => d.DstID == target.Uid).Damage;
        Assert.InRange(clock.ElapsedMilliseconds, 1900, 4000); // nine more bolts, 250 ms apart
        Assert.Equal(0, first.Split);
        await Loop.InvokeAsync(() => { Assert.Equal(1_000_000 - total, target.Hp); return Task.CompletedTask; });
    }
    [ClientDataFact]
    public async Task MeteorStormStrikesOncePerMeteor()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 2200"); await Teach("alice", 12106);
        Combat.Rolls = new FixedRolls(9999);
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000; return Task.CompletedTask; });
        // Lv1: "strikes in 2 waves" ([npc 1200014, 300 ms, 1600 ms, 550, 550, 2]).
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1210601, TargetId = target.Uid, TargetPos = target.Pos.Clone() });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1210601);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1210601);
        await Task.Delay(2500); // the shower's 1.6 s are over
        Assert.Equal(2, alice.Inbox.Snapshot().Count(e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).SkillId == 1210601));
    }
}
