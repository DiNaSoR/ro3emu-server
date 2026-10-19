using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// The Tavern (TavernService, MG_Barroom 82481-82514): rooms and the room list, chat channel 10, the mic, moderation,
/// gifts and the Daily Tavern time (5043 DBBarRoomInfo).
/// </summary>
public sealed class TavernTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-tavern-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    private async Task<(BotClient Bot, long Id)> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        var role = await bot.CreateRoleAsync(name);
        await bot.EnterWorldAsync(role.RoleBaseInfo.Id);
        return (bot, role.RoleBaseInfo.Id);
    }

    private static async Task<T> Next<T>(BotClient bot, uint msgId, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(msgId) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task Gm(string account, string line)
    {
        using var http = new HttpClient();
        (await http.PostAsync($"{_http}/ro3emu/gm?account={account}", new StringContent(line))).EnsureSuccessStatusCode();
    }

    private GameSession Session(string name) => Service<SessionRegistry>().All().Single(s => s.Role?.Name == name);
    private Task<T> Read<T>(string name, Func<GameSession, T> read) =>
        Service<GameLoop>().InvokeAsync(() => Task.FromResult(read(Session(name))));
    private Task OnLoop(string name, Action<GameSession> change) =>
        Service<GameLoop>().InvokeAsync(() => { change(Session(name)); return Task.CompletedTask; });

    private async Task Until(string name, Func<GameSession, bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!await Read(name, condition))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what);
            await Task.Delay(50);
        }
    }

    private static async Task<BarRoomTotalDetail> Open(BotClient bot, string name = "Pub")
    {
        await bot.SendGameAsync(TavernService.Create, new CreateBarRoomReq { BarRoomType = 1, BarRoomName = name, AreaId = 1 });
        var created = await Next(bot, TavernService.Create, CreateBarRoomResp.Parser);
        Assert.Equal(0, created.Result);
        return created.Info;
    }

    private static async Task<EnterBarRoomResp> Enter(BotClient bot, long room)
    {
        await bot.SendGameAsync(TavernService.Enter, new EnterBarRoomReq { BarRoomGuid = room, AreaId = 1 });
        return await Next(bot, TavernService.Enter, EnterBarRoomResp.Parser);
    }

    private static object?[] Rooms(Dictionary<object, object?> reply)
    {
        Assert.True(((object?[])reply["Ret"]!)[0] is true);
        return (object?[])reply["SummaryList"]!;
    }

    private static object?[] Query(int partyType) => [partyType, Array.Empty<object>(), Array.Empty<object>(), Array.Empty<object>(), 0, 100];

    [ClientDataFact]
    public async Task RoomsListJoinChatAndClose()
    {
        var (alice, a) = await InWorld("Alice");
        var (bob, b) = await InWorld("Bob");
        var (carol, _) = await InWorld("Carol");
        await using var _a = alice; await using var _b = bob; await using var _c = carol;

        await alice.SendGameAsync(TavernService.Create, new CreateBarRoomReq { BarRoomType = 99, BarRoomName = "Pub" });
        Assert.Equal(2579, (await Next(alice, TavernService.Create, CreateBarRoomResp.Parser)).Result);
        await alice.SendGameAsync(TavernService.Create, new CreateBarRoomReq { BarRoomType = 1, BarRoomName = new string('x', 19) });
        Assert.Equal(2576, (await Next(alice, TavernService.Create, CreateBarRoomResp.Parser)).Result);

        var room = await Open(alice);
        long id = room.BarRoomSummary.BarRoomGuid;
        Assert.Equal(a, room.OwnerGuid);
        Assert.Equal(a, room.CurSpeaks.Single().UserId);
        await Until("Alice", s => s.Tavern!.BarRoomId == id, "5043 barRoomId");

        // The room list is PartyService partyType 4; team queries do not see it.
        var listed = Rooms(await bob.MicroPositionalAsync("PartyService", "getPartysSummary", Query(TavernService.PartyType)));
        var summary = BarRoomSummary.Parser.ParseFrom((byte[])((Dictionary<object, object?>)listed.Single()!)["GameSummaryData"]!);
        Assert.Equal(id, summary.BarRoomGuid);
        Assert.Equal("Pub", summary.BarRoomName);
        Assert.Empty(Rooms(await bob.MicroPositionalAsync("PartyService", "getPartysSummary", Query(0))));

        var entered = await Enter(bob, id);
        Assert.Equal(0, entered.Result);
        Assert.Equal(2, entered.Info.BarRoomSummary.MemberList.Count);
        Assert.Equal(b, (await Next(alice, TavernService.UserEnter, BarRoomUserEnterNotify.Parser)).UserId);
        await Next(alice, TavernService.Get, BarRoomGetResp.Parser, r => r.Info?.BarRoomSummary.MemberList.Count == 2);

        // Chat channel 10 admits members only.
        await bob.JoinChatAsync($"10_{id}");
        await alice.JoinChatAsync($"10_{id}");
        await Assert.ThrowsAnyAsync<Exception>(() => carol.JoinChatAsync($"10_{id}"));
        await bob.SendChatAsync($"10_{id}", 2, "cheers");
        Assert.Equal("cheers", (await alice.NextChatLineAsync()).Content);

        await bob.SendGameAsync(TavernService.Exit, new ExitBarRoomReq { BarRoomGuid = id });
        Assert.Equal(0, (await Next(bob, TavernService.Exit, ExitBarRoomResp.Parser)).Result);
        await Next(alice, TavernService.Get, BarRoomGetResp.Parser, r => r.Info?.BarRoomSummary.MemberList.Count == 1);
        await Assert.ThrowsAnyAsync<Exception>(() => bob.JoinChatAsync($"10_{id}"));

        await alice.SendGameAsync(TavernService.Exit, new ExitBarRoomReq { BarRoomGuid = id });
        Assert.Equal(0, (await Next(alice, TavernService.Exit, ExitBarRoomResp.Parser)).Result);
        Assert.Empty(Rooms(await bob.MicroPositionalAsync("PartyService", "getPartysSummary", Query(TavernService.PartyType))));
        Assert.Equal(2552, (await Enter(bob, id)).Result);
    }

    [ClientDataFact]
    public async Task OwnerRulesMicKickAndHandover()
    {
        var (alice, a) = await InWorld("Alice");
        var (bob, b) = await InWorld("Bob");
        await using var _a = alice; await using var _b = bob;
        long id = (await Open(alice)).BarRoomSummary.BarRoomGuid;
        Assert.Equal(0, (await Enter(bob, id)).Result);

        // Only the owner changes settings.
        await bob.SendGameAsync(TavernService.SetInfo, new SetBarRoomInfoReq { BarRoomGuid = id, BarRoomType = 2, BarRoomName = "Mine" });
        Assert.Equal(2554, (await Next(bob, TavernService.SetInfo, SetBarRoomInfoResp.Parser)).Result);

        // Free mode needs no request; in Set mode the owner approves one.
        await bob.SendGameAsync(TavernService.ApplyMic, new ApplyOpenMicReq { BarRoomGuid = id });
        Assert.Equal(2567, (await Next(bob, TavernService.ApplyMic, ApplyOpenMicResp.Parser)).Result);
        Assert.True(await Read("Bob", _ => Service<TavernService>().MayTalk(b)));
        await alice.SendGameAsync(TavernService.MicMode, new ModifyControlMicTypeReq { BarRoomGuid = id, ControlMicType = SpeakAuthorityType.Manual });
        Assert.Equal(0, (await Next(alice, TavernService.MicMode, ModifyControlMicTypeResp.Parser)).Result);
        Assert.False(await Read("Bob", _ => Service<TavernService>().MayTalk(b)));
        await bob.SendGameAsync(TavernService.ApplyMic, new ApplyOpenMicReq { BarRoomGuid = id, Reason = "song" });
        Assert.Equal(0, (await Next(bob, TavernService.ApplyMic, ApplyOpenMicResp.Parser)).Result);
        Assert.Equal("song", (await Next(alice, TavernService.Get, BarRoomGetResp.Parser, r => r.Info?.ApplyUsers.ContainsKey(b) == true)).Info.ApplyUsers[b]);
        await alice.SendGameAsync(TavernService.HandleMic, new OwnerHandleOpenMicReq { BarRoomGuid = id, ApplyerId = b, IsAgree = true });
        Assert.Equal(0, (await Next(alice, TavernService.HandleMic, OwnerHandleOpenMicResp.Parser)).Result);
        Assert.True(await Read("Bob", _ => Service<TavernService>().MayTalk(b)));

        // A mute keeps its value equal to the role (LC_BarRoomHelper.CheckIsForbiddenUser).
        await alice.SendGameAsync(TavernService.Forbid, new ForbiddenUserReq { BarRoomGuid = id, TargetGuid = b });
        Assert.Equal(0, (await Next(alice, TavernService.Forbid, ForbiddenUserResp.Parser)).Result);
        var muted = await Next(bob, TavernService.Get, BarRoomGetResp.Parser, r => r.Info?.ForbiddenSpeaker.ContainsKey(b) == true);
        Assert.Equal(b, muted.Info.ForbiddenSpeaker[b]);
        Assert.DoesNotContain(muted.Info.CurSpeaks, s => s.UserId == b);
        await alice.SendGameAsync(TavernService.CancelForbid, new CanecelForbiddenUserReq { BarRoomGuid = id, TargetGuid = b });
        Assert.Equal(0, (await Next(alice, TavernService.CancelForbid, CanecelForbiddenUserResp.Parser)).Result);

        // A kick sends the target 82483 and blocks it.
        await alice.SendGameAsync(TavernService.Kick, new KickUserReq { BarRoomGuid = id, TargetGuid = b });
        Assert.Equal(0, (await Next(alice, TavernService.Kick, KickUserResp.Parser)).Result);
        Assert.Equal(0, (await Next(bob, TavernService.Exit, ExitBarRoomResp.Parser)).Result);
        Assert.Equal(2553, (await Enter(bob, id)).Result);
        await alice.SendGameAsync(TavernService.CancelKick, new CancelKickUserReq { BarRoomGuid = id, TargetGuid = b });
        Assert.Equal(0, (await Next(alice, TavernService.CancelKick, CancelKickUserResp.Parser)).Result);
        Assert.Equal(0, (await Enter(bob, id)).Result);

        // The owner leaving hands the room to the next member.
        await alice.SendGameAsync(TavernService.Exit, new ExitBarRoomReq { BarRoomGuid = id });
        var handed = await Next(bob, TavernService.Get, BarRoomGetResp.Parser, r => r.Info?.OwnerGuid == b);
        Assert.Equal(b, handed.Info.CurSpeaks[0].UserId);
        Assert.NotEqual(0, a);
    }

    [ClientDataFact]
    public async Task InviteGiftAndDailyTime()
    {
        var (alice, a) = await InWorld("Alice");
        var (bob, b) = await InWorld("Bob");
        await using var _a = alice; await using var _b = bob;
        long id = (await Open(alice)).BarRoomSummary.BarRoomGuid;

        await alice.SendGameAsync(TavernService.Invite, new InviteEnterBarRoomReq { BarRoomGuid = id, TargetGuidList = { b }, InviterLevel = 1 });
        Assert.Equal(b, (await Next(alice, TavernService.Invite, InviteEnterBarRoomResp.Parser)).TargetGuidList.Single());
        var invite = await Next(bob, TavernService.InviteNotify, InviteEnterBarRoomNotify.Parser);
        Assert.Equal(("Pub", a), (invite.RoomName, invite.InviterGuid));
        await bob.SendGameAsync(TavernService.InviteResponse, new ResponseInviteEnterBarRoomReq { BarRoomGuid = id, InviterGuid = a, IsAccept = true });
        Assert.Equal(0, (await Next(bob, TavernService.InviteResponse, ResponseInviteEnterBarRoomResp.Parser)).Result);
        Assert.True((await Next(alice, TavernService.InviteResponse, ResponseInviteEnterBarRoomResp.Parser)).IsAccept);
        Assert.True(await Read("Bob", _ => Service<TavernService>().InRoom(b, id)));

        // Gingerbread: room Popularity 10 and Star Power 5 each.
        await alice.SendGameAsync(TavernService.Gift, new PresentGiftReq { BarRoomGuid = id, TargetGuid = b, GiftId = 1200802, GiftNum = 2 });
        Assert.Equal(154, (await Next(alice, TavernService.Gift, PresentGiftResp.Parser)).Result);
        await Gm("alice", "!item 1200802 2");
        await alice.SendGameAsync(TavernService.Gift, new PresentGiftReq { BarRoomGuid = id, TargetGuid = a, GiftId = 1200802, GiftNum = 1 });
        Assert.Equal(2574, (await Next(alice, TavernService.Gift, PresentGiftResp.Parser)).Result);
        await alice.SendGameAsync(TavernService.Gift, new PresentGiftReq { BarRoomGuid = id, TargetGuid = b, GiftId = 1200802, GiftNum = 2 });
        var gift = await Next(alice, TavernService.Gift, PresentGiftResp.Parser);
        Assert.Equal((0, 20, 10, b), (gift.Result, gift.HotAdd, gift.UserBarRoomHot, gift.ReceiveUser));
        Assert.Equal(20, (await Next(bob, TavernService.Get, BarRoomGetResp.Parser, r => r.Info?.BarRoomSummary.HotValue == 20)).Info.BarRoomSummary.HotValue);
        await Until("Bob", s => s.Tavern!.Popularity == 10 && s.Tavern.WeekPopularity == 10, "Star Power");

        // 20 minutes in a room complete the day (task 2000's [106010, 1200]).
        await OnLoop("Bob", s => s.TavernCountedAt -= 1300);
        await Until("Bob", s => s.Tavern!.IsToDayActComplete && s.Tavern.ActDays == 1 && s.Tavern.InBarRoomTime >= 1200, "daily time");
        Assert.True(await Read("Bob", s => Service<TavernService>().SecondsToday(s) >= 1200));
    }
}
