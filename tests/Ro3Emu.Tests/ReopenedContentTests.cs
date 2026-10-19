using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// content/reopened-content.json: the Season menu (function 69, condition 2000062 back at base level 50) and the blue
/// side chain "Dejected Young Man" (201010..205200, offered from base level 20, with the Bunny Hair Band materials
/// given by 201090 and turned in with mode 1 at 201120).
/// </summary>
public sealed class ReopenedContentTests : IAsyncLifetime
{
    private const long Root = 201010, AllensRequest = 201090, Gather = 201100, MeetAllen = 201110, HairBand = 201120;
    private const int Pearl = 1253001, Clover = 1253002;
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-reopened-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    public async Task InitializeAsync()
    {
        var (http, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.CustomContentDir = Path.Combine(ClientData.RepoRoot, "content");
            o.HttpPort = http; o.MicroGatePort = micro; o.GameGatePort = game;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{http}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private GameSession Alice => Service<SessionRegistry>().All().Single(s => s.Role?.Name == "ReopenAlice");
    private Task<T> Read<T>(Func<GameSession, T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read(Alice)));
    private Task<TaskState?> State(long id) => Read(s => s.Tasks!.Infos.TryGetValue(id, out var i) ? i.Status : (TaskState?)null);
    private Task<bool> SeasonOpen() => Read(s => Service<FunctionService>().IsOpen(s, 69));
    private Task<long> Count(int item) => Read(s => Service<CharacterService>().CountOf(s, item));

    private async Task Gm(string line)
    {
        using var http = new HttpClient();
        (await http.PostAsync($"{_http}/ro3emu/gm?account=reopenalice", new StringContent(line))).EnsureSuccessStatusCode();
    }

    private static async Task Barrier(BotClient bot)
    {
        await bot.SendGameAsync(TaskService.TaskFollow, new TaskFollowReq { Id = TaskService.FirstTask });
        await bot.Inbox.WaitAsync(e => e.IsGame(TaskService.TaskFollow), Wait);
    }

    [ClientDataFact]
    public async Task SeasonAndTheDejectedYoungManChainAreOpen()
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await using var _ = bot;
        await bot.LoginAsync("reopenalice", "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync("ReopenAlice")).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        await Barrier(bot);

        // Below the gates: neither is offered.
        await Gm("!lvl 19");
        await Barrier(bot);
        Assert.Null(await State(Root));
        Assert.False(await SeasonOpen());

        // Base level 20 offers the chain's first quest by itself (a condition root, no pre-task).
        await Gm("!lvl 20");
        await Barrier(bot);
        Assert.Equal(TaskState.Running, await State(Root));
        Assert.False(await SeasonOpen());

        // Base level 50 opens the Season menu again (its level before the 2026-10-18 client update).
        await Gm("!lvl 50");
        await Barrier(bot);
        Assert.True(await SeasonOpen());

        // Allen's Request hands over the two materials, so Gather Materials completes on its own.
        await Gm($"!quest done {AllensRequest}");
        await Barrier(bot);
        Assert.Equal(1, await Count(Pearl));
        Assert.Equal(1, await Count(Clover));
        Assert.True(Service<TaskService>().TargetsSupported(Gather));
        Assert.Equal(TaskState.Delivery, await State(Gather));
        Assert.Equal(TaskState.Running, await State(MeetAllen));

        // The Bunny Hair Band turn-in is supported (mode 1) and takes both items at the hand-in.
        Assert.True(Service<TaskService>().TargetsSupported(HairBand));
        await Gm($"!quest done {MeetAllen}");
        await Barrier(bot);
        Assert.Equal(TaskState.Finish, await State(HairBand));
        await Gm($"!quest done {HairBand}");
        await Barrier(bot);
        Assert.Equal(TaskState.Delivery, await State(HairBand));
        Assert.Equal(0, await Count(Pearl));
        Assert.Equal(0, await Count(Clover));

        // The placeholder Base EXP (100 a step) is raised: 10,000 a step, 150,000 to 300,000 for each arc's finale.
        var data = Service<Ro3Emu.Server.Data.GameData>();
        long Exp(long task) => data.Row("TaskConfig", task) is { } row ? row.GetProperty("_kEXP")[1].GetInt64() : 0;
        Assert.Equal(10000, Exp(Root));
        Assert.Equal(10000, Exp(HairBand));
        Assert.Equal(150000, Exp(201130));
        Assert.Equal(300000, Exp(205200));
    }
}
