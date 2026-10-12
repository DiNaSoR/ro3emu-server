using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Tests;

public sealed class ActivityFragmentCaptureTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ro3-fragment-capture-" + Guid.NewGuid().ToString("N"));
    private readonly AccountStore accounts;
    private readonly RoleStore roles;
    private readonly ItemStore items;

    public ActivityFragmentCaptureTests()
    {
        accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = directory }));
        roles = new RoleStore(accounts);
        items = new ItemStore(accounts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FragmentTransferGuardsBothInventoriesAndAppliesRecipientGuidOnlyAfterWholeCommit(bool conflict)
    {
        long sender = roles.Create("fragment", 1, "Sender", 1, 1000, 1)!.Id;
        long recipient = roles.Create("fragment", 1, "Recipient", 1, 1000, 1)!.Id;
        var original = new InvItem { Entry = 1111001, Stack = 2, Pack = 1 };
        items.Insert(sender, original);
        var senderBefore = accounts.ReadSync<List<InvItem>>(new ActivityInventoryReadPlan(sender));
        var recipientBefore = accounts.ReadSync<List<InvItem>>(new ActivityInventoryReadPlan(recipient));
        var senderAfter = senderBefore.Select(i => i.Clone()).ToList();
        senderAfter[0].Stack = 1;
        var gift = new InvItem { Entry = 1111001, Stack = 1, Pack = 1 };
        var recipientAfter = new List<InvItem> { gift };
        var transaction = new RoleItemPlanBuilder(accounts);
        transaction.Guard(new ActivityInventoryGuardPlan(sender, senderBefore));
        transaction.Guard(new ActivityInventoryGuardPlan(recipient, recipientBefore));
        transaction.Add(new ItemSnapshotWritePlan(sender, senderAfter), receipt =>
            ItemSnapshotWritePlan.ApplyGuids(senderAfter, (long[])receipt!));
        transaction.Add(new ItemSnapshotWritePlan(recipient, recipientAfter), receipt =>
            ItemSnapshotWritePlan.ApplyGuids(recipientAfter, (long[])receipt!));
        transaction.RoleBlob(recipient, "activity_runtime", [1, 2, 3]);
        Assert.Equal(0, gift.Guid);
        if (conflict)
        {
            original.Stack = 3;
            items.Update(original);
            Assert.ThrowsAny<InvalidOperationException>(() => transaction.Commit(accounts, "test/fragment-conflict"));
            Assert.Equal(0, gift.Guid);
            Assert.Equal(3, Assert.Single(items.Load(sender)).Stack);
            Assert.Empty(items.Load(recipient));
            Assert.Null(roles.LoadActivityRuntime(recipient));
        }
        else
        {
            transaction.Commit(accounts, "test/fragment-success");
            Assert.True(gift.Guid > 0);
            Assert.Equal(gift.Guid, Assert.Single(items.Load(recipient)).Guid);
            Assert.Equal(1, Assert.Single(items.Load(sender)).Stack);
            Assert.Equal(new byte[] { 1, 2, 3 }, roles.LoadActivityRuntime(recipient));
        }
    }

    [Fact]
    public void InventoryGuardComparesRowsIndependentOfLiveBagOrder()
    {
        long role = roles.Create("fragment", 1, "Ordered", 1, 1000, 1)!.Id;
        var first = new InvItem { Entry = 1111001, Stack = 1, Pack = 1 };
        var second = new InvItem { Entry = 1111002, Stack = 1, Pack = 1 };
        items.Insert(role, first);
        items.Insert(role, second);
        var reversed = items.Load(role);
        reversed.Reverse();
        var builder = new RoleItemPlanBuilder(accounts);
        builder.Guard(new ActivityInventoryGuardPlan(role, reversed));
        builder.RoleBlob(role, "activity_runtime", [3]);

        builder.Commit(accounts, "test/fragment-order-independent");

        Assert.Equal(new byte[] { 3 }, roles.LoadActivityRuntime(role));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }
}
