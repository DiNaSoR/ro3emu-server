using Google.Protobuf;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Creating a party applies the same text limits as editing it, so one party can't bloat every party list (#145).</summary>
public sealed class TeamCreateLimitsTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser) where T : IMessage<T> =>
        parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(id), Wait)).Body);

    private static async Task<int> Create(BotClient bot, TeamCreateReq req)
    {
        await bot.SendGameAsync(TeamService.TeamCreate, req);
        return (await Next(bot, TeamService.TeamCreate, TeamCreateResp.Parser)).Result;
    }

    private static TeamCreateReq Req(string desc = "", string area = "", long limit = 0) =>
        new() { TeamType = TeamType.Five, TargetId = 1001, TeamDesc = desc, TeamAreaInfo = area, LimitScore = limit };

    [Fact]
    public async Task OversizedDescriptionAreaOrNegativeScoreIsRefused()
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var alice = await host.InWorldAsync("Alice");

        Assert.Equal(ErrorCodes.Parameter, await Create(alice, Req(desc: new string('x', TeamService.MaxDescription + 1))));
        Assert.Equal(ErrorCodes.Parameter, await Create(alice, Req(area: new string('1', TeamService.MaxArea + 1))));
        Assert.Equal(ErrorCodes.Parameter, await Create(alice, Req(limit: -1)));
        Assert.Equal(0, host.Service<TeamService>().TeamOf((long)alice.Self!.Uuid).TeamId);
    }

    [Fact]
    public async Task DescriptionAndAreaAtTheLimitAreAccepted()
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var alice = await host.InWorldAsync("Alice");

        Assert.Equal(0, await Create(alice, Req(desc: new string('x', TeamService.MaxDescription), area: new string('1', TeamService.MaxArea))));
        Assert.NotEqual(0, host.Service<TeamService>().TeamOf((long)alice.Self!.Uuid).TeamId);
    }
}
