using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    /// <summary>
    /// 30441 for a party run: teamId 0 keeps the client on its real team (a nonzero id switches it to the Virtual team,
    /// LC_DAL_VirtualTeam), positions are the party's own 0-based slots (the HUD shows position + 1 in 1..5, so a full
    /// party's fifth member is visible), the leader keeps its slot, and a member leaving moves nobody.
    /// </summary>
    [ClientDataFact]
    public async Task FivePlayerPartyRosterStaysOnTheRealTeamWithZeroBasedSlots()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        await using var dave = await InWorld("Dave");
        await using var erin = await InWorld("Erin");
        BotClient[] all = [alice, bob, carol, dave, erin];
        foreach (var bot in all) await Gm(bot, "!lvl 99");
        await Party(alice, bob, carol, dave, erin);
        // The leader moves to the last party slot: the roster follows the party, not the leader-first order.
        await alice.SendGameAsync(TeamService.TeamEditor, new EditorTeamReq { IndexFirst = 0, IndexSecond = 4 });
        Assert.Equal(0, (await Next(alice, TeamService.TeamEditor, EditorTeamResp.Parser)).Result);

        Assert.Equal(0, await Enter(alice));
        foreach (var bot in all.Skip(1))
        {
            await bot.SendGameAsync(DungeonService.Confirm, new ConfirmTeamDungeonReq { IsAgree = true });
            Assert.Equal(0, (await Next(bot, DungeonService.Confirm, ConfirmTeamDungeonRsp.Parser)).Result);
        }
        foreach (var bot in all) await Load(bot);

        long Id(BotClient bot) => (long)bot.Self!.Uuid;
        var slots = new Dictionary<long, int>
        {
            [Id(erin)] = 0, [Id(bob)] = 1, [Id(carol)] = 2, [Id(dave)] = 3, [Id(alice)] = 4,
        };
        foreach (var bot in all)
        {
            var roster = await Next(bot, DungeonService.TeamInfo, DungeonTeamInfoNotify.Parser,
                n => n.Squads.Values.Sum(q => q.Members.Count) == 5);
            Assert.Equal(0, roster.TeamId);
            Assert.Equal(Id(alice), roster.LeaderGuid);
            var squad = Assert.Single(roster.Squads.Values);
            Assert.Equal(slots, squad.Members.ToDictionary(m => m.Key, m => m.Value.Position));
            // Every member lands in one of the HUD's five slots (position + 1 in 1..5).
            Assert.All(squad.Members.Values, m => Assert.InRange(m.Position + 1, 1, 5));
        }

        await dave.SendGameAsync(DungeonService.Leave, new LeaveDungeonReq());
        Assert.Equal(0, (await Next(dave, DungeonService.Leave, LeaveDungeonRsp.Parser)).Result);
        slots.Remove(Id(dave));
        foreach (var bot in all.Where(b => b != dave))
        {
            var roster = await Next(bot, DungeonService.TeamInfo, DungeonTeamInfoNotify.Parser,
                n => n.Squads.Values.Sum(q => q.Members.Count) == 4 && !n.Squads.Values.Any(q => q.Members.ContainsKey(Id(dave))));
            Assert.Equal(0, roster.TeamId);
            Assert.Equal(slots, Assert.Single(roster.Squads.Values).Members.ToDictionary(m => m.Key, m => m.Value.Position));
        }
    }
}
