using Romsg;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>
    /// While a cast forbids walking, the client turns a move key or a ground click into a bare 31124
    /// (LC_UnitChecker.CheckClearCacheSkill) and keeps casting unless the server agrees. The click that places a
    /// ground spell can come back that way right after the cast starts; it must not drop the spell.
    /// </summary>
    [ClientDataFact]
    public async Task ThePlacingClickRightAfterAGroundCastDoesNotDropTheSpell()
    {
        await using var alice = await InWorld("Alice"); await using var bob = await InWorld("Bob");
        await Gm("alice", "!job 2100"); await Teach("alice", 12010); // Fire Wall 1201001: ground, Lock, 840 ms chant
        var point = Session("alice").Position.Clone();
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1201001, TargetPos = point });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);

        await alice.SendGameAsync(CombatService.SkillInterrupt, new SkillInterruptReq());
        Assert.False((await Next(alice, CombatService.SkillInterrupt, SkillInterruptResp.Parser)).IsInterruptSucc);
        await Loop.InvokeAsync(() =>
        {
            Assert.Equal(accepted.SessionId, Session("alice").SkillCast?.Id);
            Assert.False(Session("alice").SkillCast!.Cancelled);
            return Task.CompletedTask;
        });
        var spawn = await Next(bob, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, x => x.Bases.Any(b => b.EntityTemplateID == 1200004));
        Assert.Single(spawn.Bases, b => b.EntityTemplateID == 1200004);
    }

    /// <summary>A cast cancelled before its hit gives its cooldown back, so the spell can be cast again at once.</summary>
    [ClientDataFact]
    public async Task ACastCancelledBeforeItsHitRefundsItsCooldown()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 2100"); await Teach("alice", 12010);
        var player = Session("alice");
        async Task<CastSkillRsp> Cast()
        {
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1201001, TargetPos = player.Position.Clone() });
            return await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        }

        var first = await Cast();
        Assert.Equal(0, first.Result);
        var started = await Next(alice, CombatService.SkillCdUpdate, SkillCdNotify.Parser);
        var cd = Assert.Single(started.Infos);
        Assert.True(cd.CdEndTimestampMs > cd.CdStartTimestampMs);
        int group = (int)cd.SkillId, mode = (int)cd.SkillMode;

        await alice.SendGameAsync(CombatService.SkillInterrupt, new SkillInterruptReq { InterruptSkillId = 1201001 });
        Assert.True((await Next(alice, CombatService.SkillInterrupt, SkillInterruptResp.Parser)).IsInterruptSucc);
        var refunded = await Next(alice, CombatService.SkillCdUpdate, SkillCdNotify.Parser);
        var back = Assert.Single(refunded.Infos);
        Assert.Equal(cd.SkillId, back.SkillId);
        Assert.True(back.CdEndTimestampMs <= back.CdStartTimestampMs);
        Assert.True(refunded.NotPlayCdRefreshEffect);
        await Loop.InvokeAsync(() =>
        {
            Assert.False(player.SkillModeCds.ContainsKey((group, mode)));
            Assert.False(player.SkillCds.ContainsKey(group));
            return Task.CompletedTask;
        });

        var second = await Cast();
        Assert.Equal(0, second.Result);
        // A cast that lands keeps its cooldown.
        await Next(alice, CombatService.SkillCdUpdate, SkillCdNotify.Parser);
        await Task.Delay(1500); // chant 840 + action
        await Loop.InvokeAsync(() =>
        {
            Assert.True(Math.Max(player.SkillCds.GetValueOrDefault(group), player.SkillModeCds.GetValueOrDefault((group, mode))) > MonsterService.NowMs);
            return Task.CompletedTask;
        });
    }

    /// <summary>Moving after the first moments of a chant cancels it, and the cancelled cast's cooldown comes back.</summary>
    [ClientDataFact]
    public async Task MovingDuringAGroundChantCancelsItWithoutCooldown()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 2100"); await Teach("alice", 12010); // Fire Wall 1201001: 840 ms chant
        var player = Session("alice");
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1201001, TargetPos = player.Position.Clone() });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var cd = Assert.Single((await Next(alice, CombatService.SkillCdUpdate, SkillCdNotify.Parser)).Infos);

        await Task.Delay(CombatService.MoveCancelGraceMs + 100);
        await alice.SendGameAsync(CombatService.SkillInterrupt, new SkillInterruptReq());
        Assert.True((await Next(alice, CombatService.SkillInterrupt, SkillInterruptResp.Parser)).IsInterruptSucc);
        var end = await Next(alice, CombatService.SkillEnd, SkillEndNotify.Parser, x => x.SessionId == accepted.SessionId);
        Assert.Equal(SkillInterruptedInfo.Types.SkillInterruptType.Move, end.InterruptedInfo.Type);
        var back = Assert.Single((await Next(alice, CombatService.SkillCdUpdate, SkillCdNotify.Parser)).Infos);
        Assert.Equal(cd.SkillId, back.SkillId);
        Assert.True(back.CdEndTimestampMs <= back.CdStartTimestampMs);
        await Loop.InvokeAsync(() => { Assert.Null(player.SkillCast); return Task.CompletedTask; });
    }
}
