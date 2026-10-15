using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Merge regression: quest award settlement must retain master item metadata and daily atomicity.</summary>
public sealed class MergeInventoryAwardTests : IAsyncLifetime
{
    private const int Token = 1250709;
    private const int PurpleWeapon = 2010240;
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-merge-award-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (http, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = http;
            o.MicroGatePort = micro;
            o.GameGatePort = game;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{http}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    [ClientDataFact]
    public async Task AwardKeepsExistingMetadataAndCommitsDailyOrRollsBackTogether()
    {
        await using var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync("mergebob", "pw");
        await bot.EnterGateAsync();
        long role = (await bot.CreateRoleAsync("MergeBob")).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(role);

        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == role);
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var character = _app.Services.GetRequiredService<CharacterService>();
        var items = _app.Services.GetRequiredService<ItemStore>();
        var roles = _app.Services.GetRequiredService<RoleStore>();
        var accounts = _app.Services.GetRequiredService<AccountStore>();
        var token = new AwardPlan(0, 0, [new AwardGrant(Token, 1, 1)]);
        Assert.True(await loop.InvokeAsync(() => character.ApplyAwardAsync(session, token)));

        var protectedItem = await loop.InvokeAsync(async () =>
        {
            using var held = await CharacterService.Lock(session);
            var current = session.Items.Single(i => i.Entry == Token);
            current.IsLocked = true;
            current.Attrs = new() { [777] = 9 };
            current.StringAttrs = new() { [510] = "cost-history" };
            current.Enchantments[42] = new ItemEnchantment { Attr = new() { [101] = 7 }, GetTime = 123456 };
            current.BindExpire = 654321;
            items.Update(current);
            return current;
        });

        byte[] beforeDaily = JsonSerializer.SerializeToUtf8Bytes(session.Daily!);
        var afterState = JsonSerializer.Deserialize<DailyActivityService.DailyState>(beforeDaily)!;
        afterState.Claims.Add(987654);
        byte[] afterDaily = JsonSerializer.SerializeToUtf8Bytes(afterState);
        byte[] expectedTasks = session.Tasks!.ToByteArray();
        long currencyBefore = session.Progress!.Currency.GetValueOrDefault(1);
        var plan = new AwardPlan(0, 0, [new AwardGrant(Token, 2, 1), new AwardGrant(1, 10)]);

        // A stale in-memory task snapshot cannot settle a daily delivery.
        Assert.False(await loop.InvokeAsync(() => character.ApplyAwardAsync(session, plan,
            deliveredTaskState: expectedTasks, expectedDaily: beforeDaily, nextDaily: afterDaily,
            expectedTasks: [1, 2, 3])));
        Assert.Equal(beforeDaily, roles.LoadDaily(role));
        Assert.Equal(currencyBefore, roles.LoadProgress(role).Currency.GetValueOrDefault(1));

        FixtureSql.Execute(accounts,
            "CREATE TRIGGER fail_merge_award BEFORE INSERT ON item BEGIN SELECT RAISE(ABORT, 'award test failure'); END");
        try
        {
            await Assert.ThrowsAsync<SqliteException>(() => loop.InvokeAsync(() => character.ApplyAwardAsync(session, plan,
                deliveredTaskState: expectedTasks, expectedDaily: beforeDaily, nextDaily: afterDaily,
                expectedTasks: expectedTasks)));
            Assert.Equal(beforeDaily, roles.LoadDaily(role));
            Assert.Equal(currencyBefore, roles.LoadProgress(role).Currency.GetValueOrDefault(1));
            Assert.Single(items.Load(role), i => i.Entry == Token);
            Assert.Same(protectedItem, session.Items.Single(i => i.Entry == Token));
        }
        finally
        {
            FixtureSql.Execute(accounts, "DROP TRIGGER fail_merge_award");
        }

        Assert.True(await loop.InvokeAsync(() => character.ApplyAwardAsync(session, plan,
            deliveredTaskState: expectedTasks, expectedDaily: beforeDaily, nextDaily: afterDaily,
            expectedTasks: expectedTasks)));
        Assert.Equal(afterDaily, roles.LoadDaily(role));
        Assert.Equal(currencyBefore + 10, roles.LoadProgress(role).Currency.GetValueOrDefault(1));
        var stored = items.Load(role).Where(i => i.Entry == Token).ToArray();
        Assert.Equal(2, stored.Length);
        var retained = stored.Single(i => i.Guid == protectedItem.Guid);
        Assert.True(retained.IsLocked);
        Assert.Equal(9, retained.Attrs![777]);
        Assert.Equal("cost-history", retained.StringAttrs![510]);
        Assert.Equal(7, retained.Enchantments[42].Attr[101]);
        Assert.Equal(123456, retained.Enchantments[42].GetTime);
        Assert.Equal(654321, retained.BindExpire);
        Assert.Equal(2, stored.Single(i => i.Guid != protectedItem.Guid).Stack);

        Assert.False(await loop.InvokeAsync(() => character.ApplyAwardAsync(session, plan,
            deliveredTaskState: expectedTasks, expectedDaily: beforeDaily, nextDaily: afterDaily,
            expectedTasks: expectedTasks)));
        Assert.Equal(currencyBefore + 10, roles.LoadProgress(role).Currency.GetValueOrDefault(1));
    }

    [ClientDataTheory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public async Task QuestAwardAutoDecomposesOnlyNewGearWithinTheAwardCommit(int entryCount, bool shouldDecompose)
    {
        await using var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync("mergequest", "pw");
        await bot.EnterGateAsync();
        long role = (await bot.CreateRoleAsync("MergeQuest")).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(role);

        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == role);
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var character = _app.Services.GetRequiredService<CharacterService>();
        var items = _app.Services.GetRequiredService<ItemStore>();
        var roles = _app.Services.GetRequiredService<RoleStore>();
        var accounts = _app.Services.GetRequiredService<AccountStore>();
        await loop.InvokeAsync(async () => { await character.SetLevelAsync(session, 20, null); });
        // A standalone award creates the old locked instance; auto filters apply to quest delivery only.
        Assert.True(await loop.InvokeAsync(() => character.ApplyAwardAsync(session,
            new AwardPlan(0, 0, [new AwardGrant(PurpleWeapon, 1)]))));
        var protectedItem = await loop.InvokeAsync(async () =>
        {
            using var held = await CharacterService.Lock(session);
            var current = session.Items.Single(i => i.Entry == PurpleWeapon);
            Assert.Single(EquipmentAffixes.Entries(current));
            Assert.Empty(EquipmentAffixes.Specials(current));
            current.IsLocked = true;
            items.Update(current);
            return current;
        });

        // DecomposeConditionConfig[4] is type 3: LC_CompositionHelper.lua:328-350 compares the exact entry count.
        // EquipEntryRandom quality 4 always rolls one entry, so selecting zero must keep this gear intact.
        var filter = new ItemAutoDecomposeSettingReq
        {
            TabId = 2, IsOpen = true,
            OptionSetting = { [1] = new OptionGroup { Options = { 4 } },
                [3] = new OptionGroup { Options = { 1, 2, 4 } },
                [4] = new OptionGroup { Options = { entryCount } },
                [5] = new OptionGroup { Options = { 0 } } },
        };
        await SetAutoDecompose(bot, filter);
        Assert.NotNull(roles.LoadAutoDecompose(role));
        byte[] beforeDaily = JsonSerializer.SerializeToUtf8Bytes(session.Daily!);
        var afterState = JsonSerializer.Deserialize<DailyActivityService.DailyState>(beforeDaily)!;
        afterState.Claims.Add(987655);
        byte[] afterDaily = JsonSerializer.SerializeToUtf8Bytes(afterState);
        byte[] expectedTasks = session.Tasks!.ToByteArray();
        var nextTasks = session.Tasks!.Clone();
        nextTasks.CompleteTaskNum[987654321] = 1;
        byte[] deliveredTasks = nextTasks.ToByteArray();
        byte[]? beforeRuntime = roles.LoadTaskRuntime(role);
        byte[] nextRuntime = JsonSerializer.SerializeToUtf8Bytes(new TaskRuntimeState { CompletedGuides = [987654321] });
        long zenyBefore = roles.LoadProgress(role).Currency.GetValueOrDefault(1);
        long coinBefore = roles.LoadProgress(role).Currency.GetValueOrDefault(2);
        int noticesBefore = bot.Inbox.Snapshot().Count(e => e.IsGame(EquipmentService.ItemAutoDecomposeNotice));
        var plan = new AwardPlan(0, 0,
            [new AwardGrant(PurpleWeapon, 2), new AwardGrant(Token, 1), new AwardGrant(1, 50)]);

        FixtureSql.Execute(accounts,
            "CREATE TRIGGER fail_quest_decompose BEFORE INSERT ON item BEGIN SELECT RAISE(ABORT, 'quest dismantle failure'); END");
        try
        {
            await loop.InvokeAsync(async () =>
            {
                using var held = await CharacterService.Lock(session);
                // A successful incidental task award may replace the inventory after the initial grant.
                // Bind the identity assertion to the failed operation while its original gate is held.
                var beforeItem = session.Items.Single(i => i.Entry == PurpleWeapon);
                Assert.Equal(protectedItem.Guid, beforeItem.Guid);
                Assert.True(beforeItem.IsLocked);
                await Assert.ThrowsAsync<SqliteException>(() => character.ApplyAwardAsync(session, plan,
                    deliveredTaskState: deliveredTasks, taskRuntimeState: nextRuntime,
                    expectedDaily: beforeDaily, nextDaily: afterDaily, expectedTasks: expectedTasks, stateLockHeld: true));
                Assert.Same(beforeItem, session.Items.Single(i => i.Entry == PurpleWeapon));
            });
            Assert.Equal(zenyBefore, roles.LoadProgress(role).Currency.GetValueOrDefault(1));
            Assert.Equal(coinBefore, roles.LoadProgress(role).Currency.GetValueOrDefault(2));
            Assert.Equal(beforeDaily, roles.LoadDaily(role));
            Assert.Equal(beforeRuntime, roles.LoadTaskRuntime(role));
            Assert.NotEqual(deliveredTasks, roles.LoadTasks(role));
            Assert.Single(items.Load(role), i => i.Entry == PurpleWeapon);
            Assert.DoesNotContain(items.Load(role), i => i.Entry == Token);
            // A later response on the same connection makes the absence of a success notice observable.
            await SetAutoDecompose(bot, filter);
            Assert.Equal(noticesBefore, bot.Inbox.Snapshot().Count(e => e.IsGame(EquipmentService.ItemAutoDecomposeNotice)));
        }
        finally
        {
            FixtureSql.Execute(accounts, "DROP TRIGGER fail_quest_decompose");
        }

        Assert.True(await loop.InvokeAsync(() => character.ApplyAwardAsync(session, plan,
            deliveredTaskState: deliveredTasks, taskRuntimeState: nextRuntime,
            expectedDaily: beforeDaily, nextDaily: afterDaily, expectedTasks: expectedTasks)));
        var rewardEnvelope = await bot.Inbox.WaitAsync(e => e.IsGame(CharacterService.GetRewardNotify)
            && GetRewardNotify.Parser.ParseFrom(e.Body).Rewards.Count(i => i.ItemEntry == PurpleWeapon) == 2, TimeSpan.FromSeconds(5));
        var rewards = GetRewardNotify.Parser.ParseFrom(rewardEnvelope.Body);
        Assert.All(rewards.Rewards.Where(i => i.ItemEntry == PurpleWeapon), item =>
        {
            Assert.Equal(1, item.Stack);
            Assert.Equal(1, item.ItemInfos[3].LongVal);
            Assert.True(item.ItemInfos[2].LongVal > 0);
            Assert.True(item.ItemInfos[100].LongVal > 0);
            Assert.True(item.ItemInfos[140].LongVal > 0);
            Assert.False(item.ItemInfos.ContainsKey(101));
            Assert.False(item.ItemInfos.ContainsKey(80));
        });
        await SetAutoDecompose(bot, filter); // Drain through a response sent after any award notice, including negative cases.
        if (shouldDecompose)
        {
            var envelope = await bot.Inbox.WaitAsync(e => e.IsGame(EquipmentService.ItemAutoDecomposeNotice), TimeSpan.FromSeconds(5));
            var notice = ItemAutoDecomposeTipsNtf.Parser.ParseFrom(envelope.Body);
            Assert.Equal(2, Assert.Single(notice.DecomposeItemInfo).Value);
            Assert.Equal(2, notice.DecomposeItemInfo[PurpleWeapon]);
            Assert.Equal(500, notice.DecomposeGainItemInfo[PurpleWeapon].ItemInfo[2]);
        }
        Assert.Equal(zenyBefore + 50, roles.LoadProgress(role).Currency.GetValueOrDefault(1));
        Assert.Equal(coinBefore + (shouldDecompose ? 500 : 0), roles.LoadProgress(role).Currency.GetValueOrDefault(2));
        Assert.Equal(afterDaily, roles.LoadDaily(role));
        Assert.Equal(deliveredTasks, roles.LoadTasks(role));
        Assert.Equal(nextRuntime, roles.LoadTaskRuntime(role));
        var storedGear = items.Load(role).Where(i => i.Entry == PurpleWeapon).ToArray();
        Assert.Equal(shouldDecompose ? 1 : 3, storedGear.Length);
        Assert.True(storedGear.Single(i => i.Guid == protectedItem.Guid).IsLocked);
        Assert.All(storedGear, item => Assert.Single(EquipmentAffixes.Entries(item)));
        Assert.Equal(1, Assert.Single(items.Load(role), i => i.Entry == Token).Stack);
        Assert.Equal(noticesBefore + (shouldDecompose ? 1 : 0), bot.Inbox.Snapshot().Count(e => e.IsGame(EquipmentService.ItemAutoDecomposeNotice)));
    }

    private static async Task SetAutoDecompose(BotClient bot, ItemAutoDecomposeSettingReq filter)
    {
        await bot.SendGameAsync(EquipmentService.ItemAutoDecomposeSetting, filter);
        var reply = await bot.Inbox.WaitAsync(e => e.IsGame(EquipmentService.ItemAutoDecomposeSetting), TimeSpan.FromSeconds(5));
        Assert.Equal(0, ItemAutoDecomposeSettingResp.Parser.ParseFrom(reply.Body).Result);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
