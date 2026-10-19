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
    public async Task EnchantBladeRaisesDefenseAfterDealingDamage()
    {
        await using var alice = await InWorld("Alice");
        await Buff("alice", 1131301);
        var monster = await Spawn("alice", t => t);
        var row = Data.Row("SkillConfig", 1100101) ?? Data.Row("SkillConfig", 1131301)!.Value;
        await Loop.InvokeAsync(async () => { foreach (var handler in Combat.DealtAsync) await handler(Session("alice"), monster, row, 100); });
        Assert.True(await HasBuff("alice", 1131501)); // "After dealing damage, increases your DEF by 5%"
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
    public async Task EnergyCoatGuardsAfterAHit()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 2100"); await Teach("alice", 12003);
        var monster = await Spawn("alice", t => t);
        await Loop.InvokeAsync(() => Combat.ReactToMonsterHitAsync(monster, Session("alice"), null, 100));
        Assert.True(await HasBuff("alice", 1201501)); // damage reduction +5% for 1 s
        await Loop.InvokeAsync(() => Buffs.RemoveAsync(Session("alice"), Session("alice").Buffs.First(b => b.ConfigId == 1201501)));
        await Loop.InvokeAsync(() => Combat.ReactToMonsterHitAsync(monster, Session("alice"), null, 100));
        Assert.False(await HasBuff("alice", 1201501)); // "Cooldown: 10 sec."
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
    [ClientDataFact]
    public async Task KatarMasteryNeedsAKatar()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5100");
        long bare = await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(Session("alice"))[103]));
        await Wield("alice", 2120120); // a katar (subtype 12)
        long katar = await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(Session("alice"))[103]));
        long ratio = await Loop.InvokeAsync(() => Task.FromResult(Combat.EffectiveSkillRow(Session("alice"), 1005101)!.Value.GetProperty("_kDamageParam1")[0].GetInt64()));
        await Teach("alice", 15101);
        await Until(s => Character.Stats(s)[103] > katar, "alice"); // "increases Physical ATK by ${1}"
        await Loop.InvokeAsync(() =>
        {
            Combat.SyncPassiveSkillModifiers(Session("alice"));
            long mastered = Combat.EffectiveSkillRow(Session("alice"), 1005101)!.Value.GetProperty("_kDamageParam1")[0].GetInt64();
            Assert.Equal((long)Math.Round(ratio * 1.1), mastered); // [Assassin Normal Attack] multiplier +10%
            return Task.CompletedTask;
        });
        Assert.True(katar >= bare);
    }

    [ClientDataFact]
    public async Task AdvancedKatarMasteryRaisesCritAndStacksOnCrits()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5200"); await Teach("alice", 15220); await Wield("alice", 2120120);
        var monster = await Spawn("alice", t => t);
        await Loop.InvokeAsync(() =>
        {
            Combat.SyncPassiveSkillModifiers(Session("alice"));
            var row = Combat.EffectiveSkillRow(Session("alice"), 1005101)!.Value;
            Assert.True(row.GetProperty("_iSkillCri").GetInt64() > 10000); // Crit Chance +40% (type 10)
            return Task.CompletedTask;
        });
        var attack = Data.Row("SkillConfig", 1005101)!.Value;
        await Loop.InvokeAsync(() =>
        {
            foreach (var adjust in Combat.DamageAdjusters) adjust(Session("alice"), monster, attack, DamageHitResult.Critical, 100);
            return Task.CompletedTask;
        });
        await Until(s => s.Buffs.Any(b => b.ConfigId / 100 == 15221), "alice"); // 【CRIT】 pile
    }
}
