namespace Ro3Emu.Server.Economy;

/// <summary>
/// E00 catalogue-independent resource IDs. These are separate namespaces from ItemConfig IDs;
/// values are stable within each EconomyAssetKind.
/// </summary>
public static class EconomyDenominations
{
    public const long GuildMoney = 1;
    public const long GuildContribution = 2;
    public const long GuildMaterials = 3;
    public const long GuildReputation = 4;
    public const long GuildAuctionMaterials = 5;
    public const long EscortGoods = 1; // EconomyAssetKind.EventResource, not guild materials.
}
