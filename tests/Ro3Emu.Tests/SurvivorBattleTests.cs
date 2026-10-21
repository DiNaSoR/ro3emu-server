using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

/// <summary>
/// Survivor Battle ("Chicken", match entry 2, scene 2240101) inside the match: stages, careers, landing, the 5036 round
/// state, chests and the match bag, the poison zone, death and revival, and the end ranking (TimedBattleService.Chicken*).
/// The lobby is shrunk with the server's test aid (ServerOptions.ChickenTestPlayers/TeamSize).
/// </summary>
public sealed class SurvivorBattleTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-survivor-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(DateTimeOffset.UnixEpoch);
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private Task OnLoop(Func<Task> work) => Service<GameLoop>().InvokeAsync(work);

    public Task InitializeAsync() => Task.CompletedTask;

    private async Task HostAsync(int players, int teamSize)
    {
        // Five minutes into the exported weekly window of activity 114, past the realm's opening weeks.
        _clock.Set(ScheduleRows.Weekly(114, new DateTime(2026, 11, 10), _clock.LocalTimeZone).Start + 300);
        var (port, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = port; o.MicroGatePort = micro; o.GameGatePort = game;
            o.SeasonStart = "2026-10-05";
            o.ChickenTestPlayers = players; o.ChickenTestTeamSize = teamSize; // test aid
        }, _clock);
        SureHitRolls.Install(_app);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{port}";
    }

    public async Task DisposeAsync()
    {
        if (_app == null) return;
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        await bot.EnterWorldAsync((await bot.CreateRoleAsync(name)).RoleBaseInfo.Id);
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={bot.Account}", new StringContent("!lvl 99"))).EnsureSuccessStatusCode();
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser,
        Func<T, bool>? match = null) where T : IMessage<T>
    {
        var packet = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), TimeSpan.FromSeconds(8));
        return parser.ParseFrom(packet.Body);
    }

    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);
    private static long Id(BotClient bot) => (long)bot.Self!.Uuid;
    private RoundDungeonInfo Round(BotClient bot) => (RoundDungeonInfo)Session(bot).Synced[TimedBattleService.RoundSync];
    private TimedBattleService Battles => Service<TimedBattleService>();

    private async Task Tick(long unixSeconds)
    {
        _clock.Set(unixSeconds);
        await OnLoop(() => { Battles.Tick(default); return Task.CompletedTask; });
    }

    /// <summary>Queue, ready and load into one room; returns the first 30401 (ChooseCareer).</summary>
    private async Task<DungeonBaseInfoNotify> StartAsync(params BotClient[] players)
    {
        foreach (var player in players)
        {
            await player.SendGameAsync(TimedBattleService.StartMatch, new StartMatchReq { MatchEntryId = 2 });
            Assert.Equal(0, (await Next(player, TimedBattleService.StartMatch, StartMatchResp.Parser)).Result);
        }
        foreach (var player in players)
        {
            await Next(player, TimedBattleService.ReadyNotify, MatchReadyNotify.Parser);
            await player.SendGameAsync(TimedBattleService.Ready, new MatchReadyReq { MatchEntryId = 2, PlayerId = Id(player), IsReady = true });
            Assert.Equal(0, (await Next(player, TimedBattleService.Ready, MatchReadyResp.Parser)).Result);
        }
        DungeonBaseInfoNotify info = null!;
        foreach (var player in players)
        {
            await Next(player, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, s => s.SceneEntry == 2240101);
            await player.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = 2240101 });
            Assert.Equal(0, (await Next(player, MsgId.SceneLoaded, SceneLoadedResp.Parser, s => s.SceneId == 2240101)).Result);
            info = await Next(player, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser, s => s.DungeonEntry == 2240101);
        }
        return info;
    }

    private async Task<DungeonBaseInfoNotify> NextStageAsync(BotClient[] players, DungeonBaseInfoNotify current)
    {
        int stage = current.StageModuleData.Stage + 1;
        await Tick(current.StageModuleData.StageEndTime / 1000 + 1);
        DungeonBaseInfoNotify next = null!;
        foreach (var player in players)
            next = await Next(player, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser, s => s.StageModuleData?.Stage == stage);
        return next;
    }

    /// <summary>Into the battle with everyone landing on the island centre.</summary>
    private async Task<DungeonBaseInfoNotify> BattleAsync(BotClient[] players, DungeonBaseInfoNotify stage0)
    {
        var info = await NextStageAsync(players, await NextStageAsync(players, stage0));
        foreach (var player in players)
        {
            await player.SendGameAsync(TimedBattleService.SelectPosition, new SelectRolePositionReq { PositionX = 430, PositionY = 402 });
            Assert.Equal(0, (await Next(player, TimedBattleService.SelectPosition, SelectRolePositionResp.Parser)).Result);
        }
        return await NextStageAsync(players, info);
    }

    /// <summary>Past the 3-minute free revive (a death is then final); waits for the first shrink so that tick ran.</summary>
    private async Task PastFreeReviveAsync(BotClient[] players, DungeonBaseInfoNotify battle)
    {
        await Tick(battle.StageModuleData.StageStartTime / 1000 + 181);
        foreach (var player in players)
        {
            var shrink = await Next(player, DungeonService.Event, DungeonEventNotify.Parser, e => e.Vars.Any(v => v.VarId == 100010003));
            Assert.Equal(11, shrink.Vars.Single(v => v.VarId == 100010003).Vi64.Count);
        }
    }

    private Task KillAsync(BotClient bot) => OnLoop(() => Service<OwnerRegistry>().RunRuntimeAsync("test death",
        async () => await Service<CombatService>().EnvironmentDamageAsync(Session(bot), 100_000_000)));

    private Task MoveAsync(BotClient bot, float x, float z) => OnLoop(() =>
    {
        var s = Session(bot);
        s.Position = new Vec3 { X = x, Y = s.Position.Y, Z = z };
        return Task.CompletedTask;
    });

    /// <summary>A refused or lapsed ready check fails the match for everyone with 82643, naming who did not confirm.</summary>
    [ClientDataFact]
    public async Task RefusedOrLapsedReadyCheckFailsTheMatchNamingThePlayer()
    {
        await HostAsync(2, 1);
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        var players = new[] { alice, bob };
        async Task QueueAndBobReadyAsync()
        {
            foreach (var player in players)
            {
                await player.SendGameAsync(TimedBattleService.StartMatch, new StartMatchReq { MatchEntryId = 2 });
                Assert.Equal(0, (await Next(player, TimedBattleService.StartMatch, StartMatchResp.Parser)).Result);
            }
            foreach (var player in players)
                await Next(player, TimedBattleService.ReadyNotify, MatchReadyNotify.Parser,
                    n => n.ReadyInfos.All(i => i.Status == MatchReadyStatus.None));
            await bob.SendGameAsync(TimedBattleService.Ready, new MatchReadyReq { MatchEntryId = 2, PlayerId = Id(bob), IsReady = true });
            Assert.Equal(0, (await Next(bob, TimedBattleService.Ready, MatchReadyResp.Parser)).Result);
        }
        async Task FailedAsync()
        {
            foreach (var player in players)
            {
                var fail = await Next(player, DungeonMatchService.FailNotify, FailMatchNtf.Parser);
                Assert.Equal(2, fail.MatchEntryId);
                Assert.Equal(ErrorCodes.DungeonDeclined, fail.Result);
                Assert.Equal(Id(alice), fail.PenaltyMemberGuid);
                Assert.Equal(Id(alice), fail.FailPlayer);
                Assert.Equal(0, fail.PenaltyMemberUnlockTs);
            }
        }

        await QueueAndBobReadyAsync();
        await alice.SendGameAsync(TimedBattleService.Ready, new MatchReadyReq { MatchEntryId = 2, PlayerId = Id(alice), IsReady = false });
        Assert.Equal(ErrorCodes.DungeonDeclined, (await Next(alice, TimedBattleService.Ready, MatchReadyResp.Parser)).Result);
        await FailedAsync();

        await QueueAndBobReadyAsync();
        await Tick(_clock.GetUtcNow().ToUnixTimeSeconds() + 31);
        await FailedAsync();
        Assert.False(Battles.IsMatching(Session(alice)));
        Assert.False(Battles.IsMatching(Session(bob)));
    }

    [ClientDataFact]
    public async Task StagesCareersAndLandingFollowTheDungeonScript()
    {
        await HostAsync(2, 1);
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        var players = new[] { alice, bob };
        var stage0 = await StartAsync(players);
        // DL LoginWaitingTime 13 s folded into the 30 s career pick; times in ms.
        Assert.Equal(0, stage0.StageModuleData.Stage);
        Assert.Equal(43_000, stage0.StageModuleData.StageEndTime - stage0.StageModuleData.StageStartTime);
        Assert.Equal(2, stage0.Vars.Single(v => v.VarId == 704).Vi64.Single());

        // The whole 5036 on load: the team, seeded bars and pack (client full handler pairs them unguarded).
        var full = await Next(alice, TimedBattleService.RoundSync, RoundDungeonInfoIncrement.Parser, r => r.SyncAllMark);
        Assert.True(full.TeamMemberInfo[Id(alice)].All.BattleAttr.Count > 0);
        Assert.True(full.PackInfos.ContainsKey(0));
        Assert.True(full.PackInfos[0].All.ItemInfos.ContainsKey(1200));
        var bars = full.ExtendInfo.All.ChickenDungeonInfo;
        Assert.True(bars.Items.ContainsKey(0) && bars.Skills.ContainsKey(0) && bars.Equips.ContainsKey(0));
        Assert.Equal(1, full.FreeRelifeCountLeft);

        await alice.SendGameAsync(TimedBattleService.SelectPosition, new SelectRolePositionReq { PositionX = 300, PositionY = 400 });
        Assert.Equal(ErrorCodes.FeatureUnavailable, (await Next(alice, TimedBattleService.SelectPosition, SelectRolePositionResp.Parser)).Result);
        await alice.SendGameAsync(TimedBattleService.SelectCareer, new SelectRoleCareerReq { Career = 999 });
        Assert.Equal(ErrorCodes.Parameter, (await Next(alice, TimedBattleService.SelectCareer, SelectRoleCareerResp.Parser)).Result);
        await alice.SendGameAsync(TimedBattleService.SelectCareer, new SelectRoleCareerReq { Career = 103 });
        var picked = await Next(alice, TimedBattleService.SelectCareer, SelectRoleCareerResp.Parser);
        Assert.Equal((0, 103), (picked.Result, picked.Career));
        await Next(alice, TimedBattleService.RoundSync, RoundDungeonInfoIncrement.Parser, r => r.HasCareerId && r.CareerId == 103);

        var show = await NextStageAsync(players, stage0);
        Assert.Equal(10_000, show.StageModuleData.StageEndTime - show.StageModuleData.StageStartTime);
        await alice.SendGameAsync(TimedBattleService.SelectCareer, new SelectRoleCareerReq { Career = 104 });
        Assert.Equal(ErrorCodes.FeatureUnavailable, (await Next(alice, TimedBattleService.SelectCareer, SelectRoleCareerResp.Parser)).Result);
        await OnLoop(() =>
        {
            Assert.Equal(103, Round(alice).CareerId);
            Assert.Equal(101, Round(bob).CareerId); // no pick: the first shown career its team did not take (inferred)
            // The career build replaces the character's own (EatChickenGameCareer._iJobAttr: max HP 15400).
            Assert.Equal(15400, Service<CharacterService>().Stats(Session(alice))[101]);
            Assert.False(Battles.CanFight(Session(alice), Session(bob)));
            return Task.CompletedTask;
        });

        var landing = await NextStageAsync(players, show);
        Assert.Equal(30_000, landing.StageModuleData.StageEndTime - landing.StageModuleData.StageStartTime);
        await alice.SendGameAsync(TimedBattleService.SelectPosition, new SelectRolePositionReq { PositionX = 300, PositionY = 400 });
        Assert.Equal(0, (await Next(alice, TimedBattleService.SelectPosition, SelectRolePositionResp.Parser)).Result);
        var picks = await Next(alice, TimedBattleService.PositionNotify, SelectRolePositionNotify.Parser);
        Assert.Equal((300, 400), (picks.Infos[Id(alice)].PositionX, picks.Infos[Id(alice)].PositionY));
        Assert.False(picks.Infos.ContainsKey(Id(bob))); // the other team's landing is not shown

        var battle = await NextStageAsync(players, landing);
        Assert.Equal(1_200_000, battle.StageModuleData.StageEndTime - battle.StageModuleData.StageStartTime);
        Assert.Equal(battle.StageModuleData.StageStartTime / 1000 + 180, battle.Vars.Single(v => v.VarId == 707).Vi64.Single());
        var countdown = await Next(alice, DungeonService.Event, DungeonEventNotify.Parser, e => e.Vars.Any(v => v.VarId == 100010002));
        Assert.Equal(7, countdown.Vars.Single(v => v.VarId == 100010002).Vi64.Count);
        await OnLoop(() =>
        {
            var a = Session(alice);
            Assert.True(Math.Abs(a.Position.X - 300) < 10 && Math.Abs(a.Position.Z - 400) < 10, $"landed at {a.Position}");
            Assert.True(Battles.CanFight(a, Session(bob)));
            return Task.CompletedTask;
        });
    }

    [ClientDataFact]
    public async Task ChestPickupUseInsertUpgradeAndDiscardUseTheMatchBag()
    {
        await HostAsync(2, 1);
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        var players = new[] { alice, bob };
        await BattleAsync(players, await StartAsync(players));
        int normalBag = 0;
        await OnLoop(() => { normalBag = Session(alice).Items.Count; return Task.CompletedTask; });
        long chest = 0;
        await OnLoop(async () =>
        {
            Assert.True(Battles.ChestsForTest(Session(alice)).Count >= 12); // supply crates rolled at the battle start
            chest = await Battles.PlaceChestForTestAsync(Session(alice), 15000501, (10003, 2), (22001, 3), (30002, 1));
        });
        var items = Battles.ChestsForTest(Session(alice)).Single(c => c.Uid == chest).Items.Values.ToArray();
        long potion = items.Single(i => i.Entry == 10003).Guid;
        await MoveAsync(bob, Session(alice).Position.X, Session(alice).Position.Z);

        // Two players grab the same stack at once: exactly one gets it.
        await Task.WhenAll(
            alice.SendGameAsync(TimedBattleService.PickupItem, new PickUpChickenItemReq { NpcId = chest, ItemGuid = potion, Count = 2 }),
            bob.SendGameAsync(TimedBattleService.PickupItem, new PickUpChickenItemReq { NpcId = chest, ItemGuid = potion, Count = 2 }));
        var aPick = await Next(alice, TimedBattleService.PickupItem, PickUpChickenItemResp.Parser);
        var bPick = await Next(bob, TimedBattleService.PickupItem, PickUpChickenItemResp.Parser);
        Assert.Equal(1, new[] { aPick.Result, bPick.Result }.Count(r => r == 0));
        var winner = aPick.Result == 0 ? alice : bob;
        Assert.Equal(2, (aPick.Result == 0 ? aPick : bPick).Count);
        Assert.Equal(10003, (await Next(winner, TimedBattleService.PickupNotify, PickChickenItemNotify.Parser)).PickItems.Single().ItemEntry);

        await winner.SendGameAsync(TimedBattleService.PickupAll, new PickUpAllChickenItemReq { NpcIds = { chest } });
        Assert.Equal(0, (await Next(winner, TimedBattleService.PickupAll, PickUpAllChickenItemResp.Parser)).Result);
        await OnLoop(() =>
        {
            var pack = Round(winner).PackInfos.Values.Where(i => i.ItemEntry != 0).ToArray();
            Assert.Equal(2, pack.Single(i => i.ItemEntry == 10003).Stack);
            Assert.Equal(3, pack.Count(i => i.ItemEntry == 22001)); // stack limit 1
            Assert.Single(pack, i => i.ItemEntry == 30002);
            Assert.DoesNotContain(Battles.ChestsForTest(Session(winner)), c => c.Uid == chest); // emptied: gone
            Assert.Equal(normalBag, Session(alice).Items.Count); // the character's own bag is untouched
            return Task.CompletedTask;
        });
        RoundDungeonInfo round = null!;
        await OnLoop(() => { round = Round(winner); return Task.CompletedTask; });
        long Guid(int entry) => round.PackInfos.Values.First(i => i.ItemEntry == entry).ItemGuid;

        // Potion 10003: buff 15999700 restores 15% of max HP (T_AXadd0_etc [[2,1500,101,3]]); _iCd 30 s.
        await OnLoop(() => { Session(winner).CurrentHp = 1000; return Task.CompletedTask; });
        await winner.SendGameAsync(TimedBattleService.UseItem, new UseChickenItemReq { ItemGuid = Guid(10003), Count = 1 });
        Assert.Equal(0, (await Next(winner, TimedBattleService.UseItem, UseChickenItemResp.Parser)).Result);
        await OnLoop(() =>
        {
            // Out-of-combat regeneration (1 HP per 5 s with the career build) may add a point meanwhile.
            Assert.InRange(Service<CharacterService>().Stats(Session(winner))[2000], 1000 + 15400 * 1500 / 10000, 1000 + 15400 * 1500 / 10000 + 2);
            Assert.Equal(1, Round(winner).PackInfos.Values.Single(i => i.ItemEntry == 10003).Stack);
            Assert.True(Round(winner).ItemCdExpireTime[10003] > _clock.GetUtcNow().ToUnixTimeMilliseconds());
            return Task.CompletedTask;
        });
        await winner.SendGameAsync(TimedBattleService.UseItem, new UseChickenItemReq { ItemGuid = Guid(10003), Count = 1 });
        Assert.Equal(ErrorCodes.Cooldown, (await Next(winner, TimedBattleService.UseItem, UseChickenItemResp.Parser)).Result);

        // Equipment on the bar adds its _kAttribute (30002: max HP +1600); a skill on the bar, then its upgrade.
        await winner.SendGameAsync(TimedBattleService.InsertItem, new InsertChickenItemReq { Type = ChickenItemType.Equip, Index = 1, ItemGuid = Guid(30002) });
        Assert.Equal(0, (await Next(winner, TimedBattleService.InsertItem, InsertChickenItemResp.Parser)).Result);
        long skill = Guid(22001);
        await winner.SendGameAsync(TimedBattleService.InsertItem, new InsertChickenItemReq { Type = ChickenItemType.Skill, Index = 1, ItemGuid = skill });
        Assert.Equal(0, (await Next(winner, TimedBattleService.InsertItem, InsertChickenItemResp.Parser)).Result);
        await OnLoop(() =>
        {
            Assert.Equal(17000, Service<CharacterService>().Stats(Session(winner))[101]);
            Assert.Equal(skill, Round(winner).ExtendInfo.ChickenDungeonInfo.Skills[1]);
            return Task.CompletedTask;
        });
        await winner.SendGameAsync(TimedBattleService.ItemUpgrade, new ChickenItemUpgradeReq { ItemConfigId = 22001 });
        var upgraded = await Next(winner, TimedBattleService.ItemUpgrade, ChickenItemUpgradeResp.Parser);
        Assert.Equal(0, upgraded.Result);
        await OnLoop(() =>
        {
            var r = Round(winner);
            Assert.DoesNotContain(r.PackInfos.Values, i => i.ItemEntry == 22001);
            Assert.Equal((long)upgraded.NewItemGuid, r.PackInfos.Values.Single(i => i.ItemEntry == 21001).ItemGuid);
            Assert.Equal((long)upgraded.NewItemGuid, r.ExtendInfo.ChickenDungeonInfo.Skills[1]);
            return Task.CompletedTask;
        });

        // Discard: into a new Discarded Supply Crate where the player stands.
        await winner.SendGameAsync(TimedBattleService.DiscardItem, new DiscardChickenItemReq
        { Items = { [Guid(10003)] = 1 }, Pos = Session(winner).Position.Clone() });
        var discarded = await Next(winner, TimedBattleService.DiscardItem, DiscardChickenItemResp.Parser);
        Assert.Equal(0, discarded.Result);
        await OnLoop(() =>
        {
            var crate = Battles.ChestsForTest(Session(winner)).Single(c => c.Uid == discarded.NpcId);
            Assert.Equal(15000053, crate.NpcId);
            Assert.Equal(10003, crate.Items.Values.Single().Entry);
            Assert.DoesNotContain(Round(winner).PackInfos.Values, i => i.ItemEntry == 10003);
            return Task.CompletedTask;
        });
    }

    [ClientDataFact]
    public async Task PoisonHurtsOnlyOutsideTheSafeCircle()
    {
        await HostAsync(2, 1);
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        var players = new[] { alice, bob };
        var battle = await BattleAsync(players, await StartAsync(players));
        // The resurrection point (81, 145) is ~433 m from the island circle's centre (430, 402; r 400).
        await MoveAsync(alice, 81, 145);
        await Tick(battle.StageModuleData.StageStartTime / 1000 + 1);
        var poison = await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, b => b.Buffs.Any(x => x.ConfigId == 15999999));
        Assert.Equal(Id(alice), poison.UnitId);
        long expected = 15400 - 15400 * 500 / 10000; // circle 0: chicken_poison 500 basis points of max HP
        for (int i = 0; i < 40; i++)
        {
            long hp = 0;
            await OnLoop(() => { hp = Service<CharacterService>().Stats(Session(alice))[2000]; return Task.CompletedTask; });
            if (hp <= expected + 2) break;
            await Task.Delay(50);
        }
        await OnLoop(() =>
        {
            Assert.InRange(Service<CharacterService>().Stats(Session(alice))[2000], expected, expected + 2); // + regeneration
            Assert.Equal(15400, Service<CharacterService>().Stats(Session(bob))[2000]);
            return Task.CompletedTask;
        });
    }

    [ClientDataFact]
    public async Task FreeReviveThenIdTagAndTeammateRevive()
    {
        await HostAsync(4, 2);
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var cara = await InWorld("Cara");
        await using var dan = await InWorld("Dan");
        var players = new[] { alice, bob, cara, dan };
        var battle = await BattleAsync(players, await StartAsync(players));
        Assert.Equal(Round(alice).TeamId, Round(bob).TeamId);

        // Inside the first 3 minutes: the free revive map, then the chosen point.
        await KillAsync(bob);
        var map = await Next(bob, TimedBattleService.RelifeNotify, ChickenRelifeNotify.Parser, n => n.RelifeEndTime > 0);
        Assert.True(map.RelifeEndTime > battle.StageModuleData.StageStartTime);
        await bob.SendGameAsync(CombatService.Relive, new EntityReliveReq());
        Assert.Equal(ErrorCodes.SkillReviveNotAllowed, (await Next(bob, CombatService.Relive, EntityReliveAck.Parser)).Result);
        await bob.SendGameAsync(TimedBattleService.Relife, new ChickenRelifeReq { PositionX = 431, PositionY = 403 });
        Assert.Equal(0, (await Next(bob, TimedBattleService.Relife, ChickenRelifeResp.Parser)).Result);
        await Next(bob, TimedBattleService.RelifeNotify, ChickenRelifeNotify.Parser, n => n.RelifeEndTime == 0);
        await OnLoop(() =>
        {
            Assert.False(Session(bob).Dead);
            Assert.Equal(0, Round(bob).FreeRelifeCountLeft);
            Assert.Equal(RoundRoleStatus.InWar, Round(alice).TeamMemberInfo[Id(bob)].State);
            return Task.CompletedTask;
        });

        // Past it: a final death drops the ID tag; a teammate carries it and channels the revive (20 s).
        await PastFreeReviveAsync(players, battle);
        await KillAsync(bob);
        ChickenChestUnit tag = null!;
        await OnLoop(() =>
        {
            tag = Battles.ChestsForTest(Session(alice)).Single(c => c.NpcId == 15000056);
            Assert.Equal(Id(bob), tag.Items.Values.Single().OwnerId);
            Assert.Equal(RoundRoleStatus.Death, Round(alice).TeamMemberInfo[Id(bob)].State);
            return Task.CompletedTask;
        });
        await MoveAsync(alice, tag.X, tag.Z);
        await alice.SendGameAsync(TimedBattleService.PickupItem, new PickUpChickenItemReq
        { NpcId = tag.Uid, ItemGuid = tag.Items.Values.Single().Guid, Count = 1 });
        Assert.Equal(0, (await Next(alice, TimedBattleService.PickupItem, PickUpChickenItemResp.Parser)).Result);
        await OnLoop(() =>
        {
            Assert.True(Round(alice).ExtendInfo.ChickenDungeonInfo.IdentityCards.ContainsKey(Id(bob)));
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(TimedBattleService.StartRevive, new ChickenStartReviveReq());
        Assert.Equal(0, (await Next(alice, TimedBattleService.StartRevive, ChickenStartReviveResp.Parser)).Result);
        var channel = await Next(bob, TimedBattleService.RevivingNotify, ChickenOtherRevivingNotify.Parser);
        Assert.Equal((Id(alice), false, 20_000L), (channel.Reviver, channel.Interrupted, channel.ReviveDuration));
        Assert.Equal(Id(bob), channel.BeReviveIds.Single());
        await Tick(_clock.GetUtcNow().ToUnixTimeSeconds() + 21);
        Assert.True((await Next(bob, TimedBattleService.RelifeNotify, ChickenRelifeNotify.Parser, n => n.RelifeEndTime > 0)).RelifeEndTime > 0);
        await bob.SendGameAsync(TimedBattleService.Relife, new ChickenRelifeReq { PositionX = 430, PositionY = 402 });
        Assert.Equal(0, (await Next(bob, TimedBattleService.Relife, ChickenRelifeResp.Parser)).Result);
        await Next(bob, TimedBattleService.RelifeNotify, ChickenRelifeNotify.Parser, n => n.RelifeEndTime == 0);
        await OnLoop(() =>
        {
            Assert.False(Session(bob).Dead);
            Assert.DoesNotContain(Round(alice).PackInfos.Values, i => i.ItemEntry == 50001); // the card is spent
            Assert.True(Battles.CanFight(Session(bob), Session(cara)));
            return Task.CompletedTask;
        });
    }

    [ClientDataFact]
    public async Task GivingUpAndTheLastTeamStandingDecideTheRanking()
    {
        await HostAsync(3, 1);
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var cara = await InWorld("Cara");
        var players = new[] { alice, bob, cara };
        var battle = await BattleAsync(players, await StartAsync(players));
        await PastFreeReviveAsync(players, battle);

        await bob.SendGameAsync(TimedBattleService.ForceDead, new ChickenPlayerForceDeadReq());
        Assert.Equal(0, (await Next(bob, TimedBattleService.ForceDead, ChickenPlayerForceDeadResp.Parser)).Result);
        var provisional = await Next(bob, TimedBattleService.ChickenResult, ChickenResultNotify.Parser);
        Assert.Equal(3, provisional.RoleStatistics.Count);
        var hud = await Next(alice, DungeonService.Event, DungeonEventNotify.Parser, e => e.Vars.Any(v => v.VarId == 701));
        Assert.Equal(2, hud.Vars.Single(v => v.VarId == 701).Vi64.Single());

        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Battles.SettlementCompletedForTest = () => settled.TrySetResult();
        await KillAsync(cara);
        await settled.Task.WaitAsync(TimeSpan.FromSeconds(8));
        var result = await Next(alice, TimedBattleService.ChickenResult, ChickenResultNotify.Parser);
        Assert.False(result.HasAborted);
        Assert.Equal(battle.StageModuleData.StageStartTime / 1000, result.BattleStartTime); // seconds
        Assert.Equal(1, result.TeamStatistics.ExtraData[1012001].LongValue);
        Assert.Equal(Id(alice), result.TeamStatistics.ExtraData[1012002].LongValue);
        Assert.Equal(3, result.SceneStatistics[1015001].LongValue);
        Assert.Equal(2, result.RoleStatistics[Id(cara)].ExtraData[1011003].LongValue); // eliminated last
        Assert.Equal(3, result.RoleStatistics[Id(bob)].ExtraData[1011003].LongValue);
        Assert.Equal(1, result.RoleStatistics[Id(cara)].ExtraData[1001006].LongValue);
        Assert.Equal(100, result.RoleStatistics[Id(alice)].ExtraData[1011006].LongValue); // AddScore
        Assert.Equal(2, result.RoleStatistics[Id(alice)].ExtraData[1011011].LongValue); // CurRankID
        var loss = await Next(cara, TimedBattleService.ChickenResult, ChickenResultNotify.Parser);
        Assert.Equal(2, loss.TeamStatistics.ExtraData[1012001].LongValue);
        await OnLoop(() =>
        {
            Assert.Equal(2, Session(alice).TimedBattle!.ChickenRank);
            Assert.Equal(0, Battles.ActiveMatchCountForTest);
            Assert.False(Session(alice).Synced.ContainsKey(TimedBattleService.RoundSync));
            return Task.CompletedTask;
        });
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private long _seconds = now.ToUnixTimeSeconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _seconds));
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");
        public void Set(long value) => Interlocked.Exchange(ref _seconds, value);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "data", "config"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("data/config");
    }

    /// <summary>Shop NPCs from the respawn regions sell the match's rolled goods for the pack's currency 10006.</summary>
    [ClientDataFact]
    public async Task ShopNpcsSellRolledGoodsForTheMatchCurrency()
    {
        await HostAsync(2, 1);
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        var players = new[] { alice, bob };
        await BattleAsync(players, await StartAsync(players));
        ChickenNpcUnit shop = null!;
        await OnLoop(() =>
        {
            var npcs = Battles.NpcsForTest(Session(alice));
            Assert.Equal(3, npcs.Count(n => n.NpcId == 15000054 && n.ShopId == 801));
            Assert.Equal(5, npcs.Count(n => n.NpcId == 15000060 && n.ShopId == 802));
            Assert.Equal(2, npcs.Count(n => n.NpcId == 15000055 && n.ShopId == 0));
            shop = npcs.First(n => n.ShopId == 801);
            return Task.CompletedTask;
        });

        // Too far: the interaction is refused; at the NPC it is echoed and the list is the rolled goods.
        await MoveAsync(alice, shop.X + 30, shop.Z);
        await alice.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq { NpcUid = shop.Uid, InteractionId = 2900003 });
        Assert.Equal(ErrorCodes.NpcTooFar, (await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
        await MoveAsync(alice, shop.X, shop.Z);
        await alice.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq { NpcUid = shop.Uid, InteractionId = 2900003 });
        Assert.Equal(0, (await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
        await alice.SendGameAsync(TimedBattleService.DungeonShop, new GetDungeonShopReq { NpcGuid = shop.Uid });
        var list = await Next(alice, TimedBattleService.DungeonShop, GetDungeonShopResp.Parser);
        Assert.Equal(4 + 6 + 3 + 5 + 15, list.CommodityInfo.Count); // ShopConfig 27-29 _kGroup counts
        var commodities = Service<GameData>().Table("ShopCommodityConfig");
        long Group(long id) => GameData.Int(commodities[id.ToString()], "_iGroup");
        Assert.Equal(4, list.CommodityInfo.Keys.Count(id => Group(id) == 1001));
        long skill = list.CommodityInfo.Keys.First(id => Group(id) == 1010); // a skill book: 3888, one per match
        Assert.Equal((1L, 1L), (list.CommodityInfo[skill].RemainCount, list.CommodityInfo[skill].CanBuyCount));

        await alice.SendGameAsync(ShopService.BuyCommodity, new BuyCommodityReq { CommodityId = (int)skill, Count = 1, NpcId = shop.Uid });
        Assert.Equal(ErrorCodes.CurrencyShort, (await Next(alice, ShopService.BuyCommodity, BuyCommodityResp.Parser)).Result);
        long chest = 0;
        await OnLoop(async () => chest = await Battles.PlaceChestForTestAsync(Session(alice), 15000501, (10006, 5000)));
        await alice.SendGameAsync(TimedBattleService.PickupAll, new PickUpAllChickenItemReq { NpcIds = { chest } });
        Assert.Equal(0, (await Next(alice, TimedBattleService.PickupAll, PickUpAllChickenItemResp.Parser)).Result);
        await alice.SendGameAsync(ShopService.BuyCommodity, new BuyCommodityReq { CommodityId = (int)skill, Count = 2, NpcId = shop.Uid });
        Assert.Equal(ErrorCodes.PurchaseLimit, (await Next(alice, ShopService.BuyCommodity, BuyCommodityResp.Parser)).Result);
        await alice.SendGameAsync(ShopService.BuyCommodity, new BuyCommodityReq { CommodityId = (int)skill, Count = 1, NpcId = shop.Uid });
        var bought = await Next(alice, ShopService.BuyCommodity, BuyCommodityResp.Parser);
        Assert.Equal((0, 3888, 1), (bought.Result, bought.Price, bought.BuyTimes));
        var after = await Next(alice, TimedBattleService.DungeonShop, GetDungeonShopResp.Parser);
        Assert.Equal((0L, 0L), (after.CommodityInfo[skill].RemainCount, after.CommodityInfo[skill].CanBuyCount));
        int book = (int)GameData.Int(commodities[skill.ToString()], "_iItemId");
        await OnLoop(() =>
        {
            var pack = Round(alice).PackInfos.Values.Where(i => i.ItemEntry != 0).ToArray();
            Assert.Equal(5000 - 3888, pack.Single(i => i.ItemEntry == 10006).Stack);
            Assert.Single(pack, i => i.ItemEntry == book);
            return Task.CompletedTask;
        });

        // Another player's list keeps its own counts.
        await bob.SendGameAsync(TimedBattleService.DungeonShop, new GetDungeonShopReq { NpcGuid = shop.Uid });
        Assert.Equal(1L, (await Next(bob, TimedBattleService.DungeonShop, GetDungeonShopResp.Parser)).CommodityInfo[skill].RemainCount);
    }

    /// <summary>Follow: an invite, the accepted follow (the follower lands with its leader); the Rescue Wagon spends
    /// a carried ID card to bring a fallen teammate back; the shop head sign reaches the others.</summary>
    [ClientDataFact]
    public async Task FollowLandingRescueWagonAndHeadSigns()
    {
        await HostAsync(4, 2);
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var cara = await InWorld("Cara");
        await using var dan = await InWorld("Dan");
        var players = new[] { alice, bob, cara, dan };
        var choose = await NextStageAsync(players, await NextStageAsync(players, await StartAsync(players)));
        Assert.Equal(Round(alice).TeamId, Round(bob).TeamId);

        await alice.SendGameAsync(TimedBattleService.SelectFollow, new SelectFollowReq
        { Type = EChickenFollowNotifyType.Invite, RoleId = Id(bob), FollowedRoleId = Id(alice) });
        Assert.Equal(0, (await Next(alice, TimedBattleService.SelectFollow, SelectFollowResp.Parser)).Result);
        Assert.Equal(Id(alice), (await Next(bob, TimedBattleService.InviteFollowNotify, ChickenInviteFollowNotify.Parser)).FollowedRoleId);
        Assert.True((await Next(alice, TimedBattleService.FollowNotify, ChickenSelectFollowResp.Parser)).InviteCd[Id(bob)] > 0);
        await alice.SendGameAsync(TimedBattleService.SelectFollow, new SelectFollowReq
        { Type = EChickenFollowNotifyType.Invite, RoleId = Id(bob), FollowedRoleId = Id(alice) });
        Assert.Equal(ErrorCodes.DungeonBusy, (await Next(alice, TimedBattleService.SelectFollow, SelectFollowResp.Parser)).Result);
        await bob.SendGameAsync(TimedBattleService.SelectFollow, new SelectFollowReq
        { Type = EChickenFollowNotifyType.Followed, RoleId = Id(bob), FollowedRoleId = Id(alice) });
        Assert.Equal(0, (await Next(bob, TimedBattleService.SelectFollow, SelectFollowResp.Parser)).Result);
        Assert.Equal(Id(alice), (await Next(alice, TimedBattleService.FollowNotify, ChickenSelectFollowResp.Parser,
            f => f.FollowedMap.ContainsKey(Id(bob)))).FollowedMap[Id(bob)]);
        await alice.SendGameAsync(TimedBattleService.SelectFollow, new SelectFollowReq
        { Type = EChickenFollowNotifyType.Followed, RoleId = Id(alice), FollowedRoleId = Id(bob) });
        Assert.Equal(ErrorCodes.Parameter, (await Next(alice, TimedBattleService.SelectFollow, SelectFollowResp.Parser)).Result); // a circle

        // Bob picks elsewhere, but lands with Alice.
        foreach (var (player, x, z) in new[] { (alice, 430, 402), (bob, 200, 500), (cara, 430, 402), (dan, 430, 402) })
        {
            await player.SendGameAsync(TimedBattleService.SelectPosition, new SelectRolePositionReq { PositionX = x, PositionY = z });
            Assert.Equal(0, (await Next(player, TimedBattleService.SelectPosition, SelectRolePositionResp.Parser)).Result);
        }
        var battle = await NextStageAsync(players, choose);
        await OnLoop(() =>
        {
            Assert.InRange(MonsterService.Distance(Session(bob).Position.X, Session(bob).Position.Z, 430, 402), 0, 45);
            return Task.CompletedTask;
        });

        // The shop sign: shown to the others while the panel is open.
        await alice.SendGameAsync(TimedBattleService.OpenPanel, new ChickenOpenPanelReq { PanelNum = EChickenOpenPanel.Shop, IsOpen = true });
        await Next(alice, TimedBattleService.OpenPanel, ChickenOpenPanelResp.Parser);
        var sign = await Next(cara, WorldCoreService.CustomChange, CustomDataChange.Parser, c => c.CustomDatas.Any(d => d.Id == 5000008));
        Assert.Equal((Id(alice), 1L), (sign.EntityId, sign.CustomDatas.Single().CustomParam.IntParam));

        // A final death, the ID tag carried to a Rescue Wagon: Bob is back without the 20 s channel.
        await PastFreeReviveAsync(players, battle);
        await KillAsync(bob);
        ChickenChestUnit tag = null!;
        ChickenNpcUnit wagon = null!;
        await OnLoop(() =>
        {
            tag = Battles.ChestsForTest(Session(alice)).Single(c => c.NpcId == 15000056);
            wagon = Battles.NpcsForTest(Session(alice)).First(n => n.NpcId == 15000055);
            return Task.CompletedTask;
        });
        await MoveAsync(alice, wagon.X, wagon.Z);
        await alice.SendGameAsync(TimedBattleService.RelifeOther, new ChickenRelifeOtherReq { NpcId = wagon.Uid });
        Assert.Equal(ErrorCodes.ItemsShort, (await Next(alice, TimedBattleService.RelifeOther, ChickenRelifeOtherResp.Parser)).Result);
        await MoveAsync(alice, tag.X, tag.Z);
        await alice.SendGameAsync(TimedBattleService.PickupItem, new PickUpChickenItemReq
        { NpcId = tag.Uid, ItemGuid = tag.Items.Values.Single().Guid, Count = 1 });
        Assert.Equal(0, (await Next(alice, TimedBattleService.PickupItem, PickUpChickenItemResp.Parser)).Result);
        await MoveAsync(alice, wagon.X, wagon.Z);
        await alice.SendGameAsync(TimedBattleService.RelifeOther, new ChickenRelifeOtherReq { NpcId = wagon.Uid });
        Assert.Equal(0, (await Next(alice, TimedBattleService.RelifeOther, ChickenRelifeOtherResp.Parser)).Result);
        var map = await Next(bob, TimedBattleService.RelifeNotify, ChickenRelifeNotify.Parser, n => n.RelifeEndTime > 0);
        Assert.Equal(((int)wagon.X, (int)wagon.Z), (map.PositionX, map.PositionY));
        await bob.SendGameAsync(TimedBattleService.Relife, new ChickenRelifeReq { PositionX = (int)wagon.X, PositionY = (int)wagon.Z });
        Assert.Equal(0, (await Next(bob, TimedBattleService.Relife, ChickenRelifeResp.Parser)).Result);
        await Next(bob, TimedBattleService.RelifeNotify, ChickenRelifeNotify.Parser, n => n.RelifeEndTime == 0);
        await OnLoop(() =>
        {
            Assert.False(Session(bob).Dead);
            Assert.DoesNotContain(Round(alice).PackInfos.Values, i => i.ItemEntry == 50001); // the card is spent
            return Task.CompletedTask;
        });
    }

    [ClientDataFact]
    public async Task EliminatedPlayerWatchesAStandingTeammate()
    {
        await HostAsync(4, 2);
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var cara = await InWorld("Cara");
        await using var dan = await InWorld("Dan");
        var players = new[] { alice, bob, cara, dan };
        var battle = await BattleAsync(players, await StartAsync(players));
        Assert.Equal(Round(alice).TeamId, Round(bob).TeamId);
        long start = battle.StageModuleData.StageStartTime / 1000;
        await PastFreeReviveAsync(players, battle);

        // Out for good: the countdown to watching, then the server picks Alice.
        await KillAsync(bob);
        var offer = await Next(bob, TimedBattleService.WatchNotify, ChickenWatchNotify.Parser);
        Assert.InRange(offer.AutoWatchTime, start + 181 + 9, start + 181 + 11);
        await Tick(offer.AutoWatchTime + 1);
        var bind = await Next(bob, WorldCoreService.CustomChange, CustomDataChange.Parser, c => c.CustomDatas.Any(d => d.Id == 1002));
        Assert.Equal((Id(bob), Id(alice)), (bind.EntityId, bind.CustomDatas.Single().CustomParam.IntParam));
        await OnLoop(() =>
        {
            var b = Session(bob);
            Assert.True(b.AoiHidden);
            Assert.Contains(Session(alice), b.Visible);
            Assert.DoesNotContain(b, Session(cara).Visible);
            Assert.DoesNotContain(b, Session(alice).Visible);
            return Task.CompletedTask;
        });

        // Only a standing teammate can be watched; the view follows Alice.
        await bob.SendGameAsync(TimedBattleService.ChangeWatch, new ChickenChangeWatchPlayerReq { PlayerId = Id(cara) });
        await MoveAsync(alice, 300, 300);
        await Tick(offer.AutoWatchTime + 2);
        for (int i = 0; ; i++)
        {
            bool there = false;
            await OnLoop(() =>
            {
                there = MonsterService.Distance(Session(bob).Position.X, Session(bob).Position.Z, 300, 300) < 1;
                return Task.CompletedTask;
            });
            if (there) break;
            Assert.True(i < 50, "the spectator's view did not follow");
            await Task.Delay(100);
        }
    }

    /// <summary>Magic Dice: a touch opens a 3-card pick through 5039; Kaka Coin redraws it; a pick applies the card;
    /// an unanswered pick takes the first card at its endTime.</summary>
    [ClientDataFact]
    public async Task MagicDieOffersACardPickThatRefreshesAndApplies()
    {
        await HostAsync(2, 1);
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        var players = new[] { alice, bob };
        await BattleAsync(players, await StartAsync(players));
        ChickenNpcUnit die = null!;
        await OnLoop(() =>
        {
            var dice = Battles.NpcsForTest(Session(alice)).Where(n => n.NpcId == 15000301).ToArray();
            Assert.Equal(50, dice.Length);
            die = dice[0];
            return Task.CompletedTask;
        });
        DBAdventureInfo Seen(BotClient bot) => (DBAdventureInfo)Session(bot).Synced[TimedBattleService.AdventureSync];
        AdventureEventSelectInfo Open(BotClient bot) => Seen(bot).FunctionInfos[1003].Deck.Values.Single().NeedSelectMap.Values.Single();

        await MoveAsync(alice, die.X, die.Z);
        await alice.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq { NpcUid = die.Uid, InteractionId = 2900010 });
        Assert.Equal(0, (await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
        var first = await Next(alice, TimedBattleService.AdventureSync, DBAdventureInfoIncrement.Parser);
        Assert.True(first.SyncAllMark);
        Assert.Equal(1003, first.CurActiveFunctionId);
        AdventureEventSelectInfo pick = null!;
        var pool = Service<GameData>().Table("AdventureCardConfig").Values
            .Where(r => GameData.Int(r, "_iPoolId") == 1003).Select(r => GameData.Int(r, "_iCardId")).ToHashSet();
        await OnLoop(() =>
        {
            Assert.DoesNotContain(Battles.NpcsForTest(Session(alice)), n => n.Uid == die.Uid); // used up
            pick = Open(alice).Clone();
            Assert.Equal((1003, 1, 0), (pick.PoolId, pick.ChooseCount, pick.RefreshCount));
            Assert.Equal(new[] { 1, 2, 3 }, pick.Cards.Keys.Order().ToArray());
            Assert.Equal(3, pick.Cards.Values.Distinct().Count());
            Assert.All(pick.Cards.Values, c => Assert.Contains(c, pool));
            Assert.InRange(pick.EndTime - _clock.GetUtcNow().ToUnixTimeSeconds(), 9, 10);
            return Task.CompletedTask;
        });

        // A redraw costs 1000 Kaka Coin, then 2000.
        await alice.SendGameAsync(TimedBattleService.AdventureRefresh, new AdventureRefreshReq { OperateId = pick.OperateId });
        Assert.Equal(ErrorCodes.CurrencyShort, (await Next(alice, TimedBattleService.AdventureRefresh, AdventureRefreshResp.Parser)).Result);
        long chest = 0;
        await OnLoop(async () => chest = await Battles.PlaceChestForTestAsync(Session(alice), 15000501, (10006, 5000), (30002, 1)));
        await alice.SendGameAsync(TimedBattleService.PickupAll, new PickUpAllChickenItemReq { NpcIds = { chest } });
        Assert.Equal(0, (await Next(alice, TimedBattleService.PickupAll, PickUpAllChickenItemResp.Parser)).Result);
        await alice.SendGameAsync(TimedBattleService.AdventureRefresh, new AdventureRefreshReq { OperateId = pick.OperateId });
        Assert.Equal(0, (await Next(alice, TimedBattleService.AdventureRefresh, AdventureRefreshResp.Parser)).Result);
        await OnLoop(() =>
        {
            Assert.Equal(1, Open(alice).RefreshCount);
            Assert.Equal(4000, Round(alice).PackInfos.Values.Single(i => i.ItemEntry == 10006).Stack);
            pick = Open(alice).Clone();
            return Task.CompletedTask;
        });

        // Only an offered card, exactly chooseCount of them; the pick closes and the card joins the deck.
        await alice.SendGameAsync(TimedBattleService.AdventureSelect, new AdventureEventSelectReq { OperateId = pick.OperateId, CardId = { 1 } });
        Assert.Equal(ErrorCodes.Parameter, (await Next(alice, TimedBattleService.AdventureSelect, AdventureEventSelectResp.Parser)).Result);
        long chosen = pick.Cards[1];
        await alice.SendGameAsync(TimedBattleService.AdventureSelect, new AdventureEventSelectReq { OperateId = pick.OperateId, CardId = { chosen } });
        var selected = await Next(alice, TimedBattleService.AdventureSelect, AdventureEventSelectResp.Parser);
        Assert.Equal((0, pick.OperateId), (selected.Result, selected.Req.OperateId));
        await OnLoop(() =>
        {
            var deck = Seen(alice).FunctionInfos[1003].Deck.Values.Single();
            Assert.Empty(deck.NeedSelectMap);
            Assert.Equal(chosen, deck.Cards.Values.Single().CardId);
            return Task.CompletedTask;
        });

        // Golden Thief Bug: +5000 Kaka Coin. Baphomet: the worn 30002 becomes 30102 (max HP +1600 -> +3200).
        long coins = 0;
        await OnLoop(() => { coins = Round(alice).PackInfos.Values.Where(i => i.ItemEntry == 10006).Sum(i => (long)i.Stack); return Task.CompletedTask; });
        long equip = 0;
        await OnLoop(() => { equip = Round(alice).PackInfos.Values.First(i => i.ItemEntry == 30002).ItemGuid; return Task.CompletedTask; });
        await alice.SendGameAsync(TimedBattleService.InsertItem, new InsertChickenItemReq { Type = ChickenItemType.Equip, Index = 1, ItemGuid = equip });
        Assert.Equal(0, (await Next(alice, TimedBattleService.InsertItem, InsertChickenItemResp.Parser)).Result);
        foreach (long card in new long[] { 3001011, 3001014 })
        {
            await OnLoop(() => Battles.OfferForTestAsync(Session(alice), card));
            long operate = 0;
            await OnLoop(() => { operate = Open(alice).OperateId; return Task.CompletedTask; });
            await alice.SendGameAsync(TimedBattleService.AdventureSelect, new AdventureEventSelectReq { OperateId = operate, CardId = { card } });
            Assert.Equal(0, (await Next(alice, TimedBattleService.AdventureSelect, AdventureEventSelectResp.Parser)).Result);
        }
        await OnLoop(() =>
        {
            var pack = Round(alice).PackInfos.Values.Where(i => i.ItemEntry != 0).ToArray();
            Assert.Equal(coins + 5000, pack.Where(i => i.ItemEntry == 10006).Sum(i => (long)i.Stack));
            Assert.DoesNotContain(pack, i => i.ItemEntry == 30002);
            var better = pack.Single(i => i.ItemEntry == 30102);
            Assert.Equal(better.ItemGuid, Round(alice).ExtendInfo.ChickenDungeonInfo.Equips[1]);
            Assert.Equal(15400 + 3200, Service<CharacterService>().Stats(Session(alice))[101]);
            return Task.CompletedTask;
        });

        // Unanswered: the first card at endTime.
        await OnLoop(() => Battles.OfferForTestAsync(Session(alice), 3001016, 3001017, 3001018));
        long end = 0;
        await OnLoop(() => { end = Open(alice).EndTime; return Task.CompletedTask; });
        await Tick(end);
        for (int i = 0; ; i++)
        {
            bool done = false;
            await OnLoop(() =>
            {
                var deck = Seen(alice).FunctionInfos[1003].Deck.Values.Single();
                done = deck.NeedSelectMap.Count == 0 && deck.Cards.Values.Any(c => c.CardId == 3001016);
                return Task.CompletedTask;
            });
            if (done) break;
            Assert.True(i < 50, "the pick was not taken at its end time");
            await Task.Delay(100);
        }
    }
}
