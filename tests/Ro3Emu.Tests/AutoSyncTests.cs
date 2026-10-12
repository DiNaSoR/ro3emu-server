using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>AUTO_SYNC increments generated from DB object versions (the shapes the client expects).</summary>
public sealed class AutoSyncTests
{
    private static ItemBaseInfo Item(long guid, int entry, int stack) => new()
    {
        ItemGuid = guid, ItemEntry = entry, Stack = stack, ItemBindInfo = new ItemBindInfo(),
        ItemInfos = { [10001] = new ItemAttrValue { LongVal = 0 } },
    };

    private static DBItemsInfo Bag(params ItemBaseInfo[] items)
    {
        var bag = new DBItemsInfo { Currency = { [1] = 1000 } };
        var pack = new DBPackInfo { PackCapacity = 300 };
        foreach (var (item, i) in items.Select((x, i) => (x, i)))
        {
            pack.Items[item.ItemGuid] = item;
            pack.SortedIndex[item.ItemGuid] = i + 1;
        }
        bag.PackInfo[1] = pack;
        return bag;
    }

    [Fact]
    public void FullSyncSendsEverythingWithAllAndSyncAllMark()
    {
        var inc = (DBItemsInfoIncrement)AutoSync.Full(Bag(Item(5, 1000000, 20)), version: 1);
        Assert.True(inc.SyncAllMark);
        Assert.Equal(1, inc.Version);
        Assert.Equal(20, inc.PackInfo[1].All.Items[5].Stack);
        Assert.Equal(1000, inc.Currency[1].All);
    }

    [Fact]
    public void StackChangeIsANestedIncAndNewItemIsAll()
    {
        var before = Bag(Item(5, 1000000, 20));
        var after = Bag(Item(5, 1000000, 25), Item(6, 1000001, 1));
        var inc = (DBItemsInfoIncrement)AutoSync.Changes(before, after, version: 2)!;
        Assert.False(inc.SyncAllMark);
        var pack = inc.PackInfo[1].Inc;
        Assert.Equal(25, pack.Items[5].Inc.Stack);          // existing key: merge
        Assert.False(pack.Items[5].Inc.HasItemEntry);       // unchanged fields absent
        Assert.Equal(1000001, pack.Items[6].All.ItemEntry); // new key: all (inc on a new key decodes wrong)
        Assert.Equal(2, pack.SortedIndex[6].All);
        Assert.False(pack.SortedIndex.ContainsKey(5));
        Assert.Empty(inc.Currency);
    }

    [Fact]
    public void RemovedItemIsDelAndNothingChangedIsNull()
    {
        var before = Bag(Item(5, 1000000, 20), Item(6, 1000001, 1));
        var after = Bag(Item(5, 1000000, 20));
        var inc = (DBItemsInfoIncrement)AutoSync.Changes(before, after, 3)!;
        Assert.True(inc.PackInfo[1].Inc.Items[6].Del);
        Assert.True(inc.PackInfo[1].Inc.SortedIndex[6].Del);
        Assert.Null(AutoSync.Changes(after, after.Clone(), 4));
    }

    [Fact]
    public void ScalarsAndOptionalPresence()
    {
        var before = new RoleBaseInfo { Id = 7, Level = 1, Exp = 0, Career = 1000 };
        var after = before.Clone();
        after.Level = 2;
        after.Exp = 3;
        var inc = (RoleBaseInfoIncrement)AutoSync.Changes(before, after, 2)!;
        Assert.Equal(2, inc.Level);
        Assert.Equal(3, inc.Exp);
        Assert.False(inc.HasCareer);
        Assert.False(inc.HasId);

        // The full sync carries zeros too (the client indexes some fields unguarded).
        var full = (RoleBaseInfoIncrement)AutoSync.Full(before, 1);
        Assert.True(full.HasExp);
        Assert.Equal(0, full.Exp);
    }

    [Fact]
    public void EquipPlanGridGetsNestedMapIncrement()
    {
        var before = new DBEquipInfo { CurrentEquipPlan = 0, EquipPlans = { [0] = new DBEquipPlan() } };
        var after = before.Clone();
        after.Items[9] = Item(9, 2010121, 1);
        after.EquipPlans[0].ItemsEquipGrid[1] = 9;
        var inc = (DBEquipInfoIncrement)AutoSync.Changes(before, after, 2)!;
        Assert.Equal(2010121, inc.Items[9].All.ItemEntry);
        Assert.Equal(9, inc.EquipPlans[0].Inc.ItemsEquipGrid[1].All);
    }
}
