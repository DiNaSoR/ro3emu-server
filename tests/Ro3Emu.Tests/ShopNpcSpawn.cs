using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Issue #147: an NPC shop sells only beside an NPC the client data says opens it, and no map of this client places one
/// for the Kafra shops 301-303. Tests about other buying rules sell them beside NPC 100196 instead, which stands 7 m from
/// the map 10111 born point where new roles start.
/// </summary>
internal static class ShopNpcSpawn
{
    public const int SpawnNpc = 100196;

    public static void SellNpcShopsAtSpawn(this ShopService shops)
    {
        foreach (long shop in new long[] { 301, 302, 303 }) shops.AddShopNpcForTest(shop, SpawnNpc);
    }
}
