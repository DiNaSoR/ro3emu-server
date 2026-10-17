using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Award boxes: 31323 on a usable ItemConfig row with _iAward (a MultiverseGroupConfig route) and no use effect
/// (LC_ItemHelper._doUseItem: GetAwardIdList not empty -> _useItemForBox). 301/302 boxes send no parameter, a box with
/// _kAwardperformance sends isFirstHeartBox, a 303 SelfChooseGift box selfChooseGift.chooseGift {option from 1: units}
/// whose units add up to the count (UI_Com_RewardSelectWidgetCtrl.BuildUseParam, _onClickConfirm). The options are the
/// award's _kAward tuples in order (Lua_CFG_AwardConfig.GetAwardList). A plain box rolls its award once per unit.
/// The boxes go and the goods come in one family commit with the use's cooldown and use-time count; 31322 shows the
/// goods as _kAwardperformance[1] (5 ManifestTypeNewer: CongratulationObtain2NPanel_BoxBase) or else as the common
/// reward popup (inferred). Also owns the 05:00 rollover of 5009 useTimes for online roles.
/// </summary>
public sealed class ItemBoxService : IGameTicker
{
    internal const string BoxKind = "cd13a/character-box";
    private const int ManifestCommon = 2;
    private const int ErrUseParam = 155; // ErrorCodeConfig 155 "Invalid item use parameters"

    private readonly CharacterService character;
    private readonly AwardService awards;
    private readonly SeasonService seasons;
    private readonly SessionRegistry sessions;
    private readonly GameLoop loop;
    private readonly TimeProvider clock;
    private readonly ILogger<ItemBoxService> log;
    private TimeSpan lastTick;
    private DateTime gameDay;

    public ItemBoxService(CharacterService character, AwardService awards, SeasonService seasons, SessionRegistry sessions,
        GameLoop loop, TimeProvider clock, ILogger<ItemBoxService> log)
    {
        this.character = character; this.awards = awards; this.seasons = seasons; this.sessions = sessions;
        this.loop = loop; this.clock = clock; this.log = log;
        character.UseClock = clock;
        character.SpecialItemUse.Add(TryOpenAsync);
    }

    /// <summary>At the 05:00 game day the online roles with use-time counts get 5009 again (ended periods drop out).</summary>
    public void Tick(GameTime time)
    {
        if (time.Now - lastTick < TimeSpan.FromSeconds(1)) return;
        lastTick = time.Now;
        var day = DailyActivityService.GameDay(clock.GetLocalNow().DateTime);
        if (day == gameDay) return;
        bool first = gameDay == default;
        gameDay = day;
        if (first) return;
        foreach (var session in sessions.All())
            if (session.Role != null && character.HasUseTimes(session))
                loop.PostOwned(session, () => session.Role != null ? character.SyncUseTimesAsync(session) : Task.CompletedTask);
    }

    /// <summary>Server open day as LC_InteractCondition's iOpenDayCount: day 1 is the season start.</summary>
    private int OpenDay() => Math.Max(1, (int)Math.Floor((clock.GetUtcNow().ToUnixTimeSeconds() - seasons.StartTime) / 86400d) + 1);

    private async Task<int?> TryOpenAsync(GameSession s, ItemUseReq req, InvItem item, JsonElement row, int count)
    {
        if (!character.IsAwardBox(row)) return null;
        var param = req.ItemParam.Count == 1 ? req.ItemParam[0] : null;
        bool choice = character.IsChoiceBox(row);
        if (req.ItemParam.Count > 1 || (choice ? param?.VarCase != ItemUseParamInfo.VarOneofCase.SelfChooseGift
                : param != null && param.VarCase != ItemUseParamInfo.VarOneofCase.IsFirstHeartBox))
            return ErrUseParam;
        long group = GameData.Int(row, "_iAward");
        var goods = new List<(int Entry, int Count, int Bind)>();
        try
        {
            if (choice)
            {
                var options = awards.ResolveItemAward(s, group, seasons.PlayBook, OpenDay()).Grants;
                var picks = param!.SelfChooseGift.ChooseGift;
                if (picks.Count == 0 || picks.Any(p => p.Key < 1 || p.Key > options.Count || p.Value <= 0)
                    || picks.Values.Sum(v => (long)v) != count) return ErrUseParam;
                foreach (var (index, units) in picks.OrderBy(p => p.Key))
                {
                    var option = options[index - 1];
                    goods.Add((option.Entry, checked(option.Count * units), option.BindType));
                }
            }
            else
                for (int n = 0; n < count; n++)
                    foreach (var grant in awards.ResolveItemAward(s, group, seasons.PlayBook, OpenDay()).Grants)
                        goods.Add((grant.Entry, grant.Count, grant.BindType));
        }
        catch (AwardConditionException) { return CharacterService.ErrLevelNeed; }
        catch (AwardResolutionException error)
        {
            log.LogWarning("Box {Item} award {Group} does not resolve: {Reason}", item.Entry, group, error.Message);
            return ErrorCodes.ItemNoConfig;
        }
        goods = goods.GroupBy(g => (g.Entry, g.Bind)).Select(g => (g.Key.Entry, checked(g.Sum(x => x.Count)), g.Key.Bind)).ToList();
        // No box in the data grants EXP or an expiring item; the bag exchange below carries neither.
        if (goods.Count == 0 || goods.Any(g => g.Entry is 100 or 101 || g.Bind == 2)) return ErrorCodes.FeatureUnavailable;
        var plain = goods.Where(g => g.Bind == 0).Select(g => (g.Entry, g.Count)).ToList();
        var bound = goods.Where(g => g.Bind != 0).ToList();
        if (character.PlainGrantError(s, plain, boundGoods: bound) is var full and not 0) return full;
        var granted = await character.ExchangeLockedAsync(s, [], plain, new Dictionary<long, int> { [item.Guid] = count },
            popup: false, sync: false, boundGoods: bound,
            persist: tx => tx.WithEconomy(character.FamilyEconomy(s, BoxKind, [(item.Entry, (long)count)],
                goods.Select(g => (g.Entry, g.Count)).ToList(),
                new { Item = item.Entry, ItemGuid = item.Guid, Count = count, Award = group, Goods = goods })),
            asyncCommit: true);
        if (granted == null) return ErrorCodes.ItemsShort;
        var performance = row.TryGetProperty("_kAwardperformance", out var shown) && shown.ValueKind == JsonValueKind.Array
            && shown.GetArrayLength() > 0 && GameData.Number(shown[0]) is { } manifest and > 0 ? (int)manifest : ManifestCommon;
        await s.SendGameAsync(CharacterService.GetRewardNotify, character.BoxRewardNotify(s, goods, granted, performance));
        return 0;
    }
}
