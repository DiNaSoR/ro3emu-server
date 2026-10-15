using System.Reflection;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class LotteryActivityTests
{
    private async Task RunLotteryDeadlineAt(GameSession session, DateTimeOffset instant, Action? beforeTick = null)
    {
        var loop = app.Services.GetRequiredService<GameLoop>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await loop.InvokeAsync(() =>
        {
            var deadlines = app.Services.GetRequiredService<Deadlines>();
            var scheduled = deadlines.ScheduledLease(session, nameof(LotteryService));
            Assert.True(scheduled.HasValue);
            Assert.True(deadlines.ScheduledDue(session, nameof(LotteryService)) is { } due && due <= instant);
            deadlines.CallbackCompletedForTest = lease =>
            {
                if (lease != scheduled.Value) return;
                deadlines.CallbackCompletedForTest = null;
                completed.TrySetResult();
            };
            // Advance and enqueue atomically with capturing the exact lease. A queued progress
            // refresh must not replace the observed schedule between the clock jump and Tick.
            clock.Set(instant);
            beforeTick?.Invoke();
            // A large clock jump can put more than one dispatch quota ahead of this lease.
            int waiting;
            do
            {
                waiting = deadlines.WaitingCount;
                deadlines.Tick(default);
            } while (deadlines.WaitingCount < waiting);
            return Task.CompletedTask;
        });
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private async Task DrainDeadlines()
    {
        var loop = app.Services.GetRequiredService<GameLoop>();
        await loop.InvokeAsync(() => { app.Services.GetRequiredService<Deadlines>().Tick(default); return Task.CompletedTask; });
        // For clocks before the due instant and repeated ticks, no callback is expected.
        await loop.InvokeAsync(() => Task.CompletedTask);
        await loop.InvokeAsync(() => Task.CompletedTask);
    }

    [ClientDataTheory]
    [InlineData(0)]
    [InlineData(350)]
    [InlineData(172800125)]
    public async Task LotteryDeadlineMatchesPollingProjectionAndPersistedCounters(int lateMilliseconds)
    {
        await using var bot = await InWorld(create: true);
        await Gm("!lvl 99"); await Gm("!item 1250717 1");
        await bot.SendGameAsync(LotteryService.DrawRequest, new LotteryReq { PoolId = 201, Type = ELotteryPoolType.GachaCountMin });
        Assert.Equal(0, (await Next(bot, LotteryService.DrawRequest, LotteryResp.Parser)).Result);
        var session = app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == bot.Account);
        var service = app.Services.GetRequiredService<LotteryService>();
        var loop = app.Services.GetRequiredService<GameLoop>();
        var deadlines = app.Services.GetRequiredService<Deadlines>();
        await loop.InvokeAsync(() => { loop.Remove(deadlines); return Task.CompletedTask; });
        var tomorrow = clock.GetLocalNow().Date.AddDays(1);
        var boundary = new DateTimeOffset(tomorrow, clock.LocalTimeZone.GetUtcOffset(tomorrow));
        clock.Set(boundary.AddMilliseconds(-1));
        await DrainDeadlines();
        Assert.Equal(1, session.Lottery!.LotteryPool[201].DailyCount);
        var before = session.Lottery.Clone();
        var tickets = session.Items.Where(i => i.Entry == 1250717).Sum(i => i.Stack);
        // This is the original polling transformation, driven at exactly the same late clock instant.
        DBLotteryData? expected = null;
        await RunLotteryDeadlineAt(session, boundary.AddMilliseconds(lateMilliseconds), () =>
            expected = (DBLotteryData)typeof(LotteryService).GetMethod("Project", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [session, before, true])!);
        Assert.NotNull(expected);
        Assert.Equal(expected, session.Lottery);
        Assert.Equal(0, session.Lottery.LotteryPool[201].DailyCount);
        Assert.Equal(before.PityRecords, session.Lottery.PityRecords);
        Assert.Equal(tickets, session.Items.Where(i => i.Entry == 1250717).Sum(i => i.Stack));
        using var stored = JsonDocument.Parse(app.Services.GetRequiredService<RoleStore>().LoadLottery(session.Role!.Id)!);
        Assert.Equal(expected, DBLotteryData.Parser.ParseFrom(stored.RootElement.GetProperty("Data").GetBytesFromBase64()));
        Assert.Equal(session.LotteryDay, stored.RootElement.GetProperty("Day").GetInt64());
        var bytes = app.Services.GetRequiredService<RoleStore>().LoadLottery(session.Role.Id);
        await DrainDeadlines();
        Assert.Equal(bytes, app.Services.GetRequiredService<RoleStore>().LoadLottery(session.Role.Id));
    }

    [ClientDataFact]
    public async Task LotteryBannerClosesAtConfiguredEndWithoutTickerAndRejectsDraw()
    {
        await using var bot = await InWorld(create: true);
        await Gm("!lvl 99");
        var session = app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == bot.Account);
        var loop = app.Services.GetRequiredService<GameLoop>();
        var lottery = app.Services.GetRequiredService<LotteryService>();
        var activityClock = app.Services.GetRequiredService<ActivityClock>();
        var row = app.Services.GetRequiredService<GameData>().Row("NewActivityConfig", 1000)!.Value;
        var window = activityClock.ResolveActivity(row, clock.GetUtcNow().ToUnixTimeSeconds(), session.Role!.CreateTime, 101);
        Assert.True(window.End > clock.GetUtcNow().ToUnixTimeSeconds());
        await loop.InvokeAsync(() => { loop.Remove(app.Services.GetRequiredService<Deadlines>()); return Task.CompletedTask; });
        clock.Set(DateTimeOffset.FromUnixTimeSeconds(window.End).AddMilliseconds(-1));
        await loop.InvokeAsync(() => lottery.ReconcileAsync(session));
        Assert.True(session.Lottery!.LotteryPool[201].IsOpen);
        await RunLotteryDeadlineAt(session, DateTimeOffset.FromUnixTimeSeconds(window.End).AddMilliseconds(250));
        Assert.False(session.Lottery.LotteryPool[201].IsOpen);
        await bot.SendGameAsync(LotteryService.DrawRequest, new LotteryReq { PoolId = 201, Type = ELotteryPoolType.GachaCountMin });
        Assert.Equal(ErrorCodes.ActivityClosed, (await Next(bot, LotteryService.DrawRequest, LotteryResp.Parser)).Result);
        using var saved = JsonDocument.Parse(app.Services.GetRequiredService<RoleStore>().LoadLottery(session.Role.Id)!);
        Assert.False(DBLotteryData.Parser.ParseFrom(saved.RootElement.GetProperty("Data").GetBytesFromBase64()).LotteryPool[201].IsOpen);
    }
}
