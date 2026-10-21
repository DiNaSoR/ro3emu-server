using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>What a modified client may not get away with when it casts.</summary>
public sealed partial class SkillCombatTests
{
    /// <summary>
    /// A skill with no release target (_iRelease 2) still names a single target for its hit. The hit took any monster
    /// of the scene, so a client could name an MVP across the map. A target beyond the row's reach is now dropped.
    /// </summary>
    [ClientDataFact]
    public async Task AnUntargetedSkillCannotHitAMonsterAcrossTheMap()
    {
        const int skill = 1530601; // Rogue, group 15306 "elingweixie", _iRelease 2, _iRangeType 1, _kRange 5 m
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5400"); await Teach("alice", 15306); await Seasoned("alice");
        Combat.Rolls = new FixedRolls(9999);
        var session = Session("alice");
        await Loop.InvokeAsync(() => { session.GmNoCooldown = true; return Task.CompletedTask; });
        var start = session.Position.Clone();
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.X = start.X + 1; target.Z = start.Z; target.Hp = 1_000_000; return Task.CompletedTask; });

        // Next to the caster it hits.
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skill, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == skill && h.Dsts.Any(d => d.DstID == target.Uid));
        await Task.Delay(2500);

        // 40 m away the cast still goes out, but the target is gone from it.
        long hp = await Loop.InvokeAsync(() => { target.X = start.X + 40; target.Hp = 1_000_000; return Task.FromResult(target.Hp); });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skill, TargetId = target.Uid, TargetPos = target.Pos.Clone() });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Loop.InvokeAsync(() =>
        {
            Assert.Equal(0, session.SkillCast?.Request.TargetId ?? 0);
            if (session.SkillCast is { } cast) Assert.True(MonsterService.Distance(cast.Request.TargetPos.X, cast.Request.TargetPos.Z, start.X, start.Z) < 1);
            return Task.CompletedTask;
        });
        await Task.Delay(1500);
        Assert.Equal(hp, await Loop.InvokeAsync(() => Task.FromResult(target.Hp)));
    }

    /// <summary>
    /// Normal attacks yield to other skills at once, but cancelling one after its hit (or switching to another normal
    /// attack group) let a client swing several times per animation. The next normal attack waits for the swing.
    /// </summary>
    [ClientDataFact]
    public async Task ANormalAttackCannotBeCancelledAndRecastFasterThanItsSwing()
    {
        await using var alice = await InWorld("Alice");
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000_000; return Task.CompletedTask; });
        int normal = await Loop.InvokeAsync(() => Task.FromResult(_app.Services.GetRequiredService<SkillService>().NormalAttack(Session("alice"))));

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = normal, TargetId = target.Uid });
        var first = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, first.Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == first.SessionId);
        await alice.SendGameAsync(CombatService.SkillInterrupt, new SkillInterruptReq { InterruptSkillId = normal });
        Assert.True((await Next(alice, CombatService.SkillInterrupt, SkillInterruptResp.Parser)).IsInterruptSucc);

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = normal, TargetId = target.Uid });
        Assert.Equal(ErrorCodes.SkillBusy, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);

        // Once the swing's time has passed, the next one goes out.
        await Task.Delay(2500);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = normal, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }

    /// <summary>
    /// Every normal attack of a career counts as learned for the skill window, but only the one of the weapon in hand
    /// may be cast: a Hunter without a bow does not shoot (1300001, 18 m) instead of punching (1000001, 3 m).
    /// </summary>
    [ClientDataFact]
    public async Task OnlyTheNormalAttackOfTheWeaponInHandCanBeCast()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 3100");
        var session = Session("alice");
        await Loop.InvokeAsync(() => { session.Items.RemoveAll(i => i.Grid is 1 or 2); return Task.CompletedTask; });
        Assert.Equal(1000001, await Loop.InvokeAsync(() => Task.FromResult(_app.Services.GetRequiredService<SkillService>().NormalAttack(session))));
        var target = await Target("alice", stationary: true);

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1300001, TargetId = target.Uid });
        Assert.Equal(ErrorCodes.SkillNotLearned, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }

    /// <summary>A walk to NaN would pass every later "farther than" check (NaN compares false), so it is ignored.</summary>
    [ClientDataFact]
    public async Task AWalkToANonFinitePositionIsIgnored()
    {
        await using var alice = await InWorld("Alice");
        var session = Session("alice");
        var start = session.Position.Clone();
        foreach (float x in new[] { float.NaN, 1e9f })
        {
            await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = new Vec3 { X = x, Y = start.Y, Z = start.Z }, Face = 0, BStop = true, SpeedRate = 1 });
            await Task.Delay(500);
            var now = await Loop.InvokeAsync(() => Task.FromResult(session.Position.Clone()));
            Assert.True(now.X == start.X && now.Z == start.Z, $"walk to x={x} moved the player to {now}");
        }

        // The same walk to a real point next to the player moves it.
        var nav = _app.Services.GetRequiredService<WorldData>().Map(session.SceneEntry)!.Nav!;
        var step = new[] { (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f) }
            .Select(o => new Vec3 { X = start.X + o.Item1, Y = start.Y, Z = start.Z + o.Item2 })
            .First(p => nav.IsWalkable(p.X, p.Y, p.Z) && nav.CanWalk(start.X, start.Y, start.Z, p.X, p.Z));
        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = step, Face = 0, BStop = true, SpeedRate = 1 });
        await WaitFor(() => Loop.InvokeAsync(() => Task.FromResult(session.Position.X == step.X && session.Position.Z == step.Z)), "the real walk was not applied", 5);
    }
}
