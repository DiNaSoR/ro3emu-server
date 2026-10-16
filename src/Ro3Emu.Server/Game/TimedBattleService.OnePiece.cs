using Romsg;
using Google.Protobuf;
using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Game;

/// <summary>OnePiece / Commendation Vault: 83350/83351 world window and a configured 3200x01 private treasure fight.</summary>
public sealed partial class TimedBattleService
{
    private const int TreasureMapEntry = 10110, TreasureNpc = 16100001, TreasureInteraction = 161000011;
    /// <summary>Below 2^53 (the client keeps unit ids in a double array, see DungeonService.SceneActorUidBase); apart from
    /// the per-role treasure hunt NPCs at 2^46 + role id.</summary>
    internal const long TreasureNpcUid = 1L << 46 | 1L << 45 | TreasureMapEntry;
    private sealed class Treasure
    {
        public required GameSession Player;
        public required InstanceService.Room Room;
        public required int Monster;
        public required long Started;
        public MonsterUnit? Boss;
        public bool Settled;
    }

    private readonly Dictionary<GameSession, Treasure> treasure = new();
    private long lastTreasureDay = -1;
    private bool lastTreasureOpen;

    private MapPoint? TreasurePoint()
    {
        var row = data.Row("OnePieceMapConfig", TreasureMapEntry);
        if (row is not { } r || !r.TryGetProperty("_kMonsterPoint", out var points)
            || points.ValueKind != System.Text.Json.JsonValueKind.Array || points.GetArrayLength() == 0) return null;
        long pointId = (long)(GameData.Number(points[0]) ?? 0);
        return worldData.Map(TreasureMapEntry)?.Points.TryGetValue(pointId, out var point) == true ? point : null;
    }

    private async Task SendTreasureNpcAsync(GameSession session)
    {
        if (session.Role == null || session.SceneLoading || session.SceneEntry != TreasureMapEntry || !schedule.IsOpen(200)
            || TreasurePoint() is not { } point || data.Row("NPCConfig", TreasureNpc) is not { } npc) return;
        var entity = new WorldNpc(TreasureNpcUid, TreasureMapEntry, WorldNpcKind.Npc, TreasureNpc,
            data.TextOf(npc, "_kName"), (int)GameData.Int(npc, "_iCamp"), point.X, point.Y, point.Z, point.Face, default);
        var view = new AoIViewChangeData(); view.Bases.Add(SceneService.NpcEntity(entity));
        await session.SendGameAsync(GameAoiSink.SyncAoiView, view);
    }

    private async Task RemoveTreasureNpcAsync(GameSession session)
    {
        if (session.SceneEntry != TreasureMapEntry || session.SceneLoading) return;
        var view = new AoIViewChangeData(); view.LeaveAoi.Add((ulong)TreasureNpcUid);
        await session.SendGameAsync(GameAoiSink.SyncAoiView, view);
    }

    /// <summary>32700 for the OnePieceMapConfig point and NPCConfig 16100001 interact 161000011.</summary>
    public async Task<bool> TryInteractAsync(GameSession session, NpcInteractionReq req)
    {
        if (await TryChestInteractAsync(session, req)) return true;
        if (req.NpcUid != TreasureNpcUid || req.InteractionId is not (TreasureInteraction or 161000071)) return false;
        using var scope = session.Role != null && loop.Owners is { } owners
            ? await owners.EnterAsync("timed battle onepiece interaction", [ReservationKey.Role(session.Role.Id)]) : null;
        if (session.Owner != null) scope?.Context.Pin(session.Owner);
        var point = TreasurePoint();
        int result = session.Role == null || point == null || session.SceneEntry != TreasureMapEntry
            ? ErrorCodes.NpcMissing : session.SceneLoading ? ErrorCodes.SwitchingScenes
            : !session.ActivitiesReady ? ErrorCodes.FeatureUnavailable
            : MonsterService.Distance(session.Position.X, session.Position.Z, point.Value.X, point.Value.Z) > 10f
                ? ErrorCodes.NpcTooFar
            : session.Progress?.Level < 44 || !schedule.IsOpen(200) || !functions.IsOpen(session, 95)
                ? ErrorCodes.FeatureUnavailable
            : session.TimedBattle?.OnePieceGains.GetValueOrDefault(TreasureDay()) >= TreasureCap() ? ErrorCodes.TaskRequirements
            : session.InstanceId != 0 || treasure.ContainsKey(session) ? ErrorCodes.DungeonBusy : 0;
        await session.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionRsp { Result = result, Req = req });
        return true;
    }

    public async Task OnePieceInfoAsync(GameSession session, HiddenTreasureRoundInfoReq _)
    {
        using var scope = session.Role != null && loop.Owners is { } owners
            ? await owners.EnterAsync("timed battle onepiece info", [ReservationKey.Role(session.Role.Id)]) : null;
        if (session.Owner != null) scope?.Context.Pin(session.Owner);
        var (start, end) = TreasureWindow();
        bool open = session.Progress?.Level >= 44 && schedule.IsOpen(200) && functions.IsOpen(session, 95);
        var response = new HiddenTreasureRoundInfoResp
        {
            Result = session.Role == null ? ErrorCodes.Parameter : 0,
            IsOpen = open,
            CurIndex = open ? 1 : 0,
            NextOpenTime = start > time.GetUtcNow().ToUnixTimeSeconds() ? start * 1000 : 0,
        };
        response.TimeMap[1] = new HiddenTreasureRoundInfo { Index = 1, StartTime = start * 1000, EndTime = end * 1000 };
        if (session.Role != null && session.Progress != null && data.Row("OnePieceRewardConfig", 3200101) is { } reward)
        {
            int gains = session.TimedBattle?.OnePieceGains.GetValueOrDefault(TreasureDay()) ?? 0;
            try
            {
                var baseGrants = awards.Resolve(session, GameData.Int(reward, "_iAward"), 101).Grants;
                var extraGrants = RestrictedExtra(GameData.Int(reward, "_iExtraAward"));
                var preview = baseGrants.Select(x => (x.Entry, x.Count, Cap: TreasureCap() * (long)x.Count))
                    .Concat(extraGrants.Select(x => (x.Grant.Entry, x.Grant.Count,
                        Cap: x.Cap is { } cap ? (long)cap : TreasureCap() * (long)x.Grant.Count)));
                foreach (var item in preview.GroupBy(x => x.Entry).Select(x =>
                    (Entry: x.Key, Count: x.Sum(v => v.Count), Max: x.Sum(v => v.Cap))))
                    response.ItemInfo.Add(new HiddenTreasureGainItemInfo
                    { ItemId = item.Entry, CurGainCount = session.TimedBattle?.OnePieceItemGains
                          .GetValueOrDefault(TreasureDay())?.GetValueOrDefault(item.Entry)
                          ?? Math.Min((long)gains * item.Count, item.Max), MaxGainCount = item.Max });
            }
            catch (AwardResolutionException e)
            { log.LogWarning(e, "OnePiece reward preview rejected"); response.Result = ErrorCodes.RewardMissing; }
        }
        if (TreasurePoint() != null) response.SceneInfo[TreasureMapEntry] = new HiddenTreasureSceneLineInfo
        { SceneEntry = TreasureMapEntry, SceneUid = TreasureMapEntry, LineNum = 1,
            NpcRemainCount = open ? 1 : 0, NpcRoundCount = open ? 1 : 0 };
        await session.SendGameAsync(OnePieceInfo, response);
    }

    private async Task SendOnePieceMapAsync(GameSession session)
    {
        var (_, end) = TreasureWindow();
        bool open = session.Progress?.Level >= 44 && schedule.IsOpen(200) && functions.IsOpen(session, 95);
        await session.SendGameAsync(OnePieceMap, new HiddenTreasureMapInfoNotify
        { RoundState = open ? EHiddenTreasureRoundState.Open : EHiddenTreasureRoundState.NotOpen,
            NpcCount = open ? 1 : 0, MaxNpcCount = 1, EndTime = end * 1000 });
    }

    private (long Start, long End) TreasureWindow()
    {
        var now = TimeZoneInfo.ConvertTime(time.GetUtcNow(), time.LocalTimeZone);
        var row = data.Row("DailyActivityConfig", 200);
        if (row is not { } r || !r.TryGetProperty("_kTime", out var limits)
            || limits.ValueKind != System.Text.Json.JsonValueKind.Array || limits.GetArrayLength() < 2) return (0, 0);
        int from = (int)(GameData.Number(limits[0]) ?? 0), until = (int)(GameData.Number(limits[1]) ?? 0);
        DateTime localStart = now.Date.AddHours(from / 100).AddMinutes(from % 100);
        DateTime localEnd = now.Date.AddHours(until / 100).AddMinutes(until % 100);
        if (localEnd <= localStart) localEnd = localEnd.AddDays(1);
        if (now.DateTime >= localEnd) { localStart = localStart.AddDays(1); localEnd = localEnd.AddDays(1); }
        return (new DateTimeOffset(localStart, time.LocalTimeZone.GetUtcOffset(localStart)).ToUnixTimeSeconds(),
            new DateTimeOffset(localEnd, time.LocalTimeZone.GetUtcOffset(localEnd)).ToUnixTimeSeconds());
    }

    /// <summary>Called ahead of ordinary DungeonService for configured OnePiece dungeon entries only.</summary>
    public async Task<bool> TryEnterOnePieceAsync(GameSession session, EnterDungeonReq req)
    {
        if (data.Row("OnePieceRewardConfig", req.SceneEntry) is not { } reward) return false;
        long roleId = session.Role?.Id ?? 0, selectedDay = TreasureDay();
        using var scope = roleId > 0 && loop.Owners is { } owners
            ? await owners.EnterAsync("timed battle onepiece entry", [ReservationKey.Role(roleId),
                ReservationKey.Lifecycle(roleId),
                ReservationKey.Entity("timed_onepiece_entry", $"{roleId}/{selectedDay}")]) : null;
        if (session.Role?.Id != roleId || TreasureDay() != selectedDay)
            throw new OperationCanceledException("OnePiece entry changed before admission.");
        if (session.Owner != null) scope?.Context.Pin(session.Owner);
        int error = session.Role == null || session.Progress == null || session.TimedBattle == null ? ErrorCodes.Parameter
            : !session.ActivitiesReady ? ErrorCodes.FeatureUnavailable
            : session.Progress.Level < 44 || !schedule.IsOpen(200) || !functions.IsOpen(session, 95)
                ? ErrorCodes.FeatureUnavailable
            : session.SceneEntry != TreasureMapEntry || TreasurePoint() is not { } npcPoint
                ? ErrorCodes.NpcMissing
            : MonsterService.Distance(session.Position.X, session.Position.Z, npcPoint.X, npcPoint.Z) > 10f
                ? ErrorCodes.NpcTooFar
            : session.SceneLoading || session.InstanceId != 0 || session.Dead || treasure.ContainsKey(session)
                ? ErrorCodes.DungeonBusy
            : req.InitData?.HiddenTreasureInitData is { NpcId: not 0 } namedNpc
                && namedNpc.NpcId is not (TreasureInteraction or TreasureNpc)
                ? ErrorCodes.Parameter
            : req.InitData is { HiddenTreasureInitData: not null } init && init.HiddenTreasureInitData.PlayerIds.Count > 0
                && (init.HiddenTreasureInitData.PlayerIds.Count != 1 || init.HiddenTreasureInitData.PlayerIds[0] != session.Role.Id)
                ? ErrorCodes.Parameter : 0;
        long day = TreasureDay();
        if (error == 0 && session.TimedBattle!.OnePieceGains.GetValueOrDefault(day) >= TreasureCap()) error = ErrorCodes.TaskRequirements;
        var prepared = error == 0 ? PrepareTreasure(session, req.SceneEntry) : null;
        if (error == 0 && prepared == null) error = ErrorCodes.DungeonBusy;
        await session.SendGameAsync(DungeonService.Enter, new EnterDungeonRsp { Result = error });
        if (prepared != null) await EnterTreasureAsync(prepared);
        return true;
    }

    private Treasure? PrepareTreasure(GameSession session, long sceneEntry)
    {
        InstanceService.Room room;
        try { room = instances.Create(sceneEntry, [session]); }
        catch (Exception e) { log.LogWarning(e, "OnePiece room create failed for {Role}", session.Role!.Id); return null; }
        var reward = data.Row("OnePieceRewardConfig", sceneEntry)!.Value;
        var a = new Treasure { Player = session, Room = room, Monster = (int)GameData.Int(reward, "_iMonsterId"),
            Started = time.GetUtcNow().ToUnixTimeMilliseconds() };
        treasure[session] = a;
        return a;
    }

    private async Task EnterTreasureAsync(Treasure a)
    {
        if (await instances.EnterAsync(a.Room, a.Player, 3)) return;
        treasure.Remove(a.Player);
        await instances.AbandonAsync(a.Room, a.Player);
    }

    private async Task OnePieceLoadedAsync(GameSession session)
    {
        await SendTreasureNpcAsync(session);
        if (!treasure.TryGetValue(session, out var a) || a.Room.Id != session.InstanceId || a.Settled || a.Boss != null) return;
        var init = new DungeonInitData { HiddenTreasureInitData = new HiddenTreasureInitData
        { NpcSceneId = 10110, NpcId = 16100001, NpcEntryId = 10110, MonsterEntryId = a.Monster,
            WaveEndTime = a.Started + 180000 } };
        init.HiddenTreasureInitData.PlayerIds.Add(session.Role!.Id);
        await session.SendGameAsync(DungeonService.BaseInfo, new DungeonBaseInfoNotify
        { DungeonEntry = a.Room.SceneEntry, DungeonId = a.Room.Id, Stage = 1, InitData = init,
            ChangeType = (DungeonBaseInfoNotify.Types.eChangeType)2,
            StageModuleData = new DungeonStageModuleData { MapState = 1, Stage = 1,
                StageStartTime = a.Started, StageEndTime = a.Started + 180000 } });
        var map = worldData.Map(a.Room.SceneEntry);
        MapPoint? point = map?.DungeonGroups.Values.SelectMany(x => x).FirstOrDefault() is { } spawn && spawn.Id != 0
            ? new MapPoint(spawn.X, spawn.Y, spawn.Z, spawn.Face) : map?.Born;
        if (point == null || a.Monster <= 0) return;
        a.Boss = await instances.SpawnAsync(a.Room, a.Monster, point.Value);
        loop.ScheduleRuntime(TimeSpan.FromSeconds(180), async () =>
        { if (!a.Settled && treasure.GetValueOrDefault(session) == a) await SettleTreasureAsync(a, false); });
    }

    private Task OnePieceLeavingAsync(GameSession session)
    {
        if (treasure.TryGetValue(session, out var a) && session.InstanceId == a.Room.Id)
        { a.Settled = true; treasure.Remove(session); }
        return Task.CompletedTask;
    }

    private async Task OnePieceKilledAsync(GameSession killer, MonsterUnit monster)
    {
        if (treasure.GetValueOrDefault(killer) is { } a && !a.Settled && a.Room.Id == monster.InstanceId
            && a.Boss?.Uid == monster.Uid) await SettleTreasureAsync(a, true);
    }

    private async Task SettleTreasureAsync(Treasure a, bool win)
    {
        if (a.Settled) return;
        var player = a.Player;
        long roleId = player.Role?.Id ?? 0;
        string source = $"timed_battle/onepiece/{a.Room.Id}/{roleId}";
        var keys = new[] { ReservationKey.Entity("timed_onepiece", a.Room.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ReservationKey.Role(roleId), ReservationKey.Entity("timed_battle_obligation", source) };
        TimedBattleObligationRow? toDeliver = null;
        using (var scope = loop.Owners is { } owners
            ? await owners.EnterAsync("timed battle onepiece settlement", keys) : null)
        {
        if (a.Settled || player.Role?.Id != roleId || treasure.GetValueOrDefault(player) != a) return;
        if (win && player.ActivitiesReady && player.Role != null && player.TimedBattle != null)
        {
            long day = TreasureDay();
            var next = System.Text.Json.JsonSerializer.Deserialize<State>(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(player.TimedBattle))!;
            if (next.OnePieceGains.GetValueOrDefault(day) < TreasureCap())
            {
                next.OnePieceGains[day] = next.OnePieceGains.GetValueOrDefault(day) + 1;
                long elapsed = Math.Max(1, time.GetUtcNow().ToUnixTimeMilliseconds() - a.Started);
                long previous = next.OnePieceBestMilliseconds.GetValueOrDefault(a.Room.SceneEntry);
                if (previous == 0 || elapsed < previous) next.OnePieceBestMilliseconds[a.Room.SceneEntry] = elapsed;
                var reward = data.Row("OnePieceRewardConfig", a.Room.SceneEntry)!.Value;
                try
                {
                    var baseGrants = awards.Resolve(player, GameData.Int(reward, "_iAward"), 101).Grants;
                    var extra = RestrictedExtra(GameData.Int(reward, "_iExtraAward"));
                    var itemGains = next.OnePieceItemGains.GetValueOrDefault(day) ?? new Dictionary<int, int>();
                    next.OnePieceItemGains[day] = itemGains;
                    foreach (var item in baseGrants)
                        itemGains[item.Entry] = itemGains.GetValueOrDefault(item.Entry) + item.Count;
                    var extraGrants = new List<AwardGrant>();
                    foreach (var item in extra)
                    {
                        int prior = itemGains.GetValueOrDefault(item.Grant.Entry);
                        if (prior == 0 && player.TimedBattle.OnePieceGains.GetValueOrDefault(day) > 0)
                            prior = Math.Min(item.Cap ?? int.MaxValue,
                                player.TimedBattle.OnePieceGains[day] * item.Grant.Count);
                        int count = item.Cap is { } cap ? Math.Min(item.Grant.Count, Math.Max(0, cap - prior)) : item.Grant.Count;
                        if (count <= 0) continue;
                        extraGrants.Add(item.Grant with { Count = count });
                        itemGains[item.Grant.Entry] = prior + count;
                    }
                    var grants = baseGrants.Concat(extraGrants).ToArray();
                    if (grants.Length == 0) throw new AwardResolutionException("OnePiece has no resolved reward.");
                    var result = TreasureResult(a, true);
                    var obligation = new TimedBattleObligationRow(source, a.Room.Id,
                        checked((int)a.Room.SceneEntry), roleId,
                        JsonSerializer.SerializeToUtf8Bytes(grants), JsonSerializer.SerializeToUtf8Bytes(Array.Empty<object>()),
                        JsonSerializer.SerializeToUtf8Bytes(next), result.ToByteArray());
                    var tx = roles.NewTransaction();
                    tx.RoleBlob(roleId, "timed_battle", obligation.NextState);
                    tx.Add(new TimedBattleObligationCreatePlan([obligation]));
                    var taskPlan = tasks.StageOnePieceReward(player, source);
                    taskPlan?.Persist(tx);
                    var economic = grants.Where(grant => grant.Entry is not (100 or 101)).ToArray();
                    if (economic.Length > 0)
                    {
                        var catalog = EventEconomy.Catalog(data);
                        EconomyAsset Asset(int entry) => catalog.TryAsset(entry, out var asset) ? asset
                            : throw new InvalidDataException($"OnePiece grant {entry} is not catalogued.");
                        var owner = new EconomyOwner(EconomyOwnerKind.Role,
                            roleId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        tx.WithEventEconomy(new EventEconomyMutation(catalog, Guid.NewGuid().ToString("N"),
                            "realm", 0, "event/timed-battle-onepiece", source,
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), [],
                            economic.Select(grant => new EconomyQuantity(Asset(grant.Entry), grant.Count)).ToArray(),
                            economic.Select(grant => new EventEconomyOwnerDelta(owner, Asset(grant.Entry), grant.Count)).ToArray(),
                            JsonSerializer.SerializeToUtf8Bytes(new { a.Started, Day = day, Scene = a.Room.SceneEntry })));
                    }
                    await roles.CommitAsync(tx, "timed-battle/onepiece-obligation", _ =>
                    { player.TimedBattle = next; taskPlan?.Apply(); pendingDeliveryRoles.Add(roleId); a.Settled = true; });
                    toDeliver = obligation;
                }
                catch (AwardResolutionException e)
                { log.LogWarning(e, "OnePiece award rejected for {Role}", player.Role.Id); win = false; }
            }
            else win = false;
        }
        else win = false;
        if (!win)
        {
            a.Settled = true;
            await player.SendGameAsync(DungeonService.Result, TreasureResult(a, false));
        }
        }
        if (toDeliver != null)
            await RoleFanout.EachAsync([player], async recipient =>
            {
                try
                {
                    var deliveryKeys = new[] { ReservationKey.Role(toDeliver.RoleId),
                        ReservationKey.Entity("timed_battle_obligation", toDeliver.SourceKey) };
                    if (loop.Owners is { } owners)
                        await owners.RunSessionAsync("timed battle onepiece delivery", deliveryKeys, recipient,
                            () => DeliverOneAsync(recipient, toDeliver));
                    else await DeliverOneAsync(recipient, toDeliver);
                }
                catch (OperationCanceledException) { throw; /* Login will deliver this frozen obligation. */ }
                catch (Exception error) { log.LogError(error, "OnePiece delivery deferred for role {Role}", roleId); }
            });
        treasure.Remove(player);
        if (player.InstanceId == a.Room.Id) await instances.LeaveAsync(player);
    }

    private DungeonResNotify TreasureResult(Treasure a, bool win) => new()
    {
        DungeonEntry = a.Room.SceneEntry, Res = (DungeonRes)(win ? 1 : 2),
        Data = new DungeonResData { OnePieceData = new OnePieceDungeonResData
        { PassTime = time.GetUtcNow().ToUnixTimeMilliseconds() - a.Started, BossLevel = 1 } }
    };

    private long TreasureDay()
    {
        var now = TimeZoneInfo.ConvertTime(time.GetUtcNow(), time.LocalTimeZone);
        return now.Year * 10000L + now.Month * 100 + now.Day;
    }

    private int TreasureCap() => data.Row("RestrictConfig", 1009000) is { } row
        ? Math.Clamp((int)GameData.Int(row, "_iMaxRestrictNum"), 1, 1000) : 20;

    private sealed record RestrictedItem(AwardGrant Grant, int? Cap);

    /// <summary>OnePiece's fixed extra AwardConfig row has explicit per-item daily restrictions and no replacement.</summary>
    private List<RestrictedItem> RestrictedExtra(long group)
    {
        if (data.Row("MultiverseGroupConfig", group) is not { } route || GameData.Int(route, "_itype") != 0)
            throw new AwardResolutionException($"OnePiece extra group {group} is invalid");
        long id = GameData.Int(route, "_iValue_101");
        if (id <= 0) id = GameData.Int(route, "_iValue_default");
        if (data.Row("AwardConfig", id) is not { ValueKind: JsonValueKind.Array } rows || rows.GetArrayLength() != 1
            || !rows[0].TryGetProperty("_kAward", out var fixedItems) || fixedItems.ValueKind != JsonValueKind.Array
            || GameData.Int(rows[0], "_iDropPool") != 0)
            throw new AwardResolutionException($"OnePiece extra award {id} is not one fixed row");
        var result = new List<RestrictedItem>();
        foreach (var item in fixedItems.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 3
                || GameData.Number(item[0]) is not { } entryNumber || GameData.Number(item[1]) is not { } countNumber
                || GameData.Number(item[2]) != 0)
                throw new AwardResolutionException($"OnePiece extra award {id} has unsupported item tuple");
            int entry = checked((int)entryNumber), count = checked((int)countNumber);
            if (entry <= 0 || count <= 0 || data.Row("ItemConfig", entry) == null)
                throw new AwardResolutionException($"OnePiece extra award {id} has invalid item {entry}");
            var restrictions = data.Table("AwardRestricConfig").Values.Where(r =>
                GameData.Int(r, "_iRewardID") == id && GameData.Int(r, "_iItemID") == entry).ToArray();
            if (restrictions.Length > 1) throw new AwardResolutionException($"OnePiece item {entry} has multiple restrictions");
            int? cap = null;
            if (restrictions.Length == 1)
            {
                var restriction = restrictions[0];
                long restrictId = GameData.Int(restriction, "_iRestricid");
                var rule = data.Row("RestrictConfig", restrictId);
                long max = rule is { } configured ? GameData.Int(configured, "_iMaxRestrictNum") : 0;
                if (GameData.Int(restriction, "_iReplaceAward") != 0 || rule == null
                    || GameData.Int(rule.Value, "_iRecoverType") != 2 || max is <= 0 or > int.MaxValue)
                    throw new AwardResolutionException($"OnePiece item {entry} restriction is unsupported");
                cap = (int)max;
            }
            result.Add(new RestrictedItem(new AwardGrant(entry, count), cap));
        }
        return result;
    }

    private void TickOnePiece(long now)
    {
        long day = TreasureDay();
        bool open = schedule.IsOpen(200);
        if (day == lastTreasureDay && open == lastTreasureOpen) return;
        lastTreasureDay = day; lastTreasureOpen = open;
        foreach (var session in sessions.All().Where(s => s.Role != null))
            loop.PostOwned(session, async () =>
            { await SendOnePieceMapAsync(session); if (open) await SendTreasureNpcAsync(session); else await RemoveTreasureNpcAsync(session); });
    }
}
