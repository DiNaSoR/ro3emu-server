using System.Reflection;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class NewActivityTests
{
    [Fact]
    public async Task CommitCannotResumeItsCallerAfterAttachmentChangesDuringBeforeSync()
    {
        await using var bot = await InWorld("ActTail");
        var session = Session(bot);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool resumed = false;
        var commit = typeof(NewActivityService).GetMethod("CommitAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pending = OnLoop(async () =>
        {
            await session.ActivityGate.WaitAsync();
            try
            {
                Func<Task> beforeSync = async () => { waiting.SetResult(); await release.Task; };
                var operation = (Task<int>)commit.Invoke(Service<NewActivityService>(),
                    [session, session.NewActivities!.Clone(), null, null, null, null, null, false, beforeSync, null, null, null, null])!;
                await operation;
                resumed = true;
            }
            finally { session.ActivityGate.Release(); }
        });
        try
        {
            await waiting.Task.WaitAsync(Wait);
            await OnLoop(() =>
            {
                // Same role ID, new attachment: checking role identity alone is insufficient.
                Service<Deadlines>().Detach(session);
                Service<Deadlines>().Attach(session);
                return Task.CompletedTask;
            });
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(resumed);
    }
}
