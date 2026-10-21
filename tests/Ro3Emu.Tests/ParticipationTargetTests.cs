using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// State and counter quest targets of the growth emblem titles 3-6 and the everyday quests (TaskObjectives.cs,
/// TaskService.ParticipationTargets.cs), driven by the real requests and re-read after a relogin. The targets whose
/// source is a match, a raid or a guild event are tested with their feature's fixture.
/// </summary>
public sealed class ParticipationTargetTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private readonly string dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-partquests-" + Guid.NewGuid().ToString("N"));
    private WebApplication app = null!;
    private string http = "";

    private T Service<T>() where T : notnull => app.Services.GetRequiredService<T>();
    private Task<T> Read<T>(Func<T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));
    private GameSession Session => Service<SessionRegistry>().All().Single(s => s.Account == "alice" && s.Role != null);

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        app = ServerHost.Create([], o =>
        {
            o.DataDir = dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
        });
        await app.StartAsync();
        http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await app.StopAsync(); await app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(bool create = true)
    {
        var bot = new BotClient(http, File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync("alice", "pw"); await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync("Alice")).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private async Task<BotClient> Relog(BotClient bot)
    {
        await bot.DisposeAsync();
        await QuestProbe.OfflineAsync(app, "alice");
        return await InWorld(create: false);
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null)
        where T : IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private Task Gm(string command) => QuestProbe.GmAsync(http, "alice", command);
    private Task Accept(long task) => QuestProbe.AcceptAsync(app, http, "alice", task);
    private Task At(long task, long progress) => QuestProbe.AtAsync(app, "alice", task, progress);
    private Task<TaskInfo> Done(long task) => QuestProbe.DoneAsync(app, "alice", task);

    private async Task<long> Give(int entry, int count = 1)
    {
        await Gm($"!item {entry} {count}");
        for (var until = DateTime.UtcNow + Wait; ; await Task.Delay(20))
        {
            long guid = await Read(() => Session.Items.LastOrDefault(i => i.Entry == entry && i.Grid == 0)?.Guid ?? 0);
            if (guid != 0) return guid;
            Assert.True(DateTime.UtcNow < until, $"item {entry} not granted");
        }
    }

    private static async Task Wear(BotClient bot, long guid, int grid)
    {
        await bot.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { ItemGUID = guid, Pos = (EquipGridType)grid });
        Assert.Equal(0, (await Next(bot, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task StuntTargetCountsWornGearCarryingStunts()
    {
        var bot = await InWorld();
        // 705060 [302005,1,0,0,3] "Reach a total of 3 Stunts on equipped gear" (growth emblem title 5).
        await Accept(705060);
        long before = (await QuestProbe.ReadAsync(app, "alice", 705060))!.CompleteNum[0];
        Assert.Equal(await Read(() => Session.Items.LongCount(i => i.Grid > 0 && EquipmentAffixes.Specials(i).Any())), before);
        Assert.True(before < 3);
        // EquipConfig _kFixedSpecialAttribute gear for grids 2, 4 and 5: every instance carries Stunts.
        var gear = new[] { (Entry: 90000612, Grid: 2), (Entry: 90000614, Grid: 4), (Entry: 90000615, Grid: 5) };
        var guids = new List<long>();
        foreach (var (entry, _) in gear) guids.Add(await Give(entry));
        Assert.True(await Read(() => guids.All(guid => EquipmentAffixes.Specials(Session.Items.Single(i => i.Guid == guid)).Any())));
        Assert.Equal(before, (await QuestProbe.ReadAsync(app, "alice", 705060))!.CompleteNum[0]); // carried, not worn
        await Wear(bot, guids[0], gear[0].Grid);
        await At(705060, Math.Min(3, before + 1));
        await Wear(bot, guids[1], gear[1].Grid);
        await Wear(bot, guids[2], gear[2].Grid);
        await Done(705060);

        bot = await Relog(bot);
        var info = await Done(705060);
        Assert.Equal(3, info.CompleteNum[0]);
        await bot.DisposeAsync();
    }

    [ClientDataFact]
    public async Task HatchingRoomLevelTargetReadsTheRoom()
    {
        var bot = await InWorld();
        await Gm("!quest done 10104330"); // function 33
        await Gm("!quest done 10105010"); // the hatchery
        await Gm("!item 1 100000"); // the room levels' Zeny
        await Accept(706080); // [306004,10] "Reach Pet Hatching Room Lv. 10" (growth emblem title 6)
        await At(706080, 1);
        // Hatchery by-product 1250731 [603,160]: 98 of them fill the bars of rooms 1-9 (15610 EXP). It is _QuickUse
        // [3, 0], so the server uses all 98 on receipt (_iGroupUse 0: one at a time, _iCD 0).
        await Gm("!item 1250731 98");
        await Done(706080);
        Assert.Equal(0, await Read(() => Session.Items.Where(i => i.Entry == 1250731).Sum(i => i.Stack)));
        Assert.Equal(10, await Read(() => Session.Pets!.IncubationLv));

        bot = await Relog(bot);
        Assert.Equal(10, (await Done(706080)).CompleteNum[0]);
        await bot.DisposeAsync();
    }

    private static async Task SetUp(BotClient bot, int slot, long guid)
    {
        await bot.SendGameAsync(EmblemService.SetUp, new SetUpEmblemReq { EmblemPos = slot, EmblemGuid = guid });
        Assert.Equal(0, (await Next(bot, EmblemService.SetUp, SetUpEmblemResp.Parser)).Result);
    }

    private async Task Feed(BotClient bot, long guid, int books)
    {
        await Gm($"!item 1257002 {books}");
        await bot.SendGameAsync(EmblemService.Upgrade, new UpgradeEmblemReq { EmblemGuid = guid, ConsumeItems = { [1257002] = books } });
        Assert.Equal(0, (await Next(bot, EmblemService.Upgrade, UpgradeEmblemResp.Parser, r => r.EmblemGuid == guid)).Result);
    }

    private Task<long> Level(long guid) => Read(() => Session.Items.Single(i => i.Guid == guid).Attrs!.GetValueOrDefault(1001));

    [ClientDataFact]
    public async Task SoulEchoLevelSumAndUpgradeLevels()
    {
        var bot = await InWorld();
        await Gm("!quest done 10103040"); // function 55
        await Gm("!lvl 60");
        await Accept(130006); // [317001,20] "Raise all equipped Echoes to Lv. 20"
        await Accept(130013); // [317002,50] "Enhance Echoes by a total of 50 levels"
        long worn = await Give(1890111), spare = await Give(1890111);
        await SetUp(bot, 1, worn);
        await At(130006, 0);
        await Feed(bot, worn, 1);
        long level = await Level(worn);
        Assert.True(level > 0);
        await At(130006, level);
        await At(130013, level);
        await Feed(bot, spare, 1); // upgrading a carried Echo counts toward 317002, not toward the worn sum
        long spareLevel = await Level(spare);
        await At(130013, level + spareLevel);
        await At(130006, level);

        bot = await Relog(bot);
        await At(130013, level + spareLevel); // the event count survives
        await At(130006, level);
        await Feed(bot, worn, 14);
        Assert.Equal(20, await Level(worn));
        await Done(130006);
        await At(130013, 20 + spareLevel);
        await bot.DisposeAsync();
    }

    [ClientDataFact]
    public async Task ActivityTargetReadsTodaysActivityPoints()
    {
        var bot = await InWorld();
        await Accept(100027); // [101009,60] "Reach 60 Daily Activity"
        long now = await Read(() => Service<CharacterService>().CountOf(Session, 5));
        await At(100027, Math.Min(60, now));
        await Gm("!item 5 25");
        await At(100027, Math.Min(60, now + 25));
        bot = await Relog(bot);
        await At(100027, Math.Min(60, now + 25));
        await Gm("!item 5 60");
        await Done(100027);
        await bot.DisposeAsync();
    }

    /// <summary>
    /// The growth emblem titles 3-6 wait for every child (TaskService.GrowthManual.cs): each child's target now has a
    /// supported reader and a positive R, so no title is held forever by an unsupported objective.
    /// </summary>
    [ClientDataFact]
    public void EveryChildOfTheGrowthTitlesThreeToSixIsSupported()
    {
        var data = Service<GameData>();
        var tasks = Service<TaskService>();
        var titles = data.Table("NewbieTitlesConfig").Values.Where(r => GameData.Int(r, "_iLev") is >= 3 and <= 6).ToArray();
        Assert.Equal(4, titles.Length);
        foreach (var title in titles)
        {
            var children = title.GetProperty("_iSubTaskId").EnumerateArray().Select(v => (long)GameData.Number(v)!.Value).ToArray();
            Assert.NotEmpty(children);
            foreach (long child in children.Append(GameData.Int(title, "_iTaskID")))
                Assert.True(tasks.TargetsSupported(child), $"growth title {GameData.Int(title, "_iLev")}: task {child}");
        }
    }

    /// <summary>
    /// 106611 [points, n] (1602040 "Guild Event - War of Emperium"): one per settled siege whose personal score reaches
    /// p2; 106605 / 106606 (Realm Campaign 120011 / 120013): one per settled siege / per won one. Hook level: TerritoryWarsService raises SiegeSettled for every loaded participant after the settlement commit
    /// (its own siege tests drive that path); a score below 20 does not count, 20 does, and the count survives a relogin.
    /// </summary>
    [ClientDataFact]
    public async Task SiegeSettlementCountsScoreParticipationAndWins()
    {
        var bot = await InWorld();
        long role = await Read(() => Session.Role!.Id);
        await Accept(1602040);
        await Accept(120011); // [106605,1]
        await Accept(120013); // [106606,1]
        var siege = Service<TerritoryWarsService>();
        await Service<GameLoop>().InvokeAsync(() => { foreach (var h in siege.SiegeSettled) h(role, 19, false); return Task.CompletedTask; });
        await Task.Delay(300);
        Assert.Equal(0, (await QuestProbe.ReadAsync(app, "alice", 1602040))!.CompleteNum[0]);
        await Done(120011);
        Assert.Equal(0, (await QuestProbe.ReadAsync(app, "alice", 120013))!.CompleteNum[0]); // lost
        await Service<GameLoop>().InvokeAsync(() => { foreach (var h in siege.SiegeSettled) h(role, 20, true); return Task.CompletedTask; });
        await QuestProbe.DoneAsync(app, "alice", 1602040); // delivery waits on its action 115
        await Done(120013);
        bot = await Relog(bot);
        Assert.Equal(1, (await QuestProbe.DoneAsync(app, "alice", 1602040)).CompleteNum[0]);
        await bot.DisposeAsync();
    }

    /// <summary>
    /// 106610 [n] (Realm Campaign 120001 "Reach 1000 Battle Merit") and 106609 [n] (120006 "Reach 10000 points in Guild
    /// Territory War"): a settled Territory War battle adds the role's personal score and its guild's battle score.
    /// Hook level: TerritoryWarsService raises BattleSettled after the settlement commit (TerritoryWarsTests drive that
    /// path). Another guild's score does not count; the counts survive a relogin.
    /// </summary>
    [ClientDataFact]
    public async Task TerritoryBattleSettlementCountsPersonalAndGuildPoints()
    {
        var bot = await InWorld();
        long role = await Read(() => Session.Role!.Id);
        await Accept(120001); // [106610,1000]
        await Accept(120006); // [106609,10000]
        await Service<GameLoop>().InvokeAsync(() => { Session.Guild = new DBRoleGuildInfo { GuildId = 77 }; return Task.CompletedTask; });
        var wars = Service<TerritoryWarsService>();
        Task Settle(long personal, long ownGuild, long otherGuild) => Service<GameLoop>().InvokeAsync(() =>
        {
            foreach (var h in wars.BattleSettled)
                h(new Dictionary<long, long> { [role] = personal }, new Dictionary<long, long> { [77] = ownGuild, [78] = otherGuild });
            return Task.CompletedTask;
        });
        await Settle(600, 4000, 9000);
        await At(120001, 600);
        await At(120006, 4000);
        await Settle(400, 6000, 0);
        await Done(120001);
        await Done(120006);
        bot = await Relog(bot);
        Assert.Equal(1000, (await QuestProbe.DoneAsync(app, "alice", 120001)).CompleteNum[0]);
        Assert.Equal(10000, (await QuestProbe.DoneAsync(app, "alice", 120006)).CompleteNum[0]);
        await bot.DisposeAsync();
    }
}
