using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>GuildService.GroupMail: 32241 sends a Guild-tab letter to every member, within the right, the cooldown,
/// the daily count and the length.</summary>
public sealed class GuildGroupMailTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-guildmail-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private WebApplication _app = null!;
    private string _http = "";

    private sealed class Clock : TimeProvider
    {
        private long _seconds = new DateTimeOffset(2026, 10, 15, 14, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _seconds));
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Advance(long by) => Interlocked.Add(ref _seconds, by);
    }

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private Task<T> Read<T>(Func<T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));

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

    private static async Task<GuildBroadEmailResp> Send(BotClient bot, string text)
    {
        await bot.SendGameAsync(GuildService.BroadEmail, new GuildBroadEmailReq
        { Req = new SendGuildMailReq { Title = "Astra", Content = text } });
        return await Next(bot, GuildService.BroadEmail, GuildBroadEmailResp.Parser);
    }

    private static async Task<object?[]> GuildLetters(BotClient bot, long role)
    {
        var response = await bot.MicroAsync("MailService", "getUserMailList", new Dictionary<string, object?>
        { ["Uid"] = role.ToString(), ["Start"] = 0, ["Limit"] = 150, ["MailType"] = 3 });
        Assert.True(response["Ret"] is object?[] ret && ret[0] is true);
        return (object?[])response["MailList"]!;
    }

    [ClientDataFact]
    public async Task GroupMailReachesEveryMemberWithinItsLimits()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Gm(alice, "!lvl 35");
        await Gm(bob, "!lvl 35");
        long aliceId = RoleId(alice), bobId = RoleId(bob);

        // Not in a guild yet.
        Assert.Equal(ErrorCodes.NotInGuild, (await Send(alice, "hello")).Result);

        await alice.SendGameAsync(GuildService.Create, new GuildCreateReq
        { Name = "Astra", Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 } });
        Assert.Equal(0, (await Next(alice, GuildService.Create, GuildCreateResp.Parser)).Result);
        long guildId = (await Next(alice, GuildService.Full, GuildFullInfoNtf.Parser, m => m.Guid > 0)).Guid;
        await bob.SendGameAsync(GuildService.Apply, new GuildEnterApplyReq { GuildIds = { guildId } });
        await Next(bob, GuildService.Apply, GuildEnterApplyResp.Parser);
        await alice.SendGameAsync(GuildService.ApplyOpe, new GuildEnterApplyOpeReq { Agree = true, PlayerIds = { bobId } });
        Assert.Equal(0, (await Next(alice, GuildService.ApplyOpe, GuildEnterApplyOpeResp.Parser)).Result);
        GuildStore.State Guild() => Service<GuildStore>().Get(guildId)!;
        Assert.True(await Read(() => Guild().Members.ContainsKey(bobId)));

        // A regular member has no Group Mail right (6); empty, too long and over-byte texts are refused.
        Assert.Equal(ErrorCodes.GuildPermission, (await Send(bob, "hello")).Result);
        Assert.Equal(ErrorCodes.MailBodyEmpty, (await Send(alice, "   ")).Result);
        Assert.Equal(ErrorCodes.GuildTextLength, (await Send(alice, new string('a', 81))).Result);
        Assert.Equal(ErrorCodes.GuildTextLength, (await Send(alice, new string('世', 70))).Result);
        Assert.Empty(await GuildLetters(alice, aliceId));

        // Sent: the answer and the 32231 tp=6 push carry the next time (ms) and the count used today.
        long now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var sent = await Send(alice, "  Guild war at nine  ");
        Assert.Equal(0, sent.Result);
        Assert.Equal(now + 3600_000, sent.NextBroadEmailTime);
        Assert.Equal(1, sent.BroadEmailLimit);
        var push = await Next(bob, GuildService.BaseChange, GuildBaseInfoChangeNtf.Parser,
            n => n.Tp == GuildBaseInfoChangeNtf.Types.ChangeType.ChangeBroadEmail);
        Assert.Equal(now + 3600_000, push.NextBroadEmailTime);
        Assert.Equal(1, push.BroadEmailLimit);
        foreach (long role in new[] { aliceId, bobId })
        {
            var letter = (Dictionary<object, object?>)Assert.Single(role == aliceId
                ? await GuildLetters(alice, role) : await GuildLetters(bob, role))!;
            Assert.Equal("Astra", letter["Title"]);
            Assert.Equal("Guild war at nine", letter["Content"]);
            Assert.Equal("Alice", letter["SrcName"]);
            Assert.Equal(3, Convert.ToInt32(letter["MailType"]));
            Assert.Equal(now / 1000 + 30 * 86400, Convert.ToInt64(letter["RemoveTime"]));
        }

        // The cooldown, then the next letter an hour later.
        Assert.Equal(ErrorCodes.Cooldown, (await Send(alice, "again")).Result);
        _clock.Advance(3600);
        Assert.Equal(2, (await Send(alice, "again")).BroadEmailLimit);

        // The tenth letter of the day is the last; the count starts over at the next 05:00.
        // (The clock starts at 14:00, so ten hours stay inside the guild day.)
        for (int n = 3; n <= 10; n++) { _clock.Advance(3600); Assert.Equal(n, (await Send(alice, $"letter {n}")).BroadEmailLimit); }
        _clock.Advance(3600);
        Assert.Equal(ErrorCodes.GuildMailDailyLimit, (await Send(alice, "more")).Result);
        _clock.Advance(86400);
        Assert.Equal(1, (await Send(alice, "a new day")).BroadEmailLimit);

        // Every letter reached the member; the saved state is what the 32213 full info carries.
        Assert.Equal(11, (await GuildLetters(bob, bobId)).Length);
        var full = await Read(() => Guild());
        Assert.Equal(1, full.BroadEmailCount);
        Assert.Equal(_clock.GetUtcNow().ToUnixTimeMilliseconds() + 3600_000, full.NextBroadEmailMs);
    }
}
