using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Legacy ActivityConfig clock and authoritative novice-task wire contract.</summary>
public sealed class TimedEventTests : IAsyncLifetime
{
    private const int Novice = 10000001;
    private readonly string dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-timed-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock clock = new(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
    private WebApplication app = null!;
    private string http = "";
    private T Service<T>() where T : notnull => app.Services.GetRequiredService<T>();
    private Task OnLoop(Func<Task> work) => Service<GameLoop>().InvokeAsync(work);

    public async Task InitializeAsync()
    {
        var (port, micro, game) = TestPorts.FreePorts();
        app = ServerHost.Create([], o =>
        {
            o.DataDir = dataDir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = port; o.MicroGatePort = micro; o.GameGatePort = game;
            o.SeasonStart = "2026-10-05";
        }, clock);
        SureHitRolls.Install(app); // lethal hits are prepared; a FLEE miss would only be noise
        await app.StartAsync();
        http = $"http://127.0.0.1:{port}";
    }

    public async Task DisposeAsync()
    {
        Service<RoleHandover>().BeforeCleanupForTest = null;
        Service<RoleHandover>().AfterCleanupAdmissionForTest = null;
        Service<RoleHandover>().PhaseForTest = null;
        Service<TimedBattleService>().AfterSettlementAdmissionForTest = null;
        Service<TimedBattleService>().SettlementCompletedForTest = null;
        Service<AccountStore>().BeforeOwnedCommitForTest = null;
        await app.StopAsync(); await app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(http, File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser,
        Func<T, bool>? match = null) where T : IMessage<T>
    {
        var packet = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), TimeSpan.FromSeconds(8));
        return parser.ParseFrom(packet.Body);
    }

    private async Task NextLethalPlayerHit(BotClient attacker, BotClient target)
    {
        // SkillConfig 1000001 can miss. Its server-driven auto-attack continues until a real hit kills the 1-HP target.
        long targetId = (long)target.Self!.Uuid;
        var observed = new List<string>();
        async Task<string> DiagnosticAsync()
        {
            string state = "";
            await OnLoop(() =>
            {
                var a = Session(attacker); var t = Session(target);
                state = $"targetHp={t.CurrentHp}, targetDead={t.Dead}, canFight={Service<TimedBattleService>().CanFight(a, t)}, " +
                    $"attackerInstance={a.InstanceId}, targetInstance={t.InstanceId}, " +
                    $"attackerPos=({a.Position.X},{a.Position.Z}), targetPos=({t.Position.X},{t.Position.Z})";
                return Task.CompletedTask;
            });
            return $"hits=[{string.Join(", ", observed)}]; {state}";
        }
        for (var swing = 0; swing < 24; swing++)
        {
            SkillHitInfo hit;
            try
            {
                hit = await Next(attacker, CombatService.SkillHit, SkillHitInfo.Parser,
                    h => h.Dsts.Any(d => d.DstID == targetId));
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException($"No further PvP hit for player {targetId}; {await DiagnosticAsync()}", ex);
            }
            var damage = hit.Dsts.First(d => d.DstID == targetId);
            observed.Add($"{damage.HitResult}/{damage.Damage}/dead={damage.IsDead}");
            if (damage.IsDead) return;
        }
        Assert.Fail($"Twenty-four actual attacks did not kill player {targetId}; {await DiagnosticAsync()}");
    }

    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);

    /// <summary>The exported weekly window of a timed match's activity row, past the realm's opening weeks.</summary>
    private ScheduleRows.Window MatchWindow(int todo) =>
        ScheduleRows.Weekly(todo, new DateTime(2026, 11, 10), clock.LocalTimeZone, Service<GameData>());
    private Task OverrideMatchShapeAsync(int entry, int players, int teamSize) => OnLoop(() =>
    {
        var name = entry == 2 ? "EatChickenMatchConfig" : "DragonIslandMatchConfig";
        var table = (Dictionary<string, JsonElement>)Service<GameData>().Table(name);
        var row = JsonNode.Parse(table[entry.ToString()].GetRawText())!.AsObject();
        row["_kStartQuantity"] = players;
        row["_kTeamQuantity"] = teamSize;
        table[entry.ToString()] = JsonSerializer.SerializeToElement(row);
        return Task.CompletedTask;
    });

    /// <summary>Survivor Battle stages (TimedBattleService.Chicken): each ends when the clock passes its 30401 end time.</summary>
    private async Task<DungeonBaseInfoNotify> NextChickenStageAsync(BotClient[] players, DungeonBaseInfoNotify current)
    {
        int stage = current.StageModuleData.Stage + 1;
        clock.Set(current.StageModuleData.StageEndTime / 1000 + 1);
        await OnLoop(() => { Service<TimedBattleService>().Tick(default); return Task.CompletedTask; });
        DungeonBaseInfoNotify next = null!;
        foreach (var player in players)
            next = await Next(player, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser, s => s.StageModuleData?.Stage == stage);
        return next;
    }

    /// <summary>Career and landing (everyone on the island centre) into the battle, by default past its 3-minute free
    /// revive window so a death is final; waits for the zone's first shrink so that tick has finished.</summary>
    private async Task<DungeonBaseInfoNotify> ChickenBattleAsync(BotClient[] players, DungeonBaseInfoNotify stage0, bool pastFreeRevive = true)
    {
        var info = await NextChickenStageAsync(players, stage0);
        info = await NextChickenStageAsync(players, info);
        foreach (var player in players)
        {
            await player.SendGameAsync(TimedBattleService.SelectPosition, new SelectRolePositionReq { PositionX = 430, PositionY = 402 });
            Assert.Equal(0, (await Next(player, TimedBattleService.SelectPosition, SelectRolePositionResp.Parser)).Result);
        }
        info = await NextChickenStageAsync(players, info);
        Assert.Equal(3, info.StageModuleData.Stage);
        if (pastFreeRevive)
        {
            clock.Set(info.StageModuleData.StageStartTime / 1000 + 181);
            await OnLoop(() => { Service<TimedBattleService>().Tick(default); return Task.CompletedTask; });
            foreach (var player in players)
                await Next(player, DungeonService.Event, DungeonEventNotify.Parser, e => e.Vars.Any(v => v.VarId == 100010003));
        }
        return info;
    }

    private async Task<DungeonBaseInfoNotify> StartChickenAsync(params BotClient[] players)
    {
        foreach (var player in players) await Gm(player, "!lvl 99");
        clock.Set(MatchWindow(114).Start + 300);
        await OverrideMatchShapeAsync(2, players.Length, 1);
        foreach (var player in players)
        {
            await player.SendGameAsync(TimedBattleService.StartMatch, new StartMatchReq { MatchEntryId = 2 });
            Assert.Equal(0, (await Next(player, TimedBattleService.StartMatch, StartMatchResp.Parser)).Result);
        }
        foreach (var player in players)
            Assert.Equal(players.Length, (await Next(player, TimedBattleService.ReadyNotify, MatchReadyNotify.Parser)).ReadyInfos.Count);
        foreach (var player in players)
        {
            await player.SendGameAsync(TimedBattleService.Ready, new MatchReadyReq
            { MatchEntryId = 2, PlayerId = (long)player.Self!.Uuid, IsReady = true });
            Assert.Equal(0, (await Next(player, TimedBattleService.Ready, MatchReadyResp.Parser)).Result);
        }
        long room = 0;
        DungeonBaseInfoNotify info = null!;
        foreach (var player in players)
        {
            var transfer = await Next(player, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser,
                s => s.SceneEntry == 2240101);
            if (room == 0) room = transfer.SceneID;
            Assert.Equal(room, transfer.SceneID);
            await player.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = 2240101 });
            Assert.Equal(0, (await Next(player, MsgId.SceneLoaded, SceneLoadedResp.Parser,
                s => s.SceneId == 2240101)).Result);
            info = await Next(player, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser, s => s.DungeonId == room);
        }
        return info;
    }

    private Task BeginHandover(GameSession session)
    {
        Task? completion = null;
        return Begin();
        async Task Begin()
        {
            await OnLoop(() =>
            {
                completion = Service<RoleHandover>().Begin(session);
                return Task.CompletedTask;
            });
            await completion!;
        }
    }
    private async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{http}/ro3emu/gm?account={bot.Account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    /// <summary>
    /// The stone a "!rift" command just placed. The running round's random stones can sit on the same map point (and
    /// be any star), so a test never picks a stone by map and distance alone.
    /// </summary>
    private async Task<TimedBattleService.RiftStone> GmRiftStoneAsync(BotClient bot, string command)
    {
        var before = await Service<GameLoop>().InvokeAsync(() =>
            Task.FromResult(Service<TimedBattleService>().RiftStones.Select(s => s.Uid).ToHashSet()));
        await Gm(bot, command);
        return await Service<GameLoop>().InvokeAsync(() => Task.FromResult(Service<TimedBattleService>().RiftStones
            .Single(s => !before.Contains(s.Uid) && s.Map == Session(bot).SceneEntry)));
    }
    private static ActivityReq NoviceReq(int task) => new()
    { ActivityId = Novice, ReqParam = new ActivityReqParam { NoviceReq = new NoviceActivityReq { TaskId = task } } };

    [ClientDataFact]
    public async Task LegacyAbsoluteWindowAndNoviceQuestAreServerAuthoritative()
    {
        await using var bot = await InWorld("Alice");
        var closed = await Next(bot, TimedEventService.StatusNotify, ActivityDataNtf.Parser, n => n.ActivityId == Novice);
        Assert.Equal(ActivityStatus.Close, closed.ActivityStatus);
        await bot.SendGameAsync(TimedEventService.Common, NoviceReq(100001));
        Assert.Equal(ErrorCodes.FeatureUnavailable, (await Next(bot, TimedEventService.Common, ActivityResp.Parser)).Result);

        clock.Set(new DateTimeOffset(2025, 7, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds());
        // Consume the window-opening snapshot before delivery; it is not the later successful-request snapshot.
        var started = await Next(bot, TimedEventService.StatusNotify, ActivityDataNtf.Parser,
            n => n.ActivityId == Novice && n.ActivityStatus == ActivityStatus.Start);
        Assert.False(started.ActivityParam.NoviceActivity.TaskIds.ContainsKey(100001));
        await bot.SendGameAsync(TimedEventService.Common, NoviceReq(100001));
        Assert.Equal(ErrorCodes.TaskRequirements, (await Next(bot, TimedEventService.Common, ActivityResp.Parser)).Result);
        await bot.SendGameAsync(TimedEventService.Common, NoviceReq(99999999));
        Assert.Equal(ErrorCodes.Parameter, (await Next(bot, TimedEventService.Common, ActivityResp.Parser)).Result);
        await OnLoop(() => { Session(bot).Tasks!.CompleteTaskNum[100001] = 1; return Task.CompletedTask; });
        await bot.SendGameAsync(TimedEventService.Common, NoviceReq(100001));
        Assert.Equal(0, (await Next(bot, TimedEventService.Common, ActivityResp.Parser)).Result);
        await bot.SendGameAsync(TimedEventService.Common, NoviceReq(100001));
        Assert.Equal(0, (await Next(bot, TimedEventService.Common, ActivityResp.Parser)).Result);
        var open = await Next(bot, TimedEventService.StatusNotify, ActivityDataNtf.Parser,
            n => n.ActivityId == Novice && n.ActivityStatus == ActivityStatus.Start);
        Assert.Equal(1, open.ActivityParam.NoviceActivity.TaskIds[100001]);
    }

    [ClientDataFact]
    public async Task LegacyUploadCannotForgeAnotherPlayerOrQuestProgress()
    {
        clock.Set(new DateTimeOffset(2025, 7, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds());
        await using var bot = await InWorld("Alice");
        long role = (long)bot.Self!.Uuid;
        var fake = new ActivityDataUploadReq { ActivityId = Novice, PlayerId = role + 1,
            UploadParam = new ActivityDataUploadParam { NoviceActivity = new NoviceActivity() } };
        fake.UploadParam.NoviceActivity.TaskIds[100001] = 7;
        await bot.SendGameAsync(TimedEventService.Upload, fake);
        var refused = await Next(bot, TimedEventService.Upload, ActivityDataUploadResp.Parser);
        Assert.Equal(ErrorCodes.Parameter, refused.Result);
        Assert.Empty(refused.DownloadParam.NoviceActivity.TaskIds);
        fake.PlayerId = role;
        await bot.SendGameAsync(TimedEventService.Upload, fake);
        var accepted = await Next(bot, TimedEventService.Upload, ActivityDataUploadResp.Parser, r => r.Result == 0);
        Assert.Empty(accepted.DownloadParam.NoviceActivity.TaskIds);
    }

    [ClientDataFact]
    public async Task PronteraDonateRejectsClosedLevelAndReplayAndPersistsContribution()
    {
        await using var bot = await InWorld("Alice");
        var request = new CommonActivityReq { ActivityId = PronteraDefenseService.ActivityId, RequestId = 1,
            PronteraDefenseDonate = new PronteraDefenseDonateReq { ItemCount = 1 } };
        await bot.SendGameAsync(NewActivityService.Common, request);
        Assert.Equal(ErrorCodes.FunctionLocked,
            (await Next(bot, NewActivityService.Common, CommonActivityRes.Parser)).Result);
        Assert.True(Service<ActivityClock>().TryWindow(PronteraDefenseService.TimeLimitId, out var start, out _));
        clock.Set(start.ToUnixTimeSeconds() + 122401);
        await bot.SendGameAsync(NewActivityService.Common, request);
        Assert.Equal(ErrorCodes.FunctionLocked,
            (await Next(bot, NewActivityService.Common, CommonActivityRes.Parser)).Result);
        await Gm(bot, "!lvl 99");
        await bot.SendGameAsync(NewActivityService.Common, request);
        Assert.Equal(ErrorCodes.ItemsShort,
            (await Next(bot, NewActivityService.Common, CommonActivityRes.Parser)).Result);
        await Gm(bot, "!item 600 2");
        request.PronteraDefenseDonate.ItemCount = -1;
        await bot.SendGameAsync(NewActivityService.Common, request);
        Assert.Equal(ErrorCodes.Parameter,
            (await Next(bot, NewActivityService.Common, CommonActivityRes.Parser)).Result);
        request.PronteraDefenseDonate.ItemCount = 2;
        long role = (long)bot.Self!.Uuid;
        long DonationReceipts()
        {
            using var db = Service<AccountStore>().Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM operation_receipt WHERE command_kind='event/prontera-donate' AND actor_role=$role";
            cmd.Parameters.AddWithValue("$role", role);
            return (long)cmd.ExecuteScalar()!;
        }
        long receiptsBefore = DonationReceipts();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Service<AccountStore>().BeforeOwnedCommitForTest = label =>
        {
            if (label != "economy/event/prontera-donate") return Task.CompletedTask;
            entered.TrySetResult();
            return release.Task;
        };
        try
        {
            await bot.SendGameAsync(NewActivityService.Common, request);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, Service<CharacterService>().CountOf(Session(bot), 600));
            Assert.Equal(0, Session(bot).Defense!.ContributionPoint);
            Assert.Equal(receiptsBefore, DonationReceipts());
            // The second same-role donation must wait behind the held debit and then refuse insufficient items.
            await bot.SendGameAsync(NewActivityService.Common, request);
        }
        finally { release.TrySetResult(); Service<AccountStore>().BeforeOwnedCommitForTest = null; }
        Assert.Equal(0, (await Next(bot, NewActivityService.Common, CommonActivityRes.Parser)).Result);
        await OnLoop(() =>
        {
            Assert.Equal(2, Session(bot).Defense!.ContributionPoint);
            Assert.Equal(0, Service<CharacterService>().CountOf(Session(bot), 600));
            return Task.CompletedTask;
        });
        Assert.Equal(ErrorCodes.ItemsShort,
            (await Next(bot, NewActivityService.Common, CommonActivityRes.Parser)).Result);
        Assert.Equal(receiptsBefore + 1, DonationReceipts());
        using (var db = Service<AccountStore>().Open())
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT source_key,leg_count FROM operation_receipt WHERE command_kind='event/prontera-donate' AND actor_role=$role";
            cmd.Parameters.AddWithValue("$role", role);
            using var receipt = cmd.ExecuteReader();
            Assert.True(receipt.Read());
            Assert.StartsWith("prontera/donate/", receipt.GetString(0));
            Assert.True(receipt.GetInt64(1) > 0);
            Assert.False(receipt.Read());
        }
        var saved = Service<RoleStore>().LoadTimedActivity(role);
        Assert.NotNull(saved);
        Assert.Contains("ContributionPoint", System.Text.Encoding.UTF8.GetString(saved));
    }

    [ClientDataTheory]
    [InlineData(2, 2240101, 114)]
    [InlineData(6, 3010303, 126)]
    public async Task TimedMatchRequiresWindowAndSettlesOnlyActualOpposingPlayerCombat(int entry, int sceneEntry, int todo)
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        var req = new StartMatchReq { MatchEntryId = entry };
        await alice.SendGameAsync(TimedBattleService.StartMatch, req);
        Assert.Equal(ErrorCodes.FeatureUnavailable,
            (await Next(alice, TimedBattleService.StartMatch, StartMatchResp.Parser)).Result);
        await Gm(alice, "!lvl 99"); await Gm(bob, "!lvl 99");
        var window = MatchWindow(todo);
        clock.Set(window.Start + 300);
        Assert.True(Service<TimedTodoSchedule>().IsOpen(todo));
        var exportedMatch = Service<GameData>().Row(entry == 2 ? "EatChickenMatchConfig" : "DragonIslandMatchConfig", entry)!.Value;
        Assert.Equal(entry == 2 ? 100 : 30, GameData.Int(exportedMatch, "_kStartQuantity"));
        Assert.Equal(5, GameData.Int(exportedMatch, "_kTeamQuantity"));
        await alice.SendGameAsync(TimedBattleService.StartMatch, req);
        Assert.Equal(0, (await Next(alice, TimedBattleService.StartMatch, StartMatchResp.Parser)).Result);
        await bob.SendGameAsync(TimedBattleService.StartMatch, req);
        Assert.Equal(0, (await Next(bob, TimedBattleService.StartMatch, StartMatchResp.Parser)).Result);
        Assert.Equal(0, Session(alice).EventFaction);
        Assert.Equal(0, Session(bob).EventFaction);
        Assert.Equal(0, Session(alice).InstanceId); // Two entrants cannot trigger the exported 100/30-player queue.
        foreach (var player in new[] { alice, bob })
        {
            await player.SendGameAsync(TimedBattleService.CancelMatch, new CancelMatchReq { MatchEntryId = entry });
            Assert.Equal(0, (await Next(player, TimedBattleService.CancelMatch, CancelMatchResp.Parser)).Result);
        }
        await OverrideMatchShapeAsync(entry, 2, 1); // Isolated host fixture; never changes the shared export.
        if (entry == 6)
        {
            await Gm(bob, "!item 17 80"); // Controlled prior net score below rank-1 promotion threshold.
            var today = window.LocalStart;
            await OnLoop(() =>
            {
                Session(alice).TimedBattle!.DragonCurrencyDay = today.Year * 10000L + today.Month * 100 + today.Day;
                Session(alice).TimedBattle!.DragonCurrencyEarned = 950;
                Session(bob).TimedBattle!.DragonScore = 80;
                Session(bob).TimedBattle!.DragonRankScore = 80;
                return Task.CompletedTask;
            });
        }
        await alice.SendGameAsync(CombatService.UseSkill,
            new CastSkillReq { SkillId = 1000001, TargetId = (long)bob.Self!.Uuid });
        Assert.NotEqual(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await alice.SendGameAsync(TimedBattleService.StartMatch, req);
        Assert.Equal(0, (await Next(alice, TimedBattleService.StartMatch, StartMatchResp.Parser, r => r.MatchEntryId == entry)).Result);
        await bob.SendGameAsync(TimedBattleService.StartMatch, req);
        Assert.Equal(0, (await Next(bob, TimedBattleService.StartMatch, StartMatchResp.Parser, r => r.MatchEntryId == entry)).Result);
        Assert.Equal(2, (await Next(alice, TimedBattleService.ReadyNotify, MatchReadyNotify.Parser)).ReadyInfos.Count);
        await Next(bob, TimedBattleService.ReadyNotify, MatchReadyNotify.Parser);
        await alice.SendGameAsync(TimedBattleService.Ready,
            new MatchReadyReq { MatchEntryId = entry, PlayerId = (long)bob.Self!.Uuid, IsReady = true });
        Assert.Equal(ErrorCodes.Parameter, (await Next(alice, TimedBattleService.Ready, MatchReadyResp.Parser)).Result);
        await alice.SendGameAsync(TimedBattleService.Ready,
            new MatchReadyReq { MatchEntryId = entry, PlayerId = (long)alice.Self!.Uuid, IsReady = true });
        Assert.Equal(0, (await Next(alice, TimedBattleService.Ready, MatchReadyResp.Parser)).Result);
        await bob.SendGameAsync(TimedBattleService.Ready,
            new MatchReadyReq { MatchEntryId = entry, PlayerId = (long)bob.Self.Uuid, IsReady = true });
        Assert.Equal(0, (await Next(bob, TimedBattleService.Ready, MatchReadyResp.Parser)).Result);
        var switchA = await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser,
            s => s.SceneEntry == sceneEntry);
        var switchB = await Next(bob, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser,
            s => s.SceneEntry == sceneEntry);
        Assert.Equal(switchA.SceneID, switchB.SceneID);
        await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = sceneEntry });
        await bob.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = sceneEntry });
        Assert.Equal(0, (await Next(alice, MsgId.SceneLoaded, SceneLoadedResp.Parser, s => s.SceneId == sceneEntry)).Result);
        Assert.Equal(0, (await Next(bob, MsgId.SceneLoaded, SceneLoadedResp.Parser, s => s.SceneId == sceneEntry)).Result);
        var loaded = await Next(alice, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser, s => s.DungeonId == switchA.SceneID);
        await Next(bob, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser, s => s.DungeonId == switchB.SceneID);
        if (entry == 2)
        {
            // Survivor Battle fights only in its battle stage, after the career and landing picks.
            await OnLoop(() =>
            {
                Assert.False(Service<TimedBattleService>().CanFight(Session(alice), Session(bob)));
                return Task.CompletedTask;
            });
            await ChickenBattleAsync([alice, bob], loaded);
        }
        await OnLoop(() =>
        {
            Assert.True(Service<TimedBattleService>().CanFight(Session(alice), Session(bob)));
            Session(bob).CurrentHp = 1;
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(CombatService.UseSkill,
            new CastSkillReq { SkillId = 1000001, TargetId = (long)bob.Self.Uuid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await NextLethalPlayerHit(alice, bob);
        if (entry == 2)
        {
            var result = await Next(alice, TimedBattleService.ChickenResult, ChickenResultNotify.Parser);
            Assert.False(result.HasAborted);
            Assert.Equal(2, result.RoleStatistics.Count);
            Assert.Equal(1, result.RoleStatistics[(long)alice.Self.Uuid].Totalscore);
            Assert.Equal(2, Session(alice).TimedBattle!.ChickenRank);
            Assert.Equal(100, Session(alice).TimedBattle!.ChickenScore);
            Assert.Equal(100, Service<CharacterService>().CountOf(Session(alice), 7));
            Assert.Equal(100, JsonSerializer.Deserialize<TimedBattleService.State>(
                Service<RoleStore>().LoadTimedBattle((long)alice.Self!.Uuid)!)!.ChickenScore);
            await OnLoop(() => Service<TimedBattleService>().PlayerKilledAsync(Session(alice), Session(bob)));
            Assert.Equal(100, Service<CharacterService>().CountOf(Session(alice), 7));
        }
        else
        {
            var result = await Next(alice, TimedBattleService.DragonSettle, DragonIslandSettleNtf.Parser);
            Assert.Equal(1, result.ResultType);
            Assert.Equal(2, result.NewRankId);
            Assert.Equal(50, result.CurrencyEarned);
            Assert.Equal(50, Service<CharacterService>().CountOf(Session(alice), 6));
            Assert.Equal(result.NewNetScore, Service<CharacterService>().CountOf(Session(alice), 17));
            var loss = await Next(bob, TimedBattleService.DragonSettle, DragonIslandSettleNtf.Parser);
            Assert.Equal(2, loss.ResultType);
            Assert.Equal(-60, loss.NetScoreDelta);
            Assert.Equal(20, loss.NewNetScore);
            Assert.Equal(20, Service<CharacterService>().CountOf(Session(bob), 17));
            Assert.Equal(150, Service<CharacterService>().CountOf(Session(bob), 6));
            Assert.Equal(20, System.Text.Json.JsonSerializer.Deserialize<TimedBattleService.State>(
                Service<RoleStore>().LoadTimedBattle((long)bob.Self!.Uuid)!)!.DragonScore);
            await OnLoop(() => Service<TimedBattleService>().PlayerKilledAsync(Session(alice), Session(bob)));
            Assert.Equal(50, Service<CharacterService>().CountOf(Session(alice), 6));
            Assert.Equal(150, Service<CharacterService>().CountOf(Session(bob), 6));
            Assert.Equal(20, Service<CharacterService>().CountOf(Session(bob), 17));
            await alice.SendGameAsync(TimedBattleService.DragonClaim, new DragonIslandClaimRankRewardReq());
            Assert.Equal(0, (await Next(alice, TimedBattleService.DragonClaim, DragonIslandClaimRankRewardRes.Parser)).ErrorCode);
            Assert.Equal(10, Service<CharacterService>().CountOf(Session(alice), 1200000));
            await alice.SendGameAsync(TimedBattleService.DragonClaim, new DragonIslandClaimRankRewardReq());
            Assert.Equal(ErrorCodes.TaskRequirements,
                (await Next(alice, TimedBattleService.DragonClaim, DragonIslandClaimRankRewardRes.Parser)).ErrorCode);
        }
        Assert.NotNull(Service<RoleStore>().LoadTimedBattle((long)alice.Self.Uuid));
    }

    [ClientDataFact]
    public async Task KafraTwoMemberTeamsFightOnlyOpponentsAndSettleAfterLastEnemyFalls()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var cara = await InWorld("Cara");
        await using var dan = await InWorld("Dan");
        var players = new[] { alice, bob, cara, dan };
        foreach (var player in players) await Gm(player, "!lvl 99");
        clock.Set(MatchWindow(114).Start + 300);
        await OverrideMatchShapeAsync(2, 4, 2);
        foreach (var player in players)
        {
            await player.SendGameAsync(TimedBattleService.StartMatch, new StartMatchReq { MatchEntryId = 2 });
            Assert.Equal(0, (await Next(player, TimedBattleService.StartMatch, StartMatchResp.Parser)).Result);
        }
        foreach (var player in players)
            Assert.Equal(4, (await Next(player, TimedBattleService.ReadyNotify, MatchReadyNotify.Parser)).ReadyInfos.Count);
        foreach (var player in players)
        {
            await player.SendGameAsync(TimedBattleService.Ready, new MatchReadyReq
            { MatchEntryId = 2, PlayerId = (long)player.Self!.Uuid, IsReady = true });
            Assert.Equal(0, (await Next(player, TimedBattleService.Ready, MatchReadyResp.Parser)).Result);
        }
        long room = 0;
        DungeonBaseInfoNotify info = null!;
        foreach (var player in players)
        {
            var transfer = await Next(player, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser,
                s => s.SceneEntry == 2240101);
            if (room == 0) room = transfer.SceneID;
            Assert.Equal(room, transfer.SceneID);
            await player.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = 2240101 });
            Assert.Equal(0, (await Next(player, MsgId.SceneLoaded, SceneLoadedResp.Parser,
                s => s.SceneId == 2240101)).Result);
            info = await Next(player, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser,
                s => s.DungeonId == room);
            Assert.Equal(2, info.InitData.ChickenInitData.TeamCount);
            Assert.Equal(2, info.InitData.ChickenInitData.TeamMemberCount);
            Assert.Equal(1, info.InitData.ChickenInitData.PlayerToTeamId[(long)alice.Self!.Uuid]);
            Assert.Equal(1, info.InitData.ChickenInitData.PlayerToTeamId[(long)bob.Self!.Uuid]);
            Assert.Equal(2, info.InitData.ChickenInitData.PlayerToTeamId[(long)cara.Self!.Uuid]);
            Assert.Equal(2, info.InitData.ChickenInitData.PlayerToTeamId[(long)dan.Self!.Uuid]);
        }
        await ChickenBattleAsync(players, info);
        await OnLoop(() =>
        {
            Assert.False(Service<TimedBattleService>().CanFight(Session(alice), Session(bob)));
            Assert.True(Service<TimedBattleService>().CanFight(Session(alice), Session(cara)));
            Session(cara).CurrentHp = 1;
            Session(dan).CurrentHp = 1;
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(CombatService.UseSkill,
            new CastSkillReq { SkillId = 1000001, TargetId = (long)bob.Self!.Uuid });
        Assert.NotEqual(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await alice.SendGameAsync(CombatService.UseSkill,
            new CastSkillReq { SkillId = 1000001, TargetId = (long)cara.Self!.Uuid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await NextLethalPlayerHit(alice, cara);
        await OnLoop(() =>
        {
            Assert.Equal(0, Session(alice).TimedBattle!.ChickenMatches);
            Assert.True(Service<TimedBattleService>().CanFight(Session(bob), Session(dan)));
            return Task.CompletedTask;
        });
        await bob.SendGameAsync(CombatService.UseSkill,
            new CastSkillReq { SkillId = 1000001, TargetId = (long)dan.Self!.Uuid });
        Assert.Equal(0, (await Next(bob, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await NextLethalPlayerHit(bob, dan);
        foreach (var player in players)
        {
            var result = await Next(player, TimedBattleService.ChickenResult, ChickenResultNotify.Parser);
            Assert.False(result.HasAborted);
            Assert.Equal(4, result.RoleStatistics.Count);
            Assert.Equal(2, result.TeamStatistics.MemberIds.Count);
        }
        Assert.Equal(2, Session(alice).TimedBattle!.ChickenRank);
        Assert.Equal(2, Session(bob).TimedBattle!.ChickenRank);
        Assert.Equal(1, Session(cara).TimedBattle!.ChickenRank);
        Assert.Equal(1, Session(dan).TimedBattle!.ChickenRank);
        Assert.Equal(100, Service<CharacterService>().CountOf(Session(alice), 7));
        Assert.Equal(100, Service<CharacterService>().CountOf(Session(bob), 7));
        Assert.Equal(0, Service<CharacterService>().CountOf(Session(cara), 7));
        Assert.Equal(0, Service<CharacterService>().CountOf(Session(dan), 7));
    }

    [ClientDataFact]
    public async Task SimultaneousOpponentHandoversDoNotStrandTheSettledMatch()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await ChickenBattleAsync([alice, bob], await StartChickenAsync(alice, bob));
        var a = Session(alice); var b = Session(bob);
        var aWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Service<RoleHandover>().AfterCleanupAdmissionForTest = session =>
        {
            if (!ReferenceEquals(session, a)) return Task.CompletedTask;
            aWaiting.TrySetResult();
            return releaseA.Task;
        };
        Service<RoleHandover>().BeforeCleanupForTest = session =>
        {
            if (ReferenceEquals(session, b)) { bWaiting.TrySetResult(); return releaseB.Task; }
            return Task.CompletedTask;
        };
        Service<TimedBattleService>().SettlementCompletedForTest = () => settled.TrySetResult();
        try
        {
            var detachA = BeginHandover(a);
            await aWaiting.Task.WaitAsync(TimeSpan.FromSeconds(8));
            var detachB = BeginHandover(b);
            await bWaiting.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await OnLoop(() =>
            {
                Assert.True(a.Attachment!.Cancelled);
                Assert.True(b.Attachment!.Cancelled);
                Assert.Equal(1, Service<TimedBattleService>().ActiveMatchCountForTest);
                return Task.CompletedTask;
            });
            releaseA.TrySetResult();
            await settled.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await detachA.WaitAsync(TimeSpan.FromSeconds(8));
            await OnLoop(() =>
            {
                Assert.Equal(0, Service<TimedBattleService>().ActiveMatchCountForTest);
                Assert.Equal(0, a.EventFaction);
                Assert.Equal(0, b.EventFaction);
                Assert.Equal(0, b.TimedBattle!.ChickenMatches);
                return Task.CompletedTask;
            });
            releaseB.TrySetResult();
            await detachB.WaitAsync(TimeSpan.FromSeconds(8));
        }
        finally { releaseA.TrySetResult(); releaseB.TrySetResult(); }
    }

    [ClientDataFact]
    public async Task DeadlineSettlementSkipsQuiescingRoleAndKeepsDepartedParticipantStatistics()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var cara = await InWorld("Cara");
        var battle = await ChickenBattleAsync([alice, bob, cara], await StartChickenAsync(alice, bob, cara));
        var a = Session(alice); var b = Session(bob); var c = Session(cara);
        long room = c.InstanceId;
        long aliceId = (long)alice.Self!.Uuid, bobId = (long)bob.Self!.Uuid, caraId = (long)cara.Self!.Uuid;
        int originalCareer = a.Progress!.Career;
        int currentCareer = c.Progress!.Career + 2;
        // The old role's final live presentation may differ from the value at matchmaking.
        await OnLoop(() =>
        {
            a.Progress!.Career = originalCareer + 1;
            c.Progress!.Career = currentCareer;
            return Task.CompletedTask;
        });
        await BeginHandover(a).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Null(a.Role);
        var bWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int settlementCount = 0;
        Service<RoleHandover>().BeforeCleanupForTest = session =>
        {
            if (!ReferenceEquals(session, b)) return Task.CompletedTask;
            bWaiting.TrySetResult();
            return releaseB.Task;
        };
        Service<TimedBattleService>().SettlementCompletedForTest = () =>
        { Interlocked.Increment(ref settlementCount); settled.TrySetResult(); };
        try
        {
            var detachB = BeginHandover(b);
            await bWaiting.Task.WaitAsync(TimeSpan.FromSeconds(8));
            clock.Set(battle.StageModuleData.StageEndTime / 1000 + 1); // the 20-minute battle stage is over
            await OnLoop(() => { Service<TimedBattleService>().Tick(default); return Task.CompletedTask; });
            await settled.Task.WaitAsync(TimeSpan.FromSeconds(8));
            var result = await Next(cara, TimedBattleService.ChickenResult, ChickenResultNotify.Parser);
            Assert.False(result.HasAborted);
            Assert.Equal(3, result.RoleStatistics.Count);
            Assert.Equal("Alice", result.RoleStatistics[aliceId].BaseInfo.Name);
            Assert.Equal(originalCareer + 1, result.RoleStatistics[aliceId].BaseInfo.Job);
            Assert.Equal("Bob", result.RoleStatistics[bobId].BaseInfo.Name);
            Assert.Equal("Cara", result.RoleStatistics[caraId].BaseInfo.Name);
            Assert.Equal(currentCareer, result.RoleStatistics[caraId].BaseInfo.Job);
            Assert.Equal(1, c.TimedBattle!.ChickenMatches);
            Assert.Equal(100, Service<CharacterService>().CountOf(c, 7));
            Assert.NotEqual(room, c.InstanceId);
            await OnLoop(() =>
            {
                Assert.Equal(0, Service<TimedBattleService>().ActiveMatchCountForTest);
                Assert.Equal(0, b.EventFaction);
                return Task.CompletedTask;
            });
            releaseB.TrySetResult();
            await detachB.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(1, Volatile.Read(ref settlementCount));
            var bSaved = Service<RoleStore>().LoadTimedBattle(bobId);
            Assert.True(bSaved is null or { Length: 0 }
                || JsonSerializer.Deserialize<TimedBattleService.State>(bSaved)!.ChickenMatches == 0);
        }
        finally { releaseB.TrySetResult(); }
    }

    [ClientDataFact]
    public async Task RuntimeSettlementAdmittedBeforeHandoverFinishesBothAwards()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var cara = await InWorld("Cara");
        var battle = await ChickenBattleAsync([alice, bob], await StartChickenAsync(alice, bob));
        var a = Session(alice); var b = Session(bob);
        long roomId = a.InstanceId;
        string source = $"timed-battle/match/{roomId}";
        long CountRows(string sql)
        {
            using var db = Service<AccountStore>().Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            if (sql.Contains("$source", StringComparison.Ordinal)) cmd.Parameters.AddWithValue("$source", source);
            if (sql.Contains("$match", StringComparison.Ordinal)) cmd.Parameters.AddWithValue("$match", roomId);
            return (long)cmd.ExecuteScalar()!;
        }
        const string receiptCount = "SELECT count(*) FROM operation_receipt WHERE command_kind='event/timed-battle-settle' AND source_key=$source";
        const string obligationCount = "SELECT count(*) FROM timed_battle_obligation WHERE match_id=$match";
        Assert.Equal(0, CountRows(receiptCount));
        Assert.Equal(0, CountRows(obligationCount));
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commitEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var quiescing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Service<TimedBattleService>().AfterSettlementAdmissionForTest = () =>
        { admitted.TrySetResult(); return release.Task; };
        Service<AccountStore>().BeforeOwnedCommitForTest = label =>
        {
            if (label != "economy/event/timed-battle-settle") return Task.CompletedTask;
            commitEntered.TrySetResult();
            return releaseCommit.Task;
        };
        var settlementDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Service<TimedBattleService>().SettlementCompletedForTest = () => settlementDone.TrySetResult();
        Service<RoleHandover>().PhaseForTest = (phase, session) =>
        { if (phase == "Quiescing" && ReferenceEquals(session, b)) quiescing.TrySetResult(); };
        try
        {
            // A final Survivor Battle death (past the free revive window): the match settles on its own.
            var settlement = OnLoop(() => Service<OwnerRegistry>().RunRuntimeAsync("confirmed timed battle death",
                async () => await Service<CombatService>().EnvironmentDamageAsync(b, 100_000_000)));
            await admitted.Task.WaitAsync(TimeSpan.FromSeconds(8));
            var detachB = BeginHandover(b);
            await quiescing.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.True(b.Attachment!.Cancelled);
            release.TrySetResult();
            await commitEntered.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(0, a.TimedBattle!.ChickenMatches);
            Assert.Equal(0, b.TimedBattle!.ChickenMatches);
            Assert.Equal(0, Service<CharacterService>().CountOf(a, 7));
            Assert.Equal(0, CountRows(receiptCount));
            Assert.Equal(0, CountRows(obligationCount));
            clock.Set(battle.StageModuleData.StageEndTime / 1000 + 1);
            await OnLoop(() => { Service<TimedBattleService>().Tick(default); return Task.CompletedTask; });
            await cara.SendGameAsync(TimedBattleService.DragonClaim, new DragonIslandClaimRankRewardReq());
            Assert.Equal(ErrorCodes.FeatureUnavailable,
                (await Next(cara, TimedBattleService.DragonClaim, DragonIslandClaimRankRewardRes.Parser)).ErrorCode);
            Assert.Equal(0, CountRows(receiptCount));
            releaseCommit.TrySetResult();
            await settlement.WaitAsync(TimeSpan.FromSeconds(8));
            var winner = await Next(alice, TimedBattleService.ChickenResult, ChickenResultNotify.Parser);
            var loser = await Next(bob, TimedBattleService.ChickenResult, ChickenResultNotify.Parser);
            Assert.False(winner.HasAborted);
            Assert.False(loser.HasAborted);
            Assert.Equal(100, Service<CharacterService>().CountOf(a, 7));
            await detachB.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(1, JsonSerializer.Deserialize<TimedBattleService.State>(
                Service<RoleStore>().LoadTimedBattle((long)bob.Self!.Uuid)!)!.ChickenMatches);
            Assert.Equal(1, CountRows(receiptCount));
            Assert.Equal(2, CountRows(obligationCount));
            // The results go out before the players leave the match room; the match closes after that fan-out.
            await settlementDone.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(0, Service<TimedBattleService>().ActiveMatchCountForTest);
        }
        finally
        {
            release.TrySetResult(); releaseCommit.TrySetResult();
            Service<AccountStore>().BeforeOwnedCommitForTest = null;
        }
    }

    [ClientDataFact]
    public async Task OnePiecePrivateBossPaysOnlyAfterKillAndPersistsConfiguredDailyCap()
    {
        await using (var bot = await InWorld("Alice"))
        {
        await bot.SendGameAsync(TimedBattleService.OnePieceInfo, new HiddenTreasureRoundInfoReq());
        Assert.False((await Next(bot, TimedBattleService.OnePieceInfo, HiddenTreasureRoundInfoResp.Parser)).IsOpen);
        await Gm(bot, "!lvl 99");
        await bot.SendGameAsync(TimedBattleService.OnePieceInfo, new HiddenTreasureRoundInfoReq());
        var info = await Next(bot, TimedBattleService.OnePieceInfo, HiddenTreasureRoundInfoResp.Parser, r => r.IsOpen);
        Assert.True(info.IsOpen);
        Assert.Contains(info.ItemInfo, item => item.ItemId == 1302000 && item.MaxGainCount == 20);
        Assert.Contains(info.ItemInfo, item => item.ItemId == 1302002 && item.MaxGainCount == 5);
        await bot.SendGameAsync(DungeonService.Enter, new EnterDungeonReq { SceneEntry = 3200101 });
        Assert.Equal(ErrorCodes.NpcMissing,
            (await Next(bot, DungeonService.Enter, EnterDungeonRsp.Parser)).Result);
        var point = Service<WorldData>().Map(10110)!.Points[8080201];
        await Gm(bot, $"!warp 10110 {point.X.ToString(System.Globalization.CultureInfo.InvariantCulture)} "
            + point.Z.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, s => s.SceneEntry == 10110);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = 10110 });
        await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, s => s.SceneId == 10110);
        async Task<TimedBattleService.RiftStone> PlaceStoneAsync()
        {
            // A cleared stone leaves the map, so every clear fights a fresh 1-star Cat o' Nine-Tail stone from the GM aid.
            var placed = await GmRiftStoneAsync(bot, "!rift 1 Cat");
            Assert.Equal((10110L, 1, 3200101L), (placed.Map, placed.Star, placed.Scene));
            return placed;
        }
        async Task ClearTreasureAsync()
        {
            var stone = await PlaceStoneAsync();
            await bot.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq
            { NpcUid = stone.Uid, InteractionId = 161000011 });
            Assert.Equal(0, (await Next(bot, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
            // The client names the stone by its unit uid (LC_SceneManagerNew.SendEnterDungeonWithOnePieceData).
            await bot.SendGameAsync(DungeonService.Enter, new EnterDungeonReq { SceneEntry = 3200101,
                InitData = new DungeonInitData { HiddenTreasureInitData = new HiddenTreasureInitData { NpcId = stone.Uid } } });
            Assert.Equal(0, (await Next(bot, DungeonService.Enter, EnterDungeonRsp.Parser)).Result);
            var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, s => s.SceneEntry == 3200101);
            await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = 3200101 });
            Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, s => s.SceneId == 3200101)).Result);
            await Next(bot, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser, s => s.DungeonId == sw.SceneID);
            MonsterUnit? actor = null;
            for (int i = 0; i < 100 && actor == null; i++)
            {
                actor = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(Service<World>().AllUnits()
                    .OfType<MonsterUnit>().SingleOrDefault(m => m.InstanceId == sw.SceneID && m.Template.NpcId == 16000001)));
                if (actor == null) await Task.Delay(20);
            }
            Assert.NotNull(actor);
            await OnLoop(() => { actor!.Hp = 1; return Task.CompletedTask; });
            await bot.SendGameAsync(MsgId.Walk, new WalkReq { Pos = actor!.Pos, BStop = true, SpeedRate = 1 });
            await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = actor.Uid });
            Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.Dsts.Any(d => d.DstID == actor.Uid && d.IsDead));
            Assert.Equal((DungeonRes)1, (await Next(bot, DungeonService.Result, DungeonResNotify.Parser)).Res);
            Assert.DoesNotContain(await Service<GameLoop>().InvokeAsync(() =>
                Task.FromResult(Service<TimedBattleService>().RiftStones.ToArray())), s => s.Uid == stone.Uid);
            var back = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser,
                s => s.SceneEntry != 3200101);
            await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = back.SceneEntry });
            await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, s => s.SceneId == back.SceneEntry);
        }

        long baseBefore = Service<CharacterService>().CountOf(Session(bot), 2);
        await ClearTreasureAsync();
        Assert.Equal(baseBefore + 10000, Service<CharacterService>().CountOf(Session(bot), 2));
        Assert.True(Service<CharacterService>().CountOf(Session(bot), 1302000) >= 1);
        Assert.Equal(1, Service<CharacterService>().CountOf(Session(bot), 1302002));
        Assert.Equal(1, Session(bot).TimedBattle!.OnePieceGains.Single().Value);
        Assert.NotNull(Service<RoleStore>().LoadTimedBattle((long)bot.Self!.Uuid));
        var sync = await Next(bot, 5038, DBOnePieceInfoIncrement.Parser,
            s => s.DungeonMinPassTime.ContainsKey(3200101));
        Assert.True(sync.DungeonMinPassTime.ContainsKey(3200101));
        long tokenBefore = Service<CharacterService>().CountOf(Session(bot), 1302000);
        await OnLoop(() =>
        {
            var state = Session(bot).TimedBattle!;
            long day = state.OnePieceGains.Keys.Single();
            state.OnePieceGains[day] = 4;
            state.OnePieceItemGains[day][2] = 40000;
            state.OnePieceItemGains[day][1302000] = 4;
            state.OnePieceItemGains[day][1302002] = 4;
            return Task.CompletedTask;
        });
        await ClearTreasureAsync(); // Fifth clear grants the last restricted extra item.
        Assert.Equal(2, Service<CharacterService>().CountOf(Session(bot), 1302002));
        Assert.Equal(baseBefore + 20000, Service<CharacterService>().CountOf(Session(bot), 2));
        await ClearTreasureAsync(); // Sixth clear still grants the base and unrestricted extra items.
        Assert.Equal(2, Service<CharacterService>().CountOf(Session(bot), 1302002));
        Assert.Equal(tokenBefore + 2, Service<CharacterService>().CountOf(Session(bot), 1302000));
        Assert.Equal(baseBefore + 30000, Service<CharacterService>().CountOf(Session(bot), 2));
        Assert.Equal(6, Session(bot).TimedBattle!.OnePieceGains.Single().Value);
        Assert.Equal(5, Session(bot).TimedBattle!.OnePieceItemGains.Single().Value[1302002]);
        await OnLoop(() =>
        {
            var state = Session(bot).TimedBattle!;
            long day = state.OnePieceGains.Keys.Single();
            state.OnePieceGains[day] = 19;
            state.OnePieceItemGains[day][2] = 190000;
            state.OnePieceItemGains[day][1302000] = 19;
            return Task.CompletedTask;
        });
        await ClearTreasureAsync(); // Twentieth clear remains eligible.
        Assert.Equal(baseBefore + 40000, Service<CharacterService>().CountOf(Session(bot), 2));
        Assert.Equal(20, Session(bot).TimedBattle!.OnePieceGains.Single().Value);
        await PlaceStoneAsync();
        await bot.SendGameAsync(DungeonService.Enter, new EnterDungeonReq { SceneEntry = 3200101 });
        Assert.Equal(ErrorCodes.TaskRequirements,
            (await Next(bot, DungeonService.Enter, EnterDungeonRsp.Parser)).Result);
        }
        for (int i = 0; i < 100 && Service<SessionRegistry>().All().Any(s => s.Account == "alice"); i++)
            await Task.Delay(20);
        await using var reconnected = await InWorld("Alice", create: false);
        Assert.Equal(20, Session(reconnected).TimedBattle!.OnePieceGains.Single().Value);
        Assert.Equal(5, Session(reconnected).TimedBattle!.OnePieceItemGains.Single().Value[1302002]);
        Assert.Equal(2, Service<CharacterService>().CountOf(Session(reconnected), 1302002));
        Assert.True((await Next(reconnected, 5038, DBOnePieceInfoIncrement.Parser,
            s => s.DungeonMinPassTime.ContainsKey(3200101))).DungeonMinPassTime.ContainsKey(3200101));
    }

    [ClientDataFact]
    public async Task RiftChallengerWhoDiesLosesTheRunAndReturnsAliveWithTheStoneFree()
    {
        await using var bot = await InWorld("Dusk");
        await using var other = await InWorld("Eve");
        var point = Service<WorldData>().Map(10110)!.Points[8080201];
        foreach (var b in new[] { bot, other })
        {
            await Gm(b, "!lvl 99");
            await Gm(b, $"!warp 10110 {point.X.ToString(System.Globalization.CultureInfo.InvariantCulture)} "
                + point.Z.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await Next(b, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, s => s.SceneEntry == 10110);
            await b.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = 10110 });
            await Next(b, MsgId.SceneLoaded, SceneLoadedResp.Parser, s => s.SceneId == 10110);
        }
        var stone = await GmRiftStoneAsync(bot, "!rift 1 Cat");
        Assert.Equal((10110L, 1, 3200101L), (stone.Map, stone.Star, stone.Scene));
        await bot.SendGameAsync(DungeonService.Enter, new EnterDungeonReq { SceneEntry = 3200101,
            InitData = new DungeonInitData { HiddenTreasureInitData = new HiddenTreasureInitData { NpcId = stone.Uid } } });
        Assert.Equal(0, (await Next(bot, DungeonService.Enter, EnterDungeonRsp.Parser)).Result);
        // A stone being fought is not available to anyone else.
        await other.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq { NpcUid = stone.Uid, InteractionId = 161000011 });
        Assert.Equal(ErrorCodes.DungeonBusy, (await Next(other, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
        await other.SendGameAsync(DungeonService.Enter, new EnterDungeonReq { SceneEntry = 3200101,
            InitData = new DungeonInitData { HiddenTreasureInitData = new HiddenTreasureInitData { NpcId = stone.Uid } } });
        Assert.Equal(ErrorCodes.DungeonBusy, (await Next(other, DungeonService.Enter, EnterDungeonRsp.Parser)).Result);
        var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, s => s.SceneEntry == 3200101);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = 3200101 });
        await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, s => s.SceneId == 3200101);
        await Next(bot, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser, s => s.DungeonId == sw.SceneID);

        // The fight scene is NoRevive (_iReviveTrigger 2): a solo challenger's death ends the run as a loss.
        await Service<GameLoop>().InvokeAsync(() => Service<CombatService>().EnvironmentDamageAsync(Session(bot), 10_000_000));
        Assert.Equal((DungeonRes)2, (await bot.Inbox.WaitAsync(e => e.IsGame(DungeonService.Result), TimeSpan.FromSeconds(10)) is var r
            ? DungeonResNotify.Parser.ParseFrom(r.Body) : null)!.Res);
        var back = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, s => s.SceneEntry == 10110);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = back.SceneEntry });
        await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, s => s.SceneId == 10110);
        bool dead = true;
        for (int i = 0; i < 100 && dead; i++)
        {
            dead = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(Session(bot).Dead));
            if (dead) await Task.Delay(20);
        }
        Assert.False(dead);
        var free = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(
            Service<TimedBattleService>().RiftStones.Single(s => s.Uid == stone.Uid)));
        Assert.Null(free.Challenger);
        Assert.Equal(0, Session(bot).TimedBattle!.OnePieceGains.Values.Sum()); // a loss pays nothing
        await other.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq { NpcUid = stone.Uid, InteractionId = 161000011 });
        Assert.Equal(0, (await Next(other, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task RiftRoundPlacesStarStonesAndAThreeStarClearPaysItsStar()
    {
        await using var bot = await InWorld("Rhea");
        await Gm(bot, "!lvl 99");
        TimedBattleService.RiftStone[] round = [];
        for (int i = 0; i < 200 && round.Length == 0; i++)
        {
            round = await Service<GameLoop>().InvokeAsync(() =>
                Task.FromResult(Service<TimedBattleService>().RiftStones.ToArray()));
            if (round.Length == 0) await Task.Delay(20);
        }
        // PublicParamConfig.OnePieceMapRefresh [3,4] maps; the 6-star (MVP) budget is OnePieceMonsterConfig's 5.
        Assert.InRange(round.Select(s => s.Map).Distinct().Count(), 3, 4);
        Assert.All(round, s => Assert.InRange(s.Star, 1, 6));
        Assert.True(round.Count(s => s.Star == 6) <= 5);

        var point = Service<WorldData>().Map(10110)!.Points[8080201];
        await Gm(bot, $"!warp 10110 {point.X.ToString(System.Globalization.CultureInfo.InvariantCulture)} "
            + point.Z.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, s => s.SceneEntry == 10110);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = 10110 });
        await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, s => s.SceneId == 10110);
        var stone = await GmRiftStoneAsync(bot, "!rift 3 Gryphon");
        Assert.Equal((10110L, 3, 3200201L), (stone.Map, stone.Star, stone.Scene));
        Assert.Equal(16000008, stone.Boss); // the third Gryphon (stars 1-5 are 16000006-16000010)

        long coinBefore = Service<CharacterService>().CountOf(Session(bot), 2);
        await bot.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq { NpcUid = stone.Uid, InteractionId = 161000031 });
        Assert.Equal(0, (await Next(bot, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
        await bot.SendGameAsync(DungeonService.Enter, new EnterDungeonReq { SceneEntry = 3200201,
            InitData = new DungeonInitData { HiddenTreasureInitData = new HiddenTreasureInitData { NpcId = stone.Uid } } });
        Assert.Equal(0, (await Next(bot, DungeonService.Enter, EnterDungeonRsp.Parser)).Result);
        var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, s => s.SceneEntry == 3200201);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = 3200201 });
        await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, s => s.SceneId == 3200201);
        MonsterUnit? boss = null;
        for (int i = 0; i < 100 && boss == null; i++)
        {
            boss = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(Service<World>().AllUnits()
                .OfType<MonsterUnit>().SingleOrDefault(m => m.InstanceId == sw.SceneID && m.Template.NpcId == 16000008)));
            if (boss == null) await Task.Delay(20);
        }
        Assert.NotNull(boss);
        Assert.Equal(16003101, boss!.Template.NormalSkill); // "Gryphon Normal Attack": the boss model plays its own swing
        await OnLoop(() => { boss.Hp = 1; return Task.CompletedTask; });
        await bot.SendGameAsync(MsgId.Walk, new WalkReq { Pos = boss.Pos, BStop = true, SpeedRate = 1 });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = boss.Uid });
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.Dsts.Any(d => d.DstID == boss.Uid && d.IsDead));
        var result = await Next(bot, DungeonService.Result, DungeonResNotify.Parser);
        Assert.Equal((DungeonRes)1, result.Res);
        Assert.Equal(3, result.Data.OnePieceData.BossLevel);
        // OnePieceMonsterConfig[16100003]: award 9103 (30000 Minted Coin), extra 9203 (gift box + 3 chests).
        Assert.Equal(coinBefore + 30000, Service<CharacterService>().CountOf(Session(bot), 2));
        Assert.Equal(3, Service<CharacterService>().CountOf(Session(bot), 1302000));
        Assert.Equal(1, Service<CharacterService>().CountOf(Session(bot), 1302002));
        // A cleared stone is gone: it cannot be fought (or paid) twice.
        Assert.DoesNotContain(await Service<GameLoop>().InvokeAsync(() =>
            Task.FromResult(Service<TimedBattleService>().RiftStones.ToArray())), s => s.Uid == stone.Uid);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private long seconds = now.ToUnixTimeSeconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref seconds));
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");
        public void Set(long value) => Interlocked.Exchange(ref seconds, value);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "data", "config"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("data/config");
    }
}
