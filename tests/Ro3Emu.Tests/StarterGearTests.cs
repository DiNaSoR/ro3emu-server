using Microsoft.AspNetCore.Builder;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>ServerOptions.WearStarterGear: a new character's starter dagger and shirt start worn.</summary>
public sealed class StarterGearTests : IAsyncLifetime
{
    private const int Potion = 1000000, Dagger = 2010121, Shirt = 2550120;
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-starter-" + Guid.NewGuid().ToString("N"));
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
            o.WearStarterGear = true; // the shipped default; the suite's hosts turn it off (StarterGearDefault)
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

    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint msgId, Google.Protobuf.MessageParser<T> parser)
        where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(msgId), Wait);
        return parser.ParseFrom(e.Body);
    }

    [ClientDataFact]
    public async Task ANewCharacterWearsTheStarterDaggerAndShirtAndKeepsThemOnRelog()
    {
        long dagger, shirt;
        await using (var alice = await InWorld("Alice"))
        {
            var bag = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
            Assert.Equal(Potion, Assert.Single(bag.PackInfo[1].All.Items.Values).ItemEntry);
            Assert.True(!bag.PackInfo.ContainsKey(2) || bag.PackInfo[2].All.Items.Count == 0); // nothing left in the gear tab
            Assert.Equal(1000, bag.Currency[1].All);

            var equip = await Next(alice, CharacterService.EquipSync, DBEquipInfoIncrement.Parser);
            Assert.Equal(new[] { Dagger, Shirt }, equip.Items.Values.Select(i => i.All.ItemEntry).Order());
            dagger = equip.Items.Single(i => i.Value.All.ItemEntry == Dagger).Key;
            shirt = equip.Items.Single(i => i.Value.All.ItemEntry == Shirt).Key;
            var grids = equip.EquipPlans[0].All.ItemsEquipGrid;
            Assert.Equal(dagger, grids[1]); // MainHand
            Assert.Equal(shirt, grids[3]);  // Armor
            Assert.Equal(new long[] { Dagger, 0, Shirt, 0, 0, 0, 0 }, alice.Self!.EquipInfo);
        }

        await using var again = await InWorld("Alice", create: false);
        Assert.Equal(new long[] { Dagger, 0, Shirt, 0, 0, 0, 0 }, again.Self!.EquipInfo);
        var relog = await Next(again, CharacterService.EquipSync, DBEquipInfoIncrement.Parser);
        Assert.Equal(dagger, relog.EquipPlans[0].All.ItemsEquipGrid[1]);
        Assert.Equal(shirt, relog.EquipPlans[0].All.ItemsEquipGrid[3]);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
