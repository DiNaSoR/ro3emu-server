using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using System.Globalization;

namespace Ro3Emu.Server.Game;

public sealed partial class AuctionService
{
    // Capture the denomination from the same item configuration used by Exchange and mail.
    // Auction lots are single items; reserves, prebuys and bonuses use configured currency.
    private EconomyAsset AuctionAsset(int entry) => data.Row("ItemConfig", entry) is { } row
        ? new(GameData.Int(row, "_iType") == 1 ? EconomyAssetKind.Currency : EconomyAssetKind.Item, entry)
        : throw new InvalidOperationException("Auction economy item configuration is missing: " + entry);

    private EscrowEconomyIntent AuctionIntent(string kind, long actorRole = 0, string actorAccount = "",
        string? sourceKey = null, EconomyReason reason = EconomyReason.AuctionSettlement) =>
        new(kind, actorAccount, actorRole, sourceKey, clock.GetUtcNow().ToUnixTimeMilliseconds(), reason);

    private object LotEvidence(AuctionStore.Item row) => new
    {
        row.Id, row.SourceKey, row.RunId, row.Type, row.SrcType, row.GuildId, row.Entry, row.Bind,
        row.Currency, row.State, row.Owner, row.Reserve, row.Price, row.Raise, row.FixedPrice,
        row.MaxPrice, row.Start, row.End, row.LastBid, row.Auto, row.AutoPaused,
        row.Winner, row.RandomAward, row.Revision, row.SettlementRecoveryReason,
        Eligible = row.Eligible.ToArray(), Joined = row.Joined.ToArray(), GiveUp = row.GiveUp.ToArray(),
    };

    // The paid price not returned as bidder surplus or distributed as dividends leaves circulation.
    // Claimed=1 auction_bonus rows are a history projection; their mailed dividends are counted once.
    private EscrowEconomyIntent SettlementIntent(string kind, AuctionStore.Item row, long[] dividendRoles,
        long actorRole = 0, string actorAccount = "", string? sourceKey = null, object? details = null,
        long walletDebit = 0)
    {
        var intent = AuctionIntent(kind, actorRole, actorAccount, sourceKey)
            .Evidence(new { Lot = LotEvidence(row), DividendRoles = dividendRoles, Details = details });
        if (row.Winner == 0 || row.RandomAward || row.Reserve <= 0 || row.Price <= 0) return intent;
        long paid = row.Price;
        // Mirrors SettleLetters: every dividend-bearing type distributes DividendBudget when it has recipients.
        long distributed = dividendRoles.Length > 0 && row.Type is Romsg.AuctionType.Dungeon or Romsg.AuctionType.Guild
            or Romsg.AuctionType.World or Romsg.AuctionType.Act ? DividendBudget(row) : 0;
        long fee = checked(paid - distributed);
        var winner = new EconomyOwner(EconomyOwnerKind.Role, row.Winner.ToString(CultureInfo.InvariantCulture));
        if (kind == "auction/fixed-buy")
        {
            long fromWallet = Math.Min(fee, walletDebit);
            if (fromWallet > 0) intent.Sink(AuctionAsset(row.Currency), fromWallet, EconomyReason.AuctionFee,
                EconomyCompartment.Currency, winner);
            fee -= fromWallet;
        }
        if (fee > 0) intent.Sink(AuctionAsset(row.Currency), fee, EconomyReason.AuctionFee,
            EconomyCompartment.AuctionReserve, winner);
        return intent;
    }
}
