using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>GuildService.Impeach: 32235 starts or cancels an impeachment of an absent leader, 32236 responds, and the
/// initiator takes the guild once enough members responded; the hour running out or the initiator leaving ends it.</summary>
public sealed class GuildImpeachTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-impeach-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private Task<T> Read<T>(Func<T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));

    public async Task InitializeAsync()
    {
        var (web, micro, gate) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = gate;
        });
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

    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);

    private async Task Until(Func<Task<bool>> condition, string what)
    {
        for (int i = 0; i < 500; i++) { if (await condition()) return; await Task.Delay(20); }
        Assert.Fail("timed out: " + what);
    }

    private static async Task<int> Impeach(BotClient bot, bool cancel = false)
    {
        await bot.SendGameAsync(GuildService.Peach, new GuildPeachReq { IsCancle = cancel });
        var resp = await Next(bot, GuildService.Peach, GuildPeachResp.Parser);
        Assert.Equal(cancel, resp.IsCancle);
        return resp.Result;
    }

    private static async Task<int> Respond(BotClient bot)
    {
        await bot.SendGameAsync(GuildService.PeachApply, new GuildPeachApplyReq());
        return (await Next(bot, GuildService.PeachApply, GuildPeachApplyResp.Parser)).Result;
    }

    private static Task<GuildPeachInfo> PeachPush(BotClient bot, Func<GuildPeachInfo, bool> match) =>
        Next(bot, GuildService.BaseChange, GuildBaseInfoChangeNtf.Parser,
            n => n.Tp == GuildBaseInfoChangeNtf.Types.ChangeType.ChangePeach && match(n.Peach)).ContinueWith(t => t.Result.Peach);

    /// <summary>The impeachment letters of one role: template and its parameters as text.</summary>
    private List<(int Template, string[] Params)> Letters(long role)
    {
        var bytes = Service<RoleStore>().LoadMailbox(role);
        if (bytes == null) return [];
        return System.Text.Json.JsonSerializer.Deserialize<MailService.Mailbox>(bytes)!.Letters
            .Where(l => l.TemplateId is >= 1001 and <= 1006)
            .Select(l => (l.TemplateId, l.ContentParams is { } p ? LanguageData.Parser.ParseFrom(p).Param
                .Select(x => x.Tp == LanguageParamType.Str ? x.StrParam : x.IntParam.ToString()).ToArray() : []))
            .ToList();
    }

    /// <summary>Edit the stored guild directly (only while no guild request is in flight).</summary>
    private Task Edit(long guildId, Action<GuildStore.State> change) => Read(() =>
    {
        var store = Service<GuildStore>();
        var g = store.Get(guildId)!;
        change(g);
        Assert.True(store.Save(g));
        return 0;
    });

    private static readonly long Week = 8 * 86400;

    [ClientDataFact]
    public async Task MembersImpeachAnAbsentLeaderWhoLosesTheGuild()
    {
        var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        await using var dave = await InWorld("Dave");
        foreach (var bot in new[] { alice, bob, carol, dave }) await Gm(bot, "!lvl 35");
        long aliceId = Session(alice).Role!.Id, bobId = Session(bob).Role!.Id, carolId = Session(carol).Role!.Id, daveId = Session(dave).Role!.Id;

        await alice.SendGameAsync(GuildService.Create, new GuildCreateReq
        { Name = "Astra", Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 } });
        Assert.Equal(0, (await Next(alice, GuildService.Create, GuildCreateResp.Parser)).Result);
        long guildId = (await Next(alice, GuildService.Full, GuildFullInfoNtf.Parser, m => m.Guid > 0)).Guid;
        foreach (var (bot, id) in new[] { (bob, bobId), (carol, carolId), (dave, daveId) })
        {
            await bot.SendGameAsync(GuildService.Apply, new GuildEnterApplyReq { GuildIds = { guildId } });
            await Next(bot, GuildService.Apply, GuildEnterApplyResp.Parser);
            await alice.SendGameAsync(GuildService.ApplyOpe, new GuildEnterApplyOpeReq { Agree = true, PlayerIds = { id } });
            Assert.Equal(0, (await Next(alice, GuildService.ApplyOpe, GuildEnterApplyOpeResp.Parser)).Result);
        }
        GuildStore.State Guild() => Service<GuildStore>().Get(guildId)!;

        // The leader cannot impeach; a member of a few minutes cannot; nothing runs to respond to.
        Assert.Equal(ErrorCodes.ImpeachSelf, await Impeach(alice));
        Assert.Equal(ErrorCodes.ImpeachRequirements, await Impeach(bob));
        Assert.Equal(ErrorCodes.ImpeachNotRunning, await Respond(carol));
        await Edit(guildId, g => { foreach (var m in g.Members.Values) m.EnterTime -= Week * 1000; });
        // A leader who is online, or was seen this week, gives no grounds.
        Assert.Equal(ErrorCodes.ImpeachNoGrounds, await Impeach(bob));
        await alice.DisposeAsync();
        await Until(async () => await Read(() => Guild().Members[aliceId].OfflineTime) > 0, "the leader offline");
        Assert.Equal(ErrorCodes.ImpeachNoGrounds, await Impeach(bob));
        await Edit(guildId, g => g.Members[aliceId].OfflineTime -= Week);

        // The cost is 30 Sacred Relic Crystals (item 3).
        Assert.Equal(ErrorCodes.ItemsShort, await Impeach(bob));
        Assert.Null(await Read(() => Guild().Impeach));
        await Gm(bob, "!item 3 60");
        await Until(async () => await Read(() => Session(bob).Progress!.Currency.GetValueOrDefault(3)) == 60, "crystals");

        // Started: three members are active (Bob, Carol, Dave), so two responses are needed and Bob's own counts.
        Assert.Equal(0, await Impeach(bob));
        var started = await PeachPush(carol, p => p.IsPeaching);
        Assert.Equal(bobId, started.PeachOwnerID);
        Assert.Equal(2, started.NeedCount);
        Assert.Equal([bobId], started.PeachPlayerIDs);
        Assert.True(started.CanPeach);
        Assert.InRange(started.PeachEndTime - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 3_500_000, 3_600_000);
        Assert.Equal(30, await Read(() => Session(bob).Progress!.Currency.GetValueOrDefault(3)));
        Assert.Contains((1005, new[] { "1", "2" }), Letters(bobId).Select(l => (l.Template, l.Params)), LetterComparer.Instance);
        Assert.Contains((1006, new[] { "Bob", "Alice", "1", "2", "Bob" }), Letters(carolId).Select(l => (l.Template, l.Params)),
            LetterComparer.Instance);
        Assert.Equal(ErrorCodes.ImpeachRunning, await Impeach(carol));
        Assert.Equal(ErrorCodes.ImpeachRepeat, await Respond(bob));
        Assert.Equal(ErrorCodes.ImpeachCancelState, await Impeach(carol, cancel: true));

        // Cancelled by Bob: mail 1002 and a day's cooldown.
        Assert.Equal(0, await Impeach(bob, cancel: true));
        var cancelled = await PeachPush(dave, p => !p.IsPeaching);
        Assert.InRange(cancelled.NextPeachTime - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 86_000_000, 86_400_000);
        Assert.Contains(Letters(daveId), l => l.Template == 1002 && l.Params.SequenceEqual(["Bob", "Alice"]));
        Assert.Equal(ErrorCodes.ImpeachCooldown, await Impeach(bob));

        // Started again after the cooldown; Carol's response is the second, so Bob takes the guild at once.
        await Edit(guildId, g => g.NextImpeachMs = 0);
        Assert.Equal(0, await Impeach(bob));
        Assert.Equal(0, await Respond(carol));
        await PeachPush(dave, p => !p.IsPeaching && p.NextPeachTime > 0);
        var guild = await Read(() => Guild());
        Assert.Equal(1, guild.Members[bobId].Official);
        Assert.Equal(20, guild.Members[aliceId].Official);
        Assert.Null(guild.Impeach);
        Assert.Equal(0, await Read(() => Session(bob).Progress!.Currency.GetValueOrDefault(3)));
        Assert.Contains(Letters(aliceId), l => l.Template == 1001 && l.Params.SequenceEqual(["Bob", "Alice"]));
        Assert.Equal(1, (await Next(bob, GuildService.GuildSync, DBRoleGuildInfoIncrement.Parser, x => x.Official == 1)).Official);

        // Bob now leads and is absent in turn. Carol starts one; Alice, seen this week, counts as active: two are needed.
        await bob.DisposeAsync();
        await Until(async () => await Read(() => Guild().Members[bobId].OfflineTime) > 0, "the new leader offline");
        await Edit(guildId, g =>
        {
            g.Members[bobId].OfflineTime -= Week;
            g.Members[aliceId].OfflineTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600;
            g.NextImpeachMs = 0;
        });
        await Gm(carol, "!item 3 60");
        await Until(async () => await Read(() => Session(carol).Progress!.Currency.GetValueOrDefault(3)) == 60, "crystals");
        Assert.Equal(0, await Impeach(carol));
        Assert.Equal(2, (await PeachPush(dave, p => p.IsPeaching && p.PeachOwnerID == carolId)).NeedCount);

        // The hour runs out: mail 1004 and the cooldown.
        await Edit(guildId, g => g.Impeach!.EndMs = 1);
        await Service<GameLoop>().InvokeAsync(() => Service<GuildService>().ReconcileAsync());
        await PeachPush(dave, p => !p.IsPeaching && p.NextPeachTime > 0);
        Assert.Contains(Letters(daveId), l => l.Template == 1004 && l.Params.SequenceEqual(["Carol"]));

        // Again, and Carol leaves the guild: mail 1003 ends it.
        await Edit(guildId, g => g.NextImpeachMs = 0);
        Assert.Equal(0, await Impeach(carol));
        await PeachPush(dave, p => p.IsPeaching && p.PeachOwnerID == carolId);
        await carol.SendGameAsync(GuildService.Leave, new GuildMemberLeaveReq());
        Assert.Equal(0, (await Next(carol, GuildService.Leave, GuildMemberLeaveResp.Parser)).Result);
        await PeachPush(dave, p => !p.IsPeaching && p.NextPeachTime > 0);
        Assert.Null(await Read(() => Guild().Impeach));
        Assert.Contains(Letters(daveId), l => l.Template == 1003 && l.Params.SequenceEqual(["Carol", "Bob"]));
        Assert.Contains(Letters(carolId), l => l.Template == 1003);
    }

    private sealed class LetterComparer : IEqualityComparer<(int, string[])>
    {
        public static readonly LetterComparer Instance = new();
        public bool Equals((int, string[]) a, (int, string[]) b) => a.Item1 == b.Item1 && a.Item2.SequenceEqual(b.Item2);
        public int GetHashCode((int, string[]) x) => x.Item1;
    }
}
