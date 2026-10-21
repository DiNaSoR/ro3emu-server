using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Fashion gifts (82335-82339; LC_DAL_Shop, UI_Sys_Shop_Gift*, UI_Sys_FriendGiftlWidgetCtrl, the private-chat gift cards
/// UI_Sys_Friend_ChatList{Left,Right}WidgetCtrl). A role buys a Fashion commodity (ShopConfig type 7, _iAppearanceId) for a
/// friend: the price is paid now and a present order waits for the friend. The sender's role blob present_sent
/// (DBPresentInfo) holds the orders they paid for, in the payment's commit; the receiver's present_received holds the ones
/// they claimed, in the unlock's commit; the refund letter marks the sender's copy Refunded. The realm index joins both.
/// The friend claims it from the chat card (82336 Pick), which unlocks the look and its _iAppearanceAwardId bonus in their
/// wardrobe. An order not claimed within PublicParamConfig.OrderExpireTime (86400 s) returns the price to the sender by
/// mail PresentRefundMailId (1115). The chat card is posted by the sender's own client from the 82335 answer; both cards
/// read their state from full-list pushes: 82337 (orders to me) and 82339 (orders from me).
/// </summary>
public sealed partial class ShopService
{
    public const uint CommodityPick = 82336, PresentList = 82337, SentGiftsList = 82339;
    private const int ShopTypeFashion = 7;
    private const int ErrNotFriend = 2312, ErrOrderMissing = 2314, ErrDuplicateGift = 2318, ErrSelf = 2325, ErrGiftText = 2327,
        ErrPickMissing = 6901, ErrPickNotYours = 6902, ErrPickDone = 6903, ErrPickAppearance = 6904, ErrPickExpired = 6905;
    private const int StatePending = 0, StatePicked = 1, StateRefunded = 3;
    /// <summary>Finished orders stay this long so both chat cards keep showing "claimed" or "refunded" (inferred: the
    /// refund letter's 30 days; a card whose order is gone shows "refunded").</summary>
    private static readonly long KeepFinishedMs = (long)TimeSpan.FromDays(30).TotalMilliseconds;
    private const string ExpiryDeadline = "present-orders/expiry";

    // Orders of every role. Gift, pick and expiry each await a commit, and other roles' requests may change orders
    // meanwhile, so a commit applies only its own order to the dictionary as it is then (With at commit time), never
    // a copy taken before the await: that copy would put back a picked order as pending (refunded later, look kept).
    private Dictionary<long, PresentOrderInfo>? _presents;
    private long _lastOrderId;

    private long Param(string name, long fallback) => _data.Table("PublicParamConfig").TryGetValue(name, out var v)
        ? (long)(GameData.Number(v) ?? fallback) : fallback;

    private long ExpireMs => Param("OrderExpireTime", 86400) * 1000;

    private long NowMs => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>Every live order by id: the senders' copies, Picked where the receiver's present_received has it.</summary>
    internal Dictionary<long, PresentOrderInfo> Presents => _presents ??= LoadPresents();

    private Dictionary<long, PresentOrderInfo> LoadPresents()
    {
        var all = new Dictionary<long, PresentOrderInfo>();
        var picked = new HashSet<long>();
        foreach (var (_, sent, received) in _accounts.ReadSync<List<(long Role, byte[]? Sent, byte[]? Received)>>(new PresentOrderReadPlan()))
        {
            if (sent is { Length: > 0 }) foreach (var (id, order) in DBPresentInfo.Parser.ParseFrom(sent).Orders) all[id] = order.Clone();
            if (received is { Length: > 0 }) picked.UnionWith(DBPresentInfo.Parser.ParseFrom(received).Orders.Keys);
        }
        foreach (long id in picked) if (all.TryGetValue(id, out var order) && order.State == EPresentOrderState.Pending) order.State = EPresentOrderState.Picked;
        return all;
    }

    private bool Finished(PresentOrderInfo o) => o.State != EPresentOrderState.Pending && o.CreateTime + ExpireMs + KeepFinishedMs < NowMs;

    /// <summary>One role's blob of the orders it sent (or received): its live orders, finished ones dropped after 30 days.</summary>
    private byte[] Blob(long role, bool sender, IEnumerable<PresentOrderInfo> orders)
    {
        var info = new DBPresentInfo();
        foreach (var o in orders.Where(o => (sender ? o.SenderId : o.ReceiverId) == role && !Finished(o)
            && (sender || o.State == EPresentOrderState.Picked)))
            info.Orders[o.OrderId] = o.Clone();
        return info.ToByteArray();
    }

    private Dictionary<long, PresentOrderInfo> With(PresentOrderInfo changed)
    {
        var next = Presents.Where(p => !Finished(p.Value)).ToDictionary(p => p.Key, p => p.Value);
        next[changed.OrderId] = changed;
        return next;
    }

    private string NameOf(long role) => _sessions.All().FirstOrDefault(s => s.Role?.Id == role)?.Role?.Name ?? _roles.Get(role)?.Name ?? "";

    private int CommodityOf(long appearance) => _data.Index("ShopCommodityConfig", "_iAppearanceId", r => GameData.Int(r, "_iAppearanceId"))
        .All(appearance).Where(IsFashion)
        .Select(r => (int)GameData.Int(r, "_iID")).FirstOrDefault();

    private bool IsFashion(JsonElement commodity)
    {
        long classId = GameData.Int(commodity, "_iClassID");
        return _data.Index("ShopConfig", "_iClassID", s => GameData.Int(s, "_iClassID")).First(classId) is { ValueKind: JsonValueKind.Object } shop
            && GameData.Int(shop, "_iShopType") == ShopTypeFashion;
    }

    // ---- lists ----

    /// <summary>82337 and 82339 at login (nothing asks for them) and the first expiry deadline.</summary>
    public async Task PresentLoginAsync(GameSession session)
    {
        ArmPresentExpiry();
        await PushPresentsAsync(session);
    }

    private async Task PushPresentsAsync(GameSession session)
    {
        long me = session.Role!.Id;
        var orders = Presents.Values.OrderBy(o => o.OrderId).ToList();
        var received = new CommodityPresentListNtf();
        foreach (var o in orders.Where(o => o.ReceiverId == me))
            received.Orders.Add(new PresentOrderClientInfo
            {
                OrderId = o.OrderId, SenderId = o.SenderId, SenderName = NameOf(o.SenderId),
                AppranceId = o.AppranceId, AppranceAwardId = o.AppranceAwardId, CreateTime = o.CreateTime, State = (int)o.State,
                GiftContent = o.GiftContent
            });
        var sent = new SentGiftsListNtf();
        foreach (var o in orders.Where(o => o.SenderId == me))
            sent.SentGifts.Add(new SentGiftInfo
            {
                OrderId = o.OrderId, RecipientId = o.ReceiverId, RecipientName = NameOf(o.ReceiverId),
                AppranceId = o.AppranceId, AppranceAwardId = o.AppranceAwardId, CreateTime = o.CreateTime, State = (int)o.State,
                GiftContent = o.GiftContent, CurrencyId = (int)o.CurrencyId, CurrencyCount = o.CurrencyCount
            });
        await session.SendGameAsync(PresentList, received);
        await session.SendGameAsync(SentGiftsList, sent);
    }

    /// <summary>Both lists to whichever of these roles is online, on its own loop turn.</summary>
    private void PushPresentsTo(params long[] roleIds)
    {
        foreach (var s in _sessions.All().Where(s => s.Role != null && roleIds.Contains(s.Role.Id)))
            _loop.PostOwned(s, () => s.Role != null ? PushPresentsAsync(s) : Task.CompletedTask, "present lists");
    }

    // ---- 82335 send ----

    /// <summary>
    /// 82335: pay a Fashion commodity's price and open a pending order for a friend. The client checks only regular crystals
    /// (item 3) and the answer it echoes into the chat card, so the payment is the commodity's own currency, never the
    /// bound-crystal split of a purchase (inferred: the order and its refund carry one currencyId). Refusals: not a friend
    /// 2312, self 2325, not a Fashion look 2317, already owned 2316, an unclaimed order of the same look to them 2318,
    /// message over AppearanceMessage (20) characters 2327, then the shop's own gates and the payment.
    /// </summary>
    public async Task PresentAsync(GameSession session, CommodityPresentReq req)
    {
        int commodityId = req.BuyReq?.CommodityId ?? 0;
        var resp = new CommodityPresentResp { CommodityId = commodityId, TargetId = req.TargetId, GiftContent = req.GiftContent };
        using (await CharacterService.Lock(session))
            resp.Result = await PresentLockedAsync(session, req, commodityId, resp);
        _log.LogInformation("{Name} gifts commodity {Id} to {Target}: {Result}", session.Role!.Name, commodityId, req.TargetId, resp.Result);
        await session.SendGameAsync(CommodityPresent, resp);
        if (resp.Result == 0)
        {
            ArmPresentExpiry();
            await PushPresentsAsync(session);
            PushPresentsTo(req.TargetId);
        }
    }

    private async Task<int> PresentLockedAsync(GameSession session, CommodityPresentReq req, int commodityId, CommodityPresentResp resp)
    {
        long me = session.Role!.Id, target = req.TargetId;
        if (target == me) return ErrSelf;
        if (target <= 0 || _roles.Get(target) == null) return ErrNotFriend;
        if (_data.Row("ShopCommodityConfig", commodityId) is not { } row) return ErrorCodes.ShopNoConfig;
        int appearance = (int)GameData.Int(row, "_iAppearanceId");
        if (appearance <= 0 || !IsFashion(row)) return ErrorCodes.ShopGiftNotAllowed;
        string text = (req.GiftContent ?? "").Trim();
        if (text.Length > Param("AppearanceMessage", 20)) return ErrGiftText;
        if (Unavailable(session, row) is { } closed) return closed.Code;
        int unit = UnitPrice(session, row), currency = PaymentItem(row);
        if (Malformed(row, appearance, 1, unit) is { } bad) return bad.Code;
        if (currency <= 0 || unit <= 0) return ErrorCodes.ShopNoConfig;
        if (!await _friends.AreFriendsAsync(me, target)) return ErrNotFriend;
        if (await TargetOwnsAsync(target, appearance)) return ErrorCodes.ShopAppearanceOwned;
        if (Presents.Values.Any(o => o.ReceiverId == target && o.AppranceId == appearance && o.State == EPresentOrderState.Pending))
            return ErrDuplicateGift;

        // Ro3Emu:CashShop:Free pays a crystal price; the order then refunds nothing.
        var cost = _freeCashShop && currency == CrystalItem ? [] : new List<(int Entry, long Count)> { (currency, unit) };
        // Taken now, before the commit's await: two senders paying at once must not share an id.
        long orderId = _lastOrderId = Math.Max(_lastOrderId, Presents.Keys.DefaultIfEmpty(0).Max()) + 1;
        var order = new PresentOrderInfo
        {
            OrderId = orderId, SenderId = me, ReceiverId = target, AppranceId = appearance,
            AppranceAwardId = GameData.Int(row, "_iAppearanceAwardId"), CurrencyId = currency, CurrencyCount = cost.Count == 0 ? 0 : unit,
            CreateTime = NowMs, State = EPresentOrderState.Pending, GiftContent = text,
        };
        var next = With(order);
        var economy = _character.FamilyEconomy(session, Economy.CharacterFamilyEconomy.ShopGiftKind, cost, [],
            new { Version = 1, Order = orderId, Commodity = commodityId, Target = target, Appearance = appearance, Payment = currency, Unit = unit });
        var paid = await _character.ExchangeLockedAsync(session, cost, [],
            persist: tx => { tx.RoleBlob(me, "present_sent", Blob(me, true, next.Values)); tx.WithEconomy(economy); },
            committed: () => _presents = With(order), asyncCommit: true);
        if (paid == null) return _character.ShortageOf(session, cost);
        resp.OrderId = orderId;
        resp.GiftContent = text;
        return 0;
    }

    private async Task<bool> TargetOwnsAsync(long target, int appearance)
    {
        var online = _sessions.All().FirstOrDefault(s => s.Role?.Id == target && s.Wardrobe != null);
        var info = online?.Wardrobe ?? (await _roles.LoadWardrobeAsync(target) is { Length: > 0 } blob ? DBWardRobeInfo.Parser.ParseFrom(blob) : null);
        return info != null && info.AllLightUpAppearances.TryGetValue(appearance, out var owned)
            && (owned.OverdueTimeMs == 0 || owned.OverdueTimeMs > NowMs);
    }

    // ---- 82336 pick ----

    /// <summary>
    /// 82336: the receiver claims a pending order. The look, its suit pieces and the bonus award unlock through the same
    /// wardrobe path as a purchase, at no cost, and the order turns Picked in that commit. The client's answer handler is
    /// empty, so the card changes with the 82337/82339 pushes and the wardrobe sync. A look the receiver unlocked
    /// meanwhile answers 2316 and the order stays pending until it expires and refunds (inferred).
    /// </summary>
    public async Task PickAsync(GameSession session, CommodityPickReq req)
    {
        int result;
        long sender = 0;
        using (await CharacterService.Lock(session))
        {
            var order = Presents.GetValueOrDefault(req.OrderId);
            sender = order?.SenderId ?? 0;
            result = order == null ? ErrPickMissing
                : order.ReceiverId != session.Role!.Id ? ErrPickNotYours
                : order.State != EPresentOrderState.Pending ? ErrPickDone
                : order.CreateTime + ExpireMs <= NowMs ? ErrPickExpired
                : await PickLockedAsync(session, order);
        }
        _log.LogInformation("{Name} picks gift order {Order}: {Result}", session.Role!.Name, req.OrderId, result);
        await session.SendGameAsync(CommodityPick, new CommodityPickResp { Result = result });
        if (result == 0)
        {
            await PushPresentsAsync(session);
            PushPresentsTo(sender);
        }
    }

    private async Task<int> PickLockedAsync(GameSession session, PresentOrderInfo order)
    {
        int commodityId = CommodityOf(order.AppranceId);
        if (commodityId == 0 || _data.Row("ShopCommodityConfig", commodityId) is not { } row) return ErrPickAppearance;
        AwardPlan bonus;
        try { bonus = _awards.Resolve(session, order.AppranceAwardId, (int)(session.SeasonState?.PlayBookId ?? _seasons.PlayBook)); }
        catch (Exception e) when (e is AwardResolutionException or OverflowException) { return ErrorCodes.ShopRewardMissing; }
        var picked = order.Clone();
        picked.State = EPresentOrderState.Picked;
        var next = With(picked);
        long me = session.Role!.Id;
        return await _wardrobe.BuyFromShopLockedAsync(session, (int)order.AppranceId, Parts(row), [], bonus,
            persistShop: tx => tx.RoleBlob(me, "present_received", Blob(me, false, next.Values)), committedShop: () => _presents = With(picked),
            shopOutcome: new { Gift = order.OrderId, Sender = order.SenderId, Commodity = commodityId, Count = 1, Unit = 0 });
    }

    // ---- expiry ----

    /// <summary>One process deadline at the earliest pending order's expiry.</summary>
    private void ArmPresentExpiry()
    {
        var pending = Presents.Values.Where(o => o.State == EPresentOrderState.Pending).ToList();
        if (pending.Count == 0) { _deadlines.CancelGlobal(ExpiryDeadline); return; }
        long due = pending.Min(o => o.CreateTime) + ExpireMs;
        _deadlines.SetGlobal(ExpiryDeadline, DateTimeOffset.FromUnixTimeMilliseconds(due), (_, _, _) => ExpirePresentsAsync());
    }

    /// <summary>
    /// Each expired pending order returns its price by mail 1115 ("Appearance Gift Expiration Return"), delivered once
    /// (key present-refund:{order}) in the commit that marks the order Refunded.
    /// </summary>
    internal async Task ExpirePresentsAsync()
    {
        var expired = Presents.Values.Where(o => o.State == EPresentOrderState.Pending && o.CreateTime + ExpireMs <= NowMs)
            .Select(o => o.OrderId).ToList();
        int template = (int)Param("PresentRefundMailId", 1115);
        foreach (long id in expired)
        {
            var order = Presents[id];
            var refunded = order.Clone();
            refunded.State = EPresentOrderState.Refunded;
            var next = With(refunded);
            try
            {
                await _mail.SendTemplateOnceAsync(order.SenderId, template,
                    order.CurrencyCount > 0 ? [new MailService.Attachment((int)order.CurrencyId, order.CurrencyCount)] : [], $"present-refund:{id}",
                    tx => tx.RoleBlob(order.SenderId, "present_sent", Blob(order.SenderId, true, next.Values)), _loop.Owners?.Current);
                _presents = With(refunded);
                _log.LogInformation("Gift order {Order} expired: {Count}x {Currency} back to {Sender}", id, order.CurrencyCount, order.CurrencyId, order.SenderId);
                PushPresentsTo(order.SenderId, order.ReceiverId);
            }
            catch (Exception e) { _log.LogError(e, "Gift order {Order} refund failed", id); }
        }
        ArmPresentExpiry();
    }
}
