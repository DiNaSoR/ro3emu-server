using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Micro;

namespace Ro3Emu.Tests;

/// <summary>
/// Rank reward settlement (RankService.Rewards) on the synthetic fixture: board 1011 pays rank 1 the 9501 gift and
/// ranks 2-3 the 9502 gift by letter 9601, daily at 05:00 (_kPrizesTimeNew [[1, 5]]).
/// </summary>
public sealed class RankRewardTests
{
    private const int Letter = 9601, Currency = 102;

    private sealed class TestClock : TimeProvider
    {
        private long seconds = new DateTimeOffset(2026, 10, 6, 4, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref seconds));
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Advance(TimeSpan by) => Interlocked.Add(ref seconds, (long)by.TotalSeconds);
    }

    [Fact]
    public async Task TheTopRanksArePaidOncePerPeriodAndARestartKeepsTheSavedWinners()
    {
        var clock = new TestClock();
        await using var host = await SyntheticHost.StartAsync(clock: clock);
        var roles = host.Service<RoleStore>();
        long alice = Role(roles, "Alice"), bob = Role(roles, "Bob"), carol = Role(roles, "Carol"), dave = Role(roles, "Dave");
        // The 2026-10-05 05:00 period settles at start with an empty board: nobody is paid.
        long first = new DateTimeOffset(2026, 10, 5, 5, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        await Until(() => Settled(host)?.Period == first, "the start-up settlement");
        Assert.Empty(Settled(host)!.Winners);
        Climb(roles, alice, 94);
        Climb(roles, bob, 93);
        Climb(roles, carol, 92);
        Climb(roles, dave, 91);

        clock.Advance(TimeSpan.FromHours(1)); // 2026-10-06 05:00
        await Until(() => Rewards(roles, carol).Count == 1, "the 2026-10-06 rank letters");
        Assert.Equal([5L], Rewards(roles, alice).Select(l => Amount(l)));
        Assert.Equal([2L], Rewards(roles, bob).Select(l => Amount(l)));
        Assert.Equal([2L], Rewards(roles, carol).Select(l => Amount(l)));
        Assert.Empty(Rewards(roles, dave));
        // ${1} is the rank.
        Assert.Equal(1, LanguageData.Parser.ParseFrom(Rewards(roles, alice)[0].ContentParams).Param[0].IntParam);
        Assert.Equal(3, LanguageData.Parser.ParseFrom(Rewards(roles, carol)[0].ContentParams).Param[0].IntParam);

        // Dave climbs to the top, the server restarts in the same period: the saved winners stand, nobody is paid twice.
        Climb(roles, dave, 94, remain: 99);
        await host.RestartAsync();
        roles = host.Service<RoleStore>();
        await Task.Delay(1500); // the resume check re-sends the saved winners, already delivered
        Assert.Single(Rewards(roles, alice));
        Assert.Empty(Rewards(roles, dave));

        clock.Advance(TimeSpan.FromDays(1)); // 2026-10-07 05:00: Dave first, Alice second
        await Until(() => Rewards(roles, dave).Count == 1, "the 2026-10-07 rank letters");
        Assert.Equal(5L, Amount(Rewards(roles, dave)[0]));
        await Until(() => Rewards(roles, alice).Count == 2, "Alice's second letter");
        Assert.Equal(2L, Amount(Rewards(roles, alice)[1]));
        Assert.Single(Rewards(roles, carol)); // fourth now
    }

    private static RankService.BoardSettlement? Settled(SyntheticHost host) =>
        host.Service<AccountStore>().ReadSync<string?>(new SeasonStateReadPlan(RankService.RewardStateKey)) is { Length: > 0 } saved
            ? JsonSerializer.Deserialize<RankService.RewardState>(saved)!.Boards.GetValueOrDefault(1011) : null;

    private static long Role(RoleStore roles, string name) =>
        roles.Create("rankreward" + name.ToLowerInvariant(), 1, name, 1, 1100, 1)!.Id;

    private static void Climb(RoleStore roles, long role, int floor, int remain = 0)
    {
        var tower = new DBClimTower { LayerConfID = floor, RemainDuration = remain, CompleteTime = 1000 };
        tower.PetIds.Add([0, 0, 0, 0]);
        roles.SaveTower(role, tower.ToByteArray());
    }

    private static List<MailService.Letter> Rewards(RoleStore roles, long role) =>
        (roles.LoadMailbox(role) is { Length: > 0 } blob ? JsonSerializer.Deserialize<MailService.Mailbox>(blob)!.Letters : [])
            .Where(l => l.TemplateId == Letter).OrderBy(l => l.CreatedTime).ThenBy(l => l.DeliveryKey).ToList();

    private static long Amount(MailService.Letter letter) => letter.Attachments.Single(a => a.Entry == Currency).Count;

    private static async Task Until(Func<bool> done, string what)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Timed out waiting for " + what);
            await Task.Delay(50);
        }
    }
}
