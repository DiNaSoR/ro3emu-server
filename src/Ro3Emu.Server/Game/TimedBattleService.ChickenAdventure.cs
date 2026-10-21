using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using static Ro3Emu.Server.Game.ChickenRules;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Survivor Battle Magic Dice (the in-match "adventure", 奇遇). Touching a die (interact 2900010: action 325
/// TriggerAdventureEvent 10001, then 3 RemoveInteractionNpc) offers a card pick from AdventureDeckConfig 1003:
/// _kRewardStagePersonal [3, 1] = 3 cards drawn by _iWeight from AdventureCardConfig pool 1003, choose 1 (inferred
/// meaning). The pick reaches the client only through the 5039 DBAdventureInfo autosync (LC_DAL_Roguelike):
/// functionInfos[curActiveFunctionId 1003].deck[curActiveSourceId].needSelectMap[operateId] =
/// AdventureEventSelectInfo {poolId, refreshCount, chooseCount, cards keyed 1..n, endTime (s)}; the DAL queues them by
/// operateId and opens UI_Sys_Chicken_AdventurePanel, and an empty map closes it. deck.cards is UI_Sys_Chicken_DecksPanel.
/// 83421 AdventureEventSelectReq {operateId, cardId[]} takes exactly chooseCount cards; 83422 AdventureRefreshReq redraws
/// for Kaka Coin (_kRewardStageAuction [3 Pay, 10006, 1000, 2000, 3000, 4000...]: refresh n costs the n-th price, the
/// last one after; _iLimit 10 refreshes, LC_RoguelikeHelper.GetCostOfEachRefresh). (inferred) After _iAutoSelection 10 s
/// the server picks the first card. Card effects (_kEffect): 203 GivePlayerBuff, 326 GainChickenItem [item, count],
/// 338 UpgradeList (Baphomet: every worn equipment to its _iUpgradeItem, one quality up).
/// </summary>
public sealed partial class TimedBattleService
{
    public const uint AdventureSelect = 83421, AdventureRefresh = 83422, AdventureSync = 5039;

    private JsonElement? AdventureDeck() => _data.Row("AdventureDeckConfig", AdventureFunction);

    private static int[] DeckList(JsonElement deck, string field) =>
        deck.TryGetProperty(field, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(x => (int)(GameData.Number(x) ?? 0)).ToArray() : [];

    /// <summary>Weighted draw of distinct cards from the deck's pool.</summary>
    private List<long> DrawCards(ChickenRun run, int count)
    {
        var pool = _data.Table("AdventureCardConfig").Values
            .Where(r => GameData.Int(r, "_iPoolId") == AdventureFunction)
            .Select(r => (Id: GameData.Int(r, "_iCardId"), Weight: Math.Max(0, GameData.Int(r, "_iWeight"))))
            .Where(c => c.Weight > 0).OrderBy(c => c.Id).ToList();
        var drawn = new List<long>();
        while (drawn.Count < count && pool.Count > 0)
        {
            long roll = (long)(run.Rng.NextDouble() * pool.Sum(c => c.Weight));
            int i = 0;
            for (; i < pool.Count - 1 && roll >= pool[i].Weight; i++) roll -= pool[i].Weight;
            drawn.Add(pool[i].Id);
            pool.RemoveAt(i);
        }
        return drawn;
    }

    private long AutoSelectSeconds() =>
        AdventureDeck() is { } deck && GameData.Int(deck, "_iAutoSelection") is var s and > 0 ? s : 10;

    private void Redraw(ChickenRun run, AdventureEventSelectInfo pick)
    {
        var personal = AdventureDeck() is { } deck ? DeckList(deck, "_kRewardStagePersonal") : [];
        pick.Cards.Clear();
        int i = 1;
        foreach (long card in DrawCards(run, personal.Length > 0 && personal[0] > 0 ? personal[0] : 3)) pick.Cards[i++] = card;
        pick.EndTime = _time.GetUtcNow().ToUnixTimeSeconds() + AutoSelectSeconds();
    }

    /// <summary>32700 on a die (TryChickenInteractAsync, inside the match transition): the die goes, a pick opens.</summary>
    private async Task TriggerAdventureAsync(Match match, ChickenRun run, GameSession session, ChickenPlayer me, ChickenNpcUnit die)
    {
        run.Npcs.Remove(die);
        if (match.Room != null) await _instances.RemovePropAsync(match.Room, die);
        var personal = AdventureDeck() is { } deck ? DeckList(deck, "_kRewardStagePersonal") : [];
        var pick = new AdventureEventSelectInfo
        {
            OperateId = ++me.NextOperate, PoolId = AdventureFunction,
            ChooseCount = personal.Length > 1 && personal[1] > 0 ? personal[1] : 1,
        };
        Redraw(run, pick);
        me.Picks[pick.OperateId] = pick;
        await SyncAdventureAsync(match, session, me);
    }

    private static DBAdventureInfo AdventureState(Match match, ChickenPlayer me)
    {
        var info = new DBAdventureInfo();
        if (me.Picks.Count == 0 && me.Cards.Count == 0) return info;
        long source = match.Room?.Id ?? 0;
        var deck = new AdventureCardDeck { FunctionId = AdventureFunction };
        for (int i = 0; i < me.Cards.Count; i++) deck.Cards[i + 1] = new AdventureCardInfo { CardId = me.Cards[i] };
        foreach (var (id, pick) in me.Picks) deck.NeedSelectMap[id] = pick.Clone();
        var function = new AdventureFunctionInfo();
        function.Deck[source] = deck;
        info.FunctionInfos[AdventureFunction] = function;
        info.CurActiveFunctionId = AdventureFunction;
        info.CurActiveSourceId = source;
        return info;
    }

    private static Task SyncAdventureAsync(Match match, GameSession session, ChickenPlayer me) =>
        CharacterService.SyncAsync(session, AdventureSync, AdventureState(match, me));

    /// <summary>83421.</summary>
    public async Task AdventureSelectAsync(GameSession session, AdventureEventSelectReq req)
    {
        int result = ErrorCodes.Parameter;
        if (Chicken(session) is (var match, var run, var me))
            await RunMatchTransitionAsync(match, "chicken adventure pick", async () =>
            {
                if (!me.Picks.TryGetValue(req.OperateId, out var pick)) return;
                var offered = pick.Cards.Values.ToHashSet();
                if (req.CardId.Count != pick.ChooseCount || req.CardId.Distinct().Count() != req.CardId.Count
                    || req.CardId.Any(c => !offered.Contains(c))) return;
                result = 0;
                await ResolvePickAsync(match, run, session, me, pick, req.CardId.ToArray());
            });
        await session.SendGameAsync(AdventureSelect, new AdventureEventSelectResp { Result = result, Req = req });
    }

    /// <summary>83422.</summary>
    public async Task AdventureRefreshAsync(GameSession session, AdventureRefreshReq req)
    {
        int result = ErrorCodes.Parameter;
        if (Chicken(session) is (var match, var run, var me))
            await RunMatchTransitionAsync(match, "chicken adventure refresh", async () =>
            {
                if (!me.Picks.TryGetValue(req.OperateId, out var pick) || AdventureDeck() is not { } deck) return;
                if (pick.RefreshCount >= GameData.Int(deck, "_iLimit")) { result = ErrorCodes.PurchaseLimit; return; }
                var stage = DeckList(deck, "_kRewardStageAuction");
                if (stage.Length < 3) { result = ErrorCodes.FeatureUnavailable; return; }
                int currency = stage[1];
                long cost = stage[Math.Min(stage.Length - 1, pick.RefreshCount + 2)];
                if (me.Pack.Values.Where(i => i.Entry == currency).Sum(i => (long)i.Stack) < cost) { result = ErrorCodes.CurrencyShort; return; }
                long due = cost;
                foreach (var stack in me.Pack.Values.Where(i => i.Entry == currency).OrderBy(i => i.Stack).ToArray())
                {
                    int take = (int)Math.Min(due, stack.Stack);
                    RemoveFromPack(me, stack.Guid, take);
                    if ((due -= take) <= 0) break;
                }
                pick.RefreshCount++;
                Redraw(run, pick);
                result = 0;
                await SyncRoundAsync(match, run, session);
                await SyncAdventureAsync(match, session, me);
            });
        await session.SendGameAsync(AdventureRefresh, new AdventureRefreshResp { Result = result, Req = req });
    }

    private async Task ResolvePickAsync(Match match, ChickenRun run, GameSession session, ChickenPlayer me,
        AdventureEventSelectInfo pick, long[] cards)
    {
        me.Picks.Remove(pick.OperateId);
        long now = NowMs();
        bool bag = false, gear = false;
        foreach (long card in cards)
        {
            me.Cards.Add(card);
            if (_data.Row("AdventureCardConfig", card) is not { } row || !row.TryGetProperty("_kEffect", out var effects)
                || effects.ValueKind != JsonValueKind.Array) continue;
            foreach (var effect in effects.EnumerateArray())
            {
                if (effect.ValueKind != JsonValueKind.Array || effect.GetArrayLength() == 0) continue;
                long Arg(int i) => effect.GetArrayLength() > i ? (long)(GameData.Number(effect[i]) ?? 0) : 0;
                switch (Arg(0))
                {
                    case 203 when !session.Dead:
                        await ApplyItemBuffAsync(session, me, (int)Arg(1), now);
                        break;
                    case 326 when ItemRow((int)Arg(1)) != null && Arg(2) > 0:
                        AddToPack(run, me, (int)Arg(1), (int)Arg(2));
                        bag = true;
                        break;
                    case 338:
                        foreach (var (slot, guid) in me.EquipBar.ToArray())
                        {
                            if (!me.Pack.TryGetValue(guid, out var worn) || ItemRow(worn.Entry) is not { } item
                                || GameData.Int(item, "_iUpgradeItem") is not (> 0 and var next and <= int.MaxValue) || ItemRow((int)next) == null) continue;
                            RemoveFromPack(me, guid, worn.Stack);
                            var better = AddToPack(run, me, (int)next, 1, worn.OwnerId, worn.OwnerName);
                            if (better.Count > 0) me.EquipBar[slot] = better[0].Guid;
                            bag = gear = true;
                        }
                        break;
                }
            }
        }
        if (gear) await _character.RefreshStatsAsync(session);
        if (bag) await SyncRoundAsync(match, run, session);
        await SyncAdventureAsync(match, session, me);
    }

    /// <summary>Per tick: picks past their endTime take the first card (inferred auto-selection).</summary>
    private async Task TickAdventureAsync(Match match, ChickenRun run)
    {
        long now = _time.GetUtcNow().ToUnixTimeSeconds();
        foreach (var (player, me) in run.Players.ToArray())
            foreach (var pick in me.Picks.Values.Where(p => p.EndTime <= now).ToArray())
            {
                if (!InRoom(match).Contains(player)) { me.Picks.Remove(pick.OperateId); continue; }
                var chosen = pick.Cards.OrderBy(c => c.Key).Select(c => c.Value).Take(Math.Max(1, pick.ChooseCount)).ToArray();
                await ResolvePickAsync(match, run, player, me, pick, chosen);
            }
    }

    /// <summary>Tests: an open pick with the given cards (as a die would give).</summary>
    internal async Task OfferForTestAsync(GameSession session, params long[] cards)
    {
        if (Chicken(session) is not (var match, _, var me)) return;
        var pick = new AdventureEventSelectInfo
        {
            OperateId = ++me.NextOperate, PoolId = AdventureFunction, ChooseCount = 1,
            EndTime = _time.GetUtcNow().ToUnixTimeSeconds() + AutoSelectSeconds(),
        };
        for (int i = 0; i < cards.Length; i++) pick.Cards[i + 1] = cards[i];
        me.Picks[pick.OperateId] = pick;
        await SyncAdventureAsync(match, session, me);
    }
}
