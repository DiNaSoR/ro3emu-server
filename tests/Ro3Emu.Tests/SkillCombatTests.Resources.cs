using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Class resources (Cross Ripper's blades, spellbook pages, spear charges, Shield Bash count) and stealth.</summary>
public sealed partial class SkillCombatTests
{
    private GameData Data => _app.Services.GetRequiredService<GameData>();
    private MonsterService Monsters => _app.Services.GetRequiredService<MonsterService>();

    private int ActionMs(int skill) => (int)GameData.Int(Data.Row("SkillConfig", skill)!.Value, "_iActionTime");

    private Task<long> Meter(string name, int id) =>
        Loop.InvokeAsync(() => Task.FromResult(Session(name).Energy.TryGetValue(id, out var m) ? m.Value : -1));

    private Task SetMeter(string name, int id, long value) => Loop.InvokeAsync(() => Buffs.SetEnergyAsync(Session(name), id, value));

    private async Task Until(Func<GameSession, bool> condition, string name)
    {
        for (int i = 0; i < 150; i++)
        {
            if (await Loop.InvokeAsync(() => Task.FromResult(condition(Session(name))))) return;
            await Task.Delay(20);
        }
        Assert.Fail("condition not reached");
    }

    [ClientDataFact]
    public async Task CrossRipperThrowsEveryBladeAndResetsItsCooldownAtFour()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5400"); await Teach("alice", 15302); await Teach("alice", 15303); await Seasoned("alice"); await Wield("alice", 2120120);
        Combat.Rolls = new FixedRolls(9999);
        Assert.Equal(4, await Meter("alice", 540002)); // EnergyConfig 540002 starts full
        var target = await CastAt(alice, "alice", 1530201);
        var full = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1530201);
        await Until(s => s.Energy[540002].Value == 0, "alice");
        // At 4 blades the cooldown ends with the hit, so a second throw (now with 0 blades, counted as 1) is accepted.
        await Until(s => !s.SkillModeCds.Any(kv => kv.Key.Item1 == 15302 && kv.Value > MonsterService.NowMs), "alice");
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(Session("alice"), null, Character.Stats(Session("alice"))[102]));
        await Task.Delay(ActionMs(1530201) + 100); // the first throw's action
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1530201, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var single = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1530201 && h.SessionId != full.SessionId);
        long four = full.Dsts.Single(d => d.DstID == target.Uid).Damage, one = single.Dsts.Single(d => d.DstID == target.Uid).Damage;
        Assert.InRange(four, one * 4 - 4, one * 4 + 4);
        // That throw had fewer than 4 blades: its cooldown stays.
        await Task.Delay(200);
        Assert.True(await Loop.InvokeAsync(() => Task.FromResult(
            Session("alice").SkillModeCds.Any(kv => kv.Key.Item1 == 15302 && kv.Value > MonsterService.NowMs))));
    }

    [ClientDataFact]
    public async Task DarkIllusionRefillsTheBladesAndPowersTheNextThrow()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5400"); await Teach("alice", 15301); await Teach("alice", 15302); await Seasoned("alice"); await Wield("alice", 2120120);
        Combat.Rolls = new FixedRolls(9999);
        await SetMeter("alice", 540002, 0);
        var target = await CastAt(alice, "alice", 1530101);
        await Until(s => s.Energy[540002].Value == 4 && s.Buffs.Any(b => b.ConfigId == 1530101 && b.ExpireMs == long.MaxValue), "alice");
        await Task.Delay(ActionMs(1530101) + 100);
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(Session("alice"), null, Character.Stats(Session("alice"))[102]));
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1530201, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Until(s => s.Energy[540002].Value == 0 && !s.Buffs.Any(b => b.ConfigId == 1530101), "alice");
    }

    [ClientDataFact]
    public async Task RollingCutterAddsABladeUpToFour()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5400"); await Teach("alice", 15303); await Seasoned("alice"); await Wield("alice", 2120120);
        await SetMeter("alice", 540002, 3);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1530301, Face = 0 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Until(s => s.Energy[540002].Value == 4, "alice");
    }

    [ClientDataFact]
    public async Task ReadingSpellbookCastsJackFrostAgainForThreePages()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 2400"); await Teach("alice", 12319); await Teach("alice", 12308); await Seasoned("alice");
        Combat.Rolls = new FixedRolls(9999);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1231901 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Until(s => s.Buffs.Any(b => b.ConfigId == 1231001), "alice");
        // 【Reading】 has no countdown: it lasts until Stop Reading.
        Assert.Equal(long.MaxValue, await Loop.InvokeAsync(() => Task.FromResult(Session("alice").Buffs.Single(b => b.ConfigId == 1231001).ExpireMs)));
        Assert.Equal(8, await Meter("alice", 240001));
        await Task.Delay(600); // the shared spellbook cooldown
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1230801, TargetId = target.Uid, TargetPos = target.Pos.Clone() });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1230801);
        var copy = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1232101);
        Assert.Contains(copy.Dsts, d => d.DstID == target.Uid && d.Damage > 0);
        Assert.InRange(await Meter("alice", 240001), 5, 6); // 8 - 3 (plus a page may have come back on its 5 s timer)
        // Stop Reading (Reading Spellbook's mode 4 while 【Reading】 is on) ends it.
        await Task.Delay(600);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1231941 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Until(s => !s.Buffs.Any(b => b.ConfigId == 1231001), "alice");
    }

    [ClientDataFact]
    public async Task SpellbookPagesReadBothConditionFormats()
    {
        await using var alice = await InWorld("Alice");
        Assert.Equal(3, CombatService.SpellbookPages(Data.Row("SkillConfig", 1230801)!.Value)); // "0:1231001:1&2:240001:3"
        Assert.Equal(5, CombatService.SpellbookPages(Data.Row("SkillConfig", 1231501)!.Value)); // Comet
        Assert.Equal(5, CombatService.SpellbookPages(Data.Row("SkillConfig", 1234201)!.Value)); // "1231001:1&1233501:5"
        Assert.Equal(3, CombatService.SpellbookPages(Data.Row("SkillConfig", 1230811)!.Value)); // with the Attunement entry
        Assert.Null(CombatService.SpellbookPages(Data.Row("SkillConfig", 1231901)!.Value));
    }

    [ClientDataFact]
    public async Task PrecisionThrustTurnsSixHitsIntoAHundredSpearsCharge()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1400"); await Teach("alice", 11315); await Seasoned("alice"); await Wield("alice", 2050120);
        Combat.Rolls = new FixedRolls(9999);
        await SetMeter("alice", 140001, 5);
        long spears = await Meter("alice", 140002);
        await CastAt(alice, "alice", 1131501);
        await Until(s => s.Energy[140001].Value == 0, "alice");
        Assert.Equal(Math.Min(5, spears + 1), await Meter("alice", 140002));
    }

    [ClientDataFact]
    public async Task TwentyShieldBashesEnhanceTheNextThree()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1410"); await Teach("alice", 11636); await Seasoned("alice"); await Wield("alice", 2500120, 2);
        Combat.Rolls = new FixedRolls(9999);
        await SetMeter("alice", 141002, 19);
        var target = await CastAt(alice, "alice", 1163601);
        await Until(s => s.Buffs.Any(b => b.ConfigId == 1163101 && b.PileCount == 3 && b.ExpireMs == long.MaxValue), "alice");
        Assert.Equal(0, await Meter("alice", 141002));
        await Task.Delay(600);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1163611, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Until(s => s.Buffs.Any(b => b.ConfigId == 1163101 && b.PileCount == 2), "alice");
    }

    [ClientDataFact]
    public async Task SonicBurstFillsTheEnhancedSpearCharges()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1400"); await Teach("alice", 11316); await Seasoned("alice"); await Wield("alice", 2050120);
        await CastAt(alice, "alice", 1131601);
        await Until(s => s.Buffs.Any(b => b.ConfigId == 1132401) && s.Energy[140003].Value == 5, "alice");
    }

    [ClientDataFact]
    public async Task EnhanceCloakingHidesFromMonstersUntilFiveHits()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5400"); await Teach("alice", 15304); await Seasoned("alice"); await Wield("alice", 2120120);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1530401 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Until(s => s.Stealthed, "alice");
        var aoi = await Next(alice, CombatService.AoiAttrChange, AOIAttrChange.Parser, a => a.Property.Any(p => p.Idx == 3010 && p.Data == 1));
        Assert.NotNull(aoi);
        var monster = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() =>
        {
            Assert.False(MonsterService.DetectsStealth(monster));
            Monsters.Engage(monster, Session("alice"));
            Assert.Null(monster.Target); // it cannot see her
            return Task.CompletedTask;
        });
        for (int i = 0; i < 4; i++) await Loop.InvokeAsync(() => Buffs.StealthHitAsync(Session("alice")));
        Assert.True(await Loop.InvokeAsync(() => Task.FromResult(Session("alice").Stealthed)));
        await Loop.InvokeAsync(() => Buffs.StealthHitAsync(Session("alice"))); // the fifth hit reveals her
        await Until(s => !s.Stealthed && !s.Buffs.Any(b => b.ConfigId == 1530401), "alice");
        await Loop.InvokeAsync(() =>
        {
            Monsters.Engage(monster, Session("alice"));
            Assert.NotNull(monster.Target);
            return Task.CompletedTask;
        });
    }

    [ClientDataFact]
    public async Task DealingDamageEndsCloaking()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5400"); await Teach("alice", 15304); await Teach("alice", 15303); await Seasoned("alice"); await Wield("alice", 2120120);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1530401 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Until(s => s.Stealthed, "alice");
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000; return Task.CompletedTask; });
        await Task.Delay(ActionMs(1530401) + 100);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1530301, Face = 0 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Until(s => !s.Stealthed, "alice");
    }
}
