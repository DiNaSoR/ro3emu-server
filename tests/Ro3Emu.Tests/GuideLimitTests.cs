using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>84000 UPDATE_GUIDE input limits (issue #143): only GuideConfig ids, per-message and stored caps.</summary>
public sealed class GuideLimitTests
{
    private static readonly HashSet<long> Config = [1, 2, 21, 199, 8001];
    private static bool Known(long id) => Config.Contains(id);

    [Fact]
    public void UnknownDuplicateAndPlaceholderIdsAreDropped()
    {
        var guides = new DBGuideInfo();
        bool changed = GuideService.Apply(guides, [21, 21, 0, -5, 999999, 2], [new(21, 1), new(424242, 7)], Known);
        Assert.True(changed);
        Assert.Equal([21L, 2], guides.FinishedGuideIds);
        Assert.Equal(new Dictionary<long, long> { [21] = 1 }, guides.GuideFinishRecord);
    }

    [Fact]
    public void RepeatedReportIsNotAChange()
    {
        var guides = new DBGuideInfo();
        GuideService.Apply(guides, [21], [new(21, 1)], Known);
        Assert.False(GuideService.Apply(guides, [21], [new(21, 1)], Known));
        Assert.True(GuideService.Apply(guides, [], [new(21, 2)], Known));
    }

    [Fact]
    public void StoredSetIsCappedWithoutClientData()
    {
        var guides = new DBGuideInfo();
        var many = Enumerable.Range(1, 200_000).Select(i => (long)i).ToArray();
        GuideService.Apply(guides, many, many.Select(i => new KeyValuePair<long, long>(i, 1)), _ => true);
        Assert.Equal(GuideService.MaxStored, guides.FinishedGuideIds.Count);
        Assert.Equal(GuideService.MaxStored, guides.GuideFinishRecord.Count);
    }

    [Fact]
    public void BloatedSavedBlobIsCleanedAtLogin()
    {
        var saved = new DBGuideInfo { FinishedGuideIds = { 1, 1, 77, 199 }, GuideFinishRecord = { [1] = 3, [77] = 1 } };
        var clean = GuideService.Clean(saved, Known);
        Assert.Equal([1L, 199], clean.FinishedGuideIds);
        Assert.Equal(new Dictionary<long, long> { [1] = 3 }, clean.GuideFinishRecord);
    }
}
