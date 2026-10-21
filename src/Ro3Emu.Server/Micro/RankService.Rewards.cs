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
/// period and role, so a restart finishes an interrupted settlement for the saved winners instead of ranking again.
/// Boundaries missed while the server was down settle once, with the board as it is at the next check.
/// </para>
/// </summary>
public sealed partial class RankService : IGameTicker
{
    internal const string RewardStateKey = "rank_rewards";
    private const int DailyPrize = 1, WeeklyPrize = 2;
    private static readonly TimeSpan RewardCheckEvery = TimeSpan.FromSeconds(1);

    private TimeSpan lastRewardCheck;
    private bool rewardChecked, rewardResumed, settling;
    private RewardState? rewardState;

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

    private sealed record PayingBoard(int Id, int MailId, IReadOnlyList<(int Min, int Max, long Reward)> Gifts);

    public void Tick(GameTime time)
    {
        if (settling || rewardChecked && time.Now - lastRewardCheck < RewardCheckEvery) return;
        rewardChecked = true;
        lastRewardCheck = time.Now;
        var state = RewardStateNow();
        var due = new List<(PayingBoard Board, long Period)>();
        foreach (var board in PayingBoards())
        {
            if (LatestBoundary(board.Id) is not { } period) continue;
            long settled = state.Boards.GetValueOrDefault(board.Id)?.Period ?? 0;
            // After a restart the saved period is delivered again (once-only letters) in case it was interrupted.
            if (period > settled || !rewardResumed && period == settled) due.Add((board, period));
        }
        rewardResumed = true;
        if (due.Count == 0) return;
        settling = true;
        loop.PostLegacy(async () =>
        {
            try
            {
                foreach (var (board, period) in due) await SettleAsync(board, period);
            }
            catch (Exception e) { LogSettlementFailed(log, e); }
            finally { settling = false; }
        }, "rank reward settlement");
    }

    private async Task SettleAsync(PayingBoard board, long period)
    {
        var state = RewardStateNow();
        if (state.Boards.GetValueOrDefault(board.Id) is not { } saved || saved.Period != period)
        {
            Invalidate();
            var rows = (await BoardsAsync()).Boards.GetValueOrDefault(board.Id)?.Rows ?? [];
            saved = new BoardSettlement { Period = period };
            foreach (var (min, max, _) in board.Gifts)
                for (int rank = Math.Max(1, min); rank <= Math.Min(max, rows.Count); rank++) saved.Winners[rank] = rows[rank - 1].Id;
            var next = new RewardState { Boards = new(state.Boards) { [board.Id] = saved } };
            await accounts.WriteLegacyAsync<int>(new SeasonStateWritePlan(RewardStateKey, JsonSerializer.Serialize(next)));
            rewardState = next;
            LogSettled(log, board.Id, period, saved.Winners.Count);
        }
        foreach (var (rank, role) in saved.Winners.OrderBy(w => w.Key))
        {
            var gift = board.Gifts.First(g => rank >= g.Min && rank <= g.Max);
            try
            {
                await mail.SendTemplateOnceAsync(role, board.MailId, mail.FixedGroupAward(gift.Reward),
                    $"rank:{board.Id}:{period}:{role}", _ => { }, loop.Owners?.Current, contentParams: [rank]);
            }
            catch (Exception e) { LogLetterFailed(log, e, board.Id, rank, role); }
        }
    }

    private RewardState RewardStateNow() => rewardState ??=
        accounts.ReadSync<string?>(new SeasonStateReadPlan(RewardStateKey)) is { Length: > 0 } saved
            ? JsonSerializer.Deserialize<RewardState>(saved) ?? new RewardState() : new RewardState();

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

    /// <summary>The latest settlement boundary at or before now in the server's local time, or null without one.</summary>
    private long? LatestBoundary(int boardId)
    {
        if (data.Row("LeaderBoardsConfig", boardId) is not { } row || !row.TryGetProperty("_kPrizesTimeNew", out var times)
            || times.ValueKind != JsonValueKind.Array) return null;
        var now = clock.GetLocalNow();
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Rank reward settlement failed")]
    private static partial void LogSettlementFailed(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Rank rewards: board {Board} period {Period} pays {Count} roles")]
    private static partial void LogSettled(ILogger logger, int board, long period, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Rank reward letter for board {Board} rank {Rank} to {Role} failed")]
    private static partial void LogLetterFailed(ILogger logger, Exception error, int board, int rank, long role);

    private static long[] Numbers(JsonElement row, string field) =>
        row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray() : [];
}
