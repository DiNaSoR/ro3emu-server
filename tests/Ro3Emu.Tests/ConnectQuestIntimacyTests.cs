using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Activity Intimacy (Connect Quests): both directions, a daily limit per pair, the maximum, friends only.</summary>
public sealed class ConnectQuestIntimacyTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ro3-connect-" + Guid.NewGuid().ToString("N"));
    private readonly AccountStore accounts;
    private readonly RoleStore roles;
    private readonly FriendStore friends;

    public ConnectQuestIntimacyTests()
    {
        accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = directory }));
        roles = new RoleStore(accounts);
        friends = new FriendStore(accounts);
    }

    private long Role(string name) => roles.Create("test-account", 1, name, 1, 1000, 1)!.Id;

    [Fact]
    public async Task ActivityIntimacyIsSharedLimitedPerDayAndOnlyForFriends()
    {
        long a = Role("Alice"), b = Role("Bob"), c = Role("Carol");
        friends.Link(a, b);
        Assert.Equal(600, await friends.AddActivityIntimacyAsync(a, b, 600, day: 1, dailyLimit: 1000, max: 180000));
        Assert.Equal(600, friends.Intimacy(b, a));
        Assert.Equal(1000, await friends.AddActivityIntimacyAsync(b, a, 600, day: 1, dailyLimit: 1000, max: 180000));
        Assert.Null(await friends.AddActivityIntimacyAsync(a, b, 5, day: 1, dailyLimit: 1000, max: 180000)); // today's limit
        Assert.Equal(1005, await friends.AddActivityIntimacyAsync(a, b, 5, day: 2, dailyLimit: 1000, max: 180000)); // next day
        Assert.Equal(1010, await friends.AddActivityIntimacyAsync(a, b, 50, day: 3, dailyLimit: 1000, max: 1010)); // maximum
        Assert.Equal(1010, friends.Intimacy(a, b));
        Assert.Equal(1010, friends.Intimacy(b, a));
        Assert.Null(await friends.AddActivityIntimacyAsync(a, c, 5, day: 3, dailyLimit: 1000, max: 180000)); // not friends
        Assert.Null(await friends.AddActivityIntimacyAsync(a, a, 5, day: 3, dailyLimit: 1000, max: 180000));
    }

    [Fact]
    public void TheGameDayStartsAtFiveInTheMorning()
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 19, 12, 0, 0));
        var before = new DateTimeOffset(2026, 10, 19, 4, 59, 0, offset);
        var after = new DateTimeOffset(2026, 10, 19, 5, 0, 0, offset);
        Assert.Equal(new DateTimeOffset(2026, 10, 18, 5, 0, 0, offset).ToUnixTimeSeconds(), FriendExtraService.GameDay(before));
        Assert.Equal(after.ToUnixTimeSeconds(), FriendExtraService.GameDay(after));
    }

    public void Dispose()
    {
        accounts.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }
}
