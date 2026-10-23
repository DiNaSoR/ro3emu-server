using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>#279: a bot stage that times out says how far the server got, by the last messages it sent.</summary>
public sealed class BotStageTimeoutTests
{
    [Fact]
    public async Task AStageTimeoutListsTheLastMessagesReceived()
    {
        await using var host = await SyntheticHost.StartAsync();
        var selection = host.Service<RoleSessionService>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Hold the selection before its reply, so the bot's role-select stage waits for nothing.
        selection.SelectionPhaseForTest = (phase, _) => phase == "BeforePublish" ? release.Task : Task.CompletedTask;
        await using var bot = host.NewBot();
        try
        {
            await bot.LoginAsync("stall", "pw");
            await bot.EnterGateAsync();
            long role = (await bot.CreateRoleAsync("Stall")).RoleBaseInfo.Id;
            bot.Timeout = TimeSpan.FromSeconds(1);
            var timeout = await Assert.ThrowsAsync<TimeoutException>(() => bot.EnterWorldAsync(role));
            Assert.StartsWith("bot stage role-select timed out after ", timeout.Message, StringComparison.Ordinal);
            // Other notices may arrive while selection is held; creation must be in the history,
            // but need not be its final entry.
            Assert.Contains($"game {MsgId.LoginRoleCreate}", timeout.Message, StringComparison.Ordinal);
            Assert.IsType<TimeoutException>(timeout.InnerException);
        }
        finally
        {
            release.TrySetResult();
            selection.SelectionPhaseForTest = null;
        }
    }
}
