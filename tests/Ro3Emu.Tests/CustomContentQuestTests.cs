using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>The prototype custom quest (content/prototype-scholar.json) played through the server, as the client would.</summary>
public sealed class CustomContentQuestTests : IAsyncLifetime
{
    private const long Talk = 90000001, Kill = 90000002, Report = 90000003, Elias = 90000001;
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-custom-quest-" + Guid.NewGuid().ToString("N"));
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

    private GameSession Alice => Service<SessionRegistry>().All().Single(s => s.Role?.Name == "CustomAlice");
    private Task Change(Func<GameSession, Task> change) => Service<GameLoop>().InvokeAsync(() => change(Alice));
    private Task<T> Read<T>(Func<GameSession, T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read(Alice)));
    private Task<TaskInfo?> State(long id) => Read(s => s.Tasks!.Infos.TryGetValue(id, out var i) ? i.Clone() : null);

    private async Task Gm(string line)
    {
        using var http = new HttpClient();
        (await http.PostAsync($"{_http}/ro3emu/gm?account=customalice", new StringContent(line))).EnsureSuccessStatusCode();
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser) where T : Google.Protobuf.IMessage<T> =>
        parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(id), Wait)).Body);

    private static async Task Barrier(BotClient bot)
    {
        await bot.SendGameAsync(TaskService.TaskFollow, new TaskFollowReq { Id = TaskService.FirstTask });
        await Next(bot, TaskService.TaskFollow, TaskFollowResp.Parser);
    }

    [ClientDataFact]
    public async Task ScholarQuestRunsFromTalkToKillsToReward()
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await using var _ = bot;
        await bot.LoginAsync("customalice", "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync("CustomAlice")).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);

        // The client hook's rows arrive with the login.
        var payload = (await bot.Inbox.WaitAsync(e => e.IsGame(CustomContentService.ContentMsg), Wait)).Body;
        using (var doc = JsonDocument.Parse(Encoding.UTF8.GetString(payload)))
        {
            Assert.Equal(Talk, doc.RootElement.GetProperty("rows").GetProperty("TaskConfig").GetProperty("90000001").GetProperty("_iTaskID").GetInt64());
            Assert.Equal("Elias", doc.RootElement.GetProperty("texts").GetProperty("99000000001").GetString());
            // Elias's scene-editor entry, which the quest tracker's walk-to-NPC (track action 143) looks up by NPC id.
            var placed = Assert.Single(doc.RootElement.GetProperty("npcs").EnumerateArray());
            Assert.Equal("sc_prontera_001_sceneeditor", placed.GetProperty("module").GetString());
            var entry = placed.GetProperty("entry");
            Assert.Equal(Elias, entry.GetProperty("Id").GetInt64());
            Assert.Equal(Elias, entry.GetProperty("NpcId").GetInt64());
            Assert.True(entry.GetProperty("IsServerNpc").GetBoolean());
            Assert.False(entry.TryGetProperty("CustomContent", out var source), source.ToString());
        }
        await Barrier(bot);
        Assert.Equal(TaskState.Running, (await State(Talk))!.Status);

        // !quest drop takes it off the client's log (31203, as a give-up) and, a condition root, it is offered again.
        await Gm($"!quest drop {Talk}");
        Assert.Equal(Talk, (await Next(bot, TaskService.TaskGiveUp, TaskGiveUpResp.Parser)).Id);
        await Barrier(bot);
        Assert.Equal(TaskState.Running, (await State(Talk))!.Status);

        // Talk to Elias in Prontera.
        var npc = Service<WorldData>().Map(10100)!.Npcs.Single(n => n.NpcId == Elias);
        await Gm($"!warp 10100 {npc.X + 1} {npc.Z}");
        var arrival = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = arrival.SceneEntry });
        await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        await bot.SendGameAsync(SceneService.ClientNpcInteract, new ClientNpcInteractReq { NpcUid = npc.Uid, DialogID = 900000201 });
        await Barrier(bot);
        Assert.Equal(TaskState.Delivery, (await State(Talk))!.Status);
        Assert.Equal(TaskState.Running, (await State(Kill))!.Status);

        // Five Porings (kill credit itself is CombatService's; set here as the quest engine tests do).
        await Change(s => { s.Tasks!.Infos[Kill].CompleteNum[0] = 5; return Service<TaskService>().RefreshAsync(s); });
        await Barrier(bot);
        Assert.Equal(TaskState.Delivery, (await State(Kill))!.Status);
        Assert.Equal(TaskState.Running, (await State(Report))!.Status);

        long exp = await Read(s => s.Progress!.Exp), level = await Read(s => (long)s.Progress!.Level);
        await bot.SendGameAsync(SceneService.ClientNpcInteract, new ClientNpcInteractReq { NpcUid = npc.Uid, DialogID = 900000301 });
        await Barrier(bot);
        Assert.Equal(TaskState.Delivery, (await State(Report))!.Status);
        Assert.True(await Read(s => s.Progress!.Level) > level || await Read(s => s.Progress!.Exp) > exp, "the report pays its EXP");

        // Dropping the first quest restarts the chain: the later quests go too, and only the first is offered again.
        await Gm($"!quest drop {Talk}");
        await Barrier(bot);
        Assert.Equal(TaskState.Running, (await State(Talk))!.Status);
        Assert.Null(await State(Kill));
        Assert.Null(await State(Report));
        await bot.SendGameAsync(SceneService.ClientNpcInteract, new ClientNpcInteractReq { NpcUid = npc.Uid, DialogID = 900000201 });
        await Barrier(bot);
        Assert.Equal(TaskState.Delivery, (await State(Talk))!.Status);
        Assert.Equal(TaskState.Running, (await State(Kill))!.Status);
    }
}
