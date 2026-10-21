using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Skills that land in several parts, hit around their target, or pick a damage branch by target.</summary>
public sealed partial class SkillCombatTests
{
    private async Task<List<SkillHitInfo>> Hits(Ro3Emu.Bot.BotClient bot, int skill, int count)
    {
        var hits = new List<SkillHitInfo> { await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == skill) };
        while (hits.Count < count)
            hits.Add(await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == hits[0].SessionId));
        return hits;
    }

    /// <summary>A level 60 caster with full SP (Tetra Vortex Lv1 costs 55 SP, more than a new character has).</summary>
    /// <summary>Puts a weapon or shield in a worn grid without a stat refresh, for skills whose _iSkillCondition names an
    /// equipped subtype: Dagger 2010120 (1), Spear 2050120 (5), Bow 2110120 (11), Katar 2120120 (12), Shield 2500120 (50).
    /// A level-up's item commit may still replace the item list after the GM reply, so it is re-checked a moment later.</summary>
    private async Task Wield(string name, int entry, int grid = 1)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            await Loop.InvokeAsync(() =>
            {
                var s = Session(name);
                if (s.Items.Any(i => i.Grid == grid && i.Entry == entry)) return Task.CompletedTask;
                s.Items.RemoveAll(i => i.Grid == grid);
                s.Items.Add(new InvItem { Guid = 9_000_100 + grid, Entry = entry, Stack = 1, Pack = 1, Grid = grid });
                return Task.CompletedTask;
            });
            await Task.Delay(150);
            if (await Loop.InvokeAsync(() => Task.FromResult(Session(name).Items.Any(i => i.Grid == grid && i.Entry == entry)))) return;
        }
        Assert.Fail("weapon not kept");
    }

    private async Task Seasoned(string name)
    {
        await Gm(name, "!lvl 60");
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(Session(name), null, Character.Stats(Session(name))[102]));
    }

    [ClientDataFact]
    public async Task TetraVortexLandsFourElementsAndAlwaysOneStatus()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 2400"); await Teach("alice", 12316); await Seasoned("alice");
        Combat.Rolls = new FixedRolls(9999); // every 50% status roll fails
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1231601, TargetId = target.Uid, TargetPos = target.Pos.Clone() });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var hits = await Hits(alice, 1231601, 4);
        // Water, Fire, Earth, Wind (a packet that kills a wild monster nearby can arrive after the next one).
        Assert.Equal([2, 3, 4, 5], hits.Select(h => h.SrcElement).Order());
        Assert.All(hits, h => Assert.Contains(h.Dsts, d => d.DstID == target.Uid && d.Damage > 0));
        // "guaranteed to inflict at least 1": the 100% Frozen tuple after four misses.
        await Loop.InvokeAsync(() =>
        {
            Assert.Contains(Buffs.UnitBuffs(target), b => b.ConfigId == 902001);
            return Task.CompletedTask;
        });
    }

    [ClientDataFact]
    public async Task GravitationalImpactPullsThreeTimesThenBursts()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 2400"); await Teach("alice", 12342); await Seasoned("alice");
        Combat.Rolls = new FixedRolls(9999);
        var target = await CastAt(alice, "alice", 1234201);
        var hits = await Hits(alice, 1234201, 4);
        var damage = hits.Select(h => h.Dsts.Single(d => d.DstID == target.Uid).Damage).ToArray();
        // Lv1 [6650, 13300]: three 66.5% hits, then one 133% burst.
        Assert.Equal(damage[0], damage[1]);
        Assert.Equal(damage[0], damage[2]);
        Assert.InRange(damage[3], damage[0] * 2 - 2, damage[0] * 2 + 2);
    }

    [ClientDataFact]
    public async Task VirusSpreadHitsEnemiesAroundItsTarget()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5200"); await Teach("alice", 15106);
        Combat.Rolls = new FixedRolls(9999);
        var others = new[] { await Target("alice", stationary: true), await Target("alice", stationary: true) };
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1510601, TargetId = target.Uid, TargetPos = target.Pos.Clone() });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1510601);
        Assert.Equal(target.Uid, hit.Dsts[0].DstID); // the locked target first
        Assert.All(others, o => Assert.Contains(hit.Dsts, d => d.DstID == o.Uid && d.Damage > 0));
    }

    [ClientDataFact]
    public async Task EnvenomedBladeBouncesToThreeMoreEnemies()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5200"); await Teach("alice", 15105); await Wield("alice", 2010120);
        Combat.Rolls = new FixedRolls(0);
        var others = new[] { await Target("alice", stationary: true), await Target("alice", stationary: true), await Target("alice", stationary: true) };
        var target = await CastAt(alice, "alice", 1510501);
        var hits = await Hits(alice, 1510501, 4);
        Assert.Equal(target.Uid, hits[0].Dsts.Single().DstID);
        // Three more blades, each at an enemy not hit yet (a wild monster near the spawn point may take one).
        var bounced = hits.Skip(1).Select(h => h.Dsts.Single().DstID).ToArray();
        Assert.Equal(3, bounced.Distinct().Count());
        Assert.DoesNotContain(target.Uid, bounced);
        Assert.NotEmpty(bounced.Intersect(others.Select(o => o.Uid)));
    }

    [ClientDataFact]
    public async Task SharpShootingPiercesEnemiesInLine()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 3300"); await Teach("alice", 13204);
        Combat.Rolls = new FixedRolls(9999);
        var target = await Target("alice", stationary: true);
        var behind = await Target("alice", stationary: true);
        var aside = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() =>
        {
            var s = Session("alice");
            s.Items.RemoveAll(i => i.Grid == 1);
            s.Items.Add(new InvItem { Guid = 9_000_002, Entry = 2110120, Stack = 1, Pack = 1, Grid = 1 }); // a bow
            // Along +X from Alice: the target at 1.5 m, one behind it at 3 m, one 1.5 m off the line (the line is 1.5 m wide).
            foreach (var (m, dx, dz) in new[] { (target, 1.5f, 0f), (behind, 3f, 0f), (aside, 1.5f, 1.5f) })
            {
                m.X = s.Position.X + dx; m.Z = s.Position.Z + dz; m.Y = s.Position.Y; m.Hp = 1_000_000;
            }
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1320401, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1320401);
        Assert.Equal(target.Uid, hit.Dsts[0].DstID);
        Assert.Contains(hit.Dsts, d => d.DstID == behind.Uid);
        Assert.DoesNotContain(hit.Dsts, d => d.DstID == aside.Uid);
    }

    [ClientDataFact]
    public async Task SonicBlowPicksItsLowHpBranch()
    {
        await using var alice = await InWorld("Alice");
        var target = await Target("alice", stationary: true);
        var row = _app.Services.GetRequiredService<GameData>().Row("SkillConfig", 1510201)!.Value; // [7600, 9120, 9500], +20% at or below 50% HP
        var (high, low) = await Loop.InvokeAsync(() =>
        {
            var s = Session("alice");
            target.Hp = target.Template.MaxHp * 9 / 10;
            int a = Combat.DamageBranch(s, row, target);
            target.Hp = target.Template.MaxHp * 4 / 10;
            int b = Combat.DamageBranch(s, row, target);
            return Task.FromResult((a, b));
        });
        Assert.Equal(0, high);
        Assert.Equal(1, low);
    }
}
