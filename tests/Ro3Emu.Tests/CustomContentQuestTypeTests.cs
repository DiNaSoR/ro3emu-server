using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>A custom quest written as a main quest ("type": "main") is offered like the side-quest prototype: the
/// server's condition roots take custom quests of any type, while the client's own main quests stay out.</summary>
public sealed class CustomContentQuestTypeTests : IAsyncLifetime
{
    private const long MainQuest = 91000070;
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-quest-type-" + Guid.NewGuid().ToString("N"));
    private string ContentDir => Path.Combine(_dataDir, "content");
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(ContentDir);
        File.WriteAllText(Path.Combine(ContentDir, "main.json"), $$"""
            {"quests": [{"id": {{MainQuest}}, "type": "main", "steps": [
              {"name": "A", "description": "B", "objective": "C", "kill": 5000001, "count": 1}
            ]}]}
            """);
        var (http, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.CustomContentDir = ContentDir;
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

    [ClientDataFact]
    public async Task CustomMainQuestIsOfferedAtLogin()
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await using var _ = bot;
        await bot.LoginAsync("typealice", "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync("TypeAlice")).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        await bot.SendGameAsync(TaskService.TaskFollow, new TaskFollowReq { Id = TaskService.FirstTask });
        await bot.Inbox.WaitAsync(e => e.IsGame(TaskService.TaskFollow), Wait);

        var loop = _app.Services.GetRequiredService<GameLoop>();
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == "TypeAlice");
        var status = await loop.InvokeAsync(() => Task.FromResult(
            session.Tasks!.Infos.TryGetValue(MainQuest, out var info) ? info.Status : (TaskState?)null));
        Assert.Equal(TaskState.Running, status);
    }
}
