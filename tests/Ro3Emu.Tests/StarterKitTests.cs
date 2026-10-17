using Microsoft.AspNetCore.Builder;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>ServerOptions.StarterKit: the default kit is empty; a configured kit replaces it.</summary>
public sealed class StarterKitTests : IAsyncLifetime
{
    private const int Dagger = 2010121;
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-starter-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private WebApplication _app = null!;
    private string _http = "";

    public Task InitializeAsync() => Task.CompletedTask;

    private async Task Start(StarterItemOptions[]? kit)
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
            o.StarterKit = kit; // replaces the suite's legacy kit (LegacyStarterKit)
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<DBItemsInfoIncrement> FirstBag()
    {
        await using var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync("alice", "pw");
        await bot.EnterGateAsync();
        await bot.EnterWorldAsync((await bot.CreateRoleAsync("Alice")).RoleBaseInfo.Id);
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(CharacterService.ItemsSync), Wait);
        var bag = DBItemsInfoIncrement.Parser.ParseFrom(e.Body);
        Assert.Equal(new long[7], bot.Self!.EquipInfo); // nothing worn either way
        return bag;
    }

    [ClientDataFact]
    public async Task TheDefaultKitIsEmpty()
    {
        await Start(null);
        var bag = await FirstBag();
        Assert.All(bag.PackInfo.Values, pack => Assert.True(pack.All == null || pack.All.Items.Count == 0));
        Assert.Equal(0, bag.Currency.TryGetValue(1, out var zeny) ? zeny.All : 0);
    }

    [ClientDataFact]
    public async Task AConfiguredKitReplacesTheDefaultAndSkipsUnknownItems()
    {
        await Start([new() { Entry = Dagger, Count = 1 }, new() { Entry = 1, Count = 50 }, new() { Entry = 999999999, Count = 1 }]);
        var bag = await FirstBag();
        Assert.True(!bag.PackInfo.ContainsKey(1) || bag.PackInfo[1].All.Items.Count == 0); // no potions
        Assert.Equal(Dagger, Assert.Single(bag.PackInfo[2].All.Items.Values).ItemEntry);
        Assert.Equal(50, bag.Currency[1].All);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
