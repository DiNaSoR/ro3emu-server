using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>ServerOptions.Exchange: the built-in seller and buy-back for solo play (TradeService.System).</summary>
public sealed partial class TradeTests
{
    private async Task SystemPass(bool seller, bool buyBack)
    {
        var exchange = _app.Services.GetRequiredService<IOptions<ServerOptions>>().Value.Exchange;
        exchange.SystemSeller = seller; exchange.BuyBack = buyBack;
        var trade = _app.Services.GetRequiredService<TradeService>();
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() => trade.TickSystemAsync(force: true));
    }

    private static async Task<CQueryProductByParamWithPage> Products(BotClient bot, string entry)
    {
        await bot.SendGameAsync(TradeService.QueryProduct, new SQueryProductByParamRequest { ItemId = entry, PageNo = 1, PageSize = 50 });
        return (await Next(bot, TradeService.QueryProduct, CQueryProductByParamWithPageResp.Parser)).Data;
    }

    [ClientDataFact]
    public async Task SystemSellerStocksTheExchangeAndSellsWithoutPayingAnyone()
    {
        await using var buyer = await InWorld("SoloBuyer");
        await Gm("solobuyer", "!lvl 30"); await Gm("solobuyer", "!item 1 10000");
        var before = MarketEconomyEvidence.Cut(_dataDir, RepoRoot());
        await SystemPass(seller: true, buyBack: false);

        var page = await Products(buyer, "1254013");
        var stock = Assert.Single(page.DataList);
        Assert.Equal("0", stock.UserRoleId);
        Assert.Equal(10, stock.Quantity); // ExchangeOptions.Stock
        Assert.Equal(600, stock.ItemPrice); // TradeConfig _iBasePrice
        Assert.True(stock.SuspensionTime > _clock.GetUtcNow().ToUnixTimeSeconds());
        // Unstackable goods are single pieces: equipment 2010450 gets UniqueStock listings of one.
        var gear = await Products(buyer, "2010450");
        Assert.Equal(2, gear.TotalCount);
        Assert.All(gear.DataList, l => Assert.Equal(1, l.Quantity));

        await buyer.SendGameAsync(TradeService.BuyNormal, new TradeBuyNormalItemsReq
        { ItemId = 1254013, SpuId = stock.SpuId, Price = 600, TotalPrice = 1200, TotalQuantity = 2 });
        Assert.Equal(0, (await Next(buyer, TradeService.BuyNormal, TradeBuyNormalItemsResp.Parser)).Result);
        var mail = Assert.Single(await Mail(buyer, (long)buyer.Self!.Uuid), m => m.Template == 1064);
        Assert.Equal(2, Assert.Single(mail.Goods.Items).Stack);
        Assert.Equal(8, Assert.Single((await Products(buyer, "1254013")).DataList).Quantity);
        await buyer.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { mail.Id } });
        Assert.Equal(0, (await Next(buyer, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        var claimed = Assert.Single(Items(buyer), i => i.Entry == 1254013);
        Assert.Equal(2, claimed.BindType); // TradeConfig _kIsBindGive [2, 1209600]: not resellable for 14 days
        Assert.True(claimed.BindExpire > _clock.GetUtcNow().ToUnixTimeSeconds());

        var after = MarketEconomyEvidence.Cut(_dataDir, RepoRoot());
        var ledger = MarketEconomyEvidence.Exact(_dataDir, RepoRoot(), before, after);
        Assert.Contains(ledger.Legs, x => x.Kind == EconomyLegKind.Sink && x.Reason == EconomyReason.NpcShopPayment
            && x.Asset.Id == 1 && x.Delta == -1200);
        Assert.Contains(ledger.Legs, x => x.Kind == EconomyLegKind.Source && x.Reason == EconomyReason.SystemRestock
            && x.Asset.Id == 1254013 && x.Delta == 10);

        // A restock tops the pile back up; turning the seller off retires what is left.
        await SystemPass(seller: true, buyBack: false);
        Assert.Equal(10, Assert.Single((await Products(buyer, "1254013")).DataList).Quantity);
        await SystemPass(seller: false, buyBack: false);
        Assert.Equal(0, (await Products(buyer, "1254013")).TotalCount);
    }

    [ClientDataFact]
    public async Task BuyBackPaysForCheapListingsOnceTheyHaveWaited()
    {
        await using var seller = await InWorld("SoloSeller");
        await Gm("soloseller", "!lvl 30");
        await Gm("soloseller", "!item 1254013 3"); await Gm("soloseller", "!item 2 5000");
        long guid = Assert.Single(Items(seller), i => i.Entry == 1254013).Guid;
        await seller.SendGameAsync(TradeService.PutOn, new TradePutOnItemReq
        { ItemGuid = guid, ItemPrice = 600, CurrencyType = 1, Quantity = 1 });
        Assert.Equal(0, (await Next(seller, TradeService.PutOn, TradePutOnItemResp.Parser)).Result);
        await seller.SendGameAsync(TradeService.PutOn, new TradePutOnItemReq
        { ItemGuid = guid, ItemPrice = 900, CurrencyType = 1, Quantity = 2 });
        Assert.Equal(0, (await Next(seller, TradeService.PutOn, TradePutOnItemResp.Parser)).Result);

        await SystemPass(seller: false, buyBack: true);
        Assert.Equal(2, (await Products(seller, "1254013")).TotalCount); // not waited BuyBackMinutes yet

        _clock.Advance(TimeSpan.FromMinutes(11));
        var before = MarketEconomyEvidence.Cut(_dataDir, RepoRoot());
        await SystemPass(seller: false, buyBack: true);
        var left = Assert.Single((await Products(seller, "1254013")).DataList);
        Assert.Equal(900, left.ItemPrice); // above BuyBackPrice x base (600): stays listed
        var paid = Assert.Single(await Mail(seller, (long)seller.Self!.Uuid), m => m.Template == 1063);
        await seller.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { paid.Id } });
        Assert.Equal(0, (await Next(seller, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        var after = MarketEconomyEvidence.Cut(_dataDir, RepoRoot());
        var ledger = MarketEconomyEvidence.Exact(_dataDir, RepoRoot(), before, after);
        Assert.Contains(ledger.Legs, x => x.Kind == EconomyLegKind.Source && x.Reason == EconomyReason.NpcShopBuyback
            && x.Asset.Id == 1 && x.Delta == 600); // currency 1 pays no market tax
        Assert.Contains(ledger.Legs, x => x.Kind == EconomyLegKind.Sink && x.Reason == EconomyReason.SystemRetire
            && x.Asset.Id == 1254013 && x.Delta == -1);
    }
}
