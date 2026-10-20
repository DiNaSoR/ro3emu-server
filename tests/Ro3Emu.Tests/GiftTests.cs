using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Fashion gifts (ShopService.Gifts): 82335 pays the look's price and opens a pending order for a friend, both lists
/// (82337 to the receiver, 82339 to the sender) follow every change, 82336 unlocks the look for the receiver at no cost,
/// and an unclaimed order returns the price by mail 1115 once.
/// </summary>
public sealed class GiftTests : IAsyncLifetime
{
    private const int Crystal = 3;
    private const int TeaParty = 5000002, TeaPartySuit = 40050010, TeaPartyHat = 10050010, TeaPartyBonus = 3025101; // [3,1880], award 180002.
    private const int Macaron = 5000015, MacaronHair = 70050010;                        // [3,480].
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ro3emu-gift-" + Guid.NewGuid().ToString("N"));
    private WebApplication app = null!;
    private string http = "";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private T Service<T>() where T : notnull => app.Services.GetRequiredService<T>();
    private GameSession Session(string name) => Service<SessionRegistry>().All().Single(s => s.Role?.Name == name);

    public async Task InitializeAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        app = ServerHost.Create([], o =>
        {
            o.DataDir = directory; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
            o.PlayBook = 101;
        });
        await app.StartAsync();
        http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        await app.StopAsync(); await app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private async Task<(BotClient Bot, long Role)> Enter(string name)
    {
        var bot = new BotClient(http, File.ReadAllText(Path.Combine(directory, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        long role = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(role);
        await Next(bot, ShopService.SentGiftsList, SentGiftsListNtf.Parser);
        await Next(bot, WardrobeService.WardrobeSync, DBWardRobeInfoIncrement.Parser);
        await Gm(name, "!lvl 10"); // the Fashion shops open at base level 10
        return (bot, role);
    }

    private async Task Gm(string name, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync(http + "/ro3emu/gm?account=" + name.ToLowerInvariant(), new StringContent(command))).EnsureSuccessStatusCode();
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null) where T : IMessage<T> =>
        parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait)).Body);

    private static async Task<CommodityPresentResp> Gift(BotClient bot, long target, int commodity, string text = "for you")
    {
        await bot.SendGameAsync(ShopService.CommodityPresent, new CommodityPresentReq
        {
            TargetId = target, GiftContent = text, BuyReq = new BuyCommodityReq { CommodityId = commodity, Count = 1, Price = 0 },
        });
        return await Next(bot, ShopService.CommodityPresent, CommodityPresentResp.Parser);
    }

    private static async Task<int> Pick(BotClient bot, long order)
    {
        await bot.SendGameAsync(ShopService.CommodityPick, new CommodityPickReq { OrderId = order });
        return (await Next(bot, ShopService.CommodityPick, CommodityPickResp.Parser)).Result;
    }

    private long Count(string name, int entry) => Service<CharacterService>().CountOf(Session(name), entry);

    [ClientDataFact]
    public async Task FriendsGiftALookThatIsClaimedOrRefunded()
    {
        var (alice, a) = await Enter("GiftAlice");
        var (bob, b) = await Enter("GiftBob");
        await using var aliceLease = alice;
        await using var bobLease = bob;
        await Gm("GiftAlice", "!item 3 5000");

        Assert.Equal(2312, (await Gift(alice, b, TeaParty)).Result); // not friends yet
        Assert.Equal(2325, (await Gift(alice, a, TeaParty)).Result);
        await alice.MicroAsync("FriendService", "apply", new() { ["Uid"] = a.ToString(), ["Target"] = b.ToString() });
        await bob.MicroAsync("FriendService", "accept", new() { ["Uid"] = b.ToString(), ["Applicant"] = a.ToString() });
        Assert.Equal(2327, (await Gift(alice, b, TeaParty, new string('x', 21))).Result);
        Assert.Equal(5000, Count("GiftAlice", Crystal));

        var sent = await Gift(alice, b, TeaParty, "  happy day ");
        Assert.Equal(0, sent.Result);
        Assert.True(sent.OrderId > 0);
        Assert.Equal((TeaParty, b, "happy day"), (sent.CommodityId, sent.TargetId, sent.GiftContent));
        Assert.Equal(5000 - 1880, Count("GiftAlice", Crystal));
        var mine = await Next(alice, ShopService.SentGiftsList, SentGiftsListNtf.Parser, m => m.SentGifts.Count == 1);
        Assert.Equal((b, "GiftBob", TeaPartySuit, 0, Crystal, 1880), (mine.SentGifts[0].RecipientId, mine.SentGifts[0].RecipientName,
            (int)mine.SentGifts[0].AppranceId, mine.SentGifts[0].State, mine.SentGifts[0].CurrencyId, mine.SentGifts[0].CurrencyCount));
        var theirs = await Next(bob, ShopService.PresentList, CommodityPresentListNtf.Parser, m => m.Orders.Count == 1);
        Assert.Equal((sent.OrderId, a, "GiftAlice", "happy day", 0), (theirs.Orders[0].OrderId, theirs.Orders[0].SenderId,
            theirs.Orders[0].SenderName, theirs.Orders[0].GiftContent, theirs.Orders[0].State));
        Assert.Equal(2318, (await Gift(alice, b, TeaParty)).Result); // the same look is already waiting for Bob
        Assert.Equal(6902, await Pick(alice, sent.OrderId));
        Assert.Equal(6901, await Pick(bob, 999_999));

        Assert.Equal(0, await Pick(bob, sent.OrderId));
        Assert.True(Session("GiftBob").Wardrobe!.AllLightUpAppearances.ContainsKey(TeaPartySuit));
        // The bonus voucher is _QuickUse [3, 0]: used on receipt, it lights the matching headwear.
        Assert.Equal(0, (await Next(bob, CharacterService.ItemUse, ItemUseResp.Parser, m => m.ItemId == TeaPartyBonus)).Result);
        Assert.Equal(0, Count("GiftBob", TeaPartyBonus));
        Assert.True(Session("GiftBob").Wardrobe!.AllLightUpAppearances.ContainsKey(TeaPartyHat));
        Assert.False(Session("GiftAlice").Wardrobe!.AllLightUpAppearances.ContainsKey(TeaPartySuit));
        Assert.Single((await Next(bob, ShopService.PresentList, CommodityPresentListNtf.Parser, m => m.Orders.Any(o => o.State == 1))).Orders);
        await Next(alice, ShopService.SentGiftsList, SentGiftsListNtf.Parser, m => m.SentGifts.Any(o => o.State == 1));
        Assert.Equal(6903, await Pick(bob, sent.OrderId));
        Assert.Equal(2316, (await Gift(alice, b, TeaParty)).Result); // Bob owns it now

        // An unclaimed gift past OrderExpireTime comes back by mail 1115, once.
        var second = await Gift(alice, b, Macaron);
        Assert.Equal(0, second.Result);
        var shops = Service<ShopService>();
        var loop = Service<GameLoop>();
        await loop.InvokeAsync(async () =>
        {
            shops.Presents[second.OrderId].CreateTime -= 86_400_001;
            using var scope = loop.Owners is { } owners ? await owners.EnterAsync("test expiry", Ro3Emu.Server.Game.Ownership.ReservationKey.Realm) : null;
            await shops.ExpirePresentsAsync();
            await shops.ExpirePresentsAsync();
            return true;
        });
        Assert.Equal(6903, await Pick(bob, second.OrderId));
        Assert.False(Session("GiftBob").Wardrobe!.AllLightUpAppearances.ContainsKey(MacaronHair));
        await Next(alice, ShopService.SentGiftsList, SentGiftsListNtf.Parser, m => m.SentGifts.Any(o => o.OrderId == second.OrderId && o.State == 3));
        string mailbox = System.Text.Encoding.UTF8.GetString(Service<RoleStore>().LoadMailbox(a) ?? []);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(mailbox, "present-refund:" + second.OrderId));

        // The orders are in the role blobs: Alice's present_sent (both orders, the second Refunded) and Bob's
        // present_received (the claimed one).
        var sentBlob = DBPresentInfo.Parser.ParseFrom(Service<RoleStore>().LoadPresentSent(a)!).Orders;
        // The sender's copy follows the index when the sender writes again: Picked here (a later send rewrote it).
        Assert.Equal(EPresentOrderState.Picked, sentBlob[sent.OrderId].State);
        Assert.Equal(EPresentOrderState.Refunded, sentBlob[second.OrderId].State);
        var receivedBlob = DBPresentInfo.Parser.ParseFrom(Service<RoleStore>().LoadPresentReceived(b)!).Orders;
        Assert.Equal(EPresentOrderState.Picked, Assert.Single(receivedBlob).Value.State);
    }
}
