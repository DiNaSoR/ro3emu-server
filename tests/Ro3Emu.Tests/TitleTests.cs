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
/// The title rank ladder (TitleService): the 5031 DBTitleInfo autosync at login (row 1 by default), 43102 promotions
/// gated by level, maxHistoryPower and the next row's _kSpend items, the title's _iPowerAdd in the power package, and
/// the saved title across relog.
/// </summary>
public sealed class TitleTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-title-" + Guid.NewGuid().ToString("N"));
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

    private long PowerAdd(long title) => GameData.Int(Service<GameData>().Row("TitleConfig", title)!.Value, "_iPowerAdd");

    private Task<TitleLevelRsp> Upgrade(BotClient bot) => bot.SendGameAsync(TitleService.Upgrade, new TitleLevelReq())
        .ContinueWith(_ => Next(bot, TitleService.Upgrade, TitleLevelRsp.Parser)).Unwrap();

    private Task RaiseHistory(string name, long power) => OnLoop(name, s =>
    {
        s.Power = s.Power! with { History = new PowerUnit { NormalPower = power } };
        return Task.FromResult(true);
    });

    [ClientDataFact]
    public async Task TitlesClimbOneRowAtATimeAndAreKept()
    {
        long roleId;
        await using (var alice = await InWorld("Alice"))
        {
            var login = await Next(alice, TitleService.Sync, DBTitleInfoIncrement.Parser);
            Assert.True(login.SyncAllMark);
            Assert.Equal(TitleService.FirstTitle, login.TitleId);
            var power = await Next(alice, PowerService.PackageNtf, PlayerPowerPackageNtf.Parser);
            Assert.Equal(PowerAdd(1), power.PowerPackage.TitleScore.NormalPower);

            // Row 2 needs base level 20 and 5000 history power.
            var low = await Upgrade(alice);
            Assert.Equal(ErrorCodes.Parameter, low.Result);
            Assert.Equal(1, low.TitleId);

            await Gm("alice", "!lvl 45");
            await RaiseHistory("Alice", 1_000_000);
            var two = await Upgrade(alice);
            Assert.Equal(0, two.Result);
            Assert.Equal(2, two.TitleId);
            var update = await Next(alice, TitleService.Sync, DBTitleInfoIncrement.Parser, m => !m.SyncAllMark);
            Assert.Equal(2, update.TitleId);
            await Next(alice, PowerService.PackageNtf, PlayerPowerPackageNtf.Parser,
                m => m.PowerPackage.TitleScore.NormalPower == PowerAdd(2));

            Assert.Equal(3, (await Upgrade(alice)).TitleId);
            // Row 4 costs two Crests of Journey (1250801).
            var shortItems = await Upgrade(alice);
            Assert.Equal(ErrorCodes.ItemsShort, shortItems.Result);
            Assert.Equal(3, shortItems.TitleId);
            await Gm("alice", "!item 1250801 2");
            var four = await Upgrade(alice);
            Assert.Equal(0, four.Result);
            Assert.Equal(4, four.TitleId);
            Assert.Equal(0, await OnLoop("Alice", s => Task.FromResult(s.Items.Where(i => i.Entry == 1250801).Sum(i => (long)i.Stack))));
            roleId = await OnLoop("Alice", s => Task.FromResult(s.Role!.Id));
        }
        Assert.Equal(4, DBTitleInfo.Parser.ParseFrom(Service<RoleStore>().LoadTitle(roleId)).TitleId);

        await using var again = await InWorld("Alice", create: false);
        Assert.Equal(4, (await Next(again, TitleService.Sync, DBTitleInfoIncrement.Parser)).TitleId);
    }
}
