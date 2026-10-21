using System.Globalization;
using System.Text.Json;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

/// <summary>
/// NOT original behaviour: a solo-play aid (ServerOptions.Exchange, docs/guide/exchange.md). A built-in seller keeps
/// every TradeConfig market item listed at its recommended price, and can buy cheap player listings back. TradeConfig's
/// own copy/recycle triggers (_iCopyTrigger*, _iRecycleTriggerPrice) are disabled on every row of this client, so the
/// original server's market maker, if any, is unknown. System listings carry seller role 0, never expire into mail,
/// and pay nobody: a purchase is a sink of the buyer's currency, a buy-back a source of the seller's proceeds.
/// </summary>
public sealed partial class TradeService
{
    /// <summary>SellerId of the built-in seller's listings (and BuyerId of its buy-backs). No role has id 0.</summary>
    public const long SystemSellerId = 0;
    private const long SystemPassMs = 60_000;
    private static readonly EconomyOwner SystemOwner = new(EconomyOwnerKind.System, "trade-exchange");

    private long _nextSystemPassMs, _nextRestockMs;
    internal int SystemPasses { get; private set; }

    private ExchangeOptions Exchange => options.Value.Exchange;
    private static bool IsSystem(Listing l) => l.SellerId == SystemSellerId;

    /// <summary>Run by the realm maintenance every second; does work once a minute (restock on its own cadence).</summary>
    internal async Task TickSystemAsync(bool force = false)
    {
        long now = NowMs;
        var o = Exchange;
        // With both aids off, one pass still retires stock left from a run that had the seller on.
        if (!force && (now < _nextSystemPassMs || !o.SystemSeller && !o.BuyBack && SystemPasses > 0)) return;
        _nextSystemPassMs = now + SystemPassMs;
        bool restock = force || now >= _nextRestockMs;
        int code = await market.RunMaintenanceAsync(tx =>
        {
            var all = Active(tx);
            var system = all.Where(IsSystem).ToList();
            if (!o.SystemSeller) RetireSystemStock(tx, system);
            else if (restock) Restock(tx, system);
            else KeepListed(tx, system);
            if (o.BuyBack) BuyBack(tx, all.Where(l => !IsSystem(l)).ToList());
            return 0;
        });
        if (code != 0) return;
        SystemPasses++;
        if (restock) _nextRestockMs = now + Math.Clamp(o.RestockMinutes, 1, 24 * 60) * 60_000L;
    }

    private long SystemShowMs => Math.Max(1, Public("TradeListingsGeneralShowTime", 24)) * 3_600_000;

    private int SystemPrice(JsonElement row)
    {
        var rule = PriceRule(row);
        double factor = double.IsFinite(Exchange.SellPrice) && Exchange.SellPrice > 0 ? Exchange.SellPrice : 1;
        return (int)Math.Clamp(Math.Round(rule.Base * factor), rule.Min, rule.Max);
    }

    private IEnumerable<(int Entry, JsonElement Trade, JsonElement Item)> SystemCatalogue()
    {
        foreach (var row in data.Table("TradeConfig").Values)
        {
            int entry = (int)GameData.Int(row, "_iID");
            if (Eligible(entry) is not { } trade || data.Row("ItemConfig", entry) is not { } item) continue;
            yield return (entry, trade, item);
        }
    }

    private static bool Unique(JsonElement item) =>
        GameData.Int(item, "_iType") == 5 || Math.Max(1, GameData.Int(item, "_iStackLimit")) == 1;

    /// <summary>Top every catalogue item up to its target, at the current price, with a fresh listing window.</summary>
    private void Restock(MarketTransaction tx, List<Listing> system)
    {
        long now = NowMs;
        var catalogue = SystemCatalogue().ToList();
        var known = catalogue.Select(c => c.Entry).ToHashSet();
        RetireSystemStock(tx, system.Where(l => !known.Contains(l.Entry)).ToList());
        foreach (var (entry, trade, itemRow) in catalogue)
        {
            int price = SystemPrice(trade);
            int currency = (int)GameData.Int(trade, "_iMoney");
            var mine = system.Where(l => l.Entry == entry).ToList();
            foreach (var l in mine.Where(l => l.UnitPrice != price || l.Currency != currency || l.ExpiresAtMs < now + SystemShowMs / 2))
            {
                l.UnitPrice = price; l.Currency = currency; l.ExpiresAtMs = now + SystemShowMs;
                tx.Put(Listings, l.Sku, l);
            }
            bool unique = Unique(itemRow);
            int target = Math.Max(0, unique ? Exchange.UniqueStock : Exchange.Stock);
            int have = mine.Sum(l => l.Quantity);
            if (have >= target) continue;
            if (unique)
                for (int n = have; n < target; n++) AddSystemListing(tx, entry, trade, itemRow, currency, price, 1, now);
            else if (mine.FirstOrDefault() is { } pile)
            {
                int add = target - have;
                pile.Quantity += add; pile.Item.Stack += add;
                tx.Put(Listings, pile.Sku, pile);
                tx.Economy.Source(new(EconomyAssetKind.Item, entry), add, EconomyReason.SystemRestock,
                    EconomyCompartment.TradeListing, SystemOwner);
            }
            else AddSystemListing(tx, entry, trade, itemRow, currency, price, target - have, now);
        }
    }

    private void AddSystemListing(MarketTransaction tx, int entry, JsonElement trade, JsonElement itemRow, int currency, int price, int quantity, long now)
    {
        var item = new InvItem
        {
            Entry = entry, Stack = quantity, Pack = CharacterService.ConfigPack(itemRow),
            Attrs = character.NewItemAttrs(entry),
        };
        BindSystemGoods(item, trade);
        var listing = new Listing
        {
            Sku = NewId(), Spu = Spu(entry), SellerId = SystemSellerId, Entry = entry, Currency = currency,
            UnitPrice = price, Quantity = quantity, SaleTimeMs = now, PublicUntilMs = 0,
            ExpiresAtMs = now + SystemShowMs, Item = item, SpecialType = SpecialTypeOf(item),
        };
        tx.Put(Listings, listing.Sku, listing);
        tx.Economy.Source(new(EconomyAssetKind.Item, entry), quantity, EconomyReason.SystemRestock,
            EconomyCompartment.TradeListing, SystemOwner);
    }

    /// <summary>Between restocks, keep listings from reading as expired in the client.</summary>
    private void KeepListed(MarketTransaction tx, List<Listing> system)
    {
        long now = NowMs;
        foreach (var l in system.Where(l => l.ExpiresAtMs < now + SystemShowMs / 2))
        {
            l.ExpiresAtMs = now + SystemShowMs;
            tx.Put(Listings, l.Sku, l);
        }
    }

    private static void RetireSystemStock(MarketTransaction tx, List<Listing> system)
    {
        foreach (var l in system)
        {
            tx.Economy.Sink(new(EconomyAssetKind.Item, l.Entry), l.Quantity, EconomyReason.SystemRetire,
                EconomyCompartment.TradeListing, SystemOwner);
            tx.Delete(Listings, l.Sku);
        }
    }

    /// <summary>
    /// Buy whole player listings at or below BuyBackPrice x base once they are past publicity and BuyBackMinutes old.
    /// The seller is paid by the usual sale mail (1063), less the usual tax; the goods leave the economy.
    /// </summary>
    private void BuyBack(MarketTransaction tx, List<Listing> player)
    {
        long now = NowMs;
        long wait = Math.Max(0, Exchange.BuyBackMinutes) * 60_000L;
        double factor = double.IsFinite(Exchange.BuyBackPrice) && Exchange.BuyBackPrice > 0 ? Exchange.BuyBackPrice : 1;
        var reserved = tx.All<PreorderRow>(Orders).Where(o => o.Status == 0).Select(o => o.Sku).ToHashSet();
        foreach (var listing in player)
        {
            if (listing.PublicUntilMs > now || now - listing.SaleTimeMs < wait || reserved.Contains(listing.Sku)) continue;
            if (Eligible(listing.Entry) is not { } row || listing.Currency != GameData.Int(row, "_iMoney")) continue;
            if (listing.UnitPrice > Math.Floor(PriceRule(row).Base * factor)) continue;
            var item = listing.Item.Clone(); item.Guid = 0; item.Grid = 0;
            long gross = (long)listing.Quantity * listing.UnitPrice;
            long net = gross - MarketFee(gross, listing.Currency);
            var sale = new Sale
            {
                Id = NewId(), Sku = listing.Sku, Spu = listing.Spu, Entry = listing.Entry, SellerId = listing.SellerId,
                BuyerId = SystemSellerId, Quantity = listing.Quantity, UnitPrice = listing.UnitPrice, TimeMs = now, Item = item,
            };
            var seller = new EconomyOwner(EconomyOwnerKind.Role, listing.SellerId.ToString(CultureInfo.InvariantCulture));
            if (net > 0) tx.Economy.Source(new(EconomyAssetKind.Currency, listing.Currency), net, EconomyReason.NpcShopBuyback,
                EconomyCompartment.Mail, seller);
            tx.Economy.Sink(new(EconomyAssetKind.Item, listing.Entry), listing.Quantity, EconomyReason.SystemRetire,
                EconomyCompartment.TradeListing, seller);
            tx.DeliverMail(listing.SellerId, 1063, currency: new Dictionary<int, long> { [listing.Currency] = net },
                contentParams: SaleMailParams(sale, gross, true));
            tx.Put(History, sale.Id, sale);
            tx.Delete(Listings, listing.Sku);
            log.LogInformation("Exchange buys back {Entry} x{Count} at {Price} from {Role}", listing.Entry, listing.Quantity,
                listing.UnitPrice, listing.SellerId);
        }
    }

    /// <summary>
    /// System goods take TradeConfig._kIsBindGive when listed ([1] bound, [2, seconds] bound for a while), as system
    /// stalls apply StallSystemItem._kIsBindGive at creation; the escrowed instance is what the buyer receives.
    /// </summary>
    private void BindSystemGoods(InvItem item, JsonElement row)
    {
        if (!row.TryGetProperty("_kIsBindGive", out var bind) || bind.ValueKind != JsonValueKind.Array || bind.GetArrayLength() == 0) return;
        int type = (int)(GameData.Number(bind[0]) ?? 0);
        if (type is not (1 or 2)) return;
        item.BindType = type;
        item.BindExpire = type == 2 && bind.GetArrayLength() > 1
            ? clock.GetUtcNow().ToUnixTimeSeconds() + (long)(GameData.Number(bind[1]) ?? 0) : 0;
    }
}
