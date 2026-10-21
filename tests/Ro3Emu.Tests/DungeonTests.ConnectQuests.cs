using System.Text.Json.Nodes;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    private const long PartyGoldenThiefBug = 1904000; // Connect Quest [107005, 1, 4300001], 116 4000: Intimacy +5

    /// <summary>
    /// Connect Quests (TaskConfig type 19) run hidden for every role. A party clear of the TenGameplay boss 4300001
    /// completes 1904000 for both friends, which re-arms it and gives the pair 5 Intimacy for each delivery.
    /// </summary>
    [ClientDataFact]
    public async Task APartyBossClearDeliversTheConnectQuestAndGivesFriendsIntimacy()
    {
        ChangeRow("TenGameplayConfig", "50101", row => row["_kLimit"] = new JsonArray(2, 5)); // a party of two
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        foreach (var bot in new[] { alice, bob }) await Gm(bot, "!lvl 99");
        long a = (long)alice.Self!.Uuid, b = (long)bob.Self!.Uuid;
        var friends = Service<FriendStore>();
        await friends.LinkAsync(a, b);

        var connect = Service<GameData>().Table("TaskConfig").Values
            .Where(r => GameData.Int(r, "_iTaskType") == 19).Select(r => GameData.Int(r, "_iTaskID")).ToArray();
        Assert.Equal(53, connect.Length);
        foreach (long id in connect)
        {
            var info = await QuestProbe.ReadAsync(_app, "alice", id);
            Assert.True(info is { Status: TaskState.Running }, $"Connect Quest {id}: {info}");
        }

        await Party(alice, bob);
        Assert.Equal(0, await Enter(alice));
        await bob.SendGameAsync(DungeonService.Confirm, new ConfirmTeamDungeonReq { IsAgree = true });
        Assert.Equal(0, (await Next(bob, DungeonService.Confirm, ConfirmTeamDungeonRsp.Parser)).Result);
        long instance = await Load(alice);
        Assert.Equal(instance, await Load(bob));
        await Kill(alice, await BossIn(instance));
        Assert.Equal((DungeonRes)1, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);

        foreach (var bot in new[] { alice, bob })
        {
            var deadline = DateTime.UtcNow + Wait;
            while (await OnLoopRead(() => Session(bot).Tasks!.CompleteTaskNum.GetValueOrDefault(PartyGoldenThiefBug)) != 1)
            {
                Assert.True(DateTime.UtcNow < deadline, $"{bot.Account} did not deliver {PartyGoldenThiefBug}");
                await Task.Delay(25);
            }
            await QuestProbe.AtAsync(_app, bot.Account!, PartyGoldenThiefBug, 0); // re-armed
        }
        var until = DateTime.UtcNow + Wait;
        while (await friends.IntimacyAsync(a, b) != 10)
        {
            Assert.True(DateTime.UtcNow < until, $"intimacy {await friends.IntimacyAsync(a, b)}");
            await Task.Delay(25);
        }
        Assert.Equal(10, await friends.IntimacyAsync(b, a));
    }

    private Task<T> OnLoopRead<T>(Func<T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));
}
