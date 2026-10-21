using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Stats the server now reads (Cast Speed 116, 1205/1206/1207, 243) and the new knockbacks and pulls.</summary>
public sealed partial class SkillCombatTests
{
    [Fact]
    public void CastSpeedShortensOnlyTheVariableChant()
    {
        Assert.Equal(1, CombatService.ChantFactor(0));
        Assert.Equal(0, CombatService.ChantFactor(530));
        Assert.Equal(0, CombatService.ChantFactor(2000));
        Assert.Equal(1 - Math.Sqrt(130 / 530.0), CombatService.ChantFactor(130), 6);
        Assert.True(CombatService.ChantFactor(-50) > 1);
    }

    [ClientDataFact]
    public async Task SuffragiumShortensTheHealChant()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1100");
        var heal = Data.Row("SkillConfig", 1140501)!.Value; // Heal: _iChantVar 1000
        int before = await Loop.InvokeAsync(() => Task.FromResult(Combat.ChantTime(Session("alice"), heal)));
        await Loop.InvokeAsync(() => Buffs.AddAsync(Session("alice"), 1412305, 30000, 0)); // Suffragium: Cast Speed +10
        int after = await Loop.InvokeAsync(() => Task.FromResult(Combat.ChantTime(Session("alice"), heal)));
        Assert.InRange(before, 1, 1000);
        Assert.True(after < before, $"{after} !< {before}");
    }

    [Fact]
    public void HealingAndShieldEfficiencyRaiseWhatIsGranted()
    {
        var plain = new StatSheet();
        var prayer = new StatSheet { [1207] = 300, [1205] = 300 }; // Prayer of Gratitude: 3% / 3%
        Assert.Equal(1.03, CombatService.HealFactor(new CombatService.Fighter(0, 0, 0, 0, 1, 1) { Stats = prayer }, new CombatService.Fighter(0, 0, 0, 0, 1, 1) { Stats = plain }), 6);
        Assert.Equal(1030, CombatService.ShieldEfficiency(1000, prayer, plain));
        Assert.Equal(1000, CombatService.ShieldEfficiency(1000, plain, plain));
        Assert.Equal(1100, CombatService.ShieldEfficiency(1000, plain, new StatSheet { [1206] = 1000 }));
    }

    [ClientDataFact]
    public async Task SightrasherKnocksNearbyEnemiesBack()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 2200"); await Teach("alice", 12101);
        Combat.Rolls = new FixedRolls(9999);
        var session = Session("alice");
        var nav = _app.Services.GetRequiredService<WorldData>().Map(session.SceneEntry)!.Nav;
        var start = session.Position.Clone();
        var (dx, dz) = new[] { (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f) }.First(o => nav == null
            || nav.CanWalk(start.X, start.Y, start.Z, start.X + o.Item1 * 4, start.Z + o.Item2 * 4));
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.X = start.X + dx; target.Z = start.Z + dz; target.Hp = 1_000_000; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1210101, Face = 0, TargetPos = start.Clone() });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1210101 && h.Dsts.Any(d => d.DstID == target.Uid));
        // "knocking them back ${5} meters" (2 m): from 1 m to about 3 m away.
        await WaitFor(() => Loop.InvokeAsync(() => Task.FromResult(MonsterService.Distance(start.X, start.Z, target.X, target.Z) > 2.5f)), "not knocked back", 5);
    }

    [ClientDataFact]
    public async Task TetraVortexPullsEnemiesTowardItsTarget()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 2400"); await Teach("alice", 12316); await Seasoned("alice");
        Combat.Rolls = new FixedRolls(9999);
        var session = Session("alice");
        var start = session.Position.Clone();
        var nav = _app.Services.GetRequiredService<WorldData>().Map(session.SceneEntry)!.Nav;
        var (dx, dz) = new[] { (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f) }.First(o => nav == null
            || nav.CanWalk(start.X, start.Y, start.Z, start.X + o.Item1 * 5, start.Z + o.Item2 * 5));
        var main = await Target("alice", stationary: true);
        var other = await Target("alice", stationary: true);
        // The chosen target 2 m away, another enemy 3 m beyond it.
        await Loop.InvokeAsync(() =>
        {
            main.X = start.X + dx * 2; main.Z = start.Z + dz * 2; main.Hp = 1_000_000;
            other.X = start.X + dx * 5; other.Z = start.Z + dz * 5; other.Hp = 1_000_000;
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1231601, TargetId = main.Uid, TargetPos = main.Pos.Clone() });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1231601);
        Assert.Contains(hit.Dsts, d => d.DstID == other.Uid);
        await WaitFor(() => Loop.InvokeAsync(() => Task.FromResult(MonsterService.Distance(main.X, main.Z, other.X, other.Z) < 1.5f)), "not pulled", 8);
        await Loop.InvokeAsync(() => { Assert.Equal(start.X + dx * 2, main.X, 3); Assert.Equal(start.Z + dz * 2, main.Z, 3); return Task.CompletedTask; });
    }
}
