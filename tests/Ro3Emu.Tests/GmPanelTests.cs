using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Xunit;

namespace Ro3Emu.Tests;

/// <summary>The client's GM panel ops (30300) beyond add item / level / warp, as the panel sends them.</summary>
public sealed class GmPanelTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-gmpanel-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
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

    private async Task<(BotClient Bot, long Id)> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        return (bot, id);
    }

    private static async Task<string> Panel(BotClient bot, int op, string param)
    {
        await bot.SendGameAsync(GmCommands.GeneralProcGm, new GeneralGMProcReq { GmType = (GmOpEnum)op, ParamString = param });
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(GmCommands.GeneralProcGm), Wait);
        return GeneralGMProcRsp.Parser.ParseFrom(e.Body).ErrInfo;
    }

    [ClientDataFact]
    public async Task SelfOpsAnswerWithTheirEffect()
    {
        var (alice, id) = await InWorld("Alice");
        await using var _ = alice;

        Assert.StartsWith("Map ", await Panel(alice, 79, ""));
        Assert.Contains("on", await Panel(alice, 52, "1"));
        Assert.Contains("on", await Panel(alice, 80, "1"));
        Assert.Contains("off", await Panel(alice, 80, "0"));
        Assert.Equal("HP and SP full.", await Panel(alice, 78, ""));
        Assert.Equal("Stat points reset.", await Panel(alice, 9, ""));
        Assert.StartsWith("Server level", await Panel(alice, 64, ""));

        // Strengthen the armor (part 2, grid 3) to +2: the 31404 reply carries the new level.
        Assert.Contains("grid 3 +2", await Panel(alice, 4, "2,2"));

        // A buff on myself, listed (GmShowBuff sends the selected unit), then removed.
        Assert.StartsWith("Buff 900001", await Panel(alice, 35, "900001,,0"));
        Assert.Contains("900001", await Panel(alice, 76, id.ToString()));
        Assert.Equal("Removed buff 900001.", await Panel(alice, 36, "900001,0"));
        Assert.Equal("No buffs.", await Panel(alice, 76, id.ToString()));

        Assert.Equal("Mail 'Hi' delivered.", await Panel(alice, 70, "Hi,Body,1,0,0,1000000:2,1"));
        Assert.Contains("not implemented", await Panel(alice, 62, "2030-01-01 00:00:00"));
    }

    [ClientDataFact]
    public async Task SpawnedMonsterCanBeInspectedAndKilled()
    {
        var (alice, _) = await InWorld("Alice");
        await using var bot = alice;
        string spawned = await Panel(alice, 27, "5000002");
        var match = Regex.Match(spawned, @"uid (\d+)");
        Assert.True(match.Success, spawned);
        string uid = match.Groups[1].Value;

        Assert.Contains("NPC 5000002", await Panel(alice, 32, uid));
        Assert.Contains("killed", await Panel(alice, 30, uid));
    }

    [ClientDataFact]
    public async Task CreateGuildAndLearnSkills()
    {
        var (alice, _) = await InWorld("Alice");
        await using var bot = alice;
        await Panel(alice, 24, "40,40");
        Assert.Contains("creation requested", await Panel(alice, 94, "create,Panel"));
        var created = await alice.Inbox.WaitAsync(e => e.IsGame(GuildService.Create), Wait);
        Assert.Equal(0, GuildCreateResp.Parser.ParseFrom(created.Body).Result);

        await Panel(alice, 101, "1100"); // a first job, so there is a tree to spend points on
        string learned = await Panel(alice, 110, "1");
        Assert.StartsWith("Learned ", learned);
        Assert.Equal("Skills reset.", await Panel(alice, 110, "0"));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
