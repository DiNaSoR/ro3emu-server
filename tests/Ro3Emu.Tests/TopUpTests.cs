using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Ro3Emu:TopUp:Free: the in-game Top Up screen (2001-2004) and the currency exchange (2005).</summary>
public sealed class TopUpTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ro3emu-topup-" + Guid.NewGuid().ToString("N"));
    private WebApplication app = null!;
    private string http = "";
    private T Service<T>() where T : notnull => app.Services.GetRequiredService<T>();
    private const int Crystal = 3, BoundCrystal = 16, Zeny = 1, MintedCoin = 2;
    private const int PcTier300 = 14; // RechargeConfig 14: PC $4.99, [3,300], bonus [16,30], first purchase [16,300]

    public async Task InitializeAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        app = ServerHost.Create([], o =>
        {
            o.DataDir = directory; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
            o.TopUp.Free = true;
        });
        await app.StartAsync();
        http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        await app.StopAsync(); await app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private async Task<BotClient> Enter(string name)
    {
        var bot = new BotClient(http, File.ReadAllText(Path.Combine(directory, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        await bot.EnterWorldAsync((await bot.CreateRoleAsync(name)).RoleBaseInfo.Id);
        return bot;
    }

    private async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{http}/ro3emu/gm?account={bot.Account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);
    private long Count(BotClient bot, int entry) => Service<CharacterService>().CountOf(Session(bot), entry);

    private static async Task<T> Ask<T>(BotClient bot, uint msgId, IMessage request, MessageParser<T> parser) where T : IMessage<T>
    {
        using var expected = bot.Inbox.Expect(e => e.IsGame(msgId));
        await bot.SendGameAsync(msgId, request);
        return parser.ParseFrom((await expected.WaitAsync(TimeSpan.FromSeconds(8))).Body);
    }

    private static Task<CreateOrderResp> Order(BotClient bot, int type, int id) =>
        Ask(bot, ShopService.CreateOrder, new CreateOrderReq { ChargeType = type, ChargeId = id, Amount = 4.99f }, CreateOrderResp.Parser);

    [ClientDataFact]
    public async Task TierPaysCrystalsAndTheFirstPurchaseDoubleOnce()
    {
        await using var bot = await Enter("Topper");
        var list = await Ask(bot, ShopService.FirstRechargeAward, new GetRechargeFirstAwardReq(), GetRechargeFirstAwardResp.Parser);
        Assert.Equal(0, list.Result);
        Assert.Contains(PcTier300, list.IdList);

        using var pushed = bot.Inbox.Expect(e => e.IsGame(ShopService.FirstRechargeAwardUpdate));
        var first = await Order(bot, 1, PcTier300);
        Assert.Equal(0, first.Result);
        Assert.Equal("{}", first.OrderData); // nothing for the PC client to open in a browser
        Assert.False(string.IsNullOrEmpty(first.OrderId));
        var update = await pushed.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(PcTier300, UpdateRechargeFirstAwardResp.Parser.ParseFrom(update.Body).RechargeId);
        Assert.Equal(300, Count(bot, Crystal));
        Assert.Equal(300, Count(bot, BoundCrystal));

        list = await Ask(bot, ShopService.FirstRechargeAward, new GetRechargeFirstAwardReq(), GetRechargeFirstAwardResp.Parser);
        Assert.DoesNotContain(PcTier300, list.IdList);

        Assert.Equal(0, (await Order(bot, 1, PcTier300)).Result); // a repeat pays the regular bonus
        Assert.Equal(600, Count(bot, Crystal));
        Assert.Equal(330, Count(bot, BoundCrystal));
        Assert.Equal(2, Session(bot).Shop!.Recharged[PcTier300]);

        Assert.Equal(ErrorCodes.Parameter, (await Order(bot, 1, 999)).Result);
    }

    [ClientDataFact]
    public async Task ProductGrantsItsEntitlementItem()
    {
        await using var bot = await Enter("Passer");
        long before = Count(bot, 703);
        Assert.Equal(0, (await Order(bot, 2, 7)).Result); // ProductConfig 7: award 199008 -> AwardConfig 100199008 [703,1]
        Assert.Equal(before + 1, Count(bot, 703));
    }

    [ClientDataFact]
    public async Task ExchangeTurnsCrystalsIntoZenyAndZenyIntoMintedCoin()
    {
        await using var bot = await Enter("Changer");
        Assert.Equal(0, (await Order(bot, 1, PcTier300)).Result); // 300 crystals
        // Functions 7005/7006 open at base level 20 (conditions 2000265/2000266 [1002,20]).
        var locked = await Ask(bot, ShopService.ItemExchange, new ItemExchangeReq { ItemId = Crystal, Count = 10 }, ItemExchangeResp.Parser);
        Assert.Equal(ErrorCodes.FunctionLocked, locked.Result);
        await Gm(bot, "!lvl 20");
        long zeny = Count(bot, Zeny);
        var done = await Ask(bot, ShopService.ItemExchange, new ItemExchangeReq { ItemId = Crystal, Count = 10 }, ItemExchangeResp.Parser);
        Assert.Equal(0, done.Result);
        Assert.Equal(290, Count(bot, Crystal));
        Assert.Equal(zeny + 1000, Count(bot, Zeny)); // ItemExchangeConfig 3: rate 100

        long coins = Count(bot, MintedCoin);
        Assert.Equal(0, (await Ask(bot, ShopService.ItemExchange, new ItemExchangeReq { ItemId = Zeny, Count = 5 }, ItemExchangeResp.Parser)).Result);
        Assert.Equal(coins + 500, Count(bot, MintedCoin));

        var short_ = await Ask(bot, ShopService.ItemExchange, new ItemExchangeReq { ItemId = Crystal, Count = 1000 }, ItemExchangeResp.Parser);
        Assert.NotEqual(0, short_.Result);
        Assert.Equal(290, Count(bot, Crystal));
    }
}
