using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Ground fields, party support and description-only passives (CombatService.Ground, CombatService.Support).</summary>
public sealed partial class SkillCombatTests
{
    private Task<MonsterUnit> Spawn(string name, Func<MonsterTemplate, MonsterTemplate> change) => Loop.InvokeAsync(async () =>
    {
        var s = Session(name);
        var monsters = _app.Services.GetRequiredService<MonsterService>();
        var template = change(monsters.Template(5000001)! with { RoamDistance = 0 });
        var unit = await monsters.SpawnSingleAsync(s.SceneEntry, 5000001, new MapPoint(s.Position.X, s.Position.Y, s.Position.Z, 0),
            s.InstanceId, customTemplate: template);
        Assert.NotNull(unit);
        unit!.Hp = 1_000_000;
        return unit;
    });

    private static async Task<CastSkillRsp> CastGround(BotClient bot, int skill, Vec3 point, long target = 0)
    {
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skill, TargetId = target, TargetPos = point.Clone() });
        return await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
    }

    private Task<SkillObject[]> Fields(string name) =>
        Loop.InvokeAsync(() => Task.FromResult(Session(name).SkillObjects.Where(u => !u.Removing).ToArray()));

    [ClientDataFact]
    public async Task MagnusExorcismusLeavesACrossThatKeepsStriking()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 4200"); await Teach("alice", 14109); await Seasoned("alice");
        Combat.Rolls = new FixedRolls(9999);
        var target = await Spawn("alice", t => t);
        Assert.Equal(0, (await CastGround(alice, 1410901, target.Pos, target.Uid)).Result);
        var hits = await Hits(alice, 1410901, 3); // every 0.5 s for 4 s
        Assert.All(hits, h => Assert.Contains(h.Dsts, d => d.DstID == target.Uid && d.Damage > 0));
        var field = Assert.Single(await Fields("alice"));
        Assert.Equal(22000009, field.NpcId);
        Assert.Equal(500, field.IntervalMs);
    }

    [ClientDataFact]
    public async Task MagnusExorcismusHitsShadowMonstersHarder()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 4200"); await Teach("alice", 14109);
        var row = Data.Row("SkillConfig", 1410901)!.Value;
        var water = await Spawn("alice", t => t);
        var shadow = await Spawn("alice", t => t with { Element = 8 });
        int Branch(MonsterUnit m) => Combat.DamageBranch(Session("alice"), row, m);
        Assert.Equal(0, await Loop.InvokeAsync(() => Task.FromResult(Branch(water))));
        Assert.Equal(1, await Loop.InvokeAsync(() => Task.FromResult(Branch(shadow)))); // [500, 650]: +30%
    }

    [ClientDataFact]
    public async Task SanctuaryHealsAlliesAndOnlyHurtsDemonsAndUndead()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 4200"); await Teach("alice", 14115); await Seasoned("alice");
        Combat.Rolls = new FixedRolls(9999);
        var poring = await Spawn("alice", t => t);
        var demon = await Spawn("alice", t => t with { Race = 3 });
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(Session("alice"), 1, null));
        Assert.Equal(0, (await CastGround(alice, 1411501, Session("alice").Position)).Result);
        var hits = await Hits(alice, 1411501, 2);
        Assert.All(hits, h => Assert.DoesNotContain(h.Dsts, d => d.DstID == poring.Uid));
        Assert.Contains(hits.SelectMany(h => h.Dsts), d => d.DstID == demon.Uid && d.Damage > 0);
        Assert.Contains(hits.SelectMany(h => h.Dsts), d => d.DamageType == DamageType.Heal);
        Assert.True(await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(Session("alice"))[2000] > 1)));
        Assert.Equal(22000003, Assert.Single(await Fields("alice")).NpcId);
    }

    [ClientDataFact]
    public async Task GravitationFieldStrikesInWavesAndImmobilizes()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 2300"); await Teach("alice", 12212); await Seasoned("alice");
        Combat.Rolls = new FixedRolls(0);
        var target = await Spawn("alice", t => t);
        Assert.Equal(0, (await CastGround(alice, 1221201, target.Pos, target.Uid)).Result);
        var hits = await Hits(alice, 1221201, 3);
        Assert.All(hits, h => Assert.Contains(h.Dsts, d => d.DstID == target.Uid && d.Damage > 0));
        var field = Assert.Single(await Fields("alice"));
        Assert.Equal(1200013, field.NpcId);
        Assert.InRange(field.WavesLeft, -1, 5); // 8 waves in all
        await Loop.InvokeAsync(() =>
        {
            Assert.Contains(Buffs.UnitBuffs(target), b => b.ConfigId == 902401);
            return Task.CompletedTask;
        });
    }

    [ClientDataFact]
    public async Task FireWallKnocksBackAndOnlyOneStands()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 2100"); await Teach("alice", 12010); await Seasoned("alice");
        Combat.Rolls = new FixedRolls(9999);
        var target = await Spawn("alice", t => t);
        var start = target.Pos.Clone();
        Assert.Equal(0, (await CastGround(alice, 1201001, start)).Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1201001 && h.Dsts.Any(d => d.DstID == target.Uid));
        var first = Assert.Single(await Fields("alice"));
        Assert.Equal(5, first.TouchLimit);
        await Until(_ => Math.Abs(target.X - start.X) + Math.Abs(target.Z - start.Z) > 0.5f, "alice"); // "knocks the enemy back 2 meters"

        await Task.Delay(1500); // the first cast's action time
        await Loop.InvokeAsync(() => { Session("alice").SkillCds.Clear(); Session("alice").SkillModeCds.Clear(); return Task.CompletedTask; });
        Assert.Equal(0, (await CastGround(alice, 1201001, Session("alice").Position)).Result);
        await Until(s => s.SkillObjects.Count(u => !u.Removing) == 1 && s.SkillObjects.All(u => u.Uid != first.Uid), "alice");
    }

    [ClientDataFact]
    public async Task GanbanteinClearsGroundEffects()
    {
        await using var alice = await InWorld("Alice"); await using var bob = await InWorld("Bob");
        await Gm("bob", "!job 2100"); await Teach("bob", 12010); await Seasoned("bob");
        await Gm("alice", "!job 2300"); await Teach("alice", 12213); await Seasoned("alice");
        var point = Session("bob").Position.Clone();
        Assert.Equal(0, (await CastGround(bob, 1201001, point)).Result);
        await Until(s => s.SkillObjects.Count > 0, "bob");
        Assert.Equal(0, (await CastGround(alice, 1221301, point)).Result);
        await Until(s => s.SkillObjects.Count == 0, "bob");
    }

    [ClientDataFact]
    public async Task HymnOfDevotionRestoresLifeEverySecond()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 4400"); await Teach("alice", 14315); await Seasoned("alice");
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(Session("alice"), 1, null));
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1431501, TargetId = Session("alice").Role!.Id });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Until(s => s.Buffs.Any(b => b.ConfigId == 1432501), "alice");
        // MATK*20%+20 a second (_kData1 2000, _kData2 20).
        var heal = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.Dsts.Any(d => d.DamageType == DamageType.Heal));
        long matk = await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(Session("alice"))[104]));
        Assert.Equal(matk * 2000 / 10000 + 20, heal.Dsts.Single().Damage);
    }

    [ClientDataFact]
    public async Task HymnOfTheLambLiftsStatusEffects()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 4400"); await Teach("alice", 14302); await Seasoned("alice");
        await Loop.InvokeAsync(() => Buffs.AddAsync(Session("alice"), 902501, 30000, 0)); // Blind: categories [5, 28, 37, ...]
        Assert.True(await HasBuff("alice", 902501));
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1430201, TargetId = Session("alice").Role!.Id });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Until(s => !s.Buffs.Any(b => b.ConfigId == 902501 && b.ExpireMs > MonsterService.NowMs), "alice");
    }

    [ClientDataFact]
    public async Task AdrenalineRushGivesItsExtraOnlyToBlacksmiths()
    {
        await using var alice = await InWorld("Alice"); await using var bob = await InWorld("Bob");
        await Gm("alice", "!job 6200"); await Gm("bob", "!job 2100");
        var row = Data.Row("SkillConfig", 1610501)!.Value;
        int[] For(string name) => Combat.Buff1(Session("alice"), row, "AdrenalineRush", Session(name))
            .Select(t => (int)t[0].GetInt64()).ToArray();
        Assert.Equal(new[] { 1610501, 1610801 }, await Loop.InvokeAsync(() => Task.FromResult(For("alice"))));
        Assert.Equal(new[] { 1610501 }, await Loop.InvokeAsync(() => Task.FromResult(For("bob"))));
    }

    [ClientDataFact]
    public async Task WoodSpiritsBlessingSavesOneLethalHit()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 4400"); await Teach("alice", 14307);
        var p = Session("alice");
        bool saved = await Loop.InvokeAsync(async () =>
        {
            using var held = await CharacterService.Lock(p);
            return await Buffs.TryPreventLethalAsync(p);
        });
        Assert.True(saved);
        await Loop.InvokeAsync(() =>
        {
            var stats = Character.Stats(p);
            Assert.Equal(Math.Max(1, (long)(stats[101] * 30.0 / 100)), stats[2000]); // "recover 30% Life"
            Assert.True(p.InvincibleUntilMs > MonsterService.NowMs);
            Assert.True(Buffs.HasForbidden(p, 9));
            Assert.InRange(p.WoodSpiritReadyMs - MonsterService.NowMs, 230_000, 240_000); // Withered for 4 minutes
            return Task.CompletedTask;
        });
        Assert.True(await HasBuff("alice", 1430801));
        Assert.False(await Loop.InvokeAsync(async () =>
        {
            using var held = await CharacterService.Lock(p);
            return await Buffs.TryPreventLethalAsync(p);
        }));
    }

    [ClientDataFact]
    public async Task EmergencyTreatmentShieldsAndHealsBelowHalfLife()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 6400"); await Teach("alice", 16303);
        var p = Session("alice");
        var monster = await Spawn("alice", t => t);
        long max = await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(p)[101]));
        long low = max * 30 / 100;
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(p, low, null));
        await Loop.InvokeAsync(async () => { foreach (var handler in Combat.TakenAsync) await handler(monster, p, 10); });
        await Loop.InvokeAsync(() =>
        {
            var stats = Character.Stats(p);
            Assert.Equal(low + (long)(max * 6.0 / 100), stats[2000]); // Lv1 "restores 6% of your Life"
            Assert.Contains(p.Buffs, b => b.ConfigId == 1630802); // the 16% shield row
            Assert.True(p.EmergencyTreatmentReadyMs > MonsterService.NowMs + 170_000);
            return Task.CompletedTask;
        });
    }

    [ClientDataFact]
    public async Task DragonTrainingAndArcaneMasteryRaiseTheirSkills()
    {
        await using var alice = await InWorld("Alice");
        var p = Session("alice");
        var monster = await Spawn("alice", t => t);
        var breath = Data.Row("SkillConfig", 1130401)!.Value;
        var turn = Data.Row("SkillConfig", 1411801)!.Value;
        long Adjusted(System.Text.Json.JsonElement row) =>
            Combat.DamageAdjusters.Aggregate(1000L, (amount, adjust) => adjust(p, monster, row, DamageHitResult.Normal, amount));
        Assert.Equal(1000, await Loop.InvokeAsync(() => Task.FromResult(Adjusted(breath))));
        await Teach("alice", 11307); await Teach("alice", 14207);
        Assert.Equal(1060, await Loop.InvokeAsync(() => Task.FromResult(Adjusted(breath)))); // Lv1 +6%
        Assert.Equal(1055, await Loop.InvokeAsync(() => Task.FromResult(Adjusted(turn)))); // Lv1 +55
    }
}
