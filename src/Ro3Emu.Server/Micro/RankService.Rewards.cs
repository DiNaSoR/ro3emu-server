using System.Text.Json;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Micro;

/// <summary>
/// Rank reward settlement. A LeaderBoardsConfig row pays when it names gift rows (_iRankRewards) and a mail template
/// (_iMailId). Its settlement times are _kPrizesTimeNew entries as UI_Sys_RankPowerWidgetCtrl shows them under the
/// board: [1, hour] daily at that hour (UIWords 23716 "Daily Ranking Reward Settlement at ${1}"), [2, weekday, hour]
/// weekly (23714, enum_WeeklyUIWords 1-7 = Monday-Sunday); other kinds ([3, activity], [4, 0]) have no time here.
/// A gift row covers the ranks _kParaNum [min, max] and pays _kReward, a MultiverseGroupConfig id resolved to its fixed
/// AwardConfig grants (Lua_CFG_LeaderBoardsGiftConfig GetRange / GetGiftReward). The letter's ${1} is the rank.
/// <para>
/// (inferred) At the first check after a boundary the board is rebuilt from committed state and its winners for that
/// period are saved (server_state "rank_rewards") before any letter goes out; each letter is delivered once per board,
/// period and role. A period whose letters have not all gone out (a failed letter, a restart) is sent again every
/// minute until they have, and is finished before the board's next period replaces its winners, so a restart never
/// ranks a period again. A letter that cannot be sent at all (the role is gone, the gift no longer resolves to fixed
/// grants) is owed nothing until the next start, when the saved period is tried once more.
/// </para>
/// <para>
/// A period is paid only when the server saw it close: it directly follows the saved one and the previous check of
/// this run came before it. On the first start (nothing saved) or after the server was down across a boundary, the
/// latest is only recorded, with no winners: such boundaries are never paid, so none can be paid twice (Steven on
/// PR #225; rank rewards have no claim request, the letter is the only payout).
/// </para>
/// </summary>
public sealed partial class RankService : IGameTicker
{
    internal const string RewardStateKey = "rank_rewards";
    private const int DailyPrize = 1, WeeklyPrize = 2;
    private static readonly TimeSpan RewardCheckEvery = TimeSpan.FromSeconds(1);

    /// <summary>How long a failed settlement waits before it is tried again (tests shorten it).</summary>
    internal TimeSpan RewardRetryEvery { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Test hook: runs before a paid period is ranked (a throw fails that attempt).</summary>
    internal Func<int, Task>? BeforeRankForTest { get; set; }

    private TimeSpan _lastRewardCheck, _rewardRetryAt;
    private long? _previousCheckUnix;
    private bool _rewardChecked, _settling;
    private RewardState? _rewardState;

    /// <summary>Boards whose saved period may still owe letters: every saved board after a start, until delivered.</summary>
    private readonly HashSet<int> _unfinished = [];

    /// <summary>A boundary not yet saved and whether it pays: decided at its first check, kept for its retries.</summary>
    private readonly Dictionary<int, (long Period, bool Pay)> _pending = [];

    /// <summary>The last settled period of each paying board and its winners (rank to role).</summary>
    public sealed class RewardState
    {
        public Dictionary<int, BoardSettlement> Boards { get; set; } = new();
    }

    public sealed class BoardSettlement
    {
        /// <summary>The settlement boundary, Unix seconds.</summary>
        public long Period { get; set; }

        public Dictionary<int, long> Winners { get; set; } = new();
    }

    /// <summary>A board's latest boundary, and whether its winners are paid (false: only recorded).</summary>
    private readonly record struct DueSettlement(PayingBoard Board, long Period, bool Pay);

    private sealed record PayingBoard(int Id, int MailId, IReadOnlyList<(int Min, int Max, long Reward)> Gifts);

    public void Tick(GameTime time)
    {
        if (_settling || _rewardChecked && time.Now - _lastRewardCheck < RewardCheckEvery) return;
        _rewardChecked = true;
        _lastRewardCheck = time.Now;
        var state = RewardStateNow();
        bool retry = time.Now >= _rewardRetryAt;
        var now = clock.GetLocalNow();
        long? previousCheck = _previousCheckUnix;
        _previousCheckUnix = now.ToUnixTimeSeconds();
        var due = new List<DueSettlement>();
        foreach (var board in PayingBoards())
        {
            if (LatestBoundary(board.Id, now) is not { } period) continue;
            long settled = state.Boards.GetValueOrDefault(board.Id)?.Period ?? 0;
            if (period > settled)
            {
                if (_pending.TryGetValue(board.Id, out var decided) && decided.Period == period)
                {
                    if (retry) due.Add(new DueSettlement(board, period, decided.Pay));
                    continue;
                }
                bool pay = settled > 0 && previousCheck < period && PreviousBoundary(board.Id, period) == settled;
                _pending[board.Id] = (period, pay);
                due.Add(new DueSettlement(board, period, pay));
            }
            else if (retry && period == settled && _unfinished.Contains(board.Id)) due.Add(new DueSettlement(board, period, true));
        }
        if (due.Count == 0) return;
        _settling = true;
        _rewardRetryAt = time.Now + RewardRetryEvery;
        loop.PostLegacy(async () =>
        {
            try
            {
                foreach (var settlement in due)
                {
                    try { await SettleAsync(settlement); }
                    catch (Exception e) { LogSettlementFailed(log, e, settlement.Board.Id, settlement.Period); }
                }
            }
            finally { _settling = false; }
        }, "rank reward settlement");
    }

    private async Task SettleAsync(DueSettlement due)
    {
        var (board, period, pay) = due;
        var saved = RewardStateNow().Boards.GetValueOrDefault(board.Id);
        if (saved != null && saved.Period != period)
        {
            // An interrupted period is finished before the new one replaces its winners.
            if (_unfinished.Contains(board.Id) && !await DeliverAsync(board, saved)) return;
            saved = null;
        }
        await DeliverAsync(board, saved ?? await RankAsync(board, period, pay));
    }

    /// <summary>
    /// Ranks the board from committed state and saves the period's winners before any letter goes out; a period that is
    /// not paid is saved with no winners.
    /// </summary>
    private async Task<BoardSettlement> RankAsync(PayingBoard board, long period, bool pay)
    {
        var saved = new BoardSettlement { Period = period };
        List<Entry> rows = [];
        if (pay)
        {
            if (BeforeRankForTest is { } hook) await hook(board.Id);
            Invalidate();
            rows = (await BoardsAsync()).Boards.GetValueOrDefault(board.Id)?.Rows ?? [];
        }
        foreach (var (min, max, _) in board.Gifts)
            for (int rank = Math.Max(1, min); rank <= Math.Min(max, rows.Count); rank++) saved.Winners[rank] = rows[rank - 1].Id;
        var next = new RewardState { Boards = new(RewardStateNow().Boards) { [board.Id] = saved } };
        await accounts.WriteLegacyAsync<int>(new SeasonStateWritePlan(RewardStateKey, JsonSerializer.Serialize(next)));
        _rewardState = next;
        _pending.Remove(board.Id);
        _unfinished.Add(board.Id);
        LogSettled(log, board.Id, period, saved.Winners.Count);
        return saved;
    }

    /// <summary>Sends the saved winners' letters (each once only); true when none failed.</summary>
    private async Task<bool> DeliverAsync(PayingBoard board, BoardSettlement saved)
    {
        bool delivered = true;
        foreach (var (rank, role) in saved.Winners.OrderBy(w => w.Key))
        {
            try
            {
                // A rank the gift rows no longer cover (changed config) is owed nothing.
                var gift = board.Gifts.FirstOrDefault(g => rank >= g.Min && rank <= g.Max);
                if (gift.Reward <= 0) continue;
                await mail.SendTemplateOnceAsync(role, board.MailId, mail.FixedGroupAward(gift.Reward),
                    $"rank:{board.Id}:{saved.Period}:{role}", _ => { }, loop.Owners?.Current, contentParams: [rank]);
            }
            catch (Exception e) when (e is ArgumentException or AwardResolutionException)
            {
                // (inferred) Retrying cannot help: the role or the configuration would have to change first.
                LogLetterRefused(log, e, board.Id, rank, role);
            }
            catch (Exception e)
            {
                delivered = false;
                LogLetterFailed(log, e, board.Id, rank, role);
            }
        }
        if (delivered) _unfinished.Remove(board.Id);
        return delivered;
    }

    private RewardState RewardStateNow()
    {
        if (_rewardState != null) return _rewardState;
        _rewardState = accounts.ReadSync<string?>(new SeasonStateReadPlan(RewardStateKey)) is { Length: > 0 } saved
            ? JsonSerializer.Deserialize<RewardState>(saved) ?? new RewardState() : new RewardState();
        // After a start the saved periods are delivered again (once-only letters) in case they were interrupted.
        _unfinished.UnionWith(_rewardState.Boards.Keys);
        return _rewardState;
    }

    /// <summary>Role boards with gift rows and a letter (equipment rows are items, guild rows guilds: neither pays).</summary>
    private IEnumerable<PayingBoard> PayingBoards()
    {
        foreach (var row in data.Table("LeaderBoardsConfig").Values)
        {
            int id = (int)GameData.Int(row, "_iID"), mailId = (int)GameData.Int(row, "_iMailId");
            if (mailId == 0 || id is Equipment or GuildPrestige or GuildActivity || data.Row("MailConfig", mailId) == null) continue;
            var gifts = new List<(int, int, long)>();
            foreach (long giftId in Numbers(row, "_iRankRewards"))
                if (data.Row("LeaderBoardsGiftConfig", giftId) is { } gift && Numbers(gift, "_kParaNum") is [var min, var max, ..]
                    && GameData.Int(gift, "_kReward") is var reward and > 0)
                    gifts.Add(((int)min, (int)max, reward));
            if (gifts.Count > 0) yield return new PayingBoard(id, mailId, gifts);
        }
    }

    /// <summary>The boundary before <paramref name="period"/>, Unix seconds, or null without one.</summary>
    private long? PreviousBoundary(int boardId, long period) =>
        LatestBoundary(boardId, TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(period - 1), clock.LocalTimeZone));

    /// <summary>The latest settlement boundary at or before <paramref name="now"/> (server local time), or null.</summary>
    private long? LatestBoundary(int boardId, DateTimeOffset now)
    {
        if (data.Row("LeaderBoardsConfig", boardId) is not { } row || !row.TryGetProperty("_kPrizesTimeNew", out var times)
            || times.ValueKind != JsonValueKind.Array) return null;
        long? latest = null;
        foreach (var time in times.EnumerateArray())
        {
            long[] t = time.ValueKind == JsonValueKind.Array ? time.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray() : [];
            DateTime? boundary = t switch
            {
                [DailyPrize, var hour, ..] when hour is >= 0 and < 24 => Daily(now.DateTime, (int)hour),
                [WeeklyPrize, var weekday, var hour, ..] when weekday is >= 1 and <= 7 && hour is >= 0 and < 24
                    => Weekly(now.DateTime, (int)weekday, (int)hour),
                _ => null,
            };
            if (boundary is not { } local) continue;
            long seconds = new DateTimeOffset(local, clock.LocalTimeZone.GetUtcOffset(local)).ToUnixTimeSeconds();
            if (latest == null || seconds > latest) latest = seconds;
        }
        return latest;
    }

    private static DateTime Daily(DateTime now, int hour) =>
        now.TimeOfDay >= TimeSpan.FromHours(hour) ? now.Date.AddHours(hour) : now.Date.AddDays(-1).AddHours(hour);

    private static DateTime Weekly(DateTime now, int weekday, int hour)
    {
        int today = ((int)now.DayOfWeek + 6) % 7 + 1; // Monday 1 .. Sunday 7
        var boundary = now.Date.AddDays(weekday - today).AddHours(hour);
        return boundary > now ? boundary.AddDays(-7) : boundary;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Rank reward settlement of board {Board} period {Period} failed")]
    private static partial void LogSettlementFailed(ILogger logger, Exception error, int board, long period);

    [LoggerMessage(Level = LogLevel.Information, Message = "Rank rewards: board {Board} period {Period} pays {Count} roles")]
    private static partial void LogSettled(ILogger logger, int board, long period, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Rank reward letter for board {Board} rank {Rank} to {Role} failed")]
    private static partial void LogLetterFailed(ILogger logger, Exception error, int board, int rank, long role);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rank reward letter for board {Board} rank {Rank} to {Role} cannot be sent")]
    private static partial void LogLetterRefused(ILogger logger, Exception error, int board, int rank, long role);

    private static long[] Numbers(JsonElement row, string field) =>
        row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray() : [];
}
