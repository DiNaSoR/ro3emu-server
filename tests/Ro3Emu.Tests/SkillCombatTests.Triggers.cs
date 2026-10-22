using Romsg;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Learned and buff-borne triggers (CombatService.Triggers).</summary>
public sealed partial class SkillCombatTests
{
    [ClientDataFact]
    public async Task BlitzBeatJoinsBasicAttacksWithTheFalcon()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 3200"); await Teach("alice", 13101); await Teach("alice", 13102); await Seasoned("alice");
        await Lease(alice, FalconLease);
        Combat.Rolls = new FixedRolls(0);
        var (target, _) = await SwingAt(alice, "alice");
        var blitz = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId / 100 == 13110);
        Assert.Contains(blitz.Dsts, d => d.DstID == target.Uid && d.Damage > 0);
    }

    [ClientDataFact]
    public async Task CrimsonRockCallsDownAMeteorite()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 2400"); await Teach("alice", 12312); await Seasoned("alice");
        Combat.Rolls = new FixedRolls(0);
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1231201, TargetId = target.Uid, TargetPos = target.Pos.Clone() });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var meteor = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1235001);
        Assert.Contains(meteor.Dsts, d => d.DstID == target.Uid && d.Damage > 0);
    }

    [ClientDataFact]
    public async Task SoulDrainRestoresMpOnAKill()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 2200"); await Teach("alice", 12209);
        var monster = await Spawn("alice", t => t);
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(Session("alice"), null, 0));
        await Loop.InvokeAsync(async () => { foreach (var handler in Combat.Killed) await handler(Session("alice"), monster); });
        Assert.True(await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(Session("alice"))[2001])) > 0);
    }

    [ClientDataFact]
    public async Task RecoilStunsAnAttackerDuringAutoGuard()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1210"); await Teach("alice", 11416);
        Combat.Rolls = new FixedRolls(0);
        var monster = await Spawn("alice", t => t);
        await Buff("alice", 1140901); await Buff("alice", 1140301); // Shrink + Auto Guard
        await Loop.InvokeAsync(() => Combat.ReactToMonsterHitAsync(monster, Session("alice"), null, 100));
        await WaitFor(() => Loop.InvokeAsync(() => Task.FromResult(Buffs.UnitBuffs(monster).Any(b => b.ConfigId == 901601))), "no stun");
    }

}
