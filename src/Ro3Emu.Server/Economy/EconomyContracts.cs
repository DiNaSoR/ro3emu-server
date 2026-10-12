namespace Ro3Emu.Server.Economy;

/// <summary>Versioned E00 denomination namespace; values are never reused.</summary>
public enum EconomyAssetKind { Item = 1, Currency = 2, GuildResource = 3, EventResource = 4 }
/// <summary>
/// Epoch separates intrinsically seasonal denominations (current escort goods). An ordinary item, personal
/// currency or guild material keeps the same asset identity in inventory and escrow; its reward/archive epoch
/// belongs in the location/source key. Moving currency 133 to a world drop does not invent a new denomination.
/// </summary>
public readonly record struct EconomyAsset(EconomyAssetKind Kind, long Id, string Epoch = "");
public enum EconomyOwnerKind { Role = 1, Guild = 2, System = 3, Escrow = 4, Recovery = 5 }
public readonly record struct EconomyOwner(EconomyOwnerKind Kind, string Id);
public enum EconomyCompartment
{
    Inventory = 1, Currency = 2, Mail = 3, TradeListing = 4, TradePreorder = 5,
    Stall = 6, LegacyMail = 7, AuctionLot = 8, AuctionReserve = 9, AuctionPrebuy = 10,
    AuctionBonus = 11, GuildTreasury = 12, GuildMarket = 13, CaravanPending = 14,
    TerritoryPending = 15, TerritoryDrop = 16, TerritoryBid = 17, EscortGoods = 18,
    EscortListing = 19, EscortCart = 20, EscortCargo = 21, Pending = 22, Recovery = 23
}

/// <summary>Location and identity are independent of denomination; archive epochs belong in both where applicable.</summary>
public readonly record struct EconomyLocation(EconomyOwner Owner, EconomyCompartment Compartment,
    string Key, string Instance = "");
public sealed record EconomyHolding(EconomyAsset Asset, EconomyLocation Location, long Quantity, string Fingerprint = "");
public enum EconomyIssueKind { Corruption = 1, UnknownSchema = 2, Coverage = 3, RecoveryDebt = 4 }
public sealed record EconomyIssue(EconomyIssueKind Kind, string Key, string Detail);
public sealed record EconomyDecodeMetric(string Repository, long Rows, long Blobs, long Bytes);

/// <summary>
/// A cut is pinned by one SQLite read transaction. Legacy databases have no economic watermark: null is NOT zero
/// and cannot authorize ledger reconciliation. CapturedUtc is a diagnostic, never the commit order (§4.1).
/// </summary>
public sealed record EconomyCut(string RealmId, string SchemaVersion, string CatalogVersion,
    string CoverageVersion, long? CommitSequence, DateTimeOffset CapturedUtc);

/// <summary>Read-only evidence. E00 opens legacy holdings as a baseline and never invents source legs.</summary>
public sealed record EconomySnapshot(EconomyCut Cut, IReadOnlyList<EconomyHolding> Holdings,
    IReadOnlyList<EconomyIssue> Issues, IReadOnlyList<string> UncoveredMutationFamilies,
    IReadOnlyList<EconomyDecodeMetric> Metrics, double DecodeMilliseconds)
{
    public IReadOnlyDictionary<long, EconomyProgress> Progress { get; init; } = new Dictionary<long, EconomyProgress>();
    public IReadOnlyDictionary<long, long> ProgressRevisions { get; init; } = new Dictionary<long, long>();
}

/// <summary>Progression is receipt evidence, never a spendable economy asset.</summary>
public sealed record EconomyProgress(int BaseLevel, long BaseExp, int JobLevel, long JobExp);
