using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Psychokinetic Astral Body (CombatService.Astral).</summary>
public sealed partial class SkillCombatTests
{
    [ClientDataFact]
    public async Task AstralBodiesGatherAndLaunchWithSoulExpansion()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 2400"); await Teach("alice", 12343); await Teach("alice", 12302); await Seasoned("alice");
        // "Auto-generating 1 ... every 1.5 seconds, up to 3"
        await WaitFor(() => Loop.InvokeAsync(() => Task.FromResult(Session("alice").Buffs.Any(b => b.ConfigId == 1236101 && b.PileCount == 3))), "no 3 orbs", 10);
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000; return Task.CompletedTask; });
        var soul = Data.Row("SkillConfig", 1230201)!.Value;
        await Loop.InvokeAsync(async () => { foreach (var handler in Combat.DealtAsync) await handler(Session("alice"), target, soul, 100); });
        var launch = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1234501);
        Assert.Contains(launch.Dsts, d => d.DstID == target.Uid && d.Damage > 0);
        Assert.False(await HasBuff("alice", 1236101));
    }
}
