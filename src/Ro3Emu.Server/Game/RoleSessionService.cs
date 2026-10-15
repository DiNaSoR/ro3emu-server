using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Game;

/// <summary>
/// A role's session: create and select, the login -> world sequence, position and
/// settings persistence, the handover cleanup and role switching. Its messages are mapped by CoreMessages.
/// </summary>
public sealed class RoleSessionService(RoleStore roles, Maps.World world, ChatService chat, Data.GameData data, CharacterService character, SkillService skills, FunctionService functions, CombatService combat, BuffService buffs, SceneService scene, TaskService tasks, DailyActivityService daily, GuideService guides, EmojiService emoji, AchievementService achievements, Micro.FriendService friends, MvpService mvps, CardService cards, ShopService shops, EmblemService emblems, SeasonService seasons, SeasonPageService seasonPage, SeasonRefineService seasonRefine, SeasonTalentService talents, PetService pets, TowerService tower, MailService mail, AuctionService auctions, GuildService guilds, AdventureGroupService adventureGroups, GuildCaravanService caravan, GuildChallengeService guildChallenge, DungeonService dungeons, DungeonMatchService dungeonMatches, InstanceService instances, TradeService trade, StallsService stalls, LifeJobService life, GatheringService gathering, WildAfkService afk, WorldCoreService worldCore, WorldInteractionService interaction, NewActivityService activities, TimedEventService timedEvents, PronteraDefenseService defense, TimedBattleService timedBattles, FestivalService festival, TrainingService training, LotteryService lottery, TimeProvider clock, MountService mounts, CareerSpecialService careerSpecial, WardrobeService wardrobe, TotemService totems, GenreService genre, TerritoryWarsService territory, TerritoryEscortService escort, Deadlines deadlines, Ownership.RoleHandover handover, SessionRegistry sessions, Persistence.PositionPersistence positions, Persistence.SettingsPersistence settings, ReconnectService reconnects, ILogger<RoleSessionService> log)
{
    /// <summary>Novice; JobConfig[1000] picks the male/female unit.</summary>
    private const int NoviceCareer = 1000;

    /// <summary>Speed attribute (enum_unitAttrMap 113) when JobConfig is unavailable; Novice's _kAttri has 700.</summary>
    public const long DefaultSpeed = 700;

    private const long AttrSpeed = 113;
    private const long AttrBaseAtkSpeed = 115;

    private const int ErrNameExist = 2;
    private const int ErrNameLengthInvalid = 32;

    /// <summary>WALK is reported many times a second; the position goes to the database at most this often (and on stop).</summary>
    private static readonly TimeSpan PositionSaveInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Avatar RPC "SwitchToAccount" (HappyEngineClient.SwitchToAccount, after PRE_SWITCH_ROLE): the server answers
    /// with "SwitchToAccountCB"; the client drops its entities and reconnects as the account (ConnectServerReq
    /// SwitchToAccount, handled by the gate).
    /// </summary>
    public async Task SwitchToAccountAsync(GameSession session)
    {
        await handover.Begin(session);
        log.LogInformation("{Account} leaves its role for the role screen", session.Account);
        await session.SendRpcAsync("SwitchToAccountCB");
    }

    public Task OnDisconnectedAsync(GameSession session) => handover.Begin(session);

    /// <summary>C/D 15: the handover cleanup reserves the role, its lifecycle and account plus the shared keys its
    /// steps write - its own guild's keys (offline publication) and its timed battle's leave and settlement - not
    /// Realm. LogoutScopeAuditTests and the owner audit inventory guard that nothing else is needed.</summary>
    internal Ownership.ReservationKey[] CleanupReservation(GameSession session) =>
        [.. session.Role is { } role ? new[] { Ownership.ReservationKey.Lifecycle(role.Id) } : [],
            .. session.Account is { } account ? new[] { Ownership.ReservationKey.Account(account) } : [],
            .. guilds.OwnGuildKeys(session), .. timedBattles.CleanupKeys(session)];

    /// <summary>Essential unlinking runs after admitted role work, under the handover's cleanup capability.</summary>
    internal Func<string, GameSession, Exception?>? CleanupFailureForTest { get; set; }
    internal async Task CleanupRoleAsync(GameSession session)
    {
        var failures = new List<Exception>();
        long? snapshotRole = session.Role?.Id;
        async Task Step(string name, Func<Task> work)
        {
            try
            {
                if (CleanupFailureForTest?.Invoke(name, session) is { } injected) throw injected;
                await work();
            }
            catch (Exception e) { failures.Add(e); }
        }
        await Step("scene", () => { scene.DisconnectLine(session); return Task.CompletedTask; });
        await Step("combat", () => combat.ClearSkillsAsync(session));
        await Step("interaction", () => interaction.DisconnectAsync(session));
        await Step("gathering", () => gathering.InterruptAsync(session, notify: false));
        await Step("life", () => life.CancelAsync(session));
        if (session.Role is { } leaving)
        {
            try { await friends.OnlineChangedAsync(leaving.Id, online: false); }
            catch (Exception e) { log.LogWarning(e, "Optional offline friend notice failed for role {Role}", leaving.Id); }
        }
        if (session.RolePositionReady)
            await Step("position", () => { SavePosition(session); return Task.CompletedTask; });
        await Step("settings", () => { settings.Flush(session); return Task.CompletedTask; });
        await Step("territory", () => territory.DisconnectAsync(session));
        var afkState = session.WildAfk;
        await Step("afk", () => afk.LeaveAsync(session));
        // AFK.Leave detaches its session state. A held old-day award still needs the old
        // daily/task rows on the next login, so the ordinary daily reset must wait for it.
        if (!WildAfkService.PendingDailyAward(afkState, session.Daily, session.Tasks, session.DailyRows))
            await Step("daily boundary", () => daily.ReconcileAsync(session));
        await Step("dungeon", () => dungeons.AbandonAsync(session));
        await Step("tower", () => tower.AbandonAsync(session));
        if (session.GuildOnlinePublished)
            await Step("guild", () => guilds.OnlineChangedAsync(session, false));
        session.ActivitiesReady = false;
        await Step("training", () => training.LeaveRoleAsync(session));
        await Step("festival", () => festival.LeaveRoleAsync(session));
        await Step("dungeon match", () => dungeonMatches.LeaveRoleAsync(session));
        session.DungeonSoloMatch = false;
        await Step("timed battle", () => timedBattles.LeaveRoleAsync(session));
        session.EventFaction = 0;
        await Step("instance", () => instances.AbandonAsync(session));
        await Step("world", () => world.LeaveAsync(session));
        await Step("chat", () => { chat.OnRoleLeft(session); return Task.CompletedTask; });
        await Step("world core", () => { worldCore.Disconnected(session); return Task.CompletedTask; });
        if (snapshotRole is { } released) roles.ReleaseLoginSnapshot(released);
        if (failures.Count != 0) throw new AggregateException("Essential role cleanup failed.", failures);
    }

    internal async Task CreateRoleAsync(GameSession session, AccountLoginCreateRoleReq req)
    {
        // A replacement may have claimed the account while this request waited for its account reservation.
        if (!sessions.IsWinner(session) || session.Channel.PeerClosed) return;
        string name = req.RoleName.Trim();
        int result = 0;
        Role? role = null;
        if (name.Length is < 2 or > 16)
        {
            result = ErrNameLengthInvalid;
        }
        else
        {
            req.Appearances.TryGetValue(RoleProto.HairSlot, out int hair);
            int sex = req.Sex is 1 or 2 ? req.Sex : 1;
            // The first quest (10101010) waits next to South Gate's born point, ~200 m from SpawnScenePos.
            // C/D 15: one insert with that position on the writer thread, under the account's reservation; the
            // unique name index decides a race between accounts.
            var born = scene.BornPoint(RoleStore.SpawnSceneEntry);
            role = await roles.CreateAsync(session.Account!, req.AreaId, name, sex, NoviceCareer, hair,
                born == null ? 0 : RoleStore.SpawnSceneEntry, born?.X ?? 0, born?.Y ?? 0, born?.Z ?? 0, born?.Face ?? 0);
            if (role == null) result = ErrNameExist;
        }
        log.LogInformation("Create role '{Name}' for {Account}: result {Result} id {Id}", name, session.Account, result, role?.Id);

        var resp = new AccountLoginCreateRoleResp { Result = result };
        if (role != null)
        {
            resp.RoleBaseInfo = RoleProto.BaseInfo(role);
            resp.Appearances.Add(RoleProto.Appearances(role));
        }
        await session.SendGameAsync(MsgId.LoginRoleCreate, resp);
    }

    internal Func<string, GameSession, Task>? SelectionPhaseForTest { get; set; }

    private bool SelectionAvailable(GameSession session) => sessions.IsWinner(session) && !session.Channel.PeerClosed;

    private void EnsureSelection(GameSession session, Ownership.AttachmentLease lease)
    {
        if (!SelectionAvailable(session) || lease.Cancelled ||
            !ReferenceEquals(session.Attachment, lease) ||
            !ReferenceEquals(session.Owner?.Attachment, lease) ||
            session.Owner.State != Ownership.RoleOwnerState.Loading)
            throw new OperationCanceledException("Role selection was superseded or its connection closed.");
    }

    internal async Task SelectRoleAsync(GameSession session, AccountLoginSelectRoleReq req)
    {
        var role = await roles.GetAsync(req.Id);
        if (role == null || role.Account != session.Account)
        {
            log.LogWarning("Select role {Id} refused for {Account}", req.Id, session.Account);
            await session.SendGameAsync(MsgId.LoginRoleSelect, new AccountLoginSelectRoleResp { Result = 1 });
            return;
        }
        // A target is validated before the valid old role is touched. Neither predecessor wait nor
        // old-role cleanup owns the target or Realm reservation.
        await session.PredecessorFence;
        if (session.Role != null) await handover.Begin(session);
        if (!SelectionAvailable(session)) return;
        // C/D 14: read the whole role at one SQLite snapshot on a reader worker, after the old role's handover and
        // before the Realm scope. The loads below are served from it instead of synchronous SQL on the loop; every
        // write committed after the read began invalidates its part, so nothing older than a commit is served.
        bool prepared = await roles.PrepareLoginSnapshotAsync(req.Id, session.Account!);
        try
        {
            if (!SelectionAvailable(session)) return;
            await SelectPreparedRoleAsync(session, req);
        }
        finally
        {
            // C/D 16 part 4: an attached role keeps its snapshot for the session (blob reads after login and
            // write-through of its own saves); role cleanup releases it. A failed or abandoned select releases now.
            if (prepared && !(session.Role?.Id == req.Id && session.Owner?.State == Ownership.RoleOwnerState.Attached))
                roles.ReleaseLoginSnapshot(req.Id);
        }
    }

    /// <summary>Team entry, guild online state and the scene guild id publish together under the role's own guild
    /// keys (not Realm, which starves behind sustained owned work). A membership change while waiting re-admits.</summary>
    internal async Task<Ownership.LegacyOwnerScope> EnterScenePublicationAsync(Ownership.OwnerRegistry owners, GameSession session)
    {
        while (true)
        {
            var scope = await owners.EnterAsync("scene online publication",
                [.. guilds.OwnGuildKeys(session), .. session.Role is { } role ? new[] { Ownership.ReservationKey.Lifecycle(role.Id) } : []]);
            if (owners.Reservations.Covers(scope.Context.Operation, guilds.OwnGuildKeys(session))) return scope;
            scope.Dispose();
        }
    }

    private async Task<Ownership.ReservationKey[]> LoginReservationAsync(long roleId, string account) =>
        [Ownership.ReservationKey.Role(roleId), Ownership.ReservationKey.Account(account), Ownership.ReservationKey.Lifecycle(roleId),
            .. defense.LoginKeys(roleId), .. activities.LoginKeys(roleId), .. territory.LoginKeys(),
            .. caravan.LoginKeys(roleId), .. guildChallenge.LoginKeys(roleId),
            .. await mvps.LoginKeysAsync(roleId), .. await dungeons.LoginKeysAsync(roleId)];

    /// <summary>The login keys follow guild membership and pending obligations, which may change while the select
    /// waits: re-admit until the admitted reservation still covers them.</summary>
    private async Task<Ownership.LegacyOwnerScope> EnterSelectAsync(Ownership.OwnerRegistry owners, long roleId, string account)
    {
        while (true)
        {
            var scope = await owners.EnterAsync("role select", await LoginReservationAsync(roleId, account));
            try
            {
                if (owners.Reservations.Covers(scope.Context.Operation, await LoginReservationAsync(roleId, account))) return scope;
            }
            catch { scope.Dispose(); throw; }
            scope.Dispose();
        }
    }

    private async Task SelectPreparedRoleAsync(GameSession session, AccountLoginSelectRoleReq req)
    {
        Role? role;
        var owners = session.Owners ?? throw new InvalidOperationException("Selection has no owner registry.");
        Ownership.AttachmentLease? lease = null;
        try
        {
            // C/D 15: the select reserves its role, account and lifecycle plus the keys its login callees write (own
            // Prontera settlements, own Eve cycle, current territory season, its guild's caravan and challenge gates,
            // its pending MVP and dungeon obligations) - not Realm. Realm-shared maintenance runs on the services' own
            // tickers; LoginScopeAuditTests guards that nothing else is needed.
            using (var scope = await EnterSelectAsync(owners, req.Id, session.Account!))
            {
                if (!SelectionAvailable(session)) return;
                role = roles.Get(req.Id);
                if (role == null || role.Account != session.Account)
                {
                    await session.SendGameAsync(MsgId.LoginRoleSelect, new AccountLoginSelectRoleResp { Result = 1 });
                    return;
                }
                if (SelectionPhaseForTest is { } beforeAttach)
                    await beforeAttach("BeforeAttach", session);
                if (!SelectionAvailable(session)) return;
                await roles.TouchLoginAsync(role.Id);
                if (!SelectionAvailable(session)) return;
                session.ClearRoleLoadState();
                session.Role = role;
                owners.BindLegacySelection(session);
                lease = session.Attachment!;
                session.InstanceId = 0;
                if (SelectionPhaseForTest is { } afterBind)
                    await afterBind("AfterBindBeforeLoad", session);
                EnsureSelection(session, lease);
                // Stage G: the services' login blob writes go out as one asynchronous commit, still under this scope.
                roles.BeginLoginStaging(role.Id);
                try
                {
                    var arrival = await InitializeSelectedRoleAsync(session, role);
                    await PublishSelectedRoleAsync(session, role, arrival, lease);
                    await roles.CommitLoginStagingAsync(role.Id);
                }
                finally { roles.FlushLoginStaging(role.Id); } // a failed or cancelled selection keeps what it staged
                EnsureSelection(session, lease);
                session.Owner!.State = Ownership.RoleOwnerState.Attached;
            }
        }
        catch (OperationCanceledException) when (!SelectionAvailable(session) || lease?.Cancelled == true)
        {
            if (lease != null) await handover.Begin(session);
            else session.Role = null;
        }
        catch
        {
            if (lease != null) await handover.Begin(session);
            else session.Role = null;
            throw;
        }
    }

    private async Task<Maps.MapPoint> InitializeSelectedRoleAsync(GameSession session, Role role)
    {
        deadlines.Attach(session);
        session.SkillPlans = null; // each selected role loads its own 5025 state
        session.SkillPlanSwitchUtc = default;
        session.Lottery = null;
        session.LotteryDay = 0;
        session.Training = null;
        var arrival = scene.LoginPoint(role.SceneEntry, new Vec3 { X = role.X, Y = role.Y, Z = role.Z }, role.Face);
        session.SceneRecoveryRequired = false;
        session.Handoff = null;
        session.LineNumber = 1;
        session.RuntimeKind = Maps.RuntimeMapKind.PublicLine;
        session.SceneEntry = role.SceneEntry;
        session.Position = new Vec3 { X = arrival.X, Y = arrival.Y, Z = arrival.Z };
        session.Face = arrival.Face;
        session.RolePositionReady = true;
        session.LastWalkUtc = DateTime.UtcNow;
        session.Speed = data.JobAttributes(role.Career).GetValueOrDefault(AttrSpeed, DefaultSpeed);
        session.PositionSavedUtc = DateTime.UtcNow;
        session.DeferFunctionSync = true;
        session.DeferTaskSync = true;
        session.SkillProtocol = new();
        session.SkillModeCds.Clear();
        session.SkillCds.Clear();
        session.Buffs.Clear();
        session.BuffEffectCds.Clear();
        session.ImmuneDeathOffer = null;
        session.ImmuneRecoveryUntilMs = 0;
        session.StatusBits = 0;
        session.CurrentHp = null;
        session.CurrentSp = null;
        session.Dead = false;
        session.CareerSpecial = null; session.CareerPendants = [];
        session.Mounts = null; session.MountState = new(); session.Wardrobe = null; session.Totems = null; session.Genre = null;
        session.TotemSwitchUtc = default;
        session.SceneLoading = true;
        character.Load(session);
        await character.FinishLoadAsync(session);
        careerSpecial.Load(session); // before mounts: a leased mount entitles the battle slot
        mounts.Load(session);
        totems.Load(session);
        await life.LoadAsync(session);
        seasons.Load(session);
        session.ActivitiesReady = false;
        session.NewActivities = null;
        session.DailyReady = false;
        afk.Load(session);
        daily.Load(session);
        await emblems.LoadAsync(session);
        talents.Load(session);
        seasonRefine.Load(session);
        character.Refresh(session); // first entity/stats use this role's saved active talent plan
        log.LogInformation("Role {Name} ({Id}) of {Account} enters scene {Scene} at {X},{Z}", role.Name, role.Id, session.Account, role.SceneEntry, role.X, role.Z);
        return arrival;
    }

    private async Task PublishSelectedRoleAsync(GameSession session, Role role, Maps.MapPoint arrival,
        Ownership.AttachmentLease lease)
    {
        if (SelectionPhaseForTest is { } beforePublish)
            await beforePublish("BeforePublish", session);
        EnsureSelection(session, lease);
        await session.SendGameAsync(MsgId.LoginRoleSelect, new AccountLoginSelectRoleResp
        {
            Result = 0,
            RoleAssets = new RoleAssets
            {
                BasicInfo = RoleProto.BaseInfo(role),
                ServerTimeMs = NowMs(),
                ServerInfo = new ServerBasicInfo
                {
                    ServerOpenTime = (ulong)new DateTimeOffset(seasons.Start).ToUnixTimeSeconds(),
                    ServerLogicTime = (ulong)clock.GetUtcNow().ToUnixTimeSeconds(),
                },
            },
        });
        EnsureSelection(session, lease);

        // The avatar entity replaces ClientAccount; from now on client game messages arrive raw. It carries the
        // ReconnectInfo, and OnGetAuth follows.
        await reconnects.CreateAvatarAsync(session, $"ava-{role.Id}");
        EnsureSelection(session, lease);

        // Saved client settings (intro skipped, control mode, key bindings...) via the generic DB increment sync.
        session.Settings = LoadSettings(role.Id); // C/D 17: kept on the session, merged and written lazily from now on
        session.SettingsPart = null;
        await session.SendGameAsync(MsgId.AutoSyncSettings, SettingsSync(session.Settings));
        EnsureSelection(session, lease);
        await chat.OnRoleEnteredAsync(session);
        EnsureSelection(session, lease);
        await seasons.LoginAsync(session); // 5021 early: multiverse lookups read its playBookId
        EnsureSelection(session, lease);
        await character.SendLoginSyncAsync(session);
        EnsureSelection(session, lease);
        await skills.LoadAsync(session); // after character/cart sync, before login rewards can call skill sync under StateLock
        EnsureSelection(session, lease);
        await life.LoginAsync(session); // 5018 vigor then 5010 before To-Do/tasks/functions consume life state.
        EnsureSelection(session, lease);
        await seasonPage.LoginAsync(session); // 5022 before the Season/honor panels can open
        EnsureSelection(session, lease);
        await seasonRefine.LoginAsync(session); // 5023 after 5007, before function unlocks
        EnsureSelection(session, lease);
        await wardrobe.LoginAsync(session);
        EnsureSelection(session, lease);
        await mounts.LoginAsync(session);
        EnsureSelection(session, lease);
        await careerSpecial.LoginAsync(session); // 5002: the equipment window reads SpecialPropData unguarded
        EnsureSelection(session, lease);
        await totems.SyncAsync(session);
        EnsureSelection(session, lease);
        // 5035 must precede To-Do/scene loading: LC_OnHookHelper calls Mathf.Max on its remaining seconds.
        await afk.LoginAsync(session);
        EnsureSelection(session, lease);
        await tasks.LoginAsync(session); // 5027 before 5056: quest-gated functions (condition 4004)
        EnsureSelection(session, lease);
        await daily.LoginAsync(session); // 5004 and 5059 before 5056 opens To-Do / Dodo
        EnsureSelection(session, lease);
        await afk.ReconcileAsync(session); // finish any held old-day award before exposing today's allowance
        EnsureSelection(session, lease);
        await talents.SyncAsync(session); // TaskService may return without quest config; 5050 still has safe nonempty maps
        EnsureSelection(session, lease);
        await guides.LoginAsync(session);
        EnsureSelection(session, lease);
        await emoji.LoginAsync(session); // 5006: the emote panel enables only unlocked emotes
        EnsureSelection(session, lease);
        await cards.LoginAsync(session); // after 5009: inserted cards are its storagePack
        EnsureSelection(session, lease);
        await tower.LoginAsync(session); // 5065 before tower-dependent functions and shop conditions
        EnsureSelection(session, lease);
        await dungeons.LoginAsync(session); // 5062 before opening the Dungeon menu
        EnsureSelection(session, lease);
        await emblems.LoginAsync(session); // after 5009: plan GUIDs refer to item instances
        EnsureSelection(session, lease);
        await shops.LoginAsync(session); // after 5066: purchase replies read PlayerBasicInfo.id
        EnsureSelection(session, lease);
        await trade.LoginAsync(session); // 5033 before 5056 makes the trade UI reachable.
        EnsureSelection(session, lease);
        await stalls.LoginAsync(session); // 5058 is metadata; stock remains in 33408/33411.
        EnsureSelection(session, lease);
        await guilds.LoginAsync(session); // 5047 and, for members only, 32213 before 5056 opens guild UI
        EnsureSelection(session, lease);
        await adventureGroups.LoginAsync(session); // 5068 and, for members, 34011 before 5056 opens the Adv. Guild
        EnsureSelection(session, lease);
        await guildChallenge.LoginAsync(session);
        EnsureSelection(session, lease);
        await caravan.LoginAsync(session); // role caravan counters in 5047 before the activity card opens
        EnsureSelection(session, lease);
        await territory.LoginAsync(session);
        EnsureSelection(session, lease);
        await escort.LoginAsync(session);
        EnsureSelection(session, lease);
        await defense.LoginAsync(session);
        EnsureSelection(session, lease);
        await activities.LoginAsync(session); // 5013/5042/5054 before gated activity panels
        EnsureSelection(session, lease);
        await dungeons.DeliverJourneyPendingAsync(session);
        EnsureSelection(session, lease);
        await timedEvents.LoginAsync(session);
        EnsureSelection(session, lease);
        await timedBattles.LoginAsync(session);
        EnsureSelection(session, lease);
        await festival.LoginAsync(session);
        EnsureSelection(session, lease);
        await training.LoginAsync(session);
        EnsureSelection(session, lease);
        await auctions.LoginAsync(session); // 5063 personal state; 82000/82005 supply actual lists
        EnsureSelection(session, lease);
        await pets.LoginAsync(session); // 5017 before 5056: opening a function must not expose an uninitialized pet DAL.
        EnsureSelection(session, lease);
        await genre.LoginAsync(session);
        session.DeferTaskSync = false;
        EnsureSelection(session, lease);
        await tasks.RefreshAsync(session); // all owner snapshots are now available for passive objectives; settles with the login's one full 5027
        EnsureSelection(session, lease);
        await achievements.LoginAsync(session); // one full 5019 after every owner snapshot its conditions read; function 21 opens at level 1
        EnsureSelection(session, lease);
        session.DeferFunctionSync = false;
        await functions.SyncAsync(session);
        EnsureSelection(session, lease);
        await lottery.LoginAsync(session);
        EnsureSelection(session, lease);
        await mail.LoginAsync(session); // offline deliveries invalidate the mail categories after role selection
        await mvps.RecoverAsync(session); // frozen MVP outcomes survive commit-before-delivery and restart
        EnsureSelection(session, lease);
        await skills.SyncAsync(session); // after 5066: skill points need PlayerBasicInfo.career
        EnsureSelection(session, lease);
        await buffs.InitializeEnergyAsync(session);
        EnsureSelection(session, lease);
        await worldCore.LoginAsync(session);
        EnsureSelection(session, lease);

        await friends.OnlineChangedAsync(role.Id, online: true);
        EnsureSelection(session, lease);

        // The world is entered by a server push of 70010 with reason Login.
        if (!await scene.EnterSceneAsync(session, role.SceneEntry, arrival, reason: 7))
        {
            EnsureSelection(session, lease);
            // Unknown scene in the database (no MapConfig row): fall back to the spawn map.
            await scene.EnterSceneAsync(session, RoleStore.SpawnSceneEntry, null, reason: 7);
            EnsureSelection(session, lease);
        }
        EnsureSelection(session, lease);
    }

    internal async Task WalkAsync(GameSession session, WalkReq walk)
    {
        if (session.Role == null || walk.Pos == null || session.SceneLoading || session.Dead || buffs.HasForbidden(session, 3)) return;
        if (!await scene.AcceptWalkAsync(session, walk)) return;
        session.PreviousPosition = session.Position;
        session.Position = walk.Pos;
        session.Face = walk.Face;
        session.Walking = !walk.BStop && walk.SpeedRate > 0;
        positions.MarkDirty(session, urgent: walk.BStop); // C/D 18: written lazily, never on the loop
        await world.MoveAsync(session, walk);
        await gathering.PlayerMovedAsync(session);
        combat.PlayerMoved(session);
        await emoji.PlayerMovedAsync(session);
        await dungeons.PlayerMovedAsync(session); // scripted scene regions and points
    }

    /// <summary>The entity a remote client renders for this player (same shape as ENTITYSELF).</summary>
    public static EntityBase PlayerEntity(GameSession session) => SelfEntity(session);

    private void SavePosition(GameSession session)
    {
        // A private floor is not a persistent world location; entry saved the outside return position.
        if (session.Role is not { } role || session.InstanceId != 0) return;
        var p = session.Position;
        roles.SavePosition(role.Id, session.SceneEntry, p.X, p.Y, p.Z, session.Face);
        session.PositionSavedUtc = DateTime.UtcNow;
        positions.Persisted(session); // a durable barrier: no older lazy capture may follow it
    }

    /// <summary>
    /// SETTING_SAVE_SETTING_INFO carries only the changed parts; merge them into the stored DBSettingInfo
    /// (map entries overwrite by key, sub-messages merge field by field).
    /// </summary>
    internal async Task SaveSettingAsync(GameSession session, SaveSettingReq req, ReadOnlyMemory<byte> wire)
    {
        if (session.Role is { } role && req.DbSettingInfo != null)
        {
            var stored = session.Settings ??= LoadSettings(role.Id);
            // LC_DAL_OnHook._onSaveOnHookSetting sends only edited keys. Merge the wire submessage:
            // parsing into the existing message preserves explicitly encoded false and leaves absent siblings alone.
            // MergeFrom(a parsed proto3 object) would lose that presence information.
            using (var input = new CodedInputStream(wire.ToArray()))
                for (uint tag; (tag = input.ReadTag()) != 0;)
                    if (tag == 18) stored.MergeFrom(input.ReadBytes()); // SaveSettingReq.dbSettingInfo, field 2
                    else input.SkipLastField();
            if (req.Type.Contains(SettingType.WildAfk) && stored.WildAFKInfo != null)
            {
                // AFK accounting follows this preference: durable now, together with its reconciliation.
                roles.SaveSettings(role.Id, stored.ToByteArray());
                settings.Persisted(session);
                await afk.SettingsChangedAsync(session, stored.WildAFKInfo);
                await session.SendGameAsync(MsgId.AutoSyncSettings, SettingsSync(stored));
            }
            else settings.MarkDirty(session, urgent: false);
        }
        await session.SendGameAsync(MsgId.SaveSetting, new SaveSettingResp { Result = 0 });
    }

    private DBSettingInfo LoadSettings(long roleId)
    {
        var settings = roles.LoadSettings(roleId) is { } bytes ? DBSettingInfo.Parser.ParseFrom(bytes) : new DBSettingInfo();
        settings.WildAFKInfo ??= new SettingWildAFKInfo(); // OnHook's setting editor requires a real object.
        return settings;
    }

    /// <summary>Full-state increment (syncAllMark): each field N of DBSettingInfo travels as field N*10 { all = value }.</summary>
    private static DBSettingInfoIncrement SettingsSync(DBSettingInfo s)
    {
        var inc = new DBSettingInfoIncrement { Version = 1, SyncAllMark = true };
        if (s.GuildSettingInfo != null) inc.GuildSettingInfo = new() { All = s.GuildSettingInfo };
        if (s.ShowDetailInfo != null) inc.ShowDetailInfo = new() { All = s.ShowDetailInfo };
        if (s.WildAFKInfo != null) inc.WildAFKInfo = new() { All = s.WildAFKInfo };
        foreach (var (k, v) in s.ClientSetting) inc.ClientSetting[k] = new() { All = v };
        foreach (var (k, v) in s.CommonSetting) inc.CommonSetting[k] = new() { All = v };
        return inc;
    }

    private static EntityBase SelfEntity(GameSession session)
    {
        var (role, position, face) = (session.Role!, session.Position, session.Face);
        var entity = new EntityBase
        {
            State = session.Dead ? EntityState.Dead : EntityState.Alive,
            PlayerLifeStatus = (EPlayerLifeStatus)(session.Dead ? 1 : 0),
            WatchState = (WatchState)(session.WorldState.Watching ? 1 : 0),
            Uuid = (ulong)role.Id,
            EntityTemplateID = (ulong)role.Career,
            EntityType = 3, // UT_Player
            Gender = role.Sex,
            Name = role.Name,
            EntityCamp = session.EventFaction > 0 ? session.EventFaction : 1,
            Face = face,
            MovementInfo = new MovementInfo
            {
                Pos = position.Clone(),
                Face = face,
                ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
            NotifyType = EntityBase.Types.NotifyType.All,
        };
        entity.CurUseAppearances.Add(session.Wardrobe?.CurUseAppearances.ToDictionary(p => p.Key, p => p.Value) ?? RoleProto.Appearances(role));
        entity.CurRider = MountService.Rider(session);
        // AttributeConfig ids: STR..LUK 1-6, derived 101-116, HP/SP 2000/2001, level 2002 (CharacterService.Refresh).
        var properties = session.EntityProperties.Length > 0
            ? session.EntityProperties
            : [(101, 100), (2000, 100), (102, 50), (2001, 50), (AttrSpeed, session.Speed), (AttrBaseAtkSpeed, 100)];
        foreach (var (idx, value) in properties)
        {
            entity.Property.Add(new AoIProperty { Idx = idx, Data = value, BaseData = value });
        }
        entity.EquipInfo.Add(session.EquipLook);
        BuffService.PopulateEntity(session, entity);
        EmojiService.PopulateEntity(session, entity);
        CareerSpecialService.PopulateEntity(session, entity);
        entity.CustomDatas.Add(session.WorldState.CustomData.Values);
        return entity;
    }

    internal ulong NowMs() => (ulong)clock.GetUtcNow().ToUnixTimeMilliseconds();
}
