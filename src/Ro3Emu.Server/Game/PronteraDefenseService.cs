using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Battle for Prontera 118. TimeLimit 1012 opens the activity; the four
/// PronteraDefenseIntensityConfig offsets are measured from that opening (inferred: measuring from season
/// start would place two configured rounds before the activity opens). Round/map/point/monster config owns
/// the entities, CombatService owns kills, and 82976 only accepts server-checked item 600 or guild stations.
/// </summary>
public sealed class PronteraDefenseService : IGameTicker
{
    public const int ActivityId = 118, TimeLimitId = 1012, SupplyItem = 600;
    private const int UnlockLevel = 99;
    private readonly GameData data;
    private readonly ActivityClock clock;
    private readonly RoleStore roles;
    private readonly AccountStore accounts;
    private readonly GuildStore guilds;
    private readonly CharacterService character;
    private readonly AwardService awards;
    private readonly FunctionService functions;
    private readonly MonsterService monsters;
    private readonly WorldData worldData;
    private readonly World world;
    private readonly SessionRegistry sessions;
    private readonly GameLoop loop;
    private readonly ILogger<PronteraDefenseService> log;
    private readonly Dictionary<long, MonsterUnit> actors = new();
    private readonly HashSet<(int Round, int Point, int Wave)> spawned = new();
    private readonly Dictionary<long, long> defensePoints = new();
    private readonly Dictionary<long, long> station = new();
    private int activeRound;
    private long lastSecond = -1;
    private long lastDamageMinute = -1;
    private bool advancing;

    public Func<GameSession, Task>? Changed { get; set; }

    public sealed class State
    {
        public long SeasonOpen { get; set; }
        public long ContributionPoint { get; set; }
        public Dictionary<int, long> RoundContribution { get; set; } = new();
        public Dictionary<int, long> RoundMap { get; set; } = new();
        public HashSet<int> SettledRounds { get; set; } = new();
    }

    public PronteraDefenseService(GameData data, ActivityClock clock, RoleStore roles, AccountStore accounts,
        GuildStore guilds, CharacterService character, AwardService awards, FunctionService functions, MonsterService monsters, WorldData worldData, World world,
        CombatService combat, SessionRegistry sessions, GameLoop loop, ILogger<PronteraDefenseService> log)
    {
        this.data = data; this.clock = clock; this.roles = roles; this.accounts = accounts; this.guilds = guilds;
        this.character = character; this.awards = awards; this.functions = functions; this.monsters = monsters; this.worldData = worldData; this.world = world;
        this.sessions = sessions; this.loop = loop; this.log = log;
        LoadWorld();
        combat.Killed.Add(OnKilledAsync);
    }

    public async Task LoginAsync(GameSession session)
    {
        if (session.Role == null) return;
        session.Defense = roles.LoadTimedActivity(session.Role.Id) is { } bytes
            ? JsonSerializer.Deserialize<State>(bytes) ?? new State() : new State();
        await SettleClosedOwnedAsync(session);
    }

    /// <summary>5013 activity 118 projection. Root's bridge handles empty-map wire initialization.</summary>
    public PlayerActivityPronteraDefense BuildPlayerState(GameSession session) => new()
    {
        ContributionPoint = session.Defense?.SeasonOpen == Opening() ? session.Defense.ContributionPoint : 0,
        GlobalInfo = BuildGlobal(),
    };

    public DBPronteraDefenseInfo BuildGlobal()
    {
        long now = clock.UtcNow.ToUnixTimeSeconds();
        var result = new DBPronteraDefenseInfo { CurRound = RoundAt(now), CurIntensity = IntensityAt(now) };
        foreach (int round in Enumerable.Range(1, 4))
        {
            if (RoundWindow(round) is not { } w) continue;
            var one = new DBPronteraDefenseRoundInfo { RoundId = round, StartTime = w.Start * 1000, EndTime = w.End * 1000 };
            foreach (var row in data.Table("PronteraDefenseRoundConfig").Values.Where(r => GameData.Int(r, "_iRoundId") == round))
                one.MapIds[GameData.Int(row, "_iMapId")] = true;
            result.RoundInfo[round] = one;
        }
        foreach (var row in data.Table("PronteraDefenseMapConfig").Values)
        {
            long map = GameData.Int(row, "_iMapId"), max = GameData.Int(row, "_iDefensePoint");
            if (map <= 0 || max <= 0) continue;
            long hp = defensePoints.GetValueOrDefault(map, max);
            var one = new DBPronteraDefenseMapInfo
            {
                MapId = map, DefensePoint = hp, MaxDefensePoint = max, IsFall = hp <= 0,
                FallRound = hp <= 0 ? result.CurRound : 0,
                CurResistLevel = 0, DefendRoundCount = Math.Max(0, result.CurRound - 1),
            };
            foreach (var (guild, at) in station.Where(x => x.Value == map))
            {
                one.DefenseGuilds[guild] = true;
                one.GuildContributionBoard[guild] = new DBPronteraDefenseGuildBoardInfo { GuildId = guild };
            }
            result.MapInfo[map] = one;
        }
        foreach (var (guild, _) in station)
            if (guilds.Get(guild) is { } g)
                result.GuildInfo[guild] = new DBPronteraDefenseGuildInfo { GuildId = guild, GuildPrepLv = g.Level };
        return result;
    }

    /// <summary>Complete station/event participants selected before Common's activity gate admission.</summary>
    internal Ownership.ReservationKey[] OwnerKeys(GameSession session, CommonActivityReq req)
    {
        var keys = new List<Ownership.ReservationKey>
        { Ownership.ReservationKey.Entity("prontera_event", Opening().ToString(System.Globalization.CultureInfo.InvariantCulture)) };
        if (req.PronteraDefenseGuildDefense != null && session.Role != null
            && guilds.ForRole(session.Role.Id) is { } guild)
        {
            keys.Add(Ownership.ReservationKey.Entity("guild", guild.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            keys.Add(Ownership.ReservationKey.Entity("prontera_station", $"{Opening()}/{guild.Id}"));
        }
        return keys.ToArray();
    }

    /// <summary>82976 activity 118; caller sends response and invokes 5013 projection after success.</summary>
    public async Task<CommonActivityRes> HandleAsync(GameSession session, CommonActivityReq req)
    {
        var response = new CommonActivityRes { ActivityId = ActivityId, RequestId = req.RequestId };
        if (session.Role == null || session.Progress == null || session.Defense == null || req.ActivityId != ActivityId)
        { response.Result = ErrorCodes.Parameter; return response; }
        long now = clock.UtcNow.ToUnixTimeSeconds();
        if (session.Progress.Level < UnlockLevel || !functions.IsOpen(session, 68)
            || !clock.IsOpen(TimeLimitId, clock.UtcNow))
        { response.Result = ErrorCodes.FeatureUnavailable; return response; }
        if (req.PronteraDefenseDonate is { } donation)
        {
            response.PronteraDefenseDonate = new PronteraDefenseDonateResp { Req = donation.Clone() };
            long count = donation.ItemCount;
            if (count <= 0 || count > int.MaxValue) response.Result = ErrorCodes.Parameter;
            else if (character.CountOf(session, SupplyItem) < count) response.Result = ErrorCodes.ItemsShort;
            else
            {
                var next = CopyState(session.Defense);
                next.ContributionPoint = checked(next.ContributionPoint + count); // one point per supply: inferred.
                int round = RoundAt(now);
                if (round > 0) next.RoundContribution[round] = next.RoundContribution.GetValueOrDefault(round) + count;
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(next);
                var committed = await character.ExchangeAsync(session, [(SupplyItem, count)], [], popup: false,
                    persist: tx =>
                    {
                        tx.RoleBlob(session.Role.Id, "timed_activity", bytes);
                        tx.WithEventEconomy(EventEconomy.ForRole(data, session, "prontera-donate",
                            $"prontera/donate/{Opening()}/{session.Role.Id}/{Guid.NewGuid():N}",
                            [(SupplyItem, count)], [], new { Count = count, next.ContributionPoint }));
                    },
                    committed: () => session.Defense = next);
                response.Result = committed == null ? ErrorCodes.Unknown : 0;
                if (committed != null) await NotifyAsync(session);
            }
        }
        else if (req.PronteraDefenseGuildDefense is { } enlist)
        {
            response.PronteraDefenseGuildDefense = new PronteraDefenseGuildDefenseResp { Req = enlist.Clone() };
            var guild = guilds.ForRole(session.Role.Id);
            int round = RoundAt(now);
            response.Result = guild == null || !guild.Members.TryGetValue(session.Role.Id, out var member) || member.Official != 1
                ? ErrorCodes.FeatureUnavailable
                : !resultMap(round, enlist.MapId) ? ErrorCodes.Parameter : 0;
            if (response.Result == 0)
            {
                int changed = await accounts.WriteAsync<int>(new PronteraDefenseStationPlan(
                    Opening(), guild!.Id, enlist.MapId, enlist.BStation));
                response.Result = !enlist.BStation && changed == 0 ? ErrorCodes.Parameter : 0;
                if (response.Result == 0)
                { if (enlist.BStation) station[guild.Id] = enlist.MapId; else station.Remove(guild.Id); await NotifyAllAsync(); }
            }
        }
        else if (req.ActivityCase == CommonActivityReq.ActivityOneofCase.None)
        {
            response.Result = 0; // global-info query carries only activityId (LC_PronteraDefenseHelper.lua, inferred reply).
            await NotifyAsync(session);
        }
        else response.Result = ErrorCodes.Parameter;
        return response;
    }

    private bool resultMap(int round, long map) => round > 0 && data.Table("PronteraDefenseRoundConfig").Values.Any(r =>
        GameData.Int(r, "_iRoundId") == round && GameData.Int(r, "_iMapId") == map);

    public void Tick(GameTime _)
    {
        long now = clock.UtcNow.ToUnixTimeSeconds();
        if (now == lastSecond || advancing) return;
        lastSecond = now;
        advancing = true;
        loop.PostRuntime(async () => { try { await AdvanceAsync(now); } finally { advancing = false; } });
    }

    private async Task AdvanceAsync(long now)
    {
        bool roundChanged;
        using (var scope = loop.Owners is { } owners
            ? await owners.EnterAsync("defense/advance",
                [Ownership.ReservationKey.Entity("prontera_event", Opening().ToString(System.Globalization.CultureInfo.InvariantCulture))]) : null)
        {
            int round = RoundAt(now);
            roundChanged = round != activeRound;
            if (roundChanged)
            {
                foreach (var actor in actors.Values.ToArray())
                { actor.Dead = true; actor.Target = null; await world.RemoveUnitAsync(actor); }
                actors.Clear(); spawned.Clear(); activeRound = round;
            }
            if (round != 0 && RoundWindow(round) is { } w)
            {
                await DamageDefensesAsync(now);
                foreach (var point in data.Table("PronteraDefensePointConfig").Values)
                {
                    int pointId = (int)GameData.Int(point, "_iId");
                    long map = GameData.Int(point, "_iMapId");
                    if (!resultMap(round, map) || !point.TryGetProperty("_iPointId", out var ids)
                        || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() == 0) continue;
                    long scenePoint = (long)(GameData.Number(ids[0]) ?? 0);
                    if (worldData.Map(map)?.Points.TryGetValue(scenePoint, out var at) != true) continue;
                    if (!point.TryGetProperty("_iRoundMonster", out var waves) || waves.ValueKind != JsonValueKind.Array || waves.GetArrayLength() == 0)
                    {
                        // Point rows 1-20 have no wave tuples; one configured difficulty-matched defender target (inferred).
                        if (spawned.Add((round, pointId, 0)))
                        {
                            int selector = (int)GameData.Int(point, "_iDifficulty");
                            await SpawnAsync(map, selector, at);
                        }
                        continue;
                    }
                    int index = 0;
                    foreach (var wave in waves.EnumerateArray())
                    {
                        if (wave.ValueKind != JsonValueKind.Array || wave.GetArrayLength() < 3 || GameData.Number(wave[0]) is not { } offset)
                        { index++; continue; }
                        if (now < w.Start + (long)offset || !spawned.Add((round, pointId, ++index))) continue;
                        for (int p = 1; p + 1 < wave.GetArrayLength(); p += 2)
                        {
                            int selector = (int)(GameData.Number(wave[p]) ?? 0), count = (int)(GameData.Number(wave[p + 1]) ?? 0);
                            for (int n = 0; n < Math.Clamp(count, 0, 20); n++)
                                await SpawnAsync(map, selector, at with { X = at.X + (n % 4) * 1.5f, Z = at.Z + (n / 4) * 1.5f });
                        }
                    }
                }
            }
        }
        if (roundChanged)
            await RoleFanout.EachAsync(sessions.All().Where(s => s.Role != null && s.ActivitiesReady),
                async s => { await SettleClosedOwnedAsync(s); await NotifyAsync(s); });
    }

    private async Task DamageDefensesAsync(long now)
    {
        long minute = now / 60;
        if (minute == lastDamageMinute) return;
        var changed = new Dictionary<long, long>();
        // (inferred) Living invaders erode one point per second between server ticks. The exported configs give
        // maximum defense points and monster waves, but no server damage formula; a 60-point/minute unit rate
        // lets players protect a map by killing real spawned actors before the next minute boundary.
        foreach (var group in actors.Values.Where(m => !m.Dead).GroupBy(m => m.SceneEntry))
        {
            long map = group.Key;
            long max = data.Table("PronteraDefenseMapConfig").Values
                .Where(r => GameData.Int(r, "_iMapId") == map).Select(r => GameData.Int(r, "_iDefensePoint")).FirstOrDefault();
            if (max <= 0) continue;
            long previous = defensePoints.GetValueOrDefault(map, max);
            long next = Math.Max(0, previous - group.LongCount() * 60);
            if (next == previous) continue;
            changed[map] = next;
        }
        if (changed.Count == 0) { lastDamageMinute = minute; return; }
        var tx = accounts.NewTransaction();
        foreach (var (map, point) in changed) tx.Add(new PronteraDefensePointPlan(Opening(), map, point));
        await tx.CommitAsync(accounts, "prontera-defense/damage", _ =>
        {
            foreach (var (map, point) in changed) defensePoints[map] = point;
            lastDamageMinute = minute;
        });
        await NotifyAllAsync();
    }

    private async Task SpawnAsync(long map, int selector, MapPoint point)
    {
        int npc = data.Row("PronteraDefenseMonsterConfig", selector) is { } row ? (int)GameData.Int(row, "_iNpcId") : 0;
        if (npc <= 0) return;
        if (await monsters.SpawnSingleAsync(map, npc, point) is { } unit) actors[unit.Uid] = unit;
    }

    private async Task OnKilledAsync(GameSession killer, MonsterUnit monster)
    {
        if (!actors.ContainsKey(monster.Uid) || killer.Role == null || killer.Defense == null || activeRound == 0
            || RoundAt(clock.UtcNow.ToUnixTimeSeconds()) != activeRound) return;
        var role = killer.Role;
        var state = killer.Defense;
        var attachment = killer.Attachment;
        var owner = killer.Owner;
        long? epoch = owner?.LifecycleEpoch;
        using var scope = loop.Owners is { } owners
            ? await owners.EnterAsync("defense/kill contribution", [
                Ownership.ReservationKey.Entity("prontera_event", Opening().ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Ownership.ReservationKey.Entity("prontera_actor", monster.Uid.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Ownership.ReservationKey.Role(role.Id)]) : null;
        if (!ReferenceEquals(killer.Role, role) || !ReferenceEquals(killer.Defense, state)
            || !ReferenceEquals(killer.Attachment, attachment) || !ReferenceEquals(killer.Owner, owner)
            || owner?.LifecycleEpoch != epoch || RoundAt(clock.UtcNow.ToUnixTimeSeconds()) != activeRound
            || !actors.ContainsKey(monster.Uid)) return;
        if (owner != null) scope?.Context.Pin(owner);
        var next = CopyState(state);
        next.ContributionPoint++;
        next.RoundContribution[activeRound] = next.RoundContribution.GetValueOrDefault(activeRound) + 1;
        next.RoundMap[activeRound] = monster.SceneEntry;
        var tx = roles.NewTransaction();
        tx.RoleBlob(role.Id, "timed_activity", JsonSerializer.SerializeToUtf8Bytes(next));
        await roles.CommitAsync(tx, "prontera-defense/kill", _ =>
        { killer.Defense = next; actors.Remove(monster.Uid); });
        await NotifyAsync(killer);
    }

    /// <summary>C/D 15: the role's own round settlement entities, reserved up front by the role select (the season
    /// comes from the role's blob, served by the login snapshot).</summary>
    internal Ownership.ReservationKey[] LoginKeys(long roleId)
    {
        long season = roles.LoadTimedActivity(roleId) is { } bytes
            ? (JsonSerializer.Deserialize<State>(bytes) ?? new State()).SeasonOpen : new State().SeasonOpen;
        return SettlementKeys(roleId, season);
    }

    private static Ownership.ReservationKey[] SettlementKeys(long roleId, long season) =>
        Enumerable.Range(1, 4).Select(round =>
            Ownership.ReservationKey.Entity("prontera_round_settlement", $"{season}/{round}/{roleId}")).ToArray();

    private async Task SettleClosedOwnedAsync(GameSession session)
    {
        if (session.Role == null || session.Defense == null) return;
        long roleId = session.Role.Id;
        Ownership.ReservationKey[] keys =
            [Ownership.ReservationKey.Role(roleId), Ownership.ReservationKey.Lifecycle(roleId), .. SettlementKeys(roleId, session.Defense.SeasonOpen)];
        if (loop.Owners is { } owners)
            await owners.RunSessionAsync("defense/settle", keys, session, () => SettleClosedAsync(session));
        else await SettleClosedAsync(session);
    }

    private async Task SettleClosedAsync(GameSession session)
    {
        if (session.Role == null || session.Defense == null) return;
        foreach (int round in Enumerable.Range(1, 4))
        {
            if (RoundWindowFor(session.Defense.SeasonOpen, round) is not { } w || clock.UtcNow.ToUnixTimeSeconds() < w.End
                || session.Defense.SettledRounds.Contains(round)
                || session.Defense.RoundContribution.GetValueOrDefault(round) <= 0) continue;
            long map = session.Defense.RoundMap.GetValueOrDefault(round, 10100);
            var next = CopyState(session.Defense); next.SettledRounds.Add(round);
            // One award after actual contribution and round closure is an inferred eligibility rule.
            string key = map == 10100 ? "PronteraDefenseMainCityDefendAward" : "PronteraDefenseOtherCityDefendAward";
            long group = data.Table("ActivityConstant").TryGetValue(key, out var configured)
                ? (long)(GameData.Number(configured) ?? 0) : 0;
            try
            {
                var plan = awards.Resolve(session, group, 101);
                if (plan.Grants.Count == 0) throw new AwardResolutionException($"Prontera round {round} has empty award {group}");
                if (await character.ApplyAwardAsync(session, plan, persist: tx =>
                {
                    tx.RoleBlob(session.Role.Id, "timed_activity", JsonSerializer.SerializeToUtf8Bytes(next));
                    if (plan.Grants.Any(grant => grant.Entry is not (100 or 101)))
                        tx.WithEventEconomy(EventEconomy.ForRole(data, session, "prontera-round",
                            $"prontera/round/{next.SeasonOpen}/{round}/{session.Role.Id}", [], plan.Grants,
                            new { Season = next.SeasonOpen, Round = round, Map = map }));
                }, committed: () => session.Defense = next))
                { await NotifyAsync(session); }
                else log.LogWarning("Prontera round {Round} reward transaction failed for {Role} on {Map}", round, session.Role.Id, map);
            }
            catch (AwardResolutionException e)
            { log.LogWarning(e, "Prontera round {Round} reward {Group} rejected for {Role}", round, group, session.Role.Id); }
        }
        if (session.Defense.SeasonOpen != Opening() && Enumerable.Range(1, 4).All(round =>
            session.Defense.RoundContribution.GetValueOrDefault(round) <= 0
            || session.Defense.SettledRounds.Contains(round)))
        {
            var next = new State { SeasonOpen = Opening() };
            var tx = roles.NewTransaction();
            tx.RoleBlob(session.Role.Id, "timed_activity", JsonSerializer.SerializeToUtf8Bytes(next));
            await roles.CommitAsync(tx, "prontera-defense/new-season", _ => session.Defense = next);
            await NotifyAsync(session);
        }
    }

    private Task NotifyAsync(GameSession s) => Changed?.Invoke(s) ?? Task.CompletedTask;
    private Task NotifyAllAsync() => RoleFanout.EachAsync(
        sessions.All().Where(s => s.Role != null && s.ActivitiesReady), NotifyAsync);
    private static State CopyState(State source) => JsonSerializer.Deserialize<State>(JsonSerializer.SerializeToUtf8Bytes(source))!;

    private long Opening() => clock.TryWindow(TimeLimitId, out var start, out _) ? start.ToUnixTimeSeconds() : 0;
    private (long Start, long End)? RoundWindow(int id) => RoundWindowFor(Opening(), id);
    private (long Start, long End)? RoundWindowFor(long open, int id)
    {
        if (open <= 0 || data.Row("PronteraDefenseIntensityConfig", id) is not { } row
            || !row.TryGetProperty("_iTime", out var t) || t.ValueKind != JsonValueKind.Array || t.GetArrayLength() < 2) return null;
        return (open + (long)(GameData.Number(t[0]) ?? 0), open + (long)(GameData.Number(t[1]) ?? 0));
    }
    private int RoundAt(long now) => clock.IsOpen(TimeLimitId, DateTimeOffset.FromUnixTimeSeconds(now))
        ? Enumerable.Range(1, 4).FirstOrDefault(id => RoundWindow(id) is { } w && w.Start <= now && now < w.End) : 0;
    private long IntensityAt(long now)
    {
        int id = RoundAt(now);
        if (id == 0 || RoundWindow(id) is not { } w || data.Row("PronteraDefenseIntensityConfig", id) is not { } row) return 0;
        return row.TryGetProperty("_kIntensity", out var stages) && stages.ValueKind == JsonValueKind.Array
            ? stages.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.Array && s.GetArrayLength() >= 2
                && w.Start + (long)(GameData.Number(s[0]) ?? 0) <= now).Select(s => (long)(GameData.Number(s[1]) ?? 0)).LastOrDefault() : 0;
    }

    public (int Round, long Intensity) ProjectionPhase(long now) => (RoundAt(now), IntensityAt(now));

    /// <summary>5013's global round/intensity projection changes at these configured instants.</summary>
    public long NextProjectionBoundary(long now)
    {
        long next = long.MaxValue;
        void Consider(long at) { if (at > now && at < next) next = at; }
        foreach (int id in Enumerable.Range(1, 4))
        {
            if (RoundWindow(id) is not { } window) continue;
            Consider(window.Start);
            Consider(window.End);
            if (data.Row("PronteraDefenseIntensityConfig", id) is not { } row
                || !row.TryGetProperty("_kIntensity", out var stages) || stages.ValueKind != JsonValueKind.Array) continue;
            foreach (var stage in stages.EnumerateArray())
            {
                if (stage.ValueKind != JsonValueKind.Array || stage.GetArrayLength() < 2) continue;
                long at = window.Start + (long)(GameData.Number(stage[0]) ?? 0);
                if (at < window.End) Consider(at);
            }
        }
        return next;
    }

    private void LoadWorld()
    {
        long open = Opening();
        var rows = accounts.ReadSync<PronteraDefenseWorldRows>(new PronteraDefenseWorldReadPlan(open));
        foreach (var (map, point) in rows.Points) defensePoints[map] = point;
        foreach (var (guild, map) in rows.Stations) station[guild] = map;
    }
}
