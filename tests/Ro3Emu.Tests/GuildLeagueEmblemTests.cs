using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// The Guild League honor emblem (#149): the tier's frame and design go into the guild's unlocked icons, once, and
/// survive a restart. Runs on the fixture, where 100102/200102 are locked by default and 100101 is not.
/// </summary>
public sealed class GuildLeagueEmblemTests : GuildHuntingHost
{
    protected override string GameDataDir => SyntheticData.Dir;

    [Fact]
    public async Task HonorEmblemIconsAreUnlockedOnceAndKept()
    {
        var alice = await InWorld("Alice");
        long guild = await Founder(alice, "Champs");
        var guilds = Service<GuildService>();
        var loop = Service<GameLoop>();
        Task<int> Unlock(long[] icons) => loop.InvokeAsync(() => guilds.UnlockLeagueIconsAsync(guild, icons));

        // 100101 is unlocked by default and 999999 isn't an icon: only the two locked icons are added.
        Assert.Equal(2, await Unlock([100102, 200102, 100101, 999999]));
        var push = await Next(alice, GuildService.BaseChange, GuildBaseInfoChangeNtf.Parser,
            n => n.Tp == GuildBaseInfoChangeNtf.Types.ChangeType.ChangeUnLockHeadIcon);
        Assert.Equal([100102, 200102], push.UnLockHeadIconData.Select(i => i.Id).Order().ToArray());
        Assert.All(push.UnLockHeadIconData, i => Assert.Equal(0L, i.Removetime));

        // A repeated settlement adds nothing.
        Assert.Equal(0, await Unlock([100102, 200102]));

        await alice.DisposeAsync();
        await StopAsync();
        await StartAsync();
        Assert.Equal([100102L, 200102L], Service<GuildStore>().Get(guild)!.UnlockedHeadIcons.Keys.Order().ToArray());
    }
}
