using Google.Protobuf;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    [ClientDataFact]
    public async Task DungeonRoleChoiceIsClassBoundPersistsAndReachesTeamCards()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Gm(alice, "!job 4100");
        await alice.SendGameAsync(DungeonService.SetJobPosition, new MultiDungeonSetJobPositionReq { JobPosition = 101 });
        Assert.Equal(ErrorCodes.Parameter, (await Next(alice, DungeonService.SetJobPosition, MultiDungeonSetJobPositionResp.Parser)).Result);
        await alice.SendGameAsync(DungeonService.SetJobPosition, new MultiDungeonSetJobPositionReq { JobPosition = 103 });
        Assert.Equal(0, (await Next(alice, DungeonService.SetJobPosition, MultiDungeonSetJobPositionResp.Parser)).Result);
        Assert.Equal(103, Session(alice).Dungeons!.JobPosition);
        await OnLoop(async () =>
        {
            Session(alice).Dungeons = null;
            await Service<DungeonService>().LoginAsync(Session(alice));
            Assert.Equal(103, Session(alice).Dungeons!.JobPosition);
        });
        await Party(alice, bob);
        var roster = await Next(bob, TeamService.TeamAll, TeamTotalDetailInfo.Parser, m => m.MemberList.Count == 2);
        var detail = TeamMultiDungeonInfo.Parser.ParseFrom(roster.MemberList[(long)alice.Self!.Uuid].MultiDungeonBytes);
        Assert.Equal(103, detail.JobPosition);
        Assert.Equal(103, TeamMultiDungeonInfo.Parser.ParseFrom(roster.MemberList[(long)bob.Self!.Uuid].MultiDungeonBytes).JobPosition);
        await Gm(alice, "!job 1100");
        var update = await Next(bob, TeamService.TeamMemberUpdate, TeamMemberChangeResp.Parser,
            m => m.MemberList.TryGetValue((long)alice.Self.Uuid, out var card)
                && TeamMultiDungeonInfo.Parser.ParseFrom(card.MultiDungeonBytes).JobPosition == 101);
        Assert.Equal(1100, update.MemberList[(long)alice.Self.Uuid].Job);
        Assert.Equal(101, Session(alice).Dungeons!.JobPosition);
    }

    [ClientDataFact]
    public async Task DungeonFinderUsesTargetFilteredMicroDirectoryAndRealPartyApplication()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        long target = (1L << 32) | 50104; // LC_TeamHelper: Gameplay type + configured dungeon target.
        await Party(alice);
        await Party(carol);
        await alice.SendGameAsync(TeamService.TeamChangeInfo, new ChangeTeamInfoReq { TargetId = target, IsAutoAgree = true });
        Assert.Equal(0, (await Next(alice, TeamService.TeamChangeInfo, ChangeTeamInfoResp.Parser)).Result);
        var reply = await bob.MicroPositionalAsync("PartyService", "getPartysSummary",
            [0, Array.Empty<object>(), new object[] { new Dictionary<string, object> { ["Key"] = "target", ["MinValue"] = target, ["MaxValue"] = target } }, Array.Empty<object>(), 0, 100]);
        Assert.Equal(true, ((object?[])reply["Ret"]!)[0]);
        var only = (Dictionary<object, object?>)Assert.Single((object?[])reply["SummaryList"]!)!;
        var summary = TeamTotalSimpleInfo.Parser.ParseFrom((byte[])only["GameSummaryData"]!);
        Assert.Equal(target, summary.Info.TargetId);
        Assert.Equal((long)alice.Self!.Uuid, Assert.Single(summary.MemberList));
        var full = await bob.MicroPositionalAsync("PartyService", "getParty", [summary.Info.TeamId.ToString()]);
        Assert.Equal(true, ((object?[])full["Ret"]!)[0]);
        await bob.SendGameAsync(TeamService.TeamEnter, new EnterTeamReq { TeamId = summary.Info.TeamId });
        Assert.Equal(0, (await Next(bob, TeamService.TeamEnter, EnterTeamResp.Parser)).Result);
        Assert.Equal(2, (await Next(bob, TeamService.TeamAll, TeamTotalDetailInfo.Parser)).MemberList.Count);
    }

    [ClientDataFact]
    public async Task LightOfBondsProjectsEligibilityAppliesBossBonusesAndClearsOnExit()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        await using var dave = await InWorld("Dave");
        await using var eric = await InWorld("Eric");
        BotClient[] players = [alice, bob, carol, dave, eric];
        int[] jobs = [1100, 2100, 3100, 4100, 5100];
        for (int i = 0; i < players.Length; i++)
        {
            await Gm(players[i], "!lvl 99");
            await Gm(players[i], $"!job {jobs[i]}");
        }
        // Controlled durable guild membership; dungeon and team messages below use the real wire owners.
        await OnLoop(() =>
        {
            var guild = new GuildStore.State { Name = "BondTest" };
            foreach (var player in players.Take(2))
            {
                long id = (long)player.Self!.Uuid;
                guild.Members[id] = new GuildStore.Member
                {
                    RoleId = id, Name = Session(player).Role!.Name,
                    Official = ReferenceEquals(player, alice) ? 1 : 20,
                };
            }
            Assert.True(Service<GuildStore>().Save(guild));
            return Task.CompletedTask;
        });
        await Party(alice, bob, carol, dave, eric);
        var roster = await Next(eric, TeamService.TeamAll, TeamTotalDetailInfo.Parser, m => m.MemberList.Count == 5);
        var aInfo = TeamMultiDungeonInfo.Parser.ParseFrom(roster.MemberList[(long)alice.Self!.Uuid].MultiDungeonBytes);
        var cInfo = TeamMultiDungeonInfo.Parser.ParseFrom(roster.MemberList[(long)carol.Self!.Uuid].MultiDungeonBytes);
        Assert.Equal(new long[] { 1001, 1002 }, aInfo.PlayerBonds.Single(b => b.BossId == Boss).PlayerBond);
        Assert.Equal(new long[] { 1002 }, cInfo.PlayerBonds.Single(b => b.BossId == Boss).PlayerBond);
        Assert.NotEmpty(aInfo.DungeonInfo);
        Assert.Equal(0, await Enter(alice));
        foreach (var player in players.Skip(1))
        {
            await Next(player, DungeonService.ReadyQueue, TeamEnterReadyQueueNotify.Parser);
            await player.SendGameAsync(DungeonService.Confirm, new ConfirmTeamDungeonReq { IsAgree = true });
            Assert.Equal(0, (await Next(player, DungeonService.Confirm, ConfirmTeamDungeonRsp.Parser)).Result);
        }
        long instance = await Load(alice);
        foreach (var player in players.Skip(1)) Assert.Equal(instance, await Load(player));
        var boss = await BossIn(instance);
        await OnLoop(async () =>
        {
            Assert.Contains(Session(alice).Buffs, b => b.ConfigId == 990301 && b.SourceId == instance);
            Assert.Contains(Session(alice).Buffs, b => b.ConfigId == 990101 && b.SourceId == instance);
            Assert.DoesNotContain(Session(carol).Buffs, b => b.ConfigId == 990301);
            var combat = Service<CombatService>();
            var skill = Service<GameData>().Row("SkillConfig", 1100001)!.Value;
            long dealt = combat.DamageAdjusters.Aggregate(10000L, (n, f) => f(Session(alice), boss, skill, DamageHitResult.Normal, n));
            long taken = 10000;
            foreach (var f in combat.IncomingDamageAdjusters) taken = await f(boss, Session(alice), DamageHitResult.Normal, taken);
            Assert.Equal(10500, dealt);
            Assert.Equal(9500, taken);
            Assert.Empty(Session(alice).Dungeons!.Records); // buffs do not mint completion rewards
        });
        // Leaving the party while staying inside cannot retain a five-class party bonus.
        await eric.SendGameAsync(TeamService.TeamLeave, new LeaveTeamReq());
        Assert.Equal(0, (await Next(eric, TeamService.TeamLeave, LeaveTeamResp.Parser)).Result);
        await OnLoop(async () =>
        {
            var combat = Service<CombatService>();
            var skill = Service<GameData>().Row("SkillConfig", 1100001)!.Value;
            Assert.Equal(10200, combat.DamageAdjusters.Aggregate(10000L,
                (n, f) => f(Session(alice), boss, skill, DamageHitResult.Normal, n)));
            await Service<DungeonService>().ReconcileAsync(Session(alice));
            Assert.DoesNotContain(Session(alice).Buffs, b => b.ConfigId == 990101);
            Assert.DoesNotContain(Session(eric).Buffs, b => b.SourceId == instance);
        });
        await eric.SendGameAsync(DungeonService.Leave, new LeaveDungeonReq());
        Assert.Equal(0, (await Next(eric, DungeonService.Leave, LeaveDungeonRsp.Parser)).Result);
        await Next(eric, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        await OnLoop(() =>
        {
            Assert.DoesNotContain(Session(eric).Buffs, b => b.SourceId == instance);
            Assert.DoesNotContain(Session(alice).Buffs, b => b.ConfigId == 990101);
            Assert.Contains(Session(alice).Buffs, b => b.ConfigId == 990301);
            return Task.CompletedTask;
        });
    }
}
