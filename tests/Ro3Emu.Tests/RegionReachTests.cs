using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>31209 region entries are checked against the server's position, which trails a walking player.</summary>
public sealed class RegionReachTests
{
    // A circle region of radius 3 (shapeType 1): the client reports entry at 3 m from the centre.
    private static readonly MapRegion Circle = new(1, 0, 0, 0, Width: 3, Length: 3, Actions: "", Shape: 1);

    private static GameSession At(float x, bool walking, double secondsSinceWalk) => new(null!, null!)
    {
        Position = new Vec3 { X = x },
        Speed = 700,
        Walking = walking,
        LastWalkUtc = DateTime.UtcNow - TimeSpan.FromSeconds(secondsSinceWalk),
    };

    [Fact]
    public void AWalkingPlayerOneKeepAliveBehindTheEdgeReachesTheRegion()
    {
        // Last WALK 0.9 s ago, 6.3 m (0.9 s at 7 m/s) before the edge: 9.3 m from the centre.
        Assert.True(SceneService.NearRegion(At(9.3f, walking: true, secondsSinceWalk: 0.9), Circle));
    }

    [Fact]
    public void AStoppedPlayerGetsOnlyTheFixedTolerance()
    {
        Assert.True(SceneService.NearRegion(At(8f, walking: false, secondsSinceWalk: 0.9), Circle));
        Assert.False(SceneService.NearRegion(At(9.3f, walking: false, secondsSinceWalk: 0.9), Circle));
    }

    [Fact]
    public void ALateWalkReportWidensTheReachByAtMostTwoSeconds()
    {
        // 1.9 s since the report, still counted: 3 + 5 + 13.3 = 21.3 m.
        Assert.True(SceneService.NearRegion(At(21f, walking: true, secondsSinceWalk: 1.9), Circle));
        Assert.False(SceneService.NearRegion(At(22f, walking: true, secondsSinceWalk: 1.9), Circle));
    }

    [Fact]
    public void AStraightLegLongerThanTheRegionWindowKeepsTheCastSlack()
    {
        // No WALK on a straight leg: 4 s in, casts still count 4 s at 7 m/s.
        Assert.Equal(28f, At(0, walking: true, secondsSinceWalk: 4).WalkReportLag(5), 0.5f);
    }

    [Fact]
    public void AStaleWalkReportGivesNoAllowance()
    {
        // A client that stops sending WALK keeps no bonus past 5 s: only the fixed 3 + 5 m.
        Assert.True(SceneService.NearRegion(At(8f, walking: true, secondsSinceWalk: 60), Circle));
        Assert.False(SceneService.NearRegion(At(9.3f, walking: true, secondsSinceWalk: 60), Circle));
        Assert.Equal(0, At(0, walking: true, secondsSinceWalk: 5.5).WalkReportLag(5));
    }

    [Fact]
    public async Task AMapChangeEndsTheWalkBeforeIt()
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var bot = await host.InWorldAsync("Walker");
        var session = host.Service<SessionRegistry>().All().Single(s => s.Role != null);
        await host.Service<GameLoop>().InvokeAsync(async () =>
        {
            session.Walking = true;
            session.LastWalkUtc = DateTime.UtcNow - TimeSpan.FromSeconds(10);
            Assert.True(await host.Service<SceneService>().EnterSceneAsync(session, SyntheticData.Town));
            Assert.False(session.Walking);
            Assert.Equal(0, session.WalkReportLag(2));
        });
    }
}
