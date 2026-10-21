using System.Globalization;
using System.Text.Json;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Issue #147: an NPC shop (ShopConfig _iShopType 3, the UI_Sys_ShopNpcPanel window) sells only beside an NPC that the
/// client data says opens it. The other shop types are menu windows the client opens anywhere (OpenFunctionUI from the
/// item-source links), so they have no NPC to stand next to.
/// </summary>
public sealed partial class ShopService
{
    private const int NpcShopType = 3;
    private const string NpcShopPanel = "UI_Sys_ShopNpcPanel";

    /// <summary>How far from its NPC a shop still sells: SceneService's talk reach (NPCConfig _iInteractDistance up to 4 m
    /// plus 8 m for client position lag).</summary>
    private const float ShopNpcReach = 12f;

    private Dictionary<long, HashSet<int>>? _shopOpeners;

    /// <summary>
    /// Null when the player stands within <see cref="ShopNpcReach"/> of an NPC on their map that opens this NPC shop, else
    /// 804 "too far". A shop no NPC opens (302 and 303 in this client) never sells.
    /// </summary>
    private Fail? FarFromShopNpc(GameSession session, JsonElement shop)
    {
        if (GameData.Int(shop, "_iShopType") != NpcShopType) return null;
        long shopId = GameData.Int(shop, "_iShopId");
        var openers = ShopOpeners().GetValueOrDefault(shopId);
        if (openers == null) return new(ErrorCodes.NpcTooFar, $"no NPC opens shop {shopId}");
        foreach (var npc in _world.Map(session.SceneEntry)?.Npcs ?? [])
            if (openers.Contains(npc.NpcId)
                && MonsterService.Distance(npc.X, npc.Z, session.Position.X, session.Position.Z) <= ShopNpcReach) return null;
        return new(ErrorCodes.NpcTooFar, $"no NPC of shop {shopId} ({string.Join('/', openers)}) near scene "
            + $"{session.SceneEntry} {session.Position.X:0.#},{session.Position.Z:0.#}");
    }

    /// <summary>
    /// Shop id -> the NPC ids that open its window, from the client data: the shop's own _iNpcId (custom content NPCs
    /// too) and NPCs whose DialogueConfig replies open the window on it (Kafra 100002's "Shop": 25 OpenFunctionUI 70110
    /// UI_Sys_ShopNpcPanel 301 301 1). In this client no map places 100002 or 25000003, nothing opens 302 or 303, and the
    /// item-source link for 301 (ResourceObtainInfoConfig 1011, 142 PathFindingNormal to target 10000101 on map 10111)
    /// names a target that map's scene editor doesn't have, so the client's own NPC shops sell nowhere.
    /// </summary>
    private Dictionary<long, HashSet<int>> ShopOpeners() => _shopOpeners ??= BuildShopOpeners();

    private Dictionary<long, HashSet<int>> BuildShopOpeners()
    {
        var openers = new Dictionary<long, HashSet<int>>();
        void Add(long shop, long npc)
        {
            if (shop <= 0 || npc <= 0 || npc > int.MaxValue) return;
            if (!openers.TryGetValue(shop, out var set)) openers[shop] = set = new();
            set.Add((int)npc);
        }
        // The shop id after the window name in an action, or 0.
        static long ShopOf(string[] action) =>
            Array.IndexOf(action, NpcShopPanel) is var i and >= 0 && i + 1 < action.Length
            && long.TryParse(action[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) ? id : 0;

        foreach (var row in _data.Table("ShopConfig").Values)
            if (GameData.Int(row, "_iShopType") == NpcShopType) Add(GameData.Int(row, "_iShopId"), GameData.Int(row, "_iNpcId"));

        foreach (var entry in _data.Table("DialogueConfig").Values)
            foreach (var row in entry.ValueKind == JsonValueKind.Array ? entry.EnumerateArray() : Enumerable.Repeat(entry, 1))
                foreach (var action in Slots(row, "_iEXECAction"))
                    Add(ShopOf(action), GameData.Int(row, "_iNPCID"));

        return openers;
    }

    internal IReadOnlyCollection<int> ShopNpcsForTest(long shop) =>
        ShopOpeners().TryGetValue(shop, out var set) ? set : [];

    /// <summary>Lets a test sell a shop beside an NPC that the client data doesn't link to it.</summary>
    internal void AddShopNpcForTest(long shop, int npc)
    {
        var openers = ShopOpeners();
        if (!openers.TryGetValue(shop, out var set)) openers[shop] = set = new();
        set.Add(npc);
    }

    /// <summary>An action list field: an array of string arrays (numbers kept as their text).</summary>
    private static IEnumerable<string[]> Slots(JsonElement row, string field)
    {
        if (!row.TryGetProperty(field, out var slots) || slots.ValueKind != JsonValueKind.Array) yield break;
        foreach (var slot in slots.EnumerateArray())
            if (slot.ValueKind == JsonValueKind.Array) yield return Strings(slot);
    }

    private static string[] Strings(JsonElement array) =>
        [.. array.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText())];
}
