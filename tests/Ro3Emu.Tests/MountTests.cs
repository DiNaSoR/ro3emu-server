using Google.Protobuf;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>MG_Mount ownership, transaction, riding properties, AOI, expiry and etchant plans over the wire.</summary>
public sealed class MountTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3emu-mount-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);
    private WebApplication _app = null!;
    private string _http = "";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    public async Task InitializeAsync()
    {
        var (h, m, g) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o => { o.DataDir = _dir; o.GameDataDir = Path.Combine(RepoRoot(), "data"); o.HttpPort = h; o.MicroGatePort = m; o.GameGatePort = g; }, _clock);
        await _app.StartAsync(); _http = $"http://127.0.0.1:{h}";
        // These tests drive the manual 31323 use of the _QuickUse [3, 0] mount and look vouchers.
        _app.Services.GetRequiredService<ItemAutoUseService>().Paused = true;
    }
    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var b = new BotClient(_http, File.ReadAllText(Path.Combine(_dir, "gate_rsa_public.pem")));
        await b.LoginAsync(name.ToLowerInvariant(), "pw"); await b.EnterGateAsync();
        long id = create ? (await b.CreateRoleAsync(name)).RoleBaseInfo.Id : b.Roles.Single().Id;
        await b.EnterWorldAsync(id); return b;
    }
    private static async Task<T> Next<T>(BotClient b, uint id, MessageParser<T> p, Func<T, bool>? match = null) where T : IMessage<T>
    { var e = await b.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(p.ParseFrom(e.Body))), Wait); return p.ParseFrom(e.Body); }
    private async Task Gm(string account, string command)
    { using var h = new HttpClient(); (await h.PostAsync($"{_http}/ro3emu/gm?account={account}", new StringContent(command))).EnsureSuccessStatusCode(); }
    private GameSession Session(string account) => _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == account && s.Role != null);
    private async Task UnlockMount(BotClient b, string account, int item)
    {
        await Gm(account, "!lvl 14"); await Gm(account, $"!item {item} 1");
        long guid = Session(account).Items.Single(i => i.Entry == item).Guid;
        await b.SendGameAsync(31323, new ItemUseReq { ItemGuid = guid, ItemCount = 1 });
        Assert.Equal(0, (await Next(b, 31323, ItemUseResp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task OwnedSelectedAndRiddenAreSeparateAndReplicatedToViewersAndRelog()
    {
        await using (var a = await InWorld("Alice"))
        await using (var b = await InWorld("Bob"))
        {
            var initial = await Next(a, 5012, DBMountsInfoIncrement.Parser);
            Assert.True(initial.SyncAllMark); Assert.Empty(initial.Mounts);
            await UnlockMount(a, "alice", 1501013);
            Assert.Equal(60030010, (await Next(a, 32747, PlayerMountUnLockNtf.Parser)).RiderId);
            await a.SendGameAsync(32743, new PlayerMountsSetCfgReq { MountId = 60030010 }); // Lua omits tp.
            Assert.Equal(1, (await Next(a, 32743, PlayerMountsSetCfgResp.Parser)).ChangeTp);
            Assert.Equal(0, MountService.Rider(Session("alice")));
            Assert.Equal(700, Session("alice").Speed);
            await a.SendGameAsync(32745, new PlayerRiderMountReq { Tp = (eRiderType)1 });
            Assert.Equal(0, (await Next(a, 32745, PlayerRiderMountResp.Parser)).Result);
            Assert.Equal(1050, Session("alice").Speed);
            foreach (var bot in new[] { a, b })
            {
                var look = await Next(bot, 31049, EntityBase.Parser, p => p.NotifyType == EntityBase.Types.NotifyType.Rider && p.CurRider == 60030010);
                Assert.Equal(a.Self!.Uuid, look.Uuid);
            }
        }
        await using (var a = await InWorld("Alice", false))
        {
            var info = await Next(a, 5012, DBMountsInfoIncrement.Parser);
            Assert.True(info.MountIds[1].All.IsRiding); Assert.Equal(60030010, a.Self!.CurRider);
            await a.SendGameAsync(32745, new PlayerRiderMountReq { Tp = (eRiderType)0 });
            Assert.Equal(0, (await Next(a, 32745, PlayerRiderMountResp.Parser)).Result);
            Assert.Equal(700, Session("alice").Speed);
            Assert.Equal(0, MountService.Rider(Session("alice")));
        }
    }

    [ClientDataFact]
    public async Task AttackingWhileRidingDismountsBeforeTheSwingStarts()
    {
        await using var a = await InWorld("Alice");
        var units = await Next(a, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, v => v.Bases.Any(b => b.EntityType == 1));
        await UnlockMount(a, "alice", 1501013);
        await a.SendGameAsync(32743, new PlayerMountsSetCfgReq { MountId = 60030010 });
        await Next(a, 32743, PlayerMountsSetCfgResp.Parser);
        await a.SendGameAsync(32745, new PlayerRiderMountReq { Tp = (eRiderType)1 });
        Assert.Equal(0, (await Next(a, 32745, PlayerRiderMountResp.Parser)).Result);
        Assert.Equal(60030010, MountService.Rider(Session("alice")));

        var self = a.Self!;
        var monster = units.Bases.Where(b => b.EntityType == 1).OrderBy(b =>
            MathF.Pow(b.MovementInfo.Pos.X - self.MovementInfo.Pos.X, 2) + MathF.Pow(b.MovementInfo.Pos.Z - self.MovementInfo.Pos.Z, 2)).First();
        await Gm("alice", $"!jump {monster.MovementInfo.Pos.X:0.0} {monster.MovementInfo.Pos.Z + 1:0.0}".Replace(',', '.'));
        await a.SendGameAsync(CombatService.LockEnemy, new LockEnemyReq { EnemyId = (long)monster.Uuid });
        await a.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = (long)monster.Uuid });
        Assert.Equal(0, (await Next(a, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(a, CombatService.UseInSkill, UseInSkillInfo.Parser, u => u.SrcID == (long)self.Uuid);

        // LC_State_Unit_Skill holds a skill while the rider is on a system mount and drops it once its time has passed:
        // the dismount must reach the client before the cast reply and the swing.
        var events = a.Inbox.Snapshot().ToList();
        int dismount = events.FindIndex(e => e.IsGame(31049) && EntityBase.Parser.ParseFrom(e.Body) is var b
            && b.Uuid == self.Uuid && b.NotifyType == EntityBase.Types.NotifyType.Rider && b.CurRider == 0);
        int reply = events.FindLastIndex(e => e.IsGame(CombatService.UseSkill));
        Assert.InRange(dismount, 0, reply - 1);
        Assert.Equal(0, MountService.Rider(Session("alice")));
        Assert.Equal(60030010, Session("alice").Mounts!.MountIds[1].MountId);
    }

    [ClientDataFact]
    public async Task RefusalsDoNotConsumeOrGrantAndInvalidRideEnumsFail()
    {
        await using var a = await InWorld("Alice");
        await a.SendGameAsync(32743, new PlayerMountsSetCfgReq { MountId = 60030010 });
        Assert.Equal(ErrorCodes.FunctionLocked, (await Next(a, 32743, PlayerMountsSetCfgResp.Parser)).Result);
        await Gm("alice", "!lvl 14");
        await a.SendGameAsync(32743, new PlayerMountsSetCfgReq { MountId = 60030010 });
        Assert.Equal(ErrorCodes.MountNotOwned, (await Next(a, 32743, PlayerMountsSetCfgResp.Parser)).Result);
        await a.SendGameAsync(32745, new PlayerRiderMountReq { Tp = (eRiderType)9 });
        Assert.Equal(ErrorCodes.Parameter, (await Next(a, 32745, PlayerRiderMountResp.Parser)).Result);
        await UnlockMount(a, "alice", 1501013);
        await Gm("alice", "!item 1501013 1");
        long guid = Session("alice").Items.Single(i => i.Entry == 1501013).Guid;
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() => { Session("alice").ItemCds.Clear(); return Task.CompletedTask; });
        await a.SendGameAsync(31323, new ItemUseReq { ItemGuid = guid, ItemCount = 1 });
        Assert.Equal(ErrorCodes.MountAlreadyOwned, (await Next(a, 31323, ItemUseResp.Parser)).Result);
        Assert.Contains(Session("alice").Items, i => i.Guid == guid);
    }

    [ClientDataFact]
    public async Task ExpiryDismountsAndPublishesDeletionAndBaseSpeed()
    {
        await using var a = await InWorld("Alice");
        await UnlockMount(a, "alice", 1501023);
        await a.SendGameAsync(32743, new PlayerMountsSetCfgReq { MountId = 60040010 }); await Next(a, 32743, PlayerMountsSetCfgResp.Parser);
        await a.SendGameAsync(32745, new PlayerRiderMountReq { Tp = (eRiderType)1 }); await Next(a, 32745, PlayerRiderMountResp.Parser);
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() => { Session("alice").Mounts!.Mounts[60040010].OverdueTimeMs = _clock.GetUtcNow().ToUnixTimeMilliseconds() - 1; return _app.Services.GetRequiredService<MountService>().ReconcileAsync(Session("alice")); });
        Assert.False(Session("alice").Mounts!.Mounts.ContainsKey(60040010));
        Assert.Equal(0, MountService.Rider(Session("alice"))); Assert.Equal(700, Session("alice").Speed);
        await Next(a, 5012, DBMountsInfoIncrement.Parser, p => p.Mounts.TryGetValue(60040010, out var m) && m.Del);
    }

    [ClientDataFact]
    public async Task FractionalDeadlineDismountsAtBoundaryAndPersistsTheSameReconcileResult()
    {
        await using var a = await InWorld("Alice");
        await using var b = await InWorld("Bob");
        await UnlockMount(a, "alice", 1501023);
        await a.SendGameAsync(32743, new PlayerMountsSetCfgReq { MountId = 60040010 });
        Assert.Equal(0, (await Next(a, 32743, PlayerMountsSetCfgResp.Parser)).Result);
        await a.SendGameAsync(32745, new PlayerRiderMountReq { Tp = (eRiderType)1 });
        Assert.Equal(0, (await Next(a, 32745, PlayerRiderMountResp.Parser)).Result);
        foreach (var viewer in new[] { a, b })
        {
            await Next(viewer, CharacterService.EntityBaseChange, EntityBase.Parser,
                p => p.Uuid == a.Self!.Uuid && p.NotifyType == EntityBase.Types.NotifyType.Rider && p.CurRider == 0);
            await Next(viewer, CharacterService.EntityBaseChange, EntityBase.Parser,
                p => p.Uuid == a.Self!.Uuid && p.NotifyType == EntityBase.Types.NotifyType.Rider && p.CurRider == 60040010);
        }

        var loop = _app.Services.GetRequiredService<GameLoop>();
        var roles = _app.Services.GetRequiredService<RoleStore>();
        var mount = _app.Services.GetRequiredService<MountService>();
        var deadlines = _app.Services.GetRequiredService<Deadlines>();
        var due = DateTimeOffset.FromUnixTimeMilliseconds(_clock.GetUtcNow().ToUnixTimeMilliseconds() + 1250);
        await loop.InvokeAsync(() =>
        {
            var session = Session("alice");
            var stored = JsonSerializer.Deserialize<MountService.SavedState>(roles.LoadMounts(session.Role!.Id)!)!;
            var info = session.Mounts!.Clone();
            info.Mounts[60040010].OverdueTimeMs = due.ToUnixTimeMilliseconds();
            stored.Info = info.ToByteArray();
            roles.SaveMounts(session.Role.Id, JsonSerializer.SerializeToUtf8Bytes(stored));
            mount.Load(session); // the normal role-load path arms the new earliest expiry
            Assert.Equal(60040010, MountService.Rider(session));
            return Task.CompletedTask;
        });

        await loop.InvokeAsync(() => { _clock.Set(due.AddTicks(-1)); deadlines.Tick(default); return Task.CompletedTask; });
        await loop.InvokeAsync(() => Task.CompletedTask);
        Assert.True(Session("alice").Mounts!.Mounts.ContainsKey(60040010));
        Assert.Equal(60040010, MountService.Rider(Session("alice")));

        await loop.InvokeAsync(() => { _clock.Set(due); deadlines.Tick(default); return Task.CompletedTask; });
        await Next(a, 5012, DBMountsInfoIncrement.Parser, p => p.Mounts.TryGetValue(60040010, out var m) && m.Del);
        foreach (var viewer in new[] { a, b })
            await Next(viewer, CharacterService.EntityBaseChange, EntityBase.Parser,
                p => p.Uuid == a.Self!.Uuid && p.NotifyType == EntityBase.Types.NotifyType.Rider && p.CurRider == 0);
        var sessionAfter = Session("alice");
        Assert.False(sessionAfter.Mounts!.Mounts.ContainsKey(60040010));
        Assert.Equal(0, MountService.Rider(sessionAfter));
        Assert.Equal(700, sessionAfter.Speed);
        var saved = JsonSerializer.Deserialize<MountService.SavedState>(roles.LoadMounts(sessionAfter.Role!.Id)!)!;
        Assert.False(DBMountsInfo.Parser.ParseFrom(saved.Info).Mounts.ContainsKey(60040010));
        await loop.InvokeAsync(() => mount.ReconcileAsync(sessionAfter)); // the former polling path is idempotent
        Assert.Equal(saved.Info, JsonSerializer.Deserialize<MountService.SavedState>(roles.LoadMounts(sessionAfter.Role.Id)!)!.Info);
    }

    [ClientDataFact]
    public async Task FailedExpiryPersistenceRetriesOnlyTheCurrentDeadline()
    {
        await using var a = await InWorld("Alice");
        await UnlockMount(a, "alice", 1501023);
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var roles = _app.Services.GetRequiredService<RoleStore>();
        var mount = _app.Services.GetRequiredService<MountService>();
        var deadlines = _app.Services.GetRequiredService<Deadlines>();
        var due = DateTimeOffset.FromUnixTimeMilliseconds(_clock.GetUtcNow().ToUnixTimeMilliseconds() + 1250);
        var expiryCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await loop.InvokeAsync(() =>
        {
            var session = Session("alice");
            var stored = JsonSerializer.Deserialize<MountService.SavedState>(roles.LoadMounts(session.Role!.Id)!)!;
            var info = session.Mounts!.Clone();
            info.Mounts[60040010].OverdueTimeMs = due.ToUnixTimeMilliseconds();
            stored.Info = info.ToByteArray();
            roles.SaveMounts(session.Role.Id, JsonSerializer.SerializeToUtf8Bytes(stored));
            mount.Load(session);
            FixtureSql.Execute(_app.Services.GetRequiredService<AccountStore>(),
                "CREATE TRIGGER reject_mount_expiry BEFORE UPDATE OF mounts ON role BEGIN SELECT RAISE(ABORT, 'retry test'); END");
            var scheduled = deadlines.ScheduledLease(session, "mount-expiry");
            Assert.True(scheduled.HasValue);
            deadlines.CallbackCompletedForTest = completed =>
            {
                if (completed != scheduled.Value) return;
                deadlines.CallbackCompletedForTest = null;
                expiryCompleted.TrySetResult();
            };
            _clock.Set(due);
            deadlines.Tick(default);
            return Task.CompletedTask;
        });
        await expiryCompleted.Task.WaitAsync(Wait);
        Assert.True(Session("alice").Mounts!.Mounts.ContainsKey(60040010));
        Assert.Equal(due.AddSeconds(1), await loop.InvokeAsync(() =>
            Task.FromResult(deadlines.ScheduledDue(Session("alice"), "mount-expiry"))));

        await loop.InvokeAsync(() =>
        {
            FixtureSql.Execute(_app.Services.GetRequiredService<AccountStore>(), "DROP TRIGGER reject_mount_expiry");
            _clock.Set(due.AddSeconds(1));
            deadlines.Tick(default);
            return Task.CompletedTask;
        });
        await Next(a, 5012, DBMountsInfoIncrement.Parser, p => p.Mounts.TryGetValue(60040010, out var m) && m.Del);
        Assert.False(Session("alice").Mounts!.Mounts.ContainsKey(60040010));
    }

    [ClientDataFact]
    public async Task EtchantRequiresPaymentAndOnlyServerPreviewCanBeSaved()
    {
        await using var a = await InWorld("Alice");
        await UnlockMount(a, "alice", 1501004);
        var req = new MountEtchantRefreshReq { MountId = 60060010, PlanId = 1, Part = 1 };
        await a.SendGameAsync(32748, req);
        Assert.NotEqual(0, (await Next(a, 32748, MountEtchantRefreshResp.Parser)).Result);
        await Gm("alice", "!item 2 2000");
        await a.SendGameAsync(32748, req); var rolled = await Next(a, 32748, MountEtchantRefreshResp.Parser);
        Assert.Equal(0, rolled.Result); Assert.Equal(req, rolled.Req); Assert.True(rolled.CfgId > 0);
        Assert.Equal(1000, Session("alice").Progress!.Currency[2]);
        await a.SendGameAsync(32749, new MountEtchantSaveReq { MountId = req.MountId, PlanId = 1, Part = 1, CfgId = int.MaxValue });
        Assert.NotEqual(0, (await Next(a, 32749, MountEtchantSaveResp.Parser)).Result);
        await a.SendGameAsync(32749, new MountEtchantSaveReq { MountId = req.MountId, PlanId = 1, Part = 1, CfgId = rolled.CfgId });
        Assert.Equal(0, (await Next(a, 32749, MountEtchantSaveResp.Parser)).Result);
        await a.SendGameAsync(32750, new MountSetEtchantPlanReq { MountId = req.MountId, PlanId = 1 });
        Assert.Equal(0, (await Next(a, 32750, MountSetEtchantPlanResp.Parser)).Result);
        Assert.Equal(rolled.CfgId, Session("alice").Mounts!.Mounts[req.MountId].EtChantInfo[1].EtChantData[1]);
    }

    [ClientDataFact]
    public async Task QuestRideDeliveryPersistsAcrossRelogAndRecycleRemovesOnlyTemporaryRide()
    {
        var roles = _app.Services.GetRequiredService<RoleStore>();
        long role;
        await using (var a = await InWorld("Alice"))
        {
            role = Session("alice").Role!.Id;
            // This real task auto-delivers on its base-level objective and executes action 412.
            await Gm("alice", "!quest accept 103012");
            Assert.Equal(TaskState.Delivery, Session("alice").Tasks!.Infos[103012].Status);
            Assert.Equal(60040033, MountService.Rider(Session("alice")));
            Assert.Empty(Session("alice").Mounts!.Mounts);
            Assert.Equal(60040033, JsonSerializer.Deserialize<MountService.SavedState>(roles.LoadMounts(role)!)!.QuestMount);
            await Next(a, 31049, EntityBase.Parser, p => p.NotifyType == EntityBase.Types.NotifyType.Rider && p.CurRider == 60040033);
        }
        await using (var a = await InWorld("Alice", false))
        {
            Assert.Equal(60040033, a.Self!.CurRider);
            // Completing the configured dialogue objective uses the regular settlement and action 54.
            await Gm("alice", "!quest finish 103014");
            Assert.Equal(TaskState.Delivery, Session("alice").Tasks!.Infos[103014].Status);
            Assert.Equal(0, MountService.Rider(Session("alice")));
            Assert.Equal(0, JsonSerializer.Deserialize<MountService.SavedState>(roles.LoadMounts(role)!)!.QuestMount);
            Assert.Equal(700, Session("alice").Speed);
            Assert.Empty(Session("alice").Mounts!.Mounts);
            await Next(a, 31049, EntityBase.Parser, p => p.NotifyType == EntityBase.Types.NotifyType.Rider && p.CurRider == 0);
        }
    }

    [ClientDataTheory]
    [InlineData(1501013, 60030010, true)]
    [InlineData(3025701, 70050010, false)]
    public async Task CollectionShortcutUsesCanonicalReplyCooldownAndSingleConsumption(int entry, int unlockedId, bool mount)
    {
        await using var player = await InWorld("Alice");
        await Gm("alice", "!lvl 14");
        await Gm("alice", $"!item {entry} 2");
        var session = Session("alice");
        Assert.Equal(2, session.Items.Where(i => i.Entry == entry).Sum(i => i.Stack));
        var voucher = session.Items.Where(i => i.Entry == entry).OrderBy(i => i.Guid).First();
        long guid = voucher.Guid;
        int originalStack = voucher.Stack;
        var character = _app.Services.GetRequiredService<CharacterService>();
        int uses = 0;
        character.ItemUsed.Add((s, item, count) => { if (s == session && item == entry) uses += count; });

        await player.SendGameAsync(CharacterService.ShortcutBarSave, new ShortcutBarSaveReq { SlotIndex = 0, ItemGuid = guid });
        Assert.Equal(0, (await Next(player, CharacterService.ShortcutBarSave, ShortcutBarSaveRes.Parser)).Result);
        int start = player.Inbox.Snapshot().Count;
        long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await player.SendGameAsync(CharacterService.ShortcutBarUse, new ShortcutBarUseReq { SlotIndex = 0 });
        var reply = await Next(player, CharacterService.ShortcutBarUse, ShortcutBarUseRes.Parser);
        Assert.Equal((0, 0), (reply.Result, reply.SlotIndex));
        var cooldown = Assert.Single((await Next(player, CharacterService.ItemsCdNotify, ItemsCDNotify.Parser)).Items);
        Assert.Equal(entry, cooldown.ItemId);
        Assert.True(cooldown.ItemCd > before);
        Assert.Equal(cooldown.ItemCd, session.ItemCds[entry]);
        var saved = _app.Services.GetRequiredService<RoleStore>().LoadBagState(session.Role!.Id);
        Assert.NotNull(saved);
        Assert.Equal(cooldown.ItemCd, DBItemsInfo.Parser.ParseFrom(saved).ItemCds.Data[entry]);
        Assert.Equal(1, session.Items.Where(i => i.Entry == entry).Sum(i => i.Stack));
        if (originalStack == 1) Assert.DoesNotContain(session.Items, i => i.Guid == guid);
        else Assert.Equal(originalStack - 1, session.Items.Single(i => i.Guid == guid).Stack);
        Assert.Equal(1, uses);
        if (mount) Assert.True(session.Mounts!.Mounts.ContainsKey(unlockedId));
        else Assert.True(session.Wardrobe!.AllLightUpAppearances.ContainsKey(unlockedId));
        Assert.DoesNotContain(player.Inbox.Snapshot().Skip(start), e => e.IsGame(CharacterService.ItemUse));
        Assert.Single(player.Inbox.Snapshot().Skip(start), e => e.IsGame(CharacterService.ItemsCdNotify));
    }

    [ClientDataFact]
    public async Task FailedCollectionCommitRollsBackConsumableAndOwnership()
    {
        await using var a = await InWorld("Alice");
        await Gm("alice", "!lvl 14"); await Gm("alice", "!item 1501013 1");
        var session = Session("alice"); var roles = _app.Services.GetRequiredService<RoleStore>();
        long guid = session.Items.Single(i => i.Entry == 1501013).Guid;
        byte[] before = roles.LoadMounts(session.Role!.Id)!;
        byte[]? beforeBag = roles.LoadBagState(session.Role.Id);
        FixtureSql.Execute(_app.Services.GetRequiredService<AccountStore>(),
            "CREATE TRIGGER reject_mount_collection BEFORE UPDATE OF mounts ON role BEGIN SELECT RAISE(ABORT, 'mount test rollback'); END");
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
            _app.Services.GetRequiredService<CharacterService>().UseItemAsync(session, new ItemUseReq { ItemGuid = guid, ItemCount = 1 })));
        Assert.Contains(session.Items, i => i.Guid == guid && i.Stack == 1);
        Assert.Empty(session.Mounts!.Mounts);
        Assert.Equal(before, roles.LoadMounts(session.Role.Id));
        Assert.Equal(beforeBag, roles.LoadBagState(session.Role.Id));
        Assert.False(session.ItemCds.ContainsKey(1501013));
    }

    // Mount ownership and its WardRobeConfig type-6 appearance (WardrobeService.Mounts.cs): the wardrobe Mount tab
    // lights only from 5034, so every mount grant, expiry and login carries the matching appearance.
    private const int BigPoring = 60030010, TimedMount = 60040010, MountSlot = 6;

    private static DBWardRobeInfo StoredWardrobe(RoleStore roles, long role) => DBWardRobeInfo.Parser.ParseFrom(roles.LoadWardrobe(role)!);
    private static DBMountsInfo StoredMounts(RoleStore roles, long role) =>
        DBMountsInfo.Parser.ParseFrom(JsonSerializer.Deserialize<MountService.SavedState>(roles.LoadMounts(role)!)!.Info);

    [ClientDataFact]
    public async Task ItemUnlockLightsTheWardrobeMountEntryWithoutASecondUnlockTip()
    {
        await using var a = await InWorld("Alice");
        var initial = await Next(a, WardrobeService.WardrobeSync, DBWardRobeInfoIncrement.Parser);
        Assert.False(initial.AllLightUpAppearances.ContainsKey(BigPoring));
        int start = a.Inbox.Snapshot().Count;
        await UnlockMount(a, "alice", 1501013);
        var lit = await Next(a, WardrobeService.WardrobeSync, DBWardRobeInfoIncrement.Parser,
            p => p.AllLightUpAppearances.ContainsKey(BigPoring));
        Assert.Equal(BigPoring, lit.AllLightUpAppearances[BigPoring].All.AppearanceId);
        Assert.Equal(0, lit.AllLightUpAppearances[BigPoring].All.OverdueTimeMs);
        Assert.Equal(0, (await Next(a, MountService.Unlock, PlayerMountUnLockNtf.Parser)).EndTime);
        Assert.DoesNotContain(a.Inbox.Snapshot().Skip(start), e => e.IsGame(WardrobeService.UnlockNotice));
        var session = Session("alice"); var roles = _app.Services.GetRequiredService<RoleStore>();
        Assert.Equal(0, session.Wardrobe!.AllLightUpAppearances[BigPoring].OverdueTimeMs);
        Assert.True(StoredWardrobe(roles, session.Role!.Id).AllLightUpAppearances.ContainsKey(BigPoring));
        Assert.True(StoredMounts(roles, session.Role.Id).Mounts.ContainsKey(BigPoring));
    }

    [ClientDataFact]
    public async Task FailedWardrobeWriteRollsBackTheWholeMountUnlock()
    {
        await using var a = await InWorld("Alice");
        await Gm("alice", "!lvl 14"); await Gm("alice", "!item 1501013 1");
        var session = Session("alice"); var roles = _app.Services.GetRequiredService<RoleStore>();
        long guid = session.Items.Single(i => i.Entry == 1501013).Guid;
        byte[] mounts = roles.LoadMounts(session.Role!.Id)!, wardrobe = roles.LoadWardrobe(session.Role.Id)!;
        FixtureSql.Execute(_app.Services.GetRequiredService<AccountStore>(),
            "CREATE TRIGGER reject_mount_look BEFORE UPDATE OF wardrobe ON role BEGIN SELECT RAISE(ABORT, 'mount look rollback'); END");
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
            _app.Services.GetRequiredService<CharacterService>().UseItemAsync(session, new ItemUseReq { ItemGuid = guid, ItemCount = 1 })));
        Assert.Contains(session.Items, i => i.Guid == guid && i.Stack == 1);
        Assert.Empty(session.Mounts!.Mounts);
        Assert.False(session.Wardrobe!.AllLightUpAppearances.ContainsKey(BigPoring));
        Assert.Equal(mounts, roles.LoadMounts(session.Role.Id));
        Assert.Equal(wardrobe, roles.LoadWardrobe(session.Role.Id));
    }

    [ClientDataFact]
    public async Task TimedMountAndItsAppearanceShareOneExpiryExtendAndDropTogether()
    {
        await using var a = await InWorld("Alice");
        await UnlockMount(a, "alice", 1501023); // [201, 600 s, 60040010]
        var session = Session("alice"); var roles = _app.Services.GetRequiredService<RoleStore>();
        var loop = _app.Services.GetRequiredService<GameLoop>();
        long due = session.Mounts!.Mounts[TimedMount].OverdueTimeMs;
        Assert.True(due > _clock.GetUtcNow().ToUnixTimeMilliseconds());
        Assert.Equal(due, session.Wardrobe!.AllLightUpAppearances[TimedMount].OverdueTimeMs);
        Assert.Equal(due, (await Next(a, MountService.Unlock, PlayerMountUnLockNtf.Parser)).EndTime);

        await Gm("alice", "!item 1501023 1"); // a second timed unlock extends both from the current expiry
        await loop.InvokeAsync(() => { session.ItemCds.Clear(); return Task.CompletedTask; });
        await a.SendGameAsync(31323, new ItemUseReq { ItemGuid = session.Items.Single(i => i.Entry == 1501023).Guid, ItemCount = 1 });
        Assert.Equal(0, (await Next(a, 31323, ItemUseResp.Parser)).Result); // the first reply was consumed by UnlockMount
        due += 600_000;
        Assert.Equal(due, session.Mounts!.Mounts[TimedMount].OverdueTimeMs);
        Assert.Equal(due, session.Wardrobe!.AllLightUpAppearances[TimedMount].OverdueTimeMs);
        Assert.Equal(due, StoredWardrobe(roles, session.Role!.Id).AllLightUpAppearances[TimedMount].OverdueTimeMs);

        var wear = new WardRobeUseAppearanceReq(); wear.AppearanceIds.Add(TimedMount);
        await a.SendGameAsync(WardrobeService.Use, wear);
        Assert.Equal(0, (await Next(a, WardrobeService.Use, WardRobeUseAppearanceRes.Parser)).Result);
        Assert.Equal(TimedMount, session.Wardrobe!.CurUseAppearances[MountSlot]);

        await loop.InvokeAsync(() =>
        {
            _clock.Set(DateTimeOffset.FromUnixTimeMilliseconds(due));
            return _app.Services.GetRequiredService<MountService>().ReconcileAsync(session);
        });
        Assert.False(session.Mounts!.Mounts.ContainsKey(TimedMount));
        Assert.False(session.Wardrobe!.AllLightUpAppearances.ContainsKey(TimedMount));
        Assert.False(session.Wardrobe.CurUseAppearances.ContainsKey(MountSlot));
        await Next(a, MountService.Sync, DBMountsInfoIncrement.Parser, p => p.Mounts.TryGetValue(TimedMount, out var m) && m.Del);
        await Next(a, WardrobeService.WardrobeSync, DBWardRobeInfoIncrement.Parser,
            p => p.AllLightUpAppearances.TryGetValue(TimedMount, out var look) && look.Del);
        Assert.False(StoredMounts(roles, session.Role.Id).Mounts.ContainsKey(TimedMount));
        Assert.False(StoredWardrobe(roles, session.Role.Id).AllLightUpAppearances.ContainsKey(TimedMount));
    }

    [ClientDataFact]
    public async Task LoginRepairsMountsOwnedBeforeTheWardrobeLink()
    {
        var roles = _app.Services.GetRequiredService<RoleStore>();
        var loop = _app.Services.GetRequiredService<GameLoop>();
        long role, due = _clock.GetUtcNow().ToUnixTimeMilliseconds() + 3_600_000;
        await using (var a = await InWorld("Alice"))
        {
            var session = Session("alice"); role = session.Role!.Id;
            await loop.InvokeAsync(async () =>
            {
                // A role saved by the previous server: 5012 ownership only, nothing lit in 5034.
                var stored = JsonSerializer.Deserialize<MountService.SavedState>(roles.LoadMounts(role)!)!;
                var info = session.Mounts!.Clone();
                info.Mounts[BigPoring] = new DBMount { MountId = BigPoring, UniqueId = BigPoring, Type = 1 };
                info.Mounts[TimedMount] = new DBMount { MountId = TimedMount, UniqueId = TimedMount, Type = 1, OverdueTimeMs = due };
                stored.Info = info.ToByteArray();
                roles.SaveMounts(role, JsonSerializer.SerializeToUtf8Bytes(stored));
                Assert.False(StoredWardrobe(roles, role).AllLightUpAppearances.ContainsKey(BigPoring));
                // The role-entry order: MountService.Load, then WardrobeService.LoginAsync.
                _app.Services.GetRequiredService<MountService>().Load(session);
                await _app.Services.GetRequiredService<WardrobeService>().LoginAsync(session);
            });
            await Next(a, WardrobeService.WardrobeSync, DBWardRobeInfoIncrement.Parser,
                p => p.AllLightUpAppearances.ContainsKey(BigPoring) && p.AllLightUpAppearances.ContainsKey(TimedMount));
            Assert.Equal(0, session.Wardrobe!.AllLightUpAppearances[BigPoring].OverdueTimeMs);
            Assert.Equal(due, session.Wardrobe.AllLightUpAppearances[TimedMount].OverdueTimeMs);
        }
        var saved = StoredWardrobe(roles, role);
        Assert.Equal(0, saved.AllLightUpAppearances[BigPoring].OverdueTimeMs);
        Assert.Equal(due, saved.AllLightUpAppearances[TimedMount].OverdueTimeMs);
        await using (var a = await InWorld("Alice", false))
        {
            var initial = await Next(a, WardrobeService.WardrobeSync, DBWardRobeInfoIncrement.Parser);
            Assert.True(initial.AllLightUpAppearances.ContainsKey(BigPoring));
            Assert.Equal(due, initial.AllLightUpAppearances[TimedMount].All.OverdueTimeMs);
        }
    }

    [ClientDataFact]
    public async Task LoginOwnsTheMountOfAShopLookBoughtBeforeThePurchaseGrantedIt()
    {
        const int DisabledLook = 60040032; // Kitty Cart: WardRobeConfig type 6 with _iDisable 1, though MountsConfig lists it.
        var roles = _app.Services.GetRequiredService<RoleStore>();
        var loop = _app.Services.GetRequiredService<GameLoop>();
        long role;
        await using (var a = await InWorld("Alice"))
        {
            var session = Session("alice"); role = session.Role!.Id;
            await loop.InvokeAsync(() =>
            {
                // A role saved by the previous server: the Fashion shop lit the look in 5034 but wrote no 5012 ownership.
                var looks = session.Wardrobe!.Clone();
                looks.AllLightUpAppearances[BigPoring] = new DBAppearanceInfo { AppearanceId = BigPoring };
                looks.AllLightUpAppearances[DisabledLook] = new DBAppearanceInfo { AppearanceId = DisabledLook };
                roles.SaveWardrobe(role, looks.ToByteArray());
                _app.Services.GetRequiredService<MountService>().Load(session);
                return Task.CompletedTask;
            });
            Assert.Equal(0, session.Mounts!.Mounts[BigPoring].OverdueTimeMs);
            Assert.False(session.Mounts.Mounts.ContainsKey(DisabledLook));
            Assert.Equal(0, StoredMounts(roles, role).Mounts[BigPoring].OverdueTimeMs);
        }
        await using (var a = await InWorld("Alice", false))
        {
            Assert.True((await Next(a, MountService.Sync, DBMountsInfoIncrement.Parser)).Mounts.ContainsKey(BigPoring));
            await Gm("alice", "!lvl 14");
            await a.SendGameAsync(MountService.SetConfig, new PlayerMountsSetCfgReq { MountId = BigPoring });
            Assert.Equal(0, (await Next(a, MountService.SetConfig, PlayerMountsSetCfgResp.Parser)).Result);
            await a.SendGameAsync(MountService.Ride, new PlayerRiderMountReq { Tp = (eRiderType)1 });
            Assert.Equal(0, (await Next(a, MountService.Ride, PlayerRiderMountResp.Parser)).Result);
            Assert.Equal(BigPoring, MountService.Rider(Session("alice")));
        }
        Assert.False(StoredMounts(roles, role).Mounts.ContainsKey(DisabledLook));
    }

    [ClientDataFact]
    public async Task WardrobeSelectionOfAnUnlockedMountWearsSelectsAndRidesIt()
    {
        await using var a = await InWorld("Alice");
        await Gm("alice", "!lvl 14");
        var wear = new WardRobeUseAppearanceReq(); wear.AppearanceIds.Add(BigPoring);
        await a.SendGameAsync(WardrobeService.Use, wear); // the reported bug: locked until 5034 lights it
        Assert.NotEqual(0, (await Next(a, WardrobeService.Use, WardRobeUseAppearanceRes.Parser)).Result);
        await UnlockMount(a, "alice", 1501013);
        // UI_Sys_WardrobePanelCtrl._doSaveWardrobe: 31183 with the worn list, then SendTakeOnSystemMount(id).
        await a.SendGameAsync(WardrobeService.Use, wear);
        Assert.Equal(0, (await Next(a, WardrobeService.Use, WardRobeUseAppearanceRes.Parser)).Result);
        await a.SendGameAsync(MountService.SetConfig, new PlayerMountsSetCfgReq { MountId = BigPoring });
        Assert.Equal(0, (await Next(a, MountService.SetConfig, PlayerMountsSetCfgResp.Parser)).Result);
        await a.SendGameAsync(MountService.Ride, new PlayerRiderMountReq { Tp = (eRiderType)1 });
        Assert.Equal(0, (await Next(a, MountService.Ride, PlayerRiderMountResp.Parser)).Result);
        var session = Session("alice");
        Assert.Equal(BigPoring, session.Wardrobe!.CurUseAppearances[MountSlot]);
        Assert.Equal(BigPoring, MountService.Rider(session));
        Assert.Equal(1050, session.Speed);
    }

    private static string RepoRoot()
    { for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent) if (File.Exists(Path.Combine(d.FullName, "Ro3Emu.slnx"))) return d.FullName; throw new DirectoryNotFoundException(); }

    // The Starshatter Dragon (Dragon Whistle 1501004) flies by its _kFlySwitchSkills: Take Flight 3112101 on the ground,
    // landing 3112201 in the air. The client sends no 31083; it flies once the status word carries eObjectStateFly (43).
    [ClientDataFact]
    public async Task StarshatterDragonTakesOffAndLandsByItsMountSkills()
    {
        const int Dragon = 60060010, TakeFlight = 3112101, Land = 3112201;
        const long FlyBit = 1L << 42;
        await using var a = await InWorld("Alice");
        var loop = _app.Services.GetRequiredService<GameLoop>();
        await UnlockMount(a, "alice", 1501004);
        var s = Session("alice");
        long self = s.Role!.Id;
        Task Cast(int skill) => a.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skill, TargetId = self });

        await Cast(TakeFlight); // not riding yet: not a skill Alice has
        Assert.NotEqual(0, (await Next(a, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.Equal(0, s.MountSkillShown);

        await a.SendGameAsync(32743, new PlayerMountsSetCfgReq { MountId = Dragon });
        await Next(a, 32743, PlayerMountsSetCfgResp.Parser);
        await a.SendGameAsync(32745, new PlayerRiderMountReq { Tp = (eRiderType)1 });
        Assert.Equal(0, (await Next(a, 32745, PlayerRiderMountResp.Parser)).Result);
        Assert.Equal(TakeFlight, s.MountSkillShown);
        Assert.Contains(s.Buffs, b => b.ConfigId == TakeFlight); // the ground buff, MountsConfig _kBindBuff

        await Cast(TakeFlight);
        Assert.Equal(0, (await Next(a, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var up = await Next(a, CombatService.AoiAttrChange, AOIAttrChange.Parser, m => m.EntityUuid == (ulong)self && m.StateInfo != null
            && (m.StateInfo.Data.Values[0] & (ulong)FlyBit) != 0);
        Assert.True(s.WorldState.Flying);
        Assert.Equal(Dragon, MountService.Rider(s)); // the take-off keeps Alice on the dragon
        Assert.Contains(s.Buffs, b => b.ConfigId == Land);
        Assert.DoesNotContain(s.Buffs, b => b.ConfigId == TakeFlight);
        Assert.Equal(Land, s.MountSkillShown);

        // In the air 31084 moves her, climbing as the take-off skill lifts her (20 m), with no echo to herself.
        var ahead = s.Position.Clone(); ahead.X += 1; ahead.Y += 15;
        int seen = a.Inbox.Snapshot().Count;
        await a.SendGameAsync(WorldCoreService.FlyMove, new FlyMoveReq { Pos = ahead, Dir = new Vec3 { X = 1 }, Face = 90 });
        await Task.Delay(300);
        Assert.Equal(ahead.X, s.Position.X);
        Assert.Equal(ahead.Y, s.Position.Y);
        Assert.DoesNotContain(a.Inbox.Snapshot().Skip(seen), e => e.IsGame(GameAoiSink.MovementChange)
            && MovementChangeNtf.Parser.ParseFrom(e.Body).Uid == self);
        Assert.DoesNotContain(a.Inbox.Snapshot().Skip(seen), e => e.IsGame(99999) && APIErrorMsg.Parser.ParseFrom(e.Body).OriginMsgId == WorldCoreService.FlyMove);

        await loop.InvokeAsync(() =>
        {
            if (s.SkillCast is { } cast) { cast.Cancelled = true; s.SkillCast = null; }
            s.SkillCds.Clear(); s.SkillModeCds.Clear(); return Task.CompletedTask;
        });
        await Cast(Land);
        Assert.Equal(0, (await Next(a, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(a, CombatService.AoiAttrChange, AOIAttrChange.Parser, m => m.EntityUuid == (ulong)self && m.StateInfo != null
            && (m.StateInfo.Data.Values[0] & (ulong)FlyBit) == 0);
        Assert.False(s.WorldState.Flying);
        Assert.Contains(s.Buffs, b => b.ConfigId == TakeFlight);
        Assert.DoesNotContain(s.Buffs, b => b.ConfigId == Land);
        Assert.Equal(TakeFlight, s.MountSkillShown);

        // Getting off in the air lands at once.
        await loop.InvokeAsync(() =>
        {
            if (s.SkillCast is { } cast) { cast.Cancelled = true; s.SkillCast = null; }
            s.SkillCds.Clear(); s.SkillModeCds.Clear(); return Task.CompletedTask;
        });
        await Cast(TakeFlight);
        Assert.Equal(0, (await Next(a, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.True(s.WorldState.Flying);
        await a.SendGameAsync(32745, new PlayerRiderMountReq { Tp = (eRiderType)0 });
        Assert.Equal(0, (await Next(a, 32745, PlayerRiderMountResp.Parser)).Result);
        await Next(a, WorldCoreService.FixLand, FixLandNtf.Parser);
        Assert.False(s.WorldState.Flying);
        Assert.Equal(0, s.StatusBits & FlyBit);
        Assert.DoesNotContain(s.Buffs, b => b.ConfigId is TakeFlight or Land);
        Assert.Equal(0, s.MountSkillShown);
    }

    // Over a cell with no walkable layer (water, a cliff face) the dragon keeps flying under the 50 m band counted from that
    // layer, cannot cast its landing there, and getting off there puts the rider back where it took off. Off the grid no
    // move is taken.
    [ClientDataFact]
    public async Task StarshatterDragonFliesOverUnwalkableGroundButLandsOnlyOnWalkableGround()
    {
        const int Dragon = 60060010, TakeFlight = 3112101, Land = 3112201;
        await using var a = await InWorld("Alice");
        var loop = _app.Services.GetRequiredService<GameLoop>();
        await UnlockMount(a, "alice", 1501004);
        var s = Session("alice");
        long self = s.Role!.Id;
        Task Cast(int skill) => a.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skill, TargetId = self });
        Task Ready() => loop.InvokeAsync(() =>
        {
            if (s.SkillCast is { } cast) { cast.Cancelled = true; s.SkillCast = null; }
            s.SkillCds.Clear(); s.SkillModeCds.Clear(); return Task.CompletedTask;
        });
        await a.SendGameAsync(32743, new PlayerMountsSetCfgReq { MountId = Dragon });
        await Next(a, 32743, PlayerMountsSetCfgResp.Parser);
        await a.SendGameAsync(32745, new PlayerRiderMountReq { Tp = (eRiderType)1 });
        Assert.Equal(0, (await Next(a, 32745, PlayerRiderMountResp.Parser)).Result);
        var takeOff = s.Position.Clone();
        await Cast(TakeFlight);
        Assert.Equal(0, (await Next(a, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.True(s.WorldState.Flying);

        var nav = _app.Services.GetRequiredService<WorldData>().Map(s.SceneEntry)!.Nav!;
        var (x, z, floor) = Enumerable.Range(0, nav.Width).SelectMany(cx => Enumerable.Range(0, nav.Height).Select(cz => (cx, cz)))
            .Select(c => (X: c.cx + 0.5f, Z: c.cz + 0.5f, Layers: nav.Layers(c.cx + 0.5f, c.cz + 0.5f).ToList()))
            .Where(c => c.Layers.Count > 0 && c.Layers.All(l => !l.Walkable))
            .Select(c => (c.X, c.Z, c.Layers[0].Height)).First();
        await loop.InvokeAsync(() => { s.Position = new Vec3 { X = x, Y = floor + 10, Z = z }; return Task.CompletedTask; });

        var ahead = new Vec3 { X = x, Y = floor + 12, Z = z };
        await a.SendGameAsync(WorldCoreService.FlyMove, new FlyMoveReq { Pos = ahead, Dir = new Vec3 { Y = 1 }, Face = 90 });
        await Task.Delay(300);
        Assert.Equal(ahead.Y, s.Position.Y); // taken over unwalkable ground

        var high = new Vec3 { X = x, Y = floor + 70, Z = z };
        await a.SendGameAsync(WorldCoreService.FlyMove, new FlyMoveReq { Pos = high, Dir = new Vec3 { Y = 1 }, Face = 90 });
        await Next(a, GameAoiSink.MovementChange, MovementChangeNtf.Parser, m => m.Uid == self && m.TransType == MovementTransitionType.MoveTransForceTeleport);
        Assert.Equal(ahead.Y, s.Position.Y); // above the band, put back

        // Off a cliff into a lower band: holding or losing height is taken, climbing further is not.
        await loop.InvokeAsync(() => { s.Position = new Vec3 { X = x, Y = floor + 60, Z = z }; return Task.CompletedTask; });
        var down = new Vec3 { X = x, Y = floor + 59, Z = z };
        await a.SendGameAsync(WorldCoreService.FlyMove, new FlyMoveReq { Pos = down, Dir = new Vec3 { Y = -1 }, Face = 90 });
        await Task.Delay(300);
        Assert.Equal(down.Y, s.Position.Y);

        await Ready();
        await Cast(Land);
        Assert.Equal(ErrorCodes.SkillRequirements, (await Next(a, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.True(s.WorldState.Flying);

        await a.SendGameAsync(32745, new PlayerRiderMountReq { Tp = (eRiderType)0 });
        Assert.Equal(0, (await Next(a, 32745, PlayerRiderMountResp.Parser)).Result);
        await Next(a, WorldCoreService.FixLand, FixLandNtf.Parser);
        Assert.False(s.WorldState.Flying);
        Assert.Equal(takeOff.X, s.Position.X);
        Assert.Equal(takeOff.Z, s.Position.Z);
        Assert.True(nav.IsWalkable(s.Position.X, s.Position.Y, s.Position.Z));
    }

    private sealed class ManualClock(DateTimeOffset initial) : TimeProvider
    {
        private long _ticks = initial.UtcTicks;
        public void Set(DateTimeOffset value) => Interlocked.Exchange(ref _ticks, value.UtcTicks);
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    }
}
