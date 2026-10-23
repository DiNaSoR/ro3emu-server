using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class DungeonShelterTests
{
    [Theory]
    [InlineData(0f, 7f, true)] // straight behind the cover
    [InlineData(1.4f, 8f, true)] // within the shadow's width
    [InlineData(2f, 8f, false)] // beside it
    [InlineData(0f, 3f, false)] // in front of it
    [InlineData(0f, 12f, false)] // past the shadow's depth
    public void ThePyramidsShadowLiesBehindItAsSeenFromTheBoss(float x, float z, bool sheltered)
    {
        var shelter = new ReconstructedDungeons.Shelter(0, 0, 0, 0, 20000, 4000, 1.5f, 6);
        Assert.Equal(sheltered, DungeonService.Sheltered(0, 0, 0, 5, x, z, shelter));
    }

}
