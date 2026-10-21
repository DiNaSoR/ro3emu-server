using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Ro3Emu:CashShop:Free: crystal prices take no crystals, and the client hook is told at login.</summary>
public sealed class CashShopFreeTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ro3emu-freeshop-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private const int Crystal = 3, BoundCrystal = 16, Zeny = 1;

    public async Task InitializeAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _directory; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
            o.CashShop.Free = true;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private async Task<BotClient> Enter(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_directory, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        await bot.EnterWorldAsync((await bot.CreateRoleAsync(name)).RoleBaseInfo.Id);
        return bot;
    }

    private async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={bot.Account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);
    private long Count(BotClient bot, int entry) => Service<CharacterService>().CountOf(Session(bot), entry);

    private static async Task<T> Ask<T>(BotClient bot, uint msgId, IMessage request, MessageParser<T> parser) where T : IMessage<T>
    {
        using var expected = bot.Inbox.Expect(e => e.IsGame(msgId));
        await bot.SendGameAsync(msgId, request);
        return parser.ParseFrom((await expected.WaitAsync(TimeSpan.FromSeconds(8))).Body);
    }

    [ClientDataFact]
    public async Task CrystalPricesCostNothing()
    {
        await using var bot = await Enter("Freebie");
        Assert.Equal(0, Count(bot, Crystal));
        await Gm(bot, "!lvl 60"); // the shop of commodity 13107 (function 70108)
        const int Commodity = 13107, Goods = 1200701; // ShopCommodityConfig 13107: [3, 5] for item 1200701
        long goods = Count(bot, Goods);

        var bought = await Ask(bot, ShopService.BuyCommodity, new BuyCommodityReq { CommodityId = Commodity, Count = 10 }, BuyCommodityResp.Parser);
        Assert.Equal(0, bought.Result);
        Assert.Equal(goods + 10, Count(bot, Goods));
        Assert.Equal(0, Count(bot, Crystal));
        Assert.Equal(0, Count(bot, BoundCrystal));
    }

    [ClientDataFact]
    public async Task CrystalExchangeCostsNothing()
    {
        await using var bot = await Enter("Changer");
        await Gm(bot, "!lvl 20"); // functions 7005/7006
        long zeny = Count(bot, Zeny);
        var done = await Ask(bot, ShopService.ItemExchange, new ItemExchangeReq { ItemId = Crystal, Count = 10 }, ItemExchangeResp.Parser);
        Assert.Equal(0, done.Result);
        Assert.Equal(zeny + 1000, Count(bot, Zeny)); // ItemExchangeConfig 3: rate 100
        Assert.Equal(0, Count(bot, Crystal));
        // Zeny is not a crystal price: still paid.
        Assert.NotEqual(0, (await Ask(bot, ShopService.ItemExchange, new ItemExchangeReq { ItemId = Zeny, Count = (int)(zeny + 1000) + 1 }, ItemExchangeResp.Parser)).Result);
    }
}
