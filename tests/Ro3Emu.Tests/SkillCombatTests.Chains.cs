using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Skill chains (CombatService.Chains): Shadow Guard -> Thousand Shadows -> Shadow Loop, Shadow Echo's return.</summary>
public sealed partial class SkillCombatTests
{
    private async Task<int> Press(BotClient bot, int skill, MonsterUnit? target = null, Vec3? at = null, float face = 0)
    {
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        {
            SkillId = skill, TargetId = target?.Uid ?? 0, TargetPos = at?.Clone() ?? target?.Pos.Clone(), Face = face,
        });
        return (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result;
    }

    private async Task Idle(string name) => await WaitFor(() => Loop.InvokeAsync(() =>
        Task.FromResult(Session(name).SkillCast is not { } cast || cast.Resolved && Now() >= cast.StartMs + cast.ChantMs + cast.ActionMs)), "still casting", 5);

    private static long Now() => MonsterService.NowMs;

    [ClientDataFact]
    public async Task ShadowGuardTurnsIntoThousandShadowsThenShadowLoopThenBack()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5400"); await Teach("alice", 15323); await Seasoned("alice"); await Wield("alice", 2010120); // a dagger
        await Loop.InvokeAsync(() => { Session("alice").Progress!.SkillBar[5] = 15323; return Task.CompletedTask; });
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000; return Task.CompletedTask; });
        // Thousand Shadows is no skill of the tree: it cannot be cast before Shadow Guard offers it.
        Assert.NotEqual(0, await Press(alice, 1532201, target));

        Assert.Equal(0, await Press(alice, 1532301, target));
        var first = await Next(alice, CombatService.SkillSlotModify, SkillSlotModifyNotify.Parser, m => m.Slot == 5 && m.SkillId == 1532201);
        Assert.Equal(1532301, first.SourceSkillId);
        await Idle("alice");

        Assert.Equal(0, await Press(alice, 1532201, target));
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1532201);
        await Next(alice, CombatService.SkillSlotModify, SkillSlotModifyNotify.Parser, m => m.Slot == 5 && m.SkillId == 1532001);
        await Idle("alice");

        Assert.Equal(0, await Press(alice, 1532001, target));
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1532001);
        var back = await Next(alice, CombatService.SkillSlotModify, SkillSlotModifyNotify.Parser, m => m.Slot == 5 && m.SkillId == 1532301);
        Assert.Equal(1532001, back.SourceSkillId);
        // Spent: the follow-up is gone again.
        await Idle("alice");
        Assert.NotEqual(0, await Press(alice, 1532001, target));
    }

    [ClientDataFact]
    public async Task ShadowEchoReturnsToItsShadow()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5400"); await Teach("alice", 15329); await Seasoned("alice"); await Wield("alice", 2120120);
        await Loop.InvokeAsync(() => { Session("alice").Progress!.SkillBar[6] = 15329; return Task.CompletedTask; });
        var start = await Loop.InvokeAsync(() => Task.FromResult(Session("alice").Position.Clone()));
        Assert.Equal(0, await Press(alice, 1532901, face: 90));
        await Next(alice, CombatService.SkillSlotModify, SkillSlotModifyNotify.Parser, m => m.Slot == 6 && m.SkillId == 1533001);
        await Idle("alice");
        Assert.Equal(0, await Press(alice, 1533001, face: 90));
        await WaitFor(() => Loop.InvokeAsync(() =>
            Task.FromResult(MonsterService.Distance(start.X, start.Z, Session("alice").Position.X, Session("alice").Position.Z) < 0.1f)), "not back", 5);
        await Next(alice, CombatService.SkillSlotModify, SkillSlotModifyNotify.Parser, m => m.Slot == 6 && m.SkillId == 1532901);
    }
}
