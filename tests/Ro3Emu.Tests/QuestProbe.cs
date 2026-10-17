using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Reads a role's quest state on the game loop, for the quest target tests of the feature fixtures.</summary>
internal static class QuestProbe
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static GameSession? Online(WebApplication app, string account) => app.Services.GetRequiredService<SessionRegistry>()
        .All().FirstOrDefault(s => s.Account == account && s.Role != null);

    /// <summary>The quest's row (a copy), null when the role is offline or has no such quest.</summary>
    public static Task<TaskInfo?> ReadAsync(WebApplication app, string account, long task) =>
        app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
            Task.FromResult(Online(app, account)?.Tasks?.Infos.GetValueOrDefault(task)?.Clone()));

    public static async Task<TaskInfo> WaitAsync(WebApplication app, string account, long task, Func<TaskInfo, bool> match, string what)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (true)
        {
            var info = await ReadAsync(app, account, task);
            if (info != null && match(info)) return info;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"{account} quest {task} {what}: {info}");
            await Task.Delay(25);
        }
    }

    /// <summary>Running (or finished) with its first target at <paramref name="progress"/>.</summary>
    public static Task<TaskInfo> AtAsync(WebApplication app, string account, long task, long progress) =>
        WaitAsync(app, account, task, i => i.Status is TaskState.Running or TaskState.Finish && i.CompleteNum[0] == progress,
            $"at {progress}");

    /// <summary>Every target met: finished (a claimed quest) or already delivered (an automatic one).</summary>
    public static Task<TaskInfo> DoneAsync(WebApplication app, string account, long task) =>
        WaitAsync(app, account, task, i => i.Status is TaskState.Finish or TaskState.Delivery, "done");

    public static async Task GmAsync(string http, string account, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{http}/ro3emu/gm?account={account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    /// <summary>GM accept, then wait until the quest is in the role's state.</summary>
    public static async Task AcceptAsync(WebApplication app, string http, string account, long task)
    {
        await GmAsync(http, account, $"!quest accept {task}");
        await WaitAsync(app, account, task, _ => true, "accepted");
    }

    /// <summary>Wait until the account's role has left the world (before a relogin).</summary>
    public static async Task OfflineAsync(WebApplication app, string account)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (await app.Services.GetRequiredService<GameLoop>().InvokeAsync(() => Task.FromResult(Online(app, account) != null)))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"{account} is still online");
            await Task.Delay(25);
        }
        await Task.Delay(200);
    }
}
