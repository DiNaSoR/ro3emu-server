using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestCollectionOrderer("Ro3Emu.Tests.LongestFirstOrderer", "Ro3Emu.Tests")]

namespace Ro3Emu.Tests;

/// <summary>
/// Starts the test classes that take longest first. A class runs its tests one after another, so a long class started
/// late ran alone at the end of a full run while the other cores sat idle (QuestWalkTests alone took the last ~90 s).
/// Only the order changes; a class missing from the list starts after these in xUnit's usual order.
/// </summary>
public sealed class LongestFirstOrderer : ITestCollectionOrderer
{
    // Longest first, from a full run with the exported data on 4 cores (2026-10-19): seconds per class.
    private static readonly string[] Longest =
    [
        "QuestWalkTests",          // 207 (Category=Slow)
        "SkillCombatTests",        // 211
        "DungeonTests",            // 171
        "GatheringTests",          // 87
        "BossSkillSchedulerTests", // 73
        "GuildTests",              // 62
        "MapTests",                // 61
        "PassPlayTests",           // 58
    ];

    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections)
    {
        var all = testCollections.ToList();
        int Rank(ITestCollection c)
        {
            int i = Array.FindIndex(Longest, name => c.DisplayName.EndsWith("." + name, StringComparison.Ordinal)
                || c.DisplayName.EndsWith(" " + name, StringComparison.Ordinal));
            return i < 0 ? Longest.Length : i;
        }
        return all.Select((c, i) => (c, i)).OrderBy(x => Rank(x.c)).ThenBy(x => x.i).Select(x => x.c);
    }
}
