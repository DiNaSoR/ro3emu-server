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
    public void AnOldWalkReportDoesNotWidenTheReachWithoutLimit()
    {
        // 2 s at most: 3 + 5 + 14 = 22 m.
        Assert.True(SceneService.NearRegion(At(21f, walking: true, secondsSinceWalk: 60), Circle));
        Assert.False(SceneService.NearRegion(At(23f, walking: true, secondsSinceWalk: 60), Circle));
    }
}
