using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    // Sonic Blow's damage pairs are [plain, +20%, +25%]: the second below its _kLogicData 50% of Life, the third with
    // Supersonic Blow below its ${1}% (62 at level 1).
    [ClientDataFact]
    public async Task SonicBlowHitsHarderBelowItsLifeThresholds()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 5200"); await Teach("alice", 15102);
        _app.Services.GetRequiredService<CombatService>().Rolls = new GuardianStableDamageRolls();

        // A fresh monster each time: a hit at low Life may kill it.
        async Task<long> SonicBlowAsync(double life)
        {
            var monster = await Target("alice", stationary: true);
            await Loop.InvokeAsync(() =>
            {
                monster.Hp = Math.Max(1, (long)(monster.Template.MaxHp * life));
                var s = Session("alice");
                s.SkillCds.Clear();
                s.SkillModeCds.Clear();
                return Task.CompletedTask;
            });
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1510201, TargetId = monster.Uid });
            var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
            Assert.Equal(0, accepted.Result);
            var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SessionId == accepted.SessionId);
            await Task.Delay(1200);
            return hit.Dsts.Single(d => d.DstID == monster.Uid).Damage;
        }

        long full = await SonicBlowAsync(1), half = await SonicBlowAsync(0.4), between = await SonicBlowAsync(0.55);
        Assert.InRange(half * 100 / full, 117, 123); // +20%
        Assert.Equal(full, between);
        await Loop.InvokeAsync(() => { Session("alice").Progress!.Skills[15103] = 1; return Task.CompletedTask; });
        Assert.InRange(await SonicBlowAsync(0.55) * 100 / full, 122, 128); // Supersonic Blow: +25% below 62%
    }

    // Arcane Mastery: "... and increases the damage of [Turn Undead] by ${2}." (100 at level 10)
    [ClientDataFact]
    public async Task ArcaneMasteryAddsItsDamageToTurnUndead()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 4300");
        var data = _app.Services.GetRequiredService<GameData>();
        long flat = await Loop.InvokeAsync(async () =>
        {
            var s = Session("alice");
            s.Progress!.Skills[14207] = 10;
            await _app.Services.GetRequiredService<CombatService>().RefreshSkillParametersAsync(s);
            return SkillParameterRules.Effective(s, data, SkillConfig.Get(data, 1411801)!.Value.Row).GetProperty("_kDamageParam2")[0].GetInt64();
        });
        Assert.Equal(102 + 100, flat);
    }

    // Psychokinetic Astral Body level 1: "Auto-generating 1 [Psychokinetic Astral Bodies] every 1.5 seconds, up to 3. If
    // you already have 3 ..., hitting with Soul Expansion Launches all": three piles of BuffConfig 1236101, then three
    // casts of its _kLogicData row 1234501 (Singularity) after Soul Expansion hits, and the piles are gone.
    [ClientDataFact]
    public async Task PsychokineticAstralBodiesGatherAndLaunchOnSoulExpansion()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 2400"); await Teach("alice", 12302); await Teach("alice", 12343);
        var full = await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
            p => p.Buffs.Any(b => b.ConfigId == 1236101 && b.PileCount == 3));
        Assert.Equal(3, full.Buffs.Single(b => b.ConfigId == 1236101).PileCount);

        var monster = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { monster.Hp = 50_000_000; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1230201, TargetId = monster.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, BuffService.BuffRemove, BuffRemoveNotify.Parser);
        await Task.Delay(1500);
        int bodies = alice.Inbox.Snapshot().Count(e => e.IsGame(CombatService.SkillHit) && SkillHitInfo.Parser.ParseFrom(e.Body).SkillId == 1234501);
        Assert.Equal(3, bodies);
    }

    // Union: "For each Crusader on the team, gain 1 stack of [Union]" (the other Crusader-line members): a Royal Guard
    // in a party with a Paladin carries one pile of BuffConfig 1162701, and none after the Paladin leaves.
    [ClientDataFact]
    public async Task UnionCountsTheOtherCrusadersInTheParty()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await UnionParty(alice, bob);
        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<TeamService>().LeaveAsync(Session("bob")));
        await Next(alice, BuffService.BuffRemove, BuffRemoveNotify.Parser);
    }

    // In-game 22.10: the Paladin going offline left the Union pile on, since only roster changes recounted it.
    [ClientDataFact]
    public async Task UnionDropsAPaladinWhoGoesOffline()
    {
        await using var alice = await InWorld("Alice");
        var bob = await InWorld("Bob");
        await UnionParty(alice, bob);
        await bob.DisposeAsync();
        await Next(alice, BuffService.BuffRemove, BuffRemoveNotify.Parser);
    }

    private async Task UnionParty(BotClient alice, BotClient bob)
    {
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 1410");
        await Gm("bob", "!lvl 99"); await Gm("bob", "!job 1310");
        await Loop.InvokeAsync(() => { Session("alice").Progress!.Skills[11614] = 1; return Task.CompletedTask; });

        await alice.SendGameAsync(TeamService.TeamCreate, new TeamCreateReq { TeamType = TeamType.Five, TargetId = 1001 });
        var created = await Next(alice, TeamService.TeamCreate, TeamCreateResp.Parser);
        Assert.Equal(0, created.Result);
        await alice.SendGameAsync(TeamService.TeamInvite, new InviteIntoTeamReq { Guid = (long)bob.Self!.Uuid });
        Assert.Equal(0, (await Next(alice, TeamService.TeamInvite, InviteIntoTeamResp.Parser)).Result);
        await Next(bob, TeamService.TeamNotifyInvitee, NotifyInvitee.Parser);
        await bob.SendGameAsync(TeamService.TeamInviteeResp, new InviteeResp
        { TeamId = created.TeamId, IsAgree = true, InviteGuid = (long)alice.Self!.Uuid });
        Assert.Equal(0, (await Next(bob, TeamService.TeamInviteeResp, InviteeRespResp.Parser)).Result);

        var union = await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, x => x.Buffs.Any(b => b.ConfigId == 1162701));
        Assert.Equal(1, union.Buffs.Single(b => b.ConfigId == 1162701).PileCount);
    }
}
