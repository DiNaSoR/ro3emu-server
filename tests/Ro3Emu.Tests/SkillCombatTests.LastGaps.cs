using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Warg Hunt's forms, Swift Warg on the next step (CombatService.Wolves) and knock-ups.</summary>
public sealed partial class SkillCombatTests
{
    private async Task<MonsterUnit> MountedRanger(BotClient alice, params (int Group, int Level)[] taught)
    {
        await Gm("alice", "!job 3400");
        await Loop.InvokeAsync(() =>
        {
            foreach (var (group, level) in taught) Session("alice").Progress!.Skills[group] = level;
            return Task.CompletedTask;
        });
        await Seasoned("alice");
        await Lease(alice, WolfLease);
        await RideBattle(alice, true);
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000; return Task.CompletedTask; });
        return target;
    }

    [ClientDataFact]
    public async Task WargHuntBitesUpCloseAndDashesFromAfar()
    {
        await using var alice = await InWorld("Alice");
        var target = await MountedRanger(alice, (13310, 1), (13323, 1));
        // _kLogicData [1332901 melee, 1333001 ranged]; within ${5} = 5 m the melee form lands in place of the main hit.
        Assert.Equal(0, await CastOn(alice, 1332301, target));
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1332901);
        await Idle("alice");
        await Task.Delay(2600); // _iCD 2500

        Vec3 start = null!;
        await Loop.InvokeAsync(() =>
        {
            var s = Session("alice");
            start = s.Position.Clone();
            target.X = s.Position.X + 8; target.Z = s.Position.Z;
            return Task.CompletedTask;
        });
        Assert.Equal(0, await CastOn(alice, 1332301, target));
        // The ranged form's show carries the dash (Lv1 show 13330: 1333014; Lv5 133301: 13330113).
        var dash = await Next(alice, CombatService.SkillMove, SkillStartMoveNotify.Parser, m => m.SkillConfigId == 1333001);
        Assert.NotEqual(0, dash.EffectId);
        Assert.Equal(1333001, dash.SkillConfigId);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1333001);
        float moved = await Loop.InvokeAsync(() => Task.FromResult(MonsterService.Distance(start.X, start.Z, Session("alice").Position.X, Session("alice").Position.Z)));
        Assert.True(moved > 1, $"moved {moved} m");
    }

    [ClientDataFact]
    public async Task SwiftWargCastsOnTheNextStepAfterWargHunt()
    {
        await using var alice = await InWorld("Alice");
        var target = await MountedRanger(alice, (13310, 1), (13323, 1), (13326, 2));
        Assert.Equal(0, await CastOn(alice, 1332301, target));
        await Until(s => s.SwiftWargArmedMs > 0, "alice");
        await Idle("alice");

        var at = await Loop.InvokeAsync(() => Task.FromResult(Session("alice").Position.Clone()));
        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = at, Dir = 90, Face = 90, SpeedRate = 1 });
        var rsp = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser, r => r.IsAutoCast);
        Assert.Equal(0, rsp.Result);
        await Until(s => s.SwiftWargArmedMs == 0 && s.SkillCds.GetValueOrDefault(13326) > MonsterService.NowMs, "alice");
        await Until(s => s.Buffs.Any(b => b.ConfigId == 1332702), "alice"); // Swift Warg's Movement Speed
    }

    [ClientDataFact]
    public async Task SwiftWargLevelOneWaitsForItsButton()
    {
        await using var alice = await InWorld("Alice");
        var target = await MountedRanger(alice, (13310, 1), (13323, 1), (13326, 1));
        Assert.Equal(0, await CastOn(alice, 1332301, target));
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1332901);
        Assert.Equal(0, await Loop.InvokeAsync(() => Task.FromResult(Session("alice").SwiftWargArmedMs)));
    }

    [ClientDataFact]
    public async Task SonicBurstKnocksItsTargetsUp()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1400"); await Teach("alice", 11316); await Seasoned("alice"); await Wield("alice", 2050120);
        var target = await CastAt(alice, "alice", 1131601);
        await WaitFor(() => Loop.InvokeAsync(() => Task.FromResult(Buffs.UnitBuffs(target).Any(b => b.ConfigId == CombatService.KnockUpBuff))),
            "no knock-up", 5);
    }
}
