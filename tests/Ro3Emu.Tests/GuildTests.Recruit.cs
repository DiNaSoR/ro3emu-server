using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class GuildTests
{
    [ClientDataFact]
    public async Task RecruitListOffersOnlineGuildlessPlayersAndEchoesTheLabel()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        await Level("alice");
        await Level("bob");
        await Level("carol");
        await CreateGuild(alice, "Astra");
        await CreateGuild(carol, "Vega");

        async Task<PullRecommendPlayersRes> Pull(RecommendLabel label)
        {
            await alice.SendGameAsync(GuildService.RecommendPlayers,
                new PullRecommendPlayersReq { Type = RecommendType.Normal, Label = label });
            return await Next(alice, GuildService.RecommendPlayers, PullRecommendPlayersRes.Parser);
        }

        var list = await Pull(RecommendLabel.InviteJoinGuild);
        Assert.Equal(0, list.Result);
        Assert.Equal(RecommendLabel.InviteJoinGuild, list.Label);
        // Bob has no guild; Carol leads one and Alice is the asker.
        Assert.Equal((long)bob.Self!.Uuid, Assert.Single(list.RecommendList).PlayerId);
        Assert.Equal(GuildService.ErrRecommendTooFrequent, (await Pull(RecommendLabel.InviteJoinGuild)).Result);
        var helper = await Pull(RecommendLabel.InviteJoinGuildHelper);
        Assert.Equal(RecommendLabel.InviteJoinGuildHelper, helper.Label);
        Assert.Equal(ErrorCodes.GuildPermission, helper.Result);
    }
}
