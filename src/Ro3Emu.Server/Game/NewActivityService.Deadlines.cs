using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class NewActivityService
{
    private const string DeadlineKey = "new-activity";

    /// <summary>Keep one replaceable wake-up at the earliest calendar or saved-state boundary.</summary>
    private void ScheduleDeadline(GameSession session)
    {
        if (session.Role == null || session.NewActivities == null || session.ActivityRuntime == null)
        {
            deadlines.Cancel(session, DeadlineKey);
            return;
        }

        long now = Now;
        long next = long.MaxValue;
        bool hasLocalDay = false;
        bool needsRefresh = session.ActivityServerDay != (long)Math.Floor((now - activityClock.ServerOpen) / 86400d) + 1;
        int book = Playbook(session);
        void Consider(long seconds)
        {
            if (seconds > now && seconds < next) next = seconds;
        }

        foreach (var row in Definitions)
        {
            long id = GameData.Int(row, "_iID");
            long type = GameData.Int(row, "_iActivityType");
            if (!InBook(row, book) && !(TerritoryWarState != null && book == 101 && id == 2000 && type == 22))
                continue;
            var window = activityClock.ResolveActivity(row, now, session.Role.CreateTime, book);
            Consider(window.Start);
            Consider(window.End);
            if (Applies(session, row))
            {
                if (!session.NewActivities.ActivityInfo.TryGetValue(id, out var info)) needsRefresh = true;
                else
                {
                    bool active = window.Contains(now) && Gate(session, row);
                    if (info.CycleStartTime != window.Start || info.CycleEndTime != window.End
                        || info.State != (active ? window.State : "_close")) needsRefresh = true;
                    if (type == 8 && active && info.ActivityParam?.SevenDay is { } seven
                        && activityClock.Day(seven.LastLoginTime) < activityClock.Day(now)) needsRefresh = true;
                    if (type == 20 && info.ActivityParam?.PronteraDefense?.GlobalInfo is { } defenseState)
                    {
                        var phase = defense.ProjectionPhase(now);
                        if (defenseState.CurRound != phase.Round || defenseState.CurIntensity != phase.Intensity)
                            needsRefresh = true;
                    }
                }
            }
            if (type is 5 or 8 or 21 && window.End > now) hasLocalDay = true;
            long timeLimit = GameData.Int(row, "_iTimeLimitId");
            if (timeLimit > 0)
                foreach (var segment in activityClock.Segments(timeLimit, now, book))
                {
                    Consider(segment.Start);
                    Consider(segment.End);
                }
        }

        // The seven-day count is per civil login day, month cards and fragment counters also use that day.
        if (hasLocalDay) Consider(StartOfLocalDay(activityClock.Day(now) + 1));

        // ServerBasicInfo day follows realm-open elapsed 86400-second days, distinct from local midnight.
        long serverDay = (long)Math.Floor((now - activityClock.ServerOpen) / 86400d) + 1;
        Consider(activityClock.ServerOpen + serverDay * 86400);

        if (session.NewActivities.ActivityInfo.TryGetValue(117, out var eve)
            && eve.CycleStartTime > 0 && session.ActivityRuntime.EveSettledCycleStart != eve.CycleStartTime)
        {
            Consider(eve.CycleEndTime);
            if (eve.CycleEndTime <= now) needsRefresh = true;
        }

        foreach (var card in session.NewActivities.ActivityInfo.Values
            .Select(info => info.ActivityParam?.MonthCard).OfType<Romsg.DBMonthCardInfo>()
            .SelectMany(cards => cards.Infos.Values))
        {
            Consider(card.ExpireTs);
            if (card.ExpireTs <= now && (card.LastDays != 0 || !card.DayAward)
                && ActivityError(session, 199, 21, allowInitializing: true) == 0) needsRefresh = true;
        }

        var treasure = session.ActivityRuntime.AdventureTreasure;
        if (treasure.PointId != 0)
        {
            Consider(treasure.Began + 10800);
            if (treasure.Began + 10800 <= now) needsRefresh = true;
        }

        if (session.NewActivities.ActivityInfo.ContainsKey(118)) Consider(defense.NextProjectionBoundary(now));

        if (needsRefresh) next = now;
        if (next == long.MaxValue) deadlines.Cancel(session, DeadlineKey);
        else deadlines.Set(session, DeadlineKey, DateTimeOffset.FromUnixTimeSeconds(next), OnDeadlineAsync,
            callbackOwnsScope: true);
    }

    private void InvalidateAllSchedules()
    {
        if (!loop.IsOnLoop)
        {
            loop.PostRuntime(() => { InvalidateAllSchedules(); return Task.CompletedTask; });
            return;
        }
        var now = clock.GetUtcNow();
        foreach (var session in sessions.All())
            if (session.ActivitiesReady && session.Role != null)
                deadlines.Set(session, DeadlineKey, now, OnDeadlineAsync, callbackOwnsScope: true);
    }

    private async Task OnDeadlineAsync(GameSession session, DateTimeOffset _, DeadlineLease lease, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested || !deadlines.IsCurrent(session, lease)) return;
        try { await ReconcileAsync(session, lease); }
        catch (OperationCanceledException error) when (error.CancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            // ReconcilePass may commit and replace this service schedule before a later step fails.
            // Recovery follows the attachment, while the entry callback itself still needs the exact schedule.
            var attachment = lease with { ScheduleGeneration = 0, Key = null };
            if (deadlines.IsCurrent(session, attachment))
            {
                log.LogError(error, "Activity deadline failed for role {Role}; retrying", session.Role?.Id);
                deadlines.Set(session, DeadlineKey, clock.GetUtcNow().AddSeconds(1), OnDeadlineAsync,
                    callbackOwnsScope: true);
            }
        }
    }
}
