using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class GuildTests
{
    private static async Task<GuildEventResp> Journal(Ro3Emu.Bot.BotClient bot, long from, long count,
        bool onlyMe = false, params long[] types)
    {
        var req = new GuildEventReq { FromIndex = from, Count = count, GetUp = true, OnlyShowMe = onlyMe };
        req.ValidType.AddRange(types);
        await bot.SendGameAsync(GuildService.Events, req);
        return await Next(bot, GuildService.Events, GuildEventResp.Parser);
    }

    [ClientDataFact]
    public async Task JournalRecordsApprovalPositionAndLeaveNewestFirst()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Level("alice");
        await Level("bob");
        long guildId = await CreateGuild(alice, "Astra");
        long aliceId = (long)alice.Self!.Uuid, bobId = (long)bob.Self!.Uuid;

        // Nothing yet: an empty success (the client clears the table).
        var none = await Journal(alice, 0, 20);
        Assert.Equal(0, none.Result);
        Assert.Empty(none.Datas);

        await bob.SendGameAsync(GuildService.Apply, new GuildEnterApplyReq { GuildIds = { guildId } });
        Assert.Equal(0, Assert.Single((await Next(bob, GuildService.Apply, GuildEnterApplyResp.Parser)).ApplyResult).Result);
        await alice.SendGameAsync(GuildService.ApplyOpe, new GuildEnterApplyOpeReq { Agree = true, PlayerIds = { bobId } });
        Assert.Equal(0, (await Next(alice, GuildService.ApplyOpe, GuildEnterApplyOpeResp.Parser)).Result);
        await alice.SendGameAsync(GuildService.ChangeOfficial, new GuildChangeOfficialReq { TarPlayerID = bobId, TarOfficial = 2 });
        Assert.Equal(0, (await Next(alice, GuildService.ChangeOfficial, GuildChangeOfficialResp.Parser)).Result);
        await bob.SendGameAsync(GuildService.Leave, new GuildMemberLeaveReq());
        Assert.Equal(0, (await Next(bob, GuildService.Leave, GuildMemberLeaveResp.Parser)).Result);

        var all = await Journal(alice, 0, 20);
        Assert.Equal(0, all.Result);
        Assert.Equal([2L, 4L, 1L], all.Datas.Select(d => d.Data.EventCfgID));
        Assert.Equal([3L, 2L, 1L], all.Datas.Select(d => d.Index));
        Assert.Equal(3, all.LastIndex);
        Assert.True(all.ToTop);
        // Formatter arity: approval = approver, newcomer, newcomer; position = actor, target, old, new.
        var approval = all.Datas[2].Data;
        Assert.Equal(["Alice", "Bob", "Bob"], approval.Param.Param.Select(p => p.StrParam));
        Assert.Equal([aliceId, bobId], approval.PlayerID);
        var position = all.Datas[1].Data.Param.Param;
        Assert.Equal([LanguageParamType.Str, LanguageParamType.Str, LanguageParamType.Int, LanguageParamType.Int],
            position.Select(p => p.Tp));
        Assert.Equal([20L, 2L], position.Skip(2).Select(p => p.IntParam));
        Assert.Equal(["Bob"], all.Datas[0].Data.Param.Param.Select(p => p.StrParam));

        // Scrolling down asks for the next older page below the oldest shown index.
        var first = await Journal(alice, 0, 1);
        Assert.False(first.ToTop);
        var older = await Journal(alice, first.Datas[0].Index - 1, 1);
        Assert.Equal(4, Assert.Single(older.Datas).Data.EventCfgID);

        // The dropdown filters by GuildEventConfig._iType (2 = positions); "only me" keeps rows naming the viewer.
        Assert.Equal(4, Assert.Single((await Journal(alice, 0, 20, false, 2)).Datas).Data.EventCfgID);
        Assert.Equal([4L, 1L], (await Journal(alice, 0, 20, true)).Datas.Select(d => d.Data.EventCfgID));
        Assert.Empty((await Journal(alice, 0, 20, false, 3)).Datas);
        Assert.Equal(ErrorCodes.NotInGuild, (await Journal(bob, 0, 20)).Result);
    }
}
