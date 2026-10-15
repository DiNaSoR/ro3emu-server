using System.Text.Json;
using System.Security.Cryptography;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

/// <summary>
/// To-Do rows, Dodo's ring and type-7 liveness boxes. Quest progress and its item-5 awards
/// remain owned by TaskService. This service mirrors accepted deliveries in 5004; it never pays _iPerTaskPoint.
/// Reset code 5 = server-local daily 05:00, 105 = Monday 05:00 (inferred, aligned with MVP reset).
/// Inferred integration: an accepted tracking quest delivery advances its row and reissues that quest until
/// _iCount; a box checks item-5 as a non-spending threshold. No global daily item-5 cap is inferred.
/// Login reconciles epochs and quests before exposing 5004/5059 and then functions 25/5902; every settlement
/// reconciles before applying a new event, so an event at the reset boundary belongs to the new epoch.
/// </summary>
public sealed partial class DailyActivityService : IGameTicker
{
    public const uint ActivitySync = 5004, StageSync = 5059, RingAccept = 43504, StageGain = 31178;
    private const int Liveness = 5, RingActivity = 3302, TodoFunction = 25, RingFunction = 5902;
    private const int RingRuns = 1; // PublicParamConfig.RunRingTaskRefreshCount (inferred as one accept per reset).
    private static readonly int[] RingStarts = [3001, 3011, 3021];
    private readonly GameData data;
    private readonly TimeProvider clock;
    private readonly CharacterService character;
    private readonly FunctionService functions;
    private readonly SeasonService seasons;
    private readonly TaskService tasks;
    private readonly RoleStore roles;
    private readonly RewardService rewards;
    private readonly SessionRegistry sessions;
    private readonly GameLoop loop;
    private readonly TimedTodoSchedule timedSchedule;
    private readonly ILogger<DailyActivityService> log;
    private Dictionary<int, ActivityDef>? definitions;
    private Dictionary<long, List<int>>? byTask;

    /// <summary>Elapsed-time owners settle the ending period under TaskLock before daily quests/currency reset.</summary>
    public Func<GameSession, long, Task<bool>>? BeforeReset { get; set; }

    public sealed class DailyState
    {
        public byte[] Rows { get; set; } = [];
        public HashSet<int> Claims { get; set; } = new(); // shared 5059 list, not only type 7
        public long DailyEpoch { get; set; }
        public long WeeklyEpoch { get; set; }
        public int ActiveRingStart { get; set; }
    }

    private sealed record ActivityDef(int Id, int Function, int Count, int Reset, int[] Books, long[] Tasks);
    public sealed record DeliveryPlan(byte[] Before, byte[] After, bool Repeat, bool MirrorChanged);

    public DailyActivityService(GameData data, CharacterService character, FunctionService functions, SeasonService seasons,
        TaskService tasks, RoleStore roles, RewardService rewards, SessionRegistry sessions, GameLoop loop,
        TimedTodoSchedule timedSchedule, TimeProvider clock, ILogger<DailyActivityService> log)
    {
        this.data = data;
        this.character = character;
        this.functions = functions;
        this.seasons = seasons;
        this.tasks = tasks;
        this.roles = roles;
        this.rewards = rewards;
        this.sessions = sessions;
        this.loop = loop;
        this.timedSchedule = timedSchedule;
        this.clock = clock;
        this.log = log;
        tasks.BeforeProgress = s => ReconcileCoreAsync(s);
        tasks.RefreshDailyTracking = s => tasks.EnsureDailyTrackingCoreAsync(s, TrackingTasks(s));
        tasks.PrepareDailyDelivery = PrepareDelivery;
        tasks.SyncDailyDelivery = SyncDeliveryAsync;
    }

    /// <summary>Restores the new day's AFK allowance into the reset transaction (WildAfkService, same role scope).</summary>
    internal Func<GameSession, (byte[] Bytes, Action Committed, Action Rollback)?>? PlanAfkRollover { get; set; }

    /// <summary>Before TaskService.Login: reconcile persisted epochs and quest rows, then 5009 will carry reset currency.</summary>
    public void Load(GameSession session, DateTime? localNow = null)
    {
        var saved = roles.LoadDaily(session.Role!.Id);
        var state = saved == null ? new DailyState() : JsonSerializer.Deserialize<DailyState>(saved) ?? new DailyState();
        var taskBytes = roles.LoadTasks(session.Role.Id);
        var taskInfo = taskBytes != null ? DBTaskInfo.Parser.ParseFrom(taskBytes) : new DBTaskInfo();
        // Selection owns the realm before the first packet; this branch never awaits a writer admission.
        RolloverAsync(session, state, taskInfo, localNow ?? clock.GetLocalNow().DateTime,
            synchronous: true).GetAwaiter().GetResult();
    }

    /// <summary>
    /// The one daily/weekly period command (C/D 09): resets rows, quests, liveness and the AFK allowance together in
    /// one transaction, from the given copies. The session is changed only after the commit; a failed commit leaves
    /// the old period in place (and retryable). Login passes the stored state; the running reset passes clones of
    /// the session's own state, so nothing is re-read from SQL. Returns the committed quests when they were reset.
    /// </summary>
    private async Task<DBTaskInfo?> RolloverAsync(GameSession session, DailyState state, DBTaskInfo taskInfo,
        DateTime now, bool synchronous = false)
    {
        long previousDailyEpoch = state.DailyEpoch, previousWeeklyEpoch = state.WeeklyEpoch;
        state.Claims ??= new();
        var rows = state.Rows.Length > 0 ? DBDailyActivityInfo.Parser.ParseFrom(state.Rows) : new DBDailyActivityInfo();
        long daily = Boundary(now), weekly = WeekBoundary(now);
        bool resetDaily = state.DailyEpoch != 0 && state.DailyEpoch < daily;
        bool resetWeekly = state.WeeklyEpoch != 0 && state.WeeklyEpoch < weekly;
        // A completed AFK objective whose award was held must settle in its own period, also after reconnect.
        if (resetDaily && WildAfkService.PendingDailyAward(session.WildAfk, state, taskInfo, rows))
        {
            session.Daily = state;
            session.DailyRows = rows;
            return null;
        }
        if (resetDaily || resetWeekly)
        {
            foreach (var def in Defs().Values.Where(d => d.Reset == 5 && resetDaily || d.Reset == 105 && resetWeekly))
            {
                if (Available(session, def)) rows.ActivityData[def.Id] = NewRow(def.Id, daily);
                else rows.ActivityData.Remove(def.Id);
                foreach (long id in def.Tasks) { taskInfo.Infos.Remove(id); taskInfo.CompleteTaskNum.Remove(id); }
            }
            if (resetDaily)
            {
                foreach (var id in RingStarts.SelectMany(start => Enumerable.Range(start, 10)))
                { taskInfo.Infos.Remove(id); taskInfo.CompleteTaskNum.Remove(id); }
                state.Claims.RemoveWhere(IsDailyStage);
                state.ActiveRingStart = 0;
            }
        }
        state.DailyEpoch = daily;
        state.WeeklyEpoch = weekly;
        // A saved row from another playbook is not enabled in this one.
        foreach (var def in Defs().Values.Where(d => !Available(session, d))) rows.ActivityData.Remove(def.Id);
        foreach (var def in Defs().Values.Where(d => Available(session, d)))
            if (!rows.ActivityData.ContainsKey(def.Id)) rows.ActivityData[def.Id] = NewRow(def.Id, daily);
        ProjectWindows(rows, ToUnix(now));
        UpdateRingCount(rows, state);
        state.Rows = rows.ToByteArray();
        if (!resetDaily && !resetWeekly)
        {
            session.Daily = state;
            session.DailyRows = rows;
            Save(session);
            return null;
        }
        var currency = new Dictionary<int, long>(session.Progress!.Currency);
        long livenessSink = resetDaily ? currency.GetValueOrDefault(Liveness) : 0;
        if (resetDaily) currency[Liveness] = 0;
        var afk = resetDaily && PlanAfkRollover is { } plan ? plan(session) : null;
        byte[] dailyBytes = JsonSerializer.SerializeToUtf8Bytes(state), taskBytes = taskInfo.ToByteArray();
        byte[] outcome = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1, PreviousDailyEpoch = previousDailyEpoch, DailyEpoch = daily,
            PreviousWeeklyEpoch = previousWeeklyEpoch, WeeklyEpoch = weekly,
            DailyHash = Convert.ToHexString(SHA256.HashData(dailyBytes)),
            TasksHash = Convert.ToHexString(SHA256.HashData(taskBytes)),
            AfkHash = afk is { } prepared ? Convert.ToHexString(SHA256.HashData(prepared.Bytes)) : null
        });
        var economy = new EconomyMutation(Guid.NewGuid().ToString("N"), session.Account!, session.Role!.Id,
            "cd13e/daily-reset", Convert.ToHexString(SHA256.HashData(outcome)),
            $"daily-reset/{session.Role.Id}/{previousDailyEpoch}/{daily}/{previousWeeklyEpoch}/{weekly}",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            livenessSink > 0 ? [new EconomyQuantity(new(EconomyAssetKind.Currency, Liveness), livenessSink)] : [],
            [], outcome);
        string currencyBytes = string.Join(',', currency.Select(c => $"{c.Key}:{c.Value}"));
        try
        {
            if (synchronous)
                roles.SaveDailyReset(session.Role.Id, dailyBytes, taskBytes, currencyBytes, session.Progress,
                    economy, afk?.Bytes);
            else
                await roles.SaveDailyResetAsync(session.Role.Id, dailyBytes, taskBytes, currencyBytes,
                    session.Progress, economy, afk?.Bytes);
        }
        catch
        {
            afk?.Rollback();
            throw;
        }
        afk?.Committed();
        if (resetDaily) session.Progress.Currency[Liveness] = 0;
        session.Daily = state;
        session.DailyRows = rows;
        return taskInfo;
    }

    /// <summary>After 5027, before the final 5056: initialize eligible tracking quests and sync nonempty 5004/5059.</summary>
    public async Task LoginAsync(GameSession session)
    {
        using var gate = await TaskService.LockTasksAsync(session);
        await ReconcileCoreAsync(session); // an old AFK award may have held the load-time reset until quest login
        await tasks.EnsureDailyTrackingCoreAsync(session, TrackingTasks(session));
        await SyncRowsAsync(session);
        await SyncClaimsAsync(session);
        session.DailyReady = true;
    }

    public IEnumerable<long> TrackingTasks(GameSession session) => Defs().Values
        .Where(d => d.Id != RingActivity && d.Count > 0 && Available(session, d) && ParticipationOpen(d.Id) && functions.IsOpen(session, TodoFunction)
            && functions.IsOpen(session, d.Function)
            && Rows(session).ActivityData.TryGetValue(d.Id, out var row) && row.TaskCompleteTimes < d.Count)
        .SelectMany(d => d.Tasks).Distinct();

    /// <summary>Preview an accepted daily tracking quest or any node of the active Dodo chain for atomic settlement.</summary>
    private DeliveryPlan? PrepareDelivery(GameSession session, long taskId)
    {
        if (session.Daily == null || session.DailyRows == null) return null;
        int start = session.Daily.ActiveRingStart;
        bool ringNode = start > 0 && taskId >= start && taskId < start + 10;
        if (!ringNode && !ByTask().ContainsKey(taskId)) return null;
        var before = JsonSerializer.SerializeToUtf8Bytes(session.Daily);
        var next = JsonSerializer.Deserialize<DailyState>(before)!;
        var rows = DBDailyActivityInfo.Parser.ParseFrom(next.Rows);
        bool repeat = false, changed = false;
        foreach (int id in ByTask().GetValueOrDefault(taskId) ?? [])
        {
            var def = Defs()[id];
            if (!Available(session, def) || !ParticipationOpen(id) || !functions.IsOpen(session, TodoFunction)
                || !functions.IsOpen(session, def.Function) || def.Count <= 0) continue;
            if (id == RingActivity && (!ringNode || taskId != start + 9)) continue;
            if (!rows.ActivityData.TryGetValue(id, out var row) || row.TaskCompleteTimes >= def.Count) continue;
            row.TaskCompleteTimes++;
            row.Tasks[taskId] = row.Tasks.GetValueOrDefault(taskId) + 1;
            row.ActivityState = row.TaskCompleteTimes >= def.Count ? (EDailyState)2 : (EDailyState)1;
            if (id == RingActivity) next.ActiveRingStart = 0;
            repeat |= id != RingActivity && row.TaskCompleteTimes < def.Count;
            changed = true;
        }
        if (changed) next.Rows = rows.ToByteArray();
        return new DeliveryPlan(before, JsonSerializer.SerializeToUtf8Bytes(next), repeat, changed);
    }

    private Task SyncDeliveryAsync(GameSession session) =>
        SyncRowsAsync(session);

    /// <summary>43504 branch 110 only. The deterministic chain rotation and consume-on-accept are inferred.</summary>
    public async Task AcceptRingAsync(GameSession session, CommonDailyActivityReq req)
    {
        using var gate = await TaskService.LockTasksAsync(session);
        if (session.Role == null || session.Progress == null || session.Daily == null || !session.DailyReady)
        {
            await session.SendGameAsync(RingAccept, new CommonDailyActivityRes
            { Result = ErrorCodes.FeatureUnavailable, ActivityId = req.ActivityId, RequestId = req.RequestId });
            return;
        }
        await ReconcileCoreAsync(session);
        int result = req.ActivityId != RingActivity || req.RunRingTaskAccept == null ? ErrorCodes.Parameter
            : !functions.IsOpen(session, TodoFunction) || !functions.IsOpen(session, RingFunction) ? ErrorCodes.FeatureUnavailable
            : session.Daily is { ActiveRingStart: not 0 } ? ErrorCodes.TaskExists
            : session.Daily == null || Remaining(session.Daily) <= 0 ? ErrorCodes.AttemptsExhausted
            : 0;
        int start = 0;
        if (result == 0)
        {
            // Local-day index rotates over the three configured chain roots, independent of requestId (often zero).
            int localDay = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(session.Daily!.DailyEpoch).LocalDateTime).DayNumber;
            start = RingStarts[localDay % RingStarts.Length];
            var oldTasks = session.Tasks?.Clone();
            var oldDaily = JsonSerializer.SerializeToUtf8Bytes(session.Daily);
            result = tasks.AssignRing(session, start) ? 0 : ErrorCodes.TaskStatus;
            if (result == 0)
            {
                session.Daily.ActiveRingStart = start;
                UpdateRingCount(Rows(session), session.Daily);
                session.Daily.Rows = Rows(session).ToByteArray();
                var nextDaily = JsonSerializer.SerializeToUtf8Bytes(session.Daily);
                bool committed;
                try { committed = roles.CommitRingAccept(session.Role!.Id, oldDaily, nextDaily, session.Tasks!.ToByteArray()); }
                catch (Exception e)
                {
                    log.LogError(e, "Ring acceptance transaction failed for {Role}", session.Role?.Id);
                    committed = false;
                }
                if (!committed)
                {
                    session.Tasks = oldTasks;
                    session.Daily = JsonSerializer.Deserialize<DailyState>(oldDaily)!;
                    session.DailyRows = DBDailyActivityInfo.Parser.ParseFrom(session.Daily.Rows);
                    result = ErrorCodes.Unknown;
                }
                else
                {
                    await tasks.SyncCurrentAsync(session);
                    await SyncRowsAsync(session);
                }
            }
        }
        log.LogInformation("Ring accept {Name} root {Root}: {Result}", session.Role?.Name, start, result);
        await session.SendGameAsync(RingAccept, new CommonDailyActivityRes
        {
            Result = result, ActivityId = req.ActivityId, RequestId = req.RequestId,
            RunRingTaskAccept = new RunRingTaskAcceptRes { Placeholder = req.RunRingTaskAccept?.Placeholder ?? 0 },
        });
    }

    /// <summary>31178: the type-7 row, function 25 and item-5 threshold authorize one shared-list claim.</summary>
    public async Task ClaimAsync(GameSession session, StageRewardGainReq req)
    {
        using var gate = await TaskService.LockTasksAsync(session);
        if (session.Role == null || session.Progress == null || session.Daily == null || !session.DailyReady)
        {
            await session.SendGameAsync(StageGain, new StageRewardGainResp
            { Result = ErrorCodes.FeatureUnavailable, StageId = req.StageId });
            return;
        }
        await ReconcileCoreAsync(session);
        var row = data.Row("GenericStageRewardsConfig", req.StageId);
        int result = row is not { } r || GameData.Int(r, "_iType") != 7 || GameData.Int(r, "_iItem") != Liveness ? ErrorCodes.RewardMissing
            : !functions.IsOpen(session, TodoFunction) ? ErrorCodes.FeatureUnavailable
            : session.Daily!.Claims.Contains(req.StageId) ? ErrorCodes.RewardClaimed
            : character.CountOf(session, Liveness) < GameData.Int(r, "_iScore") ? ErrorCodes.CurrencyShort
            : 0;
        if (result == 0)
        {
            var grant = StageAward(session, (int)GameData.Int(row!.Value, "_iAward"));
            if (grant.Count == 0) result = ErrorCodes.RewardMissing;
            else
            {
                var before = JsonSerializer.SerializeToUtf8Bytes(session.Daily);
                var next = JsonSerializer.Deserialize<DailyState>(before)!;
                next.Claims.Add(req.StageId);
                var after = JsonSerializer.SerializeToUtf8Bytes(next);
                // The claim, currency/EXP and inventory are committed together by the character owner.
                result = await character.GrantDailyStageAsync(session, grant, before, after) ? 0 : ErrorCodes.Unknown;
                if (result == 0) await SyncClaimsAsync(session);
            }
        }
        await session.SendGameAsync(StageGain, new StageRewardGainResp { Result = result, StageId = req.StageId });
    }

    private List<(int Entry, int Count, int Bind)> StageAward(GameSession session, int group)
    {
        var award = new List<(int, int, int)>();
        if (data.Row("MultiverseGroupConfig", group) is not { } mv) return award;
        int book = seasons.PlayBook;
        long target = GameData.Int(mv, $"_iValue_{book}");
        if (target == 0) target = GameData.Int(mv, "_iValue_default");
        if (data.Row("AwardConfig", target) is not { ValueKind: JsonValueKind.Array } rows || rows.GetArrayLength() == 0) return award;
        var first = rows[0];
        if (first.TryGetProperty("_kAward", out var fixedAward) && fixedAward.ValueKind == JsonValueKind.Array)
            foreach (var item in fixedAward.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() >= 2)
                    award.Add(((int)(GameData.Number(item[0]) ?? 0), (int)(GameData.Number(item[1]) ?? 0), item.GetArrayLength() > 2 ? (int)(GameData.Number(item[2]) ?? 0) : 0));
        award.AddRange(rewards.RollAwardBound(target, session));
        return award.Where(a => a.Item1 > 0 && a.Item2 > 0 && data.Row("ItemConfig", a.Item1) != null).ToList();
    }

    public void Tick(GameTime time)
    {
        if (time.Now - lastTick < TimeSpan.FromSeconds(1)) return;
        lastTick = time.Now;
        // The reconciler's own no-work test, without its quest lock: only a crossed day/week boundary or a due
        // window refresh is posted (every session used to be posted every second: a 300-item queue burst).
        var now = clock.GetLocalNow().DateTime;
        long daily = Boundary(now), weekly = WeekBoundary(now), seconds = ToUnix(now);
        foreach (var session in sessions.All())
        {
            if (session.Daily is not { } state || !session.DailyReady) continue;
            if (state.DailyEpoch >= daily && state.WeeklyEpoch >= weekly
                && seconds < session.DailyWindowRefreshAt && seconds >= session.DailyWindowSampledAt) continue;
            loop.PostOwned(session, () => ReconcileAsync(session));
        }
    }

    private TimeSpan lastTick;

    public async Task ReconcileAsync(GameSession session, DateTime? localNow = null)
    {
        using var gate = await TaskService.LockTasksAsync(session);
        await ReconcileCoreAsync(session, localNow);
    }

    // The caller owns TaskLock; resetting or accepting a ring must not interleave with a staged quest award.
    private async Task ReconcileCoreAsync(GameSession session, DateTime? localNow = null)
    {
        if (session.Daily == null || session.Role == null) return;
        var now = localNow ?? clock.GetLocalNow().DateTime;
        if (session.Daily.DailyEpoch >= Boundary(now) && session.Daily.WeeklyEpoch >= WeekBoundary(now))
        {
            long seconds = ToUnix(now);
            if (session.DailyReady && (seconds >= session.DailyWindowRefreshAt || seconds < session.DailyWindowSampledAt))
                await SyncRowsAsync(session, now);
            return;
        }
        if (session.Daily.DailyEpoch < Boundary(now) && BeforeReset is { } settle)
            if (!await settle(session, Boundary(now)))
                throw new InvalidOperationException("AFK daily award is pending; retry settlement before resetting its period.");
        bool wasReady = session.DailyReady;
        session.DailyReady = false;
        try
        {
            // The running reset works on copies of this session's own state (no SQL re-read) and replaces it only
            // after its single commit. Without quest data the stored blob is carried through unchanged.
            var current = JsonSerializer.Deserialize<DailyState>(JsonSerializer.SerializeToUtf8Bytes(session.Daily))!;
            current.Rows = Rows(session).ToByteArray();
            var quests = session.Tasks?.Clone() ?? DBTaskInfo.Parser.ParseFrom(roles.LoadTasks(session.Role.Id) ?? []);
            var committed = await RolloverAsync(session, current, quests, now);
            if (session.Tasks != null)
            {
                if (committed != null) session.Tasks = committed;
                await tasks.EnsureDailyTrackingCoreAsync(session, TrackingTasks(session));
            }
            await character.ItemsChangedAsync(session, stats: false);
            await SyncRowsAsync(session, now);
            await SyncClaimsAsync(session);
            session.DailyReady = wasReady;
            if (wasReady) await functions.SyncAsync(session);
        }
        catch
        {
            session.DailyReady = wasReady;
            throw;
        }
    }

    private long ToUnix(DateTime local) => new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), clock.LocalTimeZone.GetUtcOffset(local)).ToUnixTimeSeconds();
    private long Boundary(DateTime now) => ToUnix((now.TimeOfDay >= TimeSpan.FromHours(5) ? now.Date : now.Date.AddDays(-1)).AddHours(5));
    private long WeekBoundary(DateTime now)
    {
        var day = now.TimeOfDay >= TimeSpan.FromHours(5) ? now.Date : now.Date.AddDays(-1);
        int sinceMonday = ((int)day.DayOfWeek + 6) % 7;
        return ToUnix(day.AddDays(-sinceMonday).AddHours(5));
    }

    private static int Remaining(DailyState state) => DBDailyActivityInfo.Parser.ParseFrom(state.Rows).ActivityData.TryGetValue(RingActivity, out var row)
        ? row.ActivityInfo?.ActivityParam?.RunRingTask?.RemainRingCount ?? 0 : 0;

    private static void UpdateRingCount(DBDailyActivityInfo rows, DailyState state)
    {
        if (!rows.ActivityData.TryGetValue(RingActivity, out var row)) return;
        row.ActivityInfo ??= new PlayerActivityInfo();
        row.ActivityInfo.ActivityParam ??= new PlayerActivityParam();
        // Remain count is sent even while the chain is active; accept consumes one allowance (inferred).
        row.ActivityInfo.ActivityParam.RunRingTask = new PlayerActivityRunRingTask
        { RemainRingCount = state.ActiveRingStart != 0 || row.TaskCompleteTimes > 0 ? 0 : RingRuns };
    }

    private static DBDailyActivityData NewRow(int id, long epoch) => new()
    { ActivityId = id, ActivityState = (EDailyState)1, LastRefreshTime = epoch };

    private static DBDailyActivityInfo Rows(GameSession session) => session.DailyRows!;

    private void Save(GameSession session)
    {
        if (session.Daily == null) return;
        session.Daily.Rows = Rows(session).ToByteArray();
        roles.SaveDaily(session.Role!.Id, JsonSerializer.SerializeToUtf8Bytes(session.Daily));
    }

    private async Task SyncClaimsAsync(GameSession session)
    {
        var info = new DBStageRewardInfo();
        info.AllStageRewardsFinished.Add(session.Daily!.Claims.Order());
        if (info.AllStageRewardsFinished.Count > 0) { await CharacterService.SyncAsync(session, StageSync, info); return; }
        // NW_GameServerHandler.lua enables no_decode_default_array; LC_PbHelper.lua:1979-2112 iterates all.value.
        // Lua-pb allocates {} only when the packed value tag is present. An omitted list or all:{} leaves the
        // client's StageRewardReceiveMap nil (native client runtime NOT RUN).
        long version = session.SyncVersions.GetValueOrDefault(StageSync) + 1;
        session.SyncVersions[StageSync] = version;
        session.Synced[StageSync] = info;
        var full = AutoSync.Full(info, version).ToByteArray();
        await session.SendGameRawAsync(StageSync, [.. full, 0x52, 0x04, 0x0A, 0x02, 0x0A, 0x00]);
    }

    /// <summary>Replay the claims snapshot after reconnect without reconciling epochs or enrolling tasks.</summary>
    public async Task ReplayClaimsAsync(GameSession session)
    {
        session.Synced.Remove(StageSync);
        await SyncClaimsAsync(session);
    }

    private bool IsDailyStage(int id) => data.Row("GenericStageRewardsConfig", id) is { } row && GameData.Int(row, "_iType") == 7;

    private bool Available(GameSession session, ActivityDef def) => def.Books.Length == 0 || def.Books.Contains(0) || def.Books.Contains(seasons.PlayBook);

    /// <summary>Selected Limited-Time task rows progress only in the client calendar's active window (inferred server gate).</summary>
    private bool ParticipationOpen(int id) => id is not (114 or 126 or 200 or 201 or 3003 or 3013)
        || timedSchedule.IsOpen(id);

    private Dictionary<int, ActivityDef> Defs()
    {
        if (definitions != null) return definitions;
        var defs = new Dictionary<int, ActivityDef>();
        foreach (var row in data.Table("DailyActivityConfig").Values)
        {
            int id = (int)GameData.Int(row, "_iID");
            if (id == 0) continue;
            var ids = new HashSet<long>(Numbers(row, "_kTaskProgress"));
            if (row.TryGetProperty("_iTaskReceiveAction", out var actions) && actions.ValueKind == JsonValueKind.Array)
                foreach (var entry in actions.EnumerateArray())
                {
                    var action = entry.ValueKind == JsonValueKind.Array ? entry.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray() : [];
                    if (action.Length >= 3 && action[0] == 31)
                        foreach (long task in action.Skip(2).Take((int)action[1])) ids.Add(task);
                }
            if (data.Row("LC_ConditionList", GameData.Int(row, "_iCheckCondition")) is { } cond
                && cond.TryGetProperty("m_kSubConditionList", out var subs) && subs.ValueKind == JsonValueKind.Array)
                foreach (var sub in subs.EnumerateArray())
                    if (Numbers(sub, "m_kData") is [4004, var task, ..]) ids.Add(task);
            defs[id] = new ActivityDef(id, (int)GameData.Int(row, "_iFunction"), (int)GameData.Int(row, "_iCount"),
                (int)GameData.Int(row, "_iResetTimeType"), Numbers(row, "_kMultiverseArray").Select(n => (int)n).ToArray(),
                ids.Where(task => data.Row("TaskConfig", task) != null).ToArray());
        }
        definitions = defs;
        return defs;
    }

    private Dictionary<long, List<int>> ByTask()
    {
        if (byTask != null) return byTask;
        var result = new Dictionary<long, List<int>>();
        foreach (var def in Defs().Values)
            foreach (long task in def.Tasks)
                (result.TryGetValue(task, out var ids) ? ids : result[task] = new()).Add(def.Id);
        return byTask = result;
    }

    private static long[] Numbers(JsonElement row, string field) => row.TryGetProperty(field, out var a) && a.ValueKind == JsonValueKind.Array
        ? a.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray() : [];
}
