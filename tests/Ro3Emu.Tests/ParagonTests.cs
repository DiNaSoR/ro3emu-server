using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;
/// <summary>
/// The Astral Level (ParagonService): the 5015 sync once the level cap opens function 71, Base EXP past the cap filling
/// the Paragon level with the world-level bonus, points into stars in order, Star Cores socketed and returned, both
/// resets, the attributes, and the saved state across relog.
/// </summary>
public sealed class ParagonTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-paragon-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private WebApplication _app = null!;
    private string _httpUrl = "";

    public async Task InitializeAsync()
    {
        var (http, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = http;
            o.MicroGatePort = micro;
            o.GameGatePort = game;
        });
        await _app.StartAsync();
        _httpUrl = $"http://127.0.0.1:{http}";
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

    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(_httpUrl, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint msgId, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(msgId) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task Gm(string account, string command)
    {
        using var http = new HttpClient();
        var response = await http.PostAsync($"{_httpUrl}/ro3emu/gm?account={account}", new StringContent(command));
        response.EnsureSuccessStatusCode();
    }

    private GameSession Session(string name) => Service<SessionRegistry>().All().Single(s => s.Role?.Name == name);
    private Task<T> OnLoop<T>(string name, Func<GameSession, Task<T>> work) => Service<GameLoop>().InvokeAsync(() => work(Session(name)));

    private GameData Data => Service<GameData>();

    private static async Task<int> Ask<TReq, TResp>(BotClient bot, uint id, TReq req, Google.Protobuf.MessageParser<TResp> parser, Func<TResp, int> result)
        where TReq : Google.Protobuf.IMessage<TReq> where TResp : Google.Protobuf.IMessage<TResp>
    {
        await bot.SendGameAsync(id, req);
        return result(await Next(bot, id, parser));
    }

    private static Task<int> Point(BotClient bot, int star, int points = 1) => Ask(bot, ParagonService.Allocate,
        new ParagonLevelAllocatePointReq { Slot = star, Points = points }, ParagonLevelAllocatePointResp.Parser, r => r.Result);

    private Task<DBParagonLevel> State() => OnLoop("Alice", s => Task.FromResult(Service<ParagonService>().Snapshot(s)));

    private Task<long> Count(int entry) => OnLoop("Alice", s => Task.FromResult(s.Items.Where(i => i.Grid == 0 && i.Entry == entry).Sum(i => (long)i.Stack)));

    [ClientDataFact]
    public async Task AstralLevelFillsAtTheCapAndSpendsPointsOnStarsAndCores()
    {
        long roleId;
        await using (var alice = await InWorld("Alice"))
        {
            Assert.Equal(ErrorCodes.FeatureUnavailable, await Point(alice, 1));
            await Gm("alice", "!lvl 99");
            var first = await Next(alice, ParagonService.Sync, DBParagonLevelIncrement.Parser);
            Assert.True(first.SyncAllMark);
            Assert.Equal(1, first.Lv);
            Assert.Equal(1, first.LeftPoints);
            Assert.Equal(36, first.Runegroup.Count);
            Assert.Equal(1, Assert.Single(first.ActiveGroup).Value.All);
            // World Paragon level 100, the role 99 below: PatronExpAddConfig -10 -> 200000 (shown as 20.0%).
            Assert.Equal(200000, first.ExtraAdd);

            // 3000 Base EXP at the cap x 1.2 = 3600: level 2 (3000) with 600 over, and a second point.
            await Gm("alice", "!exp 3000");
            await Next(alice, ParagonService.Sync, DBParagonLevelIncrement.Parser, m => m.Lv == 2);
            var two = await State();
            Assert.Equal(600, two.Exp);
            Assert.Equal(2, two.LeftPoints);

            Assert.Equal(ParagonService.ErrPrerequisite, await Point(alice, 2));
            Assert.Equal(ParagonService.ErrSlot, await Point(alice, 28));
            Assert.Equal(0, await Point(alice, 1));
            Assert.Equal(0, await Point(alice, 1));
            Assert.Equal(ParagonService.ErrPoints, await Point(alice, 1));
            var attrs = await OnLoop("Alice", s => Task.FromResult(Service<ParagonService>().Attributes(s).ToList()));
            Assert.Equal(GameData.Pairs(Data.Row("PatronTalentAttrConfig", 2)!.Value, "_kAttrs").ToList(), attrs);

            // Star core node 6 (920) needs 1 -> 2 -> 3 -> 5 maxed.
            await OnLoop("Alice", async s => { await Service<ParagonService>().SetLevelAsync(s, 20); return true; });
            Assert.Equal(0, await Point(alice, 1));
            foreach (int star in new[] { 2, 3, 5 }) Assert.Equal(0, await Point(alice, star, 3));
            Assert.Equal(ParagonService.ErrMaxPoints, await Point(alice, 5));
            Assert.Equal(0, await Point(alice, 6));

            await Gm("alice", "!item 1201001 1");
            await Gm("alice", "!item 1201007 1");
            long Guid(GameSession s, int entry) => s.Items.First(i => i.Grid == 0 && i.Entry == entry).Guid;
            long prism = await OnLoop("Alice", s => Task.FromResult(Guid(s, 1201001)));
            long sun = await OnLoop("Alice", s => Task.FromResult(Guid(s, 1201007)));
            Task<int> Install(int slot, long guid) => Ask(alice, ParagonService.Install, new ParagonLevelRuneInstallReq { Slot = slot, RuneGuid = guid },
                ParagonLevelRuneInstallResp.Parser, r => r.Result);
            Assert.Equal(ParagonService.ErrRuneType, await Install(6, sun));
            Assert.Equal(ParagonService.ErrSlot, await Install(5, prism));
            Assert.Equal(ParagonService.ErrSlotInactive, await Install(11, prism));
            Assert.Equal(0, await Install(6, prism));
            Assert.Equal(0, await Count(1201001));
            Assert.Equal(1201001, (await State()).Runegroup[6].Rune.ItemEntry);
            var withCore = await OnLoop("Alice", s => Task.FromResult(Service<ParagonService>().Attributes(s).ToList()));
            Assert.Contains((103L, 10.0), withCore); // PatronRuneConfig 1201001 [[103, 10]]
            Assert.Equal(0, await Ask(alice, ParagonService.Remove, new ParagonLevelRuneRemoveReq { SlotId = 6 },
                ParagonLevelRuneRemoveResp.Parser, r => r.Result));
            Assert.Equal(1, await Count(1201001));
            Assert.Equal(0, (await State()).Runegroup[6].Rune.ItemEntry);
            Assert.Equal(ParagonService.ErrNoRune, await Ask(alice, ParagonService.Remove, new ParagonLevelRuneRemoveReq { SlotId = 6 },
                ParagonLevelRuneRemoveResp.Parser, r => r.Result));

            // Resetting star 3 clears 3, 5 and everything after 5 (4, 6, 9 ...) and returns the socketed core.
            prism = await OnLoop("Alice", s => Task.FromResult(Guid(s, 1201001)));
            Assert.Equal(0, await Install(6, prism));
            long before = (await State()).LeftPoints;
            Assert.Equal(0, await Ask(alice, ParagonService.ResetSingle, new ParagonLevelResetSingleReq { SlotId = 3 },
                ParagonLevelResetSingleResp.Parser, r => r.Result));
            var single = await State();
            Assert.Equal(before + 3 + 3 + 1, single.LeftPoints);
            Assert.Equal(3, single.Runegroup[2].Points);
            Assert.Equal(0, single.Runegroup[5].Points);
            Assert.Equal(0, single.Runegroup[6].Rune.ItemEntry);
            Assert.Equal(1, await Count(1201001));

            Assert.Equal(0, await Ask(alice, ParagonService.ResetAll, new ParagonLevelResetAllReq(), ParagonLevelResetAllResp.Parser, r => r.Result));
            Assert.Equal(20, (await State()).LeftPoints);
            Assert.Equal(ParagonService.ErrPoints, await Ask(alice, ParagonService.ResetAll, new ParagonLevelResetAllReq(), ParagonLevelResetAllResp.Parser, r => r.Result));
            Assert.Equal(0, await Point(alice, 1, 2));
            roleId = await OnLoop("Alice", s => Task.FromResult(s.Role!.Id));
        }
        var saved = ParagonService.Parse(Service<RoleStore>().LoadParagon(roleId));
        Assert.Equal(20, saved.Lv);
        Assert.Equal(2, saved.Points[1]);

        await using var again = await InWorld("Alice", create: false);
        var relog = await Next(again, ParagonService.Sync, DBParagonLevelIncrement.Parser);
        Assert.Equal(20, relog.Lv);
        Assert.Equal(18, relog.LeftPoints);
    }
}
