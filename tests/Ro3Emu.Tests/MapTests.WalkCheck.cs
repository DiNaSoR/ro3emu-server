using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>ServerOptions.WalkSpeedCheck and WalkPathCheck (issue #158).</summary>
public sealed partial class MapTests
{
    private void WalkModes(string speed, string path)
    {
        var o = _app.Services.GetRequiredService<IOptions<ServerOptions>>().Value;
        o.WalkSpeedCheck = speed;
        o.WalkPathCheck = path;
    }

    private async Task<Vec3> ServerPosition(string account)
    {
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var sessions = _app.Services.GetRequiredService<SessionRegistry>();
        return await loop.InvokeAsync(() => Task.FromResult(sessions.All().First(s => s.Account == account && s.Role != null).Position.Clone()));
    }

    /// <summary>
    /// A short legal step (so the next walk is timed from now), then a walk 10-20 m away over walkable ground: far too
    /// fast for a fraction of a second, but on the grid. Returns the step's end and the far target.
    /// </summary>
    private async Task<(Vec3 Step, Vec3 Far)> StepThenDash(BotClient bot)
    {
        var nav = _app.Services.GetRequiredService<WorldData>().Map(SouthGate)!.Nav!;
        var start = await ServerPosition(bot.Account!);
        Vec3? step = null, far = null;
        for (int dir = 0; dir < 32 && far == null; dir++)
        {
            float a = dir * MathF.Tau / 32, cx = MathF.Cos(a), cz = MathF.Sin(a);
            if (nav.WalkHeight(start.X + cx * 0.5f, start.Z + cz * 0.5f, start.Y) is not { } sy) continue;
            var s = new Vec3 { X = start.X + cx * 0.5f, Y = sy, Z = start.Z + cz * 0.5f };
            if (!nav.IsWalkable(s.X, s.Y, s.Z) || !nav.CanWalk(start.X, start.Y, start.Z, s.X, s.Z)) continue;
            for (float d = 20; d >= 10 && far == null; d -= 1)
            {
                float fx = s.X + cx * d, fz = s.Z + cz * d;
                if (nav.WalkHeight(fx, fz, s.Y) is { } fy && nav.IsWalkable(fx, fy, fz) && nav.CanWalk(s.X, s.Y, s.Z, fx, fz))
                    (step, far) = (s, new Vec3 { X = fx, Y = fy, Z = fz });
            }
        }
        Assert.NotNull(far);
        await bot.WalkAsync(step!.X, step.Y, step.Z, stop: true);
        await bot.WalkAsync(far!.X, far.Y, far.Z, stop: true);
        return (step, far);
    }

    private async Task WaitServerPosition(string account, Vec3 at)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (await ServerPosition(account) is var p && (MathF.Abs(p.X - at.X) > 0.01f || MathF.Abs(p.Z - at.Z) > 0.01f))
        {
            if (elapsed.Elapsed >= Wait) Assert.Fail($"server position {p.X:F2},{p.Z:F2}, expected {at.X:F2},{at.Z:F2}");
            await Task.Delay(10);
        }
    }

    [ClientDataFact]
    public async Task EnforcedWalkCheckSnapsATooFastWalkBack()
    {
        WalkModes("enforce", "enforce");
        await using var alice = await InWorld("Alice");
        var (step, _) = await StepThenDash(alice);
        var back = await Next(alice, GameAoiSink.MovementChange, MovementChangeNtf.Parser, m => m.Uid == (long)alice.Self!.Uuid);
        Assert.Equal(step.X, back.MovementInfo.Pos.X, 0.01f);
        Assert.Equal(step.Z, back.MovementInfo.Pos.Z, 0.01f);
        await WaitServerPosition("alice", step);
    }

    [ClientDataFact]
    public async Task ASpeedCheckSetToLogLetsATooFastWalkThroughWhileThePathCheckEnforces()
    {
        WalkModes(speed: "log", path: "enforce");
        await using var alice = await InWorld("Alice");
        var (_, far) = await StepThenDash(alice);
        await WaitServerPosition("alice", far);

        // The path check still enforces: the map corner outside the walls is refused and the player snapped back.
        await alice.WalkAsync(1f, 0f, 1f, stop: true);
        var back = await Next(alice, GameAoiSink.MovementChange, MovementChangeNtf.Parser, m => m.Uid == (long)alice.Self!.Uuid);
        Assert.Equal(far.X, back.MovementInfo.Pos.X, 0.01f);
        Assert.Equal(far.Z, back.MovementInfo.Pos.Z, 0.01f);
        await WaitServerPosition("alice", far);
    }

    [ClientDataFact]
    public async Task AWalkCheckModeIsReadInAnyCase()
    {
        WalkModes(speed: "ENFORCE", path: "log");
        await using var alice = await InWorld("Alice");
        var (step, _) = await StepThenDash(alice);
        await Next(alice, GameAoiSink.MovementChange, MovementChangeNtf.Parser, m => m.Uid == (long)alice.Self!.Uuid);
        await WaitServerPosition("alice", step);
    }
}
