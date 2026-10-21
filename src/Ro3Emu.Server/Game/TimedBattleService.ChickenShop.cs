using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;
using static Ro3Emu.Server.Game.ChickenRules;

namespace Ro3Emu.Server.Game;

/// <summary>A Survivor Battle service NPC: a shop NPC (ShopConfig _iShopType 8) or a Rescue Wagon.</summary>
internal sealed class ChickenNpcUnit : WorldUnit
{
    public required int NpcId { get; init; }
    public required string Name { get; init; }
    /// <summary>ShopConfig _iShopId; 0 for a Rescue Wagon or a Magic Die.</summary>
    public int ShopId { get; init; }

    public override EntityBase Entity() => new()
    {
        Uuid = (ulong)Uid, EntityTemplateID = (ulong)NpcId, EntityType = 2, Name = Name, EntityCamp = 3, Face = Face,
        State = EntityState.Alive, NotifyType = EntityBase.Types.NotifyType.All,
        MovementInfo = new MovementInfo { Pos = Pos, Face = Face, ServerTime = MonsterService.NowMs, MoveType = MovementType.MoveTypeNone },
    };
}

/// <summary>
/// Survivor Battle shop NPCs and Rescue Wagons. Client flow: NPCConfig 15000054 / 15000060 interact 2900003 / 2900011
/// open UI_Sys_Chicken_ShopNpcPanel with shop 801 / 802 and the NPC's guid; the panel sends 82334 DUNGEON_SHOP with the
/// guid only (LC_DAL_Shop.lua:369-375, shopId unset) and builds its list from the answer's commodities, grouped by
/// ShopCommodityConfig._iClassID under the shop's ShopConfig tabs (UI_Sys_Chicken_ShopNpcPanelCtrl.lua:139-315).
/// remainCount 0 shows "sold out", canBuyCount is the quantity cap (0 shows the condition text), a negative remainCount
/// hides the count (UI_Sys_Shop_Chicken_NpcListWidgetCtrl.lua). Buying is the ordinary 82332 with npcId = the guid;
/// the price is paid from the match pack's currency 10006 (the client checks LC_ChickenHelper.GetChickenItemCount).
/// The answer merges into DungeonShopResultData, which the client deletes when it leaves the match
/// (LC_DunGeon_Chicken.lua:1327).
/// </summary>
public sealed partial class TimedBattleService
{
    public const uint DungeonShop = 82334;
    /// <summary>Service NPC uids: inside the chest namespace (2^48), above every chest serial, so the dispatcher can tell
    /// a match NPC from the uid alone.</summary>
    private const long NpcUidBase = ChestUidBase | (1L << 46);
    private static long _npcSerial;

    internal static bool IsChickenNpcUid(long uid) => uid >= NpcUidBase && uid < NpcUidBase + (1L << 46);

    /// <summary>Spawns the shop NPCs and Rescue Wagons of the 2240101 respawn regions at the battle start.</summary>
    private async Task SpawnServiceNpcsAsync(Match match, ChickenRun run)
    {
        foreach (var row in _data.Table("NpcLevelRegionRespawnConfig").Values
            .Where(r => GameData.Int(r, "_iMapid") == ChickenScene).OrderBy(r => GameData.Int(r, "_iId")))
        {
            int regionId = (int)GameData.Int(row, "_iRespawnregionid");
            var region = regionId == MagicDiePoint ? (Region: MagicDiePoint, X: IslandX, Z: IslandZ, Width: IslandRadius * 2, Length: IslandRadius * 2)
                : ServiceRegions.FirstOrDefault(r => r.Region == regionId);
            if (region.Region <= 0 || !row.TryGetProperty("_iNpcrespawngroup", out var groups) || groups.ValueKind != JsonValueKind.Array) continue;
            foreach (var groupId in groups.EnumerateArray())
            {
                if (_data.Row("NpcRespawnGroupConfig", (long)(GameData.Number(groupId) ?? 0)) is not { } group
                    || !group.TryGetProperty("_kNpcgroup", out var npcs) || npcs.ValueKind != JsonValueKind.Array
                    || !group.TryGetProperty("_iByNum", out var counts) || counts.ValueKind != JsonValueKind.Array) continue;
                for (int i = 0; i < npcs.GetArrayLength() && i < counts.GetArrayLength(); i++)
                {
                    int npc = (int)(GameData.Number(npcs[i]) ?? 0), count = (int)(GameData.Number(counts[i]) ?? 0);
                    int shop = ShopOfNpc(npc);
                    if (shop == 0 && npc != RescueWagon && npc != MagicDie) continue;
                    for (int n = 0; n < count && !match.Settled; n++)
                        if (ServicePoint(run, region) is { } at) await SpawnServiceNpcAsync(match, run, npc, shop, at);
                }
            }
        }
    }

    private MapPoint? ServicePoint(ChickenRun run, (int Region, float X, float Z, float Width, float Length) region)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            float x = region.X + (float)((run.Rng.NextDouble() - 0.5) * region.Width);
            float z = region.Z + (float)((run.Rng.NextDouble() - 0.5) * region.Length);
            if (MonsterService.Distance(x, z, IslandX, IslandZ) > IslandRadius * 0.9f) continue;
            if (WalkablePoint(x, z) is { } at) return at with { Face = (float)(run.Rng.NextDouble() * 360) };
        }
        return null;
    }

    private async Task<ChickenNpcUnit?> SpawnServiceNpcAsync(Match match, ChickenRun run, int npcId, int shopId, MapPoint at)
    {
        if (_data.Row("NPCConfig", npcId) is not { } npc) return null;
        var unit = new ChickenNpcUnit
        {
            Uid = NpcUidBase + Interlocked.Increment(ref _npcSerial), SceneEntry = ChickenScene, InstanceId = match.Room!.Id,
            X = at.X, Y = at.Y, Z = at.Z, Face = at.Face, NpcId = npcId, Name = _data.TextOf(npc, "_kName"), ShopId = shopId,
        };
        if (!await _instances.AddPropAsync(match.Room, unit)) return null;
        run.Npcs.Add(unit);
        return unit;
    }

    /// <summary>ShopConfig: the chicken shop an NPC opens (801 for 15000054, 802 for 15000060), else 0.</summary>
    private int ShopOfNpc(int npcId) => _data.Table("ShopConfig").Values
        .Where(r => GameData.Int(r, "_iShopType") == ChickenShopType && GameData.Int(r, "_iNpcId") == npcId)
        .Select(r => (int)GameData.Int(r, "_iShopId")).FirstOrDefault();

    /// <summary>
    /// A shop's goods for this match, rolled once and shared by every NPC of the shop (inferred: the client keeps one
    /// merged list per match). Each ShopConfig tab's _kGroup [group, n] draws n different commodities of that
    /// ShopCommodityConfig _iGroup, weighted by _iWeight (inferred draw).
    /// </summary>
    private IReadOnlyList<int> ShopGoods(ChickenRun run, int shopId)
    {
        if (run.ShopGoods.TryGetValue(shopId, out var goods)) return goods;
        goods = new List<int>();
        var commodities = _data.Table("ShopCommodityConfig").Values.ToArray();
        foreach (var tab in _data.Table("ShopConfig").Values
            .Where(r => GameData.Int(r, "_iShopType") == ChickenShopType && GameData.Int(r, "_iShopId") == shopId)
            .OrderBy(r => GameData.Int(r, "_iClassID")))
        {
            if (!tab.TryGetProperty("_kGroup", out var groups) || groups.ValueKind != JsonValueKind.Array) continue;
            long classId = GameData.Int(tab, "_iClassID");
            foreach (var spec in groups.EnumerateArray())
            {
                if (spec.ValueKind != JsonValueKind.Array || spec.GetArrayLength() < 2) continue;
                long group = (long)(GameData.Number(spec[0]) ?? 0);
                int wanted = (int)(GameData.Number(spec[1]) ?? 0);
                var pool = commodities.Where(c => GameData.Int(c, "_iGroup") == group && GameData.Int(c, "_iClassID") == classId)
                    .Select(c => (Id: (int)GameData.Int(c, "_iID"), Weight: Math.Max(0, GameData.Int(c, "_iWeight"))))
                    .Where(c => c.Weight > 0).OrderBy(c => c.Id).ToList();
                for (int n = 0; n < wanted && pool.Count > 0; n++)
                {
                    long pick = run.Rng.NextInt64(pool.Sum(c => c.Weight));
                    var chosen = pool.First(c => (pick -= c.Weight) < 0);
                    pool.Remove(chosen);
                    goods.Add(chosen.Id);
                }
            }
        }
        run.ShopGoods[shopId] = goods;
        return goods;
    }

    /// <summary>What this player may still buy of a commodity: _iPurchaseNum per match (inferred), -1 = no cap.</summary>
    private int LeftToBuy(ChickenPlayer me, JsonElement commodity)
    {
        long cap = GameData.Int(commodity, "_iPurchaseNum");
        return cap <= 0 ? -1 : (int)Math.Max(0, cap - me.ShopBought.GetValueOrDefault((int)GameData.Int(commodity, "_iID")));
    }

    private GetDungeonShopResp ShopList(ChickenRun run, ChickenPlayer me, int shopId)
    {
        var resp = new GetDungeonShopResp();
        foreach (int id in ShopGoods(run, shopId))
        {
            if (_data.Row("ShopCommodityConfig", id) is not { } row) continue;
            int left = LeftToBuy(me, row);
            resp.CommodityInfo[id] = new DungeonCommodityInfo { CommodityId = id, RemainCount = left, CanBuyCount = left < 0 ? 999 : left };
        }
        return resp;
    }

    private ChickenNpcUnit? NpcInReach(ChickenRun run, GameSession session, long uid, out int error)
    {
        var npc = run.Npcs.FirstOrDefault(n => n.Uid == uid);
        error = npc == null ? ErrorCodes.NpcMissing
            : MonsterService.Distance(session.Position.X, session.Position.Z, npc.X, npc.Z) > PickupReach ? ErrorCodes.NpcTooFar : 0;
        return error == 0 ? npc : null;
    }

    /// <summary>82334: the list of the shop NPC's goods (empty outside a match or for another NPC).</summary>
    public async Task DungeonShopAsync(GameSession session, GetDungeonShopReq req)
    {
        var resp = new GetDungeonShopResp();
        if (Chicken(session) is (var match, var run, var me))
            await RunMatchTransitionAsync(match, "chicken shop list", () =>
            {
                if (run.Npcs.FirstOrDefault(n => n.Uid == req.NpcGuid) is { ShopId: > 0 } npc) resp = ShopList(run, me, npc.ShopId);
                return Task.CompletedTask;
            });
        await session.SendGameAsync(DungeonShop, resp);
    }

    /// <summary>82332 at a match shop NPC: pays the pack's currency, puts the goods in the pack, then 5036 and a fresh
    /// 82334 so the open panel shows the new counts.</summary>
    public async Task ChickenBuyAsync(GameSession session, BuyCommodityReq req)
    {
        int result = ErrorCodes.Parameter, price = 0, bought = 0;
        GetDungeonShopResp? list = null;
        if (Chicken(session) is (var match, var run, var me))
            await RunMatchTransitionAsync(match, "chicken shop buy", async () =>
            {
                if ((result = CanAct(run, me, session)) != 0) return;
                if (NpcInReach(run, session, req.NpcId, out result) is not { ShopId: > 0 } npc)
                { if (result == 0) result = ErrorCodes.NpcParameter; return; }
                if (req.Count <= 0 || !ShopGoods(run, npc.ShopId).Contains(req.CommodityId)
                    || _data.Row("ShopCommodityConfig", req.CommodityId) is not { } row) { result = ErrorCodes.NotForSale; return; }
                int left = LeftToBuy(me, row);
                if (left >= 0 && req.Count > left) { result = ErrorCodes.PurchaseLimit; return; }
                if (!row.TryGetProperty("_kItemPerPrice", out var cost) || cost.ValueKind != JsonValueKind.Array || cost.GetArrayLength() < 2)
                { result = ErrorCodes.ShopNoConfig; return; }
                int currency = (int)(GameData.Number(cost[0]) ?? 0);
                long total = (long)(GameData.Number(cost[1]) ?? 0) * req.Count;
                int entry = (int)GameData.Int(row, "_iItemId");
                long units = Math.Max(1, GameData.Int(row, "_iItemPerMount")) * (long)req.Count;
                if (ItemRow(entry) == null || total is < 0 or > int.MaxValue || units > int.MaxValue) { result = ErrorCodes.ShopNoConfig; return; }
                int amount = (int)units;
                if (me.Pack.Values.Where(i => i.Entry == currency).Sum(i => (long)i.Stack) < total) { result = ErrorCodes.CurrencyShort; return; }
                if (Fits(me, entry, amount) < amount) { result = ErrorCodes.BagFull; return; }
                long due = total;
                foreach (var stack in me.Pack.Values.Where(i => i.Entry == currency).OrderBy(i => i.Stack).ToArray())
                {
                    int take = (int)Math.Min(due, stack.Stack);
                    RemoveFromPack(me, stack.Guid, take);
                    if ((due -= take) <= 0) break;
                }
                AddToPack(run, me, entry, amount);
                bought = me.ShopBought[req.CommodityId] = me.ShopBought.GetValueOrDefault(req.CommodityId) + req.Count;
                price = (int)total;
                result = 0;
                list = ShopList(run, me, npc.ShopId);
                await SyncRoundAsync(match, run, session);
            });
        await session.SendGameAsync(ShopService.BuyCommodity, new BuyCommodityResp
        { Result = result, CommodityId = req.CommodityId, Count = req.Count, Price = price, BuyTimes = bought });
        if (list != null) await session.SendGameAsync(DungeonShop, list);
    }

    /// <summary>32700 with a match NPC: the client only waits for the echo (LC_DAL_Interact.OnNPCInteractionResponse
    /// dispatches InteractResponse with req), then opens the shop or Rescue Wagon panel itself.</summary>
    internal async Task<bool> TryChickenInteractAsync(GameSession session, NpcInteractionReq req)
    {
        if (!IsChickenNpcUid(req.NpcUid)) return false;
        int result = ErrorCodes.NpcMissing;
        if (Chicken(session) is (var match, var run, var me))
            await RunMatchTransitionAsync(match, "chicken npc interact", async () =>
            {
                if (NpcInReach(run, session, req.NpcUid, out result) is not { } npc) return;
                if (_data.Row("NPCConfig", npc.NpcId) is not { } row || !row.TryGetProperty("_iInteractID", out var ids)
                    || ids.ValueKind != JsonValueKind.Array || ids.EnumerateArray().All(x => GameData.Number(x) != req.InteractionId))
                    result = ErrorCodes.NpcParameter;
                else if (npc.NpcId == MagicDie && (result = CanAct(run, me, session)) == 0)
                    await TriggerAdventureAsync(match, run, session, me, npc);
            });
        await session.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionRsp { Result = result, Req = req });
        return true;
    }

    // ---- 82662 Rescue Wagon ----

    /// <summary>
    /// 82662 at a Rescue Wagon (UI_Com_ChickenAmbulanceRevivalPanelCtrl.lua:181-196 -> LC_ChickenHelper.lua:1334, npcId
    /// only; the panel needs one ID card 50001). (inferred) Every fallen teammate whose ID card the player carries comes
    /// back at once, one card each, with the revive map opened at the wagon, like a finished channel (teamMateId, when
    /// set, picks one).
    /// </summary>
    public async Task RelifeOtherAsync(GameSession session, ChickenRelifeOtherReq req)
    {
        int result = ErrorCodes.Parameter;
        if (Chicken(session) is (var match, var run, var me))
            await RunMatchTransitionAsync(match, "chicken wagon revive", async () =>
            {
                if ((result = CanAct(run, me, session)) != 0) return;
                if (NpcInReach(run, session, req.NpcId, out result) is not { NpcId: RescueWagon } wagon)
                { if (result == 0) result = ErrorCodes.NpcParameter; return; }
                var targets = Team(match, match.Faction[session]).Where(mate => mate != session && mate.Dead
                    && match.Fallen.Contains(mate) && !match.Absent.Contains(mate) && run.Players[mate] is { GaveUp: false } t
                    && t.Resurrections < ResurrectionLimit && me.IdentityCards.ContainsKey(match.Participants[mate].Id)
                    && (req.TeamMateId == 0 || match.Participants[mate].Id == req.TeamMateId)
                    && run.Channels.All(c => !c.Targets.Contains(mate))).ToArray();
                if (targets.Length == 0) { result = ErrorCodes.ItemsShort; return; }
                long now = NowMs();
                foreach (var target in targets)
                {
                    RemoveFromPack(me, me.IdentityCards[match.Participants[target].Id], 1);
                    var revived = run.Players[target];
                    revived.Resurrections++; revived.Rescued++; me.Help++;
                    match.Fallen.Remove(target);
                    run.EliminatedFactions.Remove(match.Faction[target]);
                    await OpenRelifeAsync(target, revived, wagon.X, wagon.Z, now);
                }
                result = 0;
                await SyncTeamAsync(match, match.Faction[session]);
            });
        await session.SendGameAsync(RelifeOther, new ChickenRelifeOtherResp { Result = result });
    }

    internal IReadOnlyList<ChickenNpcUnit> NpcsForTest(GameSession session) =>
        Chicken(session) is (_, var run, _) ? run.Npcs.ToArray() : [];
}
