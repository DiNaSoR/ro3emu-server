using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>GuildService.Merge: one guild invites another to merge into it, the other agrees and picks its members, and
/// at the next 05:00 those members join the inviter while their old guild is disbanded.</summary>
public sealed class GuildMergeTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-merge-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private WebApplication _app = null!;
    private string _http = "";

    private sealed class Clock : TimeProvider
    {
        // A Thursday afternoon.
        private long _seconds = new DateTimeOffset(2026, 10, 15, 14, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _seconds));
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Advance(long by) => Interlocked.Add(ref _seconds, by);
    }

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private Task<T> Read<T>(Func<T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));
    private Task Reconcile() => Service<GameLoop>().InvokeAsync(() => Service<GuildService>().ReconcileAsync());

    public async Task InitializeAsync()
    {
        var (web, micro, gate) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = gate;
        }, _clock);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        await bot.EnterWorldAsync((await bot.CreateRoleAsync(name)).RoleBaseInfo.Id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null)
        where T : IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={bot.Account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private long RoleId(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null).Role!.Id;

    private static async Task<long> Found(BotClient bot, string name)
    {
        await bot.SendGameAsync(GuildService.Create, new GuildCreateReq
        { Name = name, Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 } });
        Assert.Equal(0, (await Next(bot, GuildService.Create, GuildCreateResp.Parser)).Result);
        return (await Next(bot, GuildService.Full, GuildFullInfoNtf.Parser, m => m.Guid > 0)).Guid;
    }

    private async Task Join(BotClient leader, BotClient bot, long guildId)
    {
        await bot.SendGameAsync(GuildService.Apply, new GuildEnterApplyReq { GuildIds = { guildId } });
        await Next(bot, GuildService.Apply, GuildEnterApplyResp.Parser);
        await leader.SendGameAsync(GuildService.ApplyOpe, new GuildEnterApplyOpeReq { Agree = true, PlayerIds = { RoleId(bot) } });
        Assert.Equal(0, (await Next(leader, GuildService.ApplyOpe, GuildEnterApplyOpeResp.Parser)).Result);
    }

    private static async Task<int> Invite(BotClient bot, long target, bool send = true, string announce = "Join us")
    {
        await bot.SendGameAsync(GuildService.MergeInvite, new InviteMergeGuildReq { Guildid = target, IsAgree = send, Announce = announce });
        var resp = await Next(bot, GuildService.MergeInvite, InviteMergeGuildRes.Parser);
        Assert.Equal(target, resp.Guildid);
        Assert.Equal(send, resp.IsAgree);
        return resp.Result;
    }

    private static async Task<int> Answer(BotClient bot, long inviter, bool agree)
    {
        await bot.SendGameAsync(GuildService.MergeAnswer, new MergeGuildOpeReq { Guildid = inviter, IsAgree = agree });
        return (await Next(bot, GuildService.MergeAnswer, MergeGuildOpeRes.Parser)).Result;
    }

    private static async Task<PullMergeGuildDataRes> Lists(BotClient bot)
    {
        await bot.SendGameAsync(GuildService.MergeLists, new PullMergeGuildDataReq());
        return await Next(bot, GuildService.MergeLists, PullMergeGuildDataRes.Parser);
    }

    private static async Task<MergeGuildDetailDataRes> Detail(BotClient bot, long guildId, bool isInvite)
    {
        await bot.SendGameAsync(GuildService.MergeDetail, new MergeGuildDetailDataReq { Guildid = guildId, IsInvite = isInvite });
        return await Next(bot, GuildService.MergeDetail, MergeGuildDetailDataRes.Parser);
    }

    private static async Task<SaveMergeMemberRes> Save(BotClient bot, long inviter, params long[] members)
    {
        await bot.SendGameAsync(GuildService.MergeSaveMembers, new SaveMergeMemberReq { Guildid = inviter, Memberids = { members } });
        return await Next(bot, GuildService.MergeSaveMembers, SaveMergeMemberRes.Parser);
    }

    [ClientDataFact]
    public async Task AGuildMergesIntoTheInviterAtFiveTheNextMorning()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        await using var dave = await InWorld("Dave");
        await using var eve = await InWorld("Eve");
        foreach (var bot in new[] { alice, bob, carol, dave, eve }) await Gm(bot, "!lvl 35");
        long bobId = RoleId(bob), carolId = RoleId(carol), daveId = RoleId(dave), aliceId = RoleId(alice);
        long astra = await Found(alice, "Astra");
        long borea = await Found(bob, "Borea");
        await Join(bob, carol, borea);
        await Join(bob, dave, borea);
        GuildStore.State? Guild(long id) => Service<GuildStore>().Get(id);

        // Only a member with the Merge Guild right (21) uses the tab.
        Assert.Equal(ErrorCodes.GuildPermission, (await Lists(carol)).Result);
        await alice.SendGameAsync(GuildService.MergeRecommend, new PullRecommendMergeGuildDataReq());
        var recommended = await Next(alice, GuildService.MergeRecommend, PullRecommendMergeGuildDataRes.Parser);
        Assert.Contains(recommended.Data, g => g.Guid == borea);
        Assert.DoesNotContain(recommended.Data, g => g.Guid == astra);

        // Astra invites Borea; both lists show it and the comparison puts Borea on the left and Astra on the right.
        Assert.Equal(ErrorCodes.MergeAnnounce, await Invite(alice, borea, announce: new string('a', 51)));
        Assert.Equal(0, await Invite(alice, borea));
        Assert.Equal(ErrorCodes.MergeInviteRepeat, await Invite(alice, borea));
        Assert.Equal(borea, Assert.Single((await Lists(alice)).SendInvite).Guid);
        Assert.Equal(astra, Assert.Single((await Lists(bob)).RecvInvite).Guid);
        var detail = await Detail(bob, astra, isInvite: false);
        Assert.Equal(0, detail.Result);
        Assert.Equal(borea, detail.TargetGuildData.Guid);
        Assert.Equal(3, detail.TargetGuildData.MemberCount);
        Assert.Equal(astra, detail.InvitedGuildData.Guid);
        Assert.Equal(astra, detail.NewGuildData.Guid);
        Assert.Equal(4, detail.NewGuildData.MemberCount);
        Assert.Equal("Join us", detail.Announce);
        long room = await Read(() => Service<GuildService>().MaxMembers(Guild(astra)!) - 1);
        Assert.Equal(room, detail.CanMergeCount);
        Assert.Equal(detail.TargetGuildData.Buildind.Select(b => b.BuildingID), detail.InvitedGuildData.Buildind.Select(b => b.BuildingID));
        Assert.Equal(detail.InvitedGuildData.Buildind.Select(b => b.BuildingID), detail.NewGuildData.Buildind.Select(b => b.BuildingID));

        // Refused: Astra cannot ask again for 30 minutes.
        Assert.Equal(0, await Answer(bob, astra, agree: false));
        Assert.Empty((await Lists(bob)).RecvInvite);
        Assert.Equal(ErrorCodes.MergeRefused, await Invite(alice, borea));
        _clock.Advance(31 * 60);
        await Reconcile();
        Assert.Equal(0, await Invite(alice, borea));

        // Withdrawn, then sent again.
        Assert.Equal(0, await Invite(alice, borea, send: false));
        Assert.Equal(ErrorCodes.MergeAcceptMissing, await Answer(bob, astra, agree: true));
        Assert.Equal(0, await Invite(alice, borea));

        // Borea agrees: both guilds show the pending merge, and Borea's members are preselected.
        Assert.Equal(0, await Answer(bob, astra, agree: true));
        Assert.Equal(borea, (await Next(alice, GuildService.BaseChange, GuildBaseInfoChangeNtf.Parser,
            n => n.Tp == GuildBaseInfoChangeNtf.Types.ChangeType.ChangeBase && n.Base.PendingMergeGuildId != 0)).Base.PendingMergeGuildId);
        Assert.Equal(astra, (await Next(carol, GuildService.BaseChange, GuildBaseInfoChangeNtf.Parser,
            n => n.Tp == GuildBaseInfoChangeNtf.Types.ChangeType.ChangeBase && n.Base.PendingMergeGuildId != 0)).Base.PendingMergeGuildId);
        var merging = await Detail(bob, 0, false);
        Assert.Equal(0, merging.Result);
        Assert.Equal(new[] { bobId, carolId, daveId }.Order(), merging.Memberids.Order());
        long fiveAm = new DateTimeOffset(2026, 10, 16, 5, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        Assert.Equal(fiveAm, merging.LastMergeTime);
        Assert.Equal(fiveAm, (await Detail(alice, 0, false)).LastMergeTime);
        Assert.Equal(ErrorCodes.GuildPermission, await Invite(alice, borea));

        // Borea keeps Bob and Carol; Dave stays behind. Only the merging guild adjusts, and only its own members.
        Assert.Equal(ErrorCodes.GuildMemberMissing, (await Save(bob, astra, bobId, aliceId)).Result);
        Assert.Equal(ErrorCodes.MergeInviteMissing, (await Save(alice, borea, aliceId)).Result);
        var saved = await Save(bob, astra, bobId, carolId);
        Assert.Equal(0, saved.Result);
        Assert.Equal(new[] { bobId, carolId }, saved.Memberids);

        // Not before 05:00.
        _clock.Advance(fiveAm - _clock.GetUtcNow().ToUnixTimeSeconds() - 60);
        await Reconcile();
        Assert.NotNull(await Read(() => Guild(borea)));

        // 05:00: Bob and Carol are regular members of Astra, Dave has no guild, Borea is gone.
        _clock.Advance(120);
        await Reconcile();
        Assert.Null(await Read(() => Guild(borea)));
        var merged = (await Read(() => Guild(astra)))!;
        Assert.Equal(20, merged.Members[bobId].Official);
        Assert.Equal(20, merged.Members[carolId].Official);
        Assert.Equal(1, merged.Members[aliceId].Official);
        Assert.False(merged.Members.ContainsKey(daveId));
        Assert.Equal(0, merged.PendingMergeGuildId);
        Assert.Null(await Read(() => Service<GuildStore>().ForRole(daveId)));
        Assert.Equal(astra, (await Next(carol, GuildService.Full, GuildFullInfoNtf.Parser, f => f.Guid == astra)).Guid);
        await Next(dave, GuildService.Full, GuildFullInfoNtf.Parser, f => f.Guid == 0);
        var letters = System.Text.Json.JsonSerializer.Deserialize<MailService.Mailbox>(Service<RoleStore>().LoadMailbox(carolId)!)!.Letters;
        var letter = Assert.Single(letters, l => l.TemplateId == 1121);
        Assert.Equal("Borea", LanguageData.Parser.ParseFrom(letter.ContentParams).Param.Single().StrParam);

        // Astra is on the week's merge cooldown, both ways.
        long cyra = await Found(eve, "Cyra");
        Assert.Equal(ErrorCodes.MergeOwnCooldown, await Invite(alice, cyra));
        Assert.Equal(ErrorCodes.MergeOtherCooldown, await Invite(eve, astra));
    }
}
