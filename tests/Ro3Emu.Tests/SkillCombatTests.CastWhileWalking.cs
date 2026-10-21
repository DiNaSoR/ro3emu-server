using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private const ulong WalkForbidden = 4; // Lua_GameDefine.enum_ForbiddenState.Walk

    /// <summary>
    /// A skill pressed mid-walk: the client sends its position and the cast without stopping, and stops only on the
    /// server's StatusForbidden Walk (LC_Unit_Player.IsSkillBlockMove). The server never sent it, so the client kept
    /// walking and its next step cancelled the cast as a move.
    /// </summary>
    [ClientDataFact]
    public async Task ACastPressedMidWalkLocksMovementAndSurvivesTheStepsInFlight()
    {
        await using var alice = await InWorld("Alice");
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000_000; return Task.CompletedTask; });
        var start = Session("alice").Position.Clone();
        var nav = _app.Services.GetRequiredService<WorldData>().Map(Session("alice").SceneEntry)!.Nav!;
        var step = new[] { (0.4f, 0f), (-0.4f, 0f), (0f, 0.4f), (0f, -0.4f) }
            .Select(o => new Vec3 { X = start.X + o.Item1, Y = start.Y, Z = start.Z + o.Item2 })
            .First(p => nav.IsWalkable(p.X, p.Y, p.Z) && nav.CanWalk(p.X, p.Y, p.Z, start.X, start.Z));
        long self = await Loop.InvokeAsync(() => Task.FromResult(Session("alice").Role!.Id));

        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = start, Dir = 90, Face = 90, SpeedRate = 1 });
        await Task.Delay(100);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = target.Uid, ComboIndex = 1 });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var locked = await Next(alice, CombatService.AoiAttrChange, AOIAttrChange.Parser,
            a => a.EntityUuid == (ulong)self && a.ForbidInfo != null && (a.ForbidInfo.Data.Values[0] & WalkForbidden) != 0);
        Assert.NotNull(locked);

        // A step sent before the client heard of the cast, then its stop on the lock: neither cancels the cast.
        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = step, Dir = 90, Face = 90, SpeedRate = 1 });
        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = step, Face = 90, BStop = true, SpeedRate = 1 });
        await Task.Delay(50);
        await Loop.InvokeAsync(() =>
        {
            Assert.Equal(accepted.SessionId, Session("alice").SkillCast?.Id);
            Assert.False(Session("alice").SkillCast!.Cancelled);
            return Task.CompletedTask;
        });

        // The hit lifts the lock, so the player can walk off again.
        var released = await Next(alice, CombatService.AoiAttrChange, AOIAttrChange.Parser,
            a => a.EntityUuid == (ulong)self && a.ForbidInfo != null && (a.ForbidInfo.Data.Values[0] & WalkForbidden) == 0);
        Assert.NotNull(released);
        await Loop.InvokeAsync(() => { Assert.True(Session("alice").SkillCast?.Resolved ?? true); return Task.CompletedTask; });
    }
}
