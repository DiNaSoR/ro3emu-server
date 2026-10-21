using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Class companions: the falcon, the Demonic Wolf and the Madogear (CombatService.Companions).</summary>
public sealed partial class SkillCombatTests
{
    private const int FalconLease = 1500100, MadogearLease = 1500300, WolfLease = 1500400;

    private SkillService Skills => _app.Services.GetRequiredService<SkillService>();

    private static async Task Lease(BotClient bot, int id)
    {
        await bot.SendGameAsync(CareerSpecialService.Lease, new LeaseCareerSpecialItemReq { CartRemouldCfgId = id });
        Assert.Equal(0, (await Next(bot, CareerSpecialService.Lease, LeaseCareerSpecialItemRsp.Parser)).ErrCode);
    }

    /// <summary>32745 tp 3 mounts the battle slot (the leased wolf or Madogear), tp 2 dismounts it.</summary>
    private static async Task RideBattle(BotClient bot, bool on)
    {
        await bot.SendGameAsync(MountService.Ride, new PlayerRiderMountReq { Tp = (eRiderType)(on ? 3 : 2) });
        Assert.Equal(0, (await Next(bot, MountService.Ride, PlayerRiderMountResp.Parser)).Result);
    }

    private static async Task<int> CastOn(BotClient bot, int skill, MonsterUnit target)
    {
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skill, TargetId = target.Uid });
        return (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result;
    }

    private Task<bool> HasBuff(string name, int id) =>
        Loop.InvokeAsync(() => Task.FromResult(Session(name).Buffs.Any(b => b.ConfigId == id && b.ExpireMs > MonsterService.NowMs)));

    [ClientDataFact]
    public async Task BlitzBeatNeedsTheFalcon()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 3200"); await Teach("alice", 13101); await Teach("alice", 13102); await Seasoned("alice");
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000; return Task.CompletedTask; });
        // _iSkillCondition [2004, 1500400] and [2006, 1]: not on the wolf, and a falcon (PendantConfig _iType 1) shown.
        Assert.Equal(ErrorCodes.SkillRequirements, await CastOn(alice, 1310201, target));

        await Lease(alice, FalconLease);
        Assert.True(await HasBuff("alice", 1310101)); // the falcon's _iPendantBuffID 【Falconry】
        Assert.Equal(0, await CastOn(alice, 1310201, target));
    }

    [ClientDataFact]
    public async Task RidingTheWolfSwapsTheAttackAndGrantsWolfTaming()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 3400");
        foreach (int group in new[] { 13310, 13311, 13323, 13325 }) await Teach("alice", group);
        await Seasoned("alice");
        await Lease(alice, WolfLease);
        Assert.False(await HasBuff("alice", 1331001));
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000; return Task.CompletedTask; });
        Assert.Equal(ErrorCodes.SkillRequirements, await CastOn(alice, 1332501, target)); // Warg Bite: mounted only

        await RideBattle(alice, true);
        Assert.True(await HasBuff("alice", 1331001)); // the wolf's _kBindBuff 【Wolf Taming】: speed and Warg Charge
        Assert.True(await HasBuff("alice", 1331201)); // Wolf's Fang Lv1 while mounted
        Assert.Equal(1003201, await Loop.InvokeAsync(() => Task.FromResult(Skills.NormalAttack(Session("alice"))))); // Warg Strike
        await Next(alice, SkillService.SkillDataSync, DBSkillDataIncrement.Parser, d => d.CurrentAtkSkillId == 1003201);

        // Warg Bite resets Warg Hunt's cooldown.
        await Loop.InvokeAsync(() => { Session("alice").SkillCds[13323] = MonsterService.NowMs + 60_000; return Task.CompletedTask; });
        Assert.Equal(0, await CastOn(alice, 1332501, target));
        await Until(s => !s.SkillCds.ContainsKey(13323), "alice");

        await RideBattle(alice, false);
        Assert.False(await HasBuff("alice", 1331001));
        Assert.False(await HasBuff("alice", 1331201));
        Assert.Equal(1000001, await Loop.InvokeAsync(() => Task.FromResult(Skills.NormalAttack(Session("alice")))));
    }

    [ClientDataFact]
    public async Task TheFollowingWolfHowlsWhileTheRangerFights()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 3400"); await Teach("alice", 13310); await Seasoned("alice"); await Wield("alice", 2110120);
        await Lease(alice, WolfLease);
        await CastAt(alice, "alice", 1300001); // the bow's normal attack
        await Until(s => s.Buffs.Any(b => b.ConfigId == 1332501), "alice"); // Warg Howl: Physical ATK +5% for 10 sec
        long next = await Loop.InvokeAsync(() => Task.FromResult(Session("alice").WargHowlMs - MonsterService.NowMs));
        Assert.InRange(next, 25_000, 30_000); // Wolf Taming's "every 30 sec"
    }

    [ClientDataFact]
    public async Task TheMadogearPunchesAndGatesItsSkills()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 6400"); await Teach("alice", 16309); await Teach("alice", 16312); await Seasoned("alice");
        await Lease(alice, MadogearLease);
        Assert.Equal(1600001, await Loop.InvokeAsync(() => Task.FromResult(Skills.NormalAttack(Session("alice")))));
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000; return Task.CompletedTask; });
        Assert.Equal(ErrorCodes.SkillRequirements, await CastOn(alice, 1631201, target)); // Arm Cannon: [2003, 1500300]

        await RideBattle(alice, true);
        Assert.Equal(1631001, await Loop.InvokeAsync(() => Task.FromResult(Skills.NormalAttack(Session("alice"))))); // Normal Punch
        Assert.Equal(0, await CastOn(alice, 1631001, target));
    }
}
