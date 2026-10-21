using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The in-game Top Up screen and the currency exchange, with no payment provider (Ro3Emu:TopUp:Free). The client side
/// (Lua_SDK_PayMgr, LC_DAL_Payment, UI_Sys_TopUpPanel, UI_Sys_CoinExchangeWidget) as read from the client Lua:
/// <list type="bullet">
/// <item>2003 GET_RECHARGE_FIRST_AWARD_RESULT {} -> {result, idList}: the RechargeConfig ids that still show the
/// first-purchase double (UI_Sys_TopUpPanelCtrl._updateAllRechargeData marks the rows whose id is in the list).</item>
/// <item>2001 CREATE_ORDER {chargeType 1 RechargeConfig / 2 ProductConfig, chargeId, amount, subject...} ->
/// {result, ErrMsg, chargeType, chargeId, orderId, orderData}. On PC the client decodes orderData and opens
/// data.transaction_url in the browser when there is one; "{}" opens nothing (it only logs a parse error). Mobile
/// would start the store SDK with orderId.</item>
/// <item>2004 UPDATE_RECHARGE_FIRST_AWARD_RESULT (push) {rechargeId}: removes that tier from the first-purchase list.</item>
/// <item>2002 PAY_RESULT: the client only logs the answer.</item>
/// <item>2005 ITEM_EXCHANGE {itemId = the item paid, count}: ItemExchangeConfig is keyed by the paid item
/// (3 crystal -> 1 Zeny, 1 Zeny -> 2 Minted Coin, x _iRate), up to _iSingleLimit, gated by _iFunction. Paying crystals
/// takes bound crystals first, as every crystal price does (Payment).</item>
/// </list>
/// A tier grants _kAward (crystal item 3), plus _kFirstChargeAward on its first purchase or else _kAwardBonus (both
/// bound crystal item 16). That the first-purchase double replaces the regular bonus is (inferred) from the tier
/// widget, which shows one or the other. Product awards (_iAward) are MultiverseGroupConfig ids; the month card and
/// pass entitlement items they give are turned into the card or premium track by NewActivityService.
/// </summary>
public sealed partial class ShopService
{
    public const uint CreateOrder = 2001, PayResult = 2002, FirstRechargeAward = 2003, FirstRechargeAwardUpdate = 2004,
        ItemExchange = 2005;
    private const int ChargeRecharge = 1, ChargeProduct = 2;

    private readonly bool _freeTopUp = options?.Value.TopUp.Free ?? false;

    /// <summary>2003: the tiers this role has not bought yet (all platforms; the client keeps its own platform's).</summary>
    public Task FirstRechargeAwardAsync(GameSession session)
    {
        var resp = new GetRechargeFirstAwardResp { Result = _freeTopUp ? 0 : ErrorCodes.FeatureUnavailable };
        if (_freeTopUp)
            foreach (var row in _data.Table("RechargeConfig").Values)
                if ((int)GameData.Int(row, "_iID") is var id and > 0 && session.Shop!.Recharged.GetValueOrDefault(id) == 0)
                    resp.IdList.Add(id);
        return session.SendGameAsync(FirstRechargeAward, resp);
    }

    /// <summary>2001: a Top Up tier or a product, granted at once.</summary>
    public async Task CreateOrderAsync(GameSession session, CreateOrderReq req)
    {
        var resp = new CreateOrderResp { ChargeType = req.ChargeType, ChargeId = req.ChargeId };
        bool first = false;
        if (!_freeTopUp) resp.Result = ErrorCodes.FeatureUnavailable;
        else if (req.ChargeType == ChargeRecharge) (resp.Result, first) = await RechargeAsync(session, req.ChargeId);
        else if (req.ChargeType == ChargeProduct) resp.Result = await ProductAsync(session, req.ChargeId);
        else resp.Result = ErrorCodes.Parameter;
        if (resp.Result == 0) { resp.OrderId = Guid.NewGuid().ToString("N"); resp.OrderData = "{}"; }
        else resp.ErrMsg = "Top Up is not available on this server.";
        await session.SendGameAsync(CreateOrder, resp);
        if (first) await session.SendGameAsync(FirstRechargeAwardUpdate, new UpdateRechargeFirstAwardResp { RechargeId = req.ChargeId });
    }

    /// <summary>2002: nothing is pending here; every order was settled by 2001. A client report never grants anything.</summary>
    public Task PayResultAsync(GameSession session, PayResultReq req) =>
        session.SendGameAsync(PayResult, new PayResultResp { Result = _freeTopUp ? 0 : ErrorCodes.FeatureUnavailable, OrderId = req.OrderId });

    private async Task<(int Result, bool First)> RechargeAsync(GameSession session, long chargeId)
    {
        using var guard = await CharacterService.Lock(session);
        if (chargeId is <= 0 or > int.MaxValue || _data.Row("RechargeConfig", chargeId) is not { } row) return (ErrorCodes.Parameter, false);
        int id = (int)chargeId;
        bool first = session.Shop!.Recharged.GetValueOrDefault(id) == 0;
        var goods = new List<(int Entry, int Count)>();
        AddPair(goods, row, "_kAward");
        if (!AddPair(goods, row, first ? "_kFirstChargeAward" : "_kAwardBonus") && first) AddPair(goods, row, "_kAwardBonus");
        if (goods.Count == 0) return (ErrorCodes.ShopNoConfig, false);
        var next = Copy(session.Shop);
        next.Recharged[id] = next.Recharged.GetValueOrDefault(id) + 1;
        int code = await GrantChargeAsync(session, "cd13a/shop-topup", goods, next,
            new { Recharge = id, First = first, Goods = goods.Select(g => new[] { g.Entry, g.Count }) });
        _log.LogInformation("{Name} tops up tier {Id} (first {First}): {Result}", session.Role!.Name, id, first, code == 0 ? "ok" : code);
        return (code, code == 0 && first);
    }

    private async Task<int> ProductAsync(GameSession session, long chargeId)
    {
        using var guard = await CharacterService.Lock(session);
        if (_data.Row("ProductConfig", chargeId) is not { } row) return ErrorCodes.Parameter;
        long award = GameData.Int(row, "_iAward");
        AwardPlan plan;
        try
        {
            plan = _data.Row("MultiverseGroupConfig", award) != null
                ? _awards.Resolve(session, award, (int)(session.SeasonState?.PlayBookId ?? _seasons.PlayBook))
                : _awards.ResolveDirect(session, award);
        }
        catch (Exception e) when (e is AwardResolutionException or OverflowException)
        {
            _log.LogWarning("Product {Id}: award {Award} unresolved: {Why}", chargeId, award, e.Message);
            return ErrorCodes.ShopRewardMissing;
        }
        var goods = plan.Grants.Where(g => g.Count > 0 && _data.Row("ItemConfig", g.Entry) != null)
            .GroupBy(g => g.Entry).Select(g => (g.Key, g.Sum(x => x.Count))).ToList();
        if (goods.Count == 0) return ErrorCodes.ShopRewardMissing;
        int code = await GrantChargeAsync(session, "cd13a/shop-product", goods, Copy(session.Shop!),
            new { Product = chargeId, Award = award, Goods = goods.Select(g => new[] { g.Item1, g.Item2 }) });
        _log.LogInformation("{Name} gets product {Id} (award {Award}): {Result}", session.Role!.Name, chargeId, award, code == 0 ? "ok" : code);
        return code;
    }

    /// <summary>The goods and the saved shop state in one transaction, with the state lock held.</summary>
    private async Task<int> GrantChargeAsync(GameSession session, string kind, List<(int Entry, int Count)> goods,
        ShopState next, object outcome)
    {
        var economy = _character.FamilyEconomy(session, kind, [], goods, outcome);
        var done = await _character.ExchangeLockedAsync(session, [], goods,
            persist: tx =>
            {
                tx.RoleBlob(session.Role!.Id, "shop", JsonSerializer.SerializeToUtf8Bytes(next));
                tx.WithEconomy(economy);
            },
            committed: () => session.Shop = next, asyncCommit: true);
        return done == null ? ErrorCodes.Parameter : 0;
    }

    /// <summary>A [item, count] config pair, added when both are positive.</summary>
    private bool AddPair(List<(int Entry, int Count)> goods, JsonElement row, string field)
    {
        if (!row.TryGetProperty(field, out var pair) || pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() < 2) return false;
        long entry = (long)(GameData.Number(pair[0]) ?? 0), count = (long)(GameData.Number(pair[1]) ?? 0);
        if (entry is <= 0 or > int.MaxValue || count is <= 0 or > int.MaxValue || _data.Row("ItemConfig", entry) == null) return false;
        goods.Add(((int)entry, (int)count));
        return true;
    }

    /// <summary>2005: pay count of the item, get count x _iRate of its target.</summary>
    public async Task ExchangeAsync(GameSession session, ItemExchangeReq req)
    {
        int code;
        using (await CharacterService.Lock(session))
        {
            var row = req.ItemId is > 0 and <= int.MaxValue ? _data.Row("ItemExchangeConfig", req.ItemId) : null;
            long rate = row is { } r ? GameData.Int(r, "_iRate") : 0, limit = row is { } l ? GameData.Int(l, "_iSingleLimit") : 0;
            int target = row is { } t ? (int)GameData.Int(t, "_iTargetID") : 0;
            long function = row is { } f ? GameData.Int(f, "_iFunction") : 0;
            if (row == null || rate <= 0 || target <= 0 || _data.Row("ItemConfig", target) == null) code = ErrorCodes.Parameter;
            else if (function > 0 && !_functions.IsOpen(session, function)) code = ErrorCodes.FunctionLocked;
            else if (req.Count < 1 || (limit > 0 && req.Count > limit) || req.Count * rate > int.MaxValue) code = ErrorCodes.Parameter;
            else
            {
                var cost = Payment(session, (int)req.ItemId, req.Count); // crystals: bound ones first
                (int Entry, int Count)[] goods = [(target, (int)(req.Count * rate))];
                var economy = _character.FamilyEconomy(session, "cd13a/shop-exchange", cost, goods,
                    new { Paid = req.ItemId, req.Count, Target = target, Rate = rate });
                code = await _character.ExchangeLockedAsync(session, cost, goods, persist: tx => tx.WithEconomy(economy)) == null
                    ? _character.ShortageOf(session, cost) : 0;
            }
            _log.LogInformation("{Name} exchanges {Count}x {Item}: {Result}", session.Role!.Name, req.Count, req.ItemId, code == 0 ? "ok" : code);
        }
        await session.SendGameAsync(ItemExchange, new ItemExchangeResp { Result = code });
    }
}
