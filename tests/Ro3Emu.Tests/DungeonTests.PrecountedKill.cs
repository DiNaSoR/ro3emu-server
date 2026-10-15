using System.Text.Json;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Romsg;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    [ClientDataFact]
    public async Task TerminalFreezeDoesNotCountPrecountedPublishedKillTwice()
    {
        var request = ConfigureTwoActorStory();
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        foreach (var bot in new[] { alice, bob }) await Gm(bot, "!lvl 99");
        await Party(alice, bob);
        Assert.Equal(0, await Enter(alice, request));
        await bob.SendGameAsync(DungeonService.Confirm, new ConfirmTeamDungeonReq { IsAgree = true });
        Assert.Equal(0, (await Next(bob, DungeonService.Confirm, ConfirmTeamDungeonRsp.Parser)).Result);
        long room = await Load(alice, request.SceneEntry);
        Assert.Equal(room, await Load(bob, request.SceneEntry));
        MonsterUnit[] actors = [];
        for (int i = 0; i < 100; i++)
        {
            actors = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(Service<World>().AllUnits()
                .OfType<MonsterUnit>().Where(m => m.InstanceId == room).OrderBy(m => m.Uid).ToArray()));
            if (actors.Length == 2) break;
            await Task.Delay(20);
        }
        Assert.Equal(2, actors.Length);
        await OnLoop(async () =>
        {
            foreach (var (bot, actor) in new[] { (alice, actors[0]), (bob, actors[1]) })
            {
                await Service<SceneService>().TeleportInMapAsync(Session(bot),
                    new MapPoint(actor.X + 1, actor.Y, actor.Z, actor.Face));
                Service<MonsterService>().Engage(actor, Session(bot));
                actor.Hp = 1;
            }
        });

        var combat = Service<CombatService>();
        var dungeon = Service<DungeonService>();
        var firstDurable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var precounted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFreeze = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<GameSession, MonsterUnit, JsonElement, long, Task> holdFirst = (s, m, _, _) =>
        {
            if (s.Role?.Id != (long)alice.Self!.Uuid || !ReferenceEquals(m, actors[0]))
                return Task.CompletedTask;
            firstDurable.TrySetResult();
            return releaseFirst.Task;
        };
        Func<GameSession, MonsterUnit, Task> observeFirst = (_, m) =>
        {
            if (ReferenceEquals(m, actors[0])) firstObserved.TrySetResult();
            return Task.CompletedTask;
        };
        combat.DealtAsync.Add(holdFirst);
        combat.Killed.Add(observeFirst);
        dungeon.AfterTerminalKillsPrecountedForTest = attempt =>
        {
            if (attempt.Room.Id != room) return Task.CompletedTask;
            precounted.TrySetResult();
            return releaseFreeze.Task;
        };
        try
        {
            await alice.SendGameAsync(CombatService.UseSkill,
                new CastSkillReq { SkillId = 1000001, TargetId = actors[0].Uid });
            Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            await firstDurable.Task.WaitAsync(Wait); // E04 committed, before the first lethal hit/death packet
            await bob.SendGameAsync(CombatService.UseSkill,
                new CastSkillReq { SkillId = 1000001, TargetId = actors[1].Uid });
            Assert.Equal(0, (await Next(bob, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            await precounted.Task.WaitAsync(Wait); // both kills counted, report not yet serialized
            var attempt = Session(alice).DungeonAttempt!;
            Assert.Contains(actors[0].Uid, attempt.Precounted);
            Assert.Contains(actors[1].Uid, attempt.Precounted);
            Assert.False(attempt.ObligationsFrozen);

            releaseFirst.TrySetResult();
            await firstObserved.Task.WaitAsync(Wait); // Alice's Killed callback removed her precount marker
            Assert.DoesNotContain(actors[0].Uid, attempt.Precounted);
            Assert.Contains(actors[1].Uid, attempt.Precounted);
            Assert.Equal(1, attempt.Stats[(long)alice.Self!.Uuid].KillNpcData[5300036].LongValue);

            releaseFreeze.TrySetResult();
            var firstHit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
                hit => hit.SrcID == (long)alice.Self!.Uuid && hit.Dsts.Any(d => d.DstID == actors[0].Uid && d.IsDead));
            var secondHit = await Next(bob, CombatService.SkillHit, SkillHitInfo.Parser,
                hit => hit.SrcID == (long)bob.Self!.Uuid && hit.Dsts.Any(d => d.DstID == actors[1].Uid && d.IsDead));
            foreach (var bot in new[] { alice, bob })
                Assert.Equal((DungeonRes)1, (await Next(bot, DungeonService.Result, DungeonResNotify.Parser)).Res);
            var report = DungeonStatisticsInfoWithDungeon.Parser.ParseFrom(Session(alice).Dungeons!.Statistics[room]);
            var stats = report.SceneStatistics[room].RoleStatistics;
            Assert.Equal(1, stats[(long)alice.Self!.Uuid].KillNpcData[5300036].LongValue);
            Assert.Equal(1, stats[(long)bob.Self!.Uuid].KillNpcData[5300036].LongValue);
            Assert.Equal(firstHit.Dsts.Single(d => d.DstID == actors[0].Uid).Damage,
                stats[(long)alice.Self!.Uuid].TotalDamage);
            Assert.Equal(secondHit.Dsts.Single(d => d.DstID == actors[1].Uid).Damage,
                stats[(long)bob.Self!.Uuid].TotalDamage);
        }
        finally
        {
            releaseFirst.TrySetResult();
            releaseFreeze.TrySetResult();
            dungeon.AfterTerminalKillsPrecountedForTest = null;
            combat.DealtAsync.Remove(holdFirst);
            combat.Killed.Remove(observeFirst);
        }
    }
}
