using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Tests;

/// <summary>C/D 16 part 3: a runtime save of the bytes this process last persisted is skipped, never a stale one.</summary>
public sealed class UnchangedSaveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3-unchanged-" + Guid.NewGuid().ToString("N"));
    private readonly AccountStore _accounts;
    private readonly RoleStore _roles;
    private readonly long _role;
    private int _taskWrites;

    public UnchangedSaveTests()
    {
        _accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = _dir }));
        _roles = new RoleStore(_accounts);
        _role = _roles.Create("unchanged", 1, "Unchanged", 1, 1000, 1)!.Id;
        _accounts.WriteSyncPlanObserved = (plan, _) => { if (plan is RoleBlobWritePlan { Column: "tasks" }) _taskWrites++; };
    }

    private byte[]? Stored() => _roles.LoadTasks(_role);

    [Fact]
    public void ARepeatedSaveOfTheSameBytesIsSkippedAndAChangedOneIsWritten()
    {
        _roles.SaveTasks(_role, [1, 2, 3]);
        _roles.SaveTasks(_role, [1, 2, 3]);
        _roles.SaveTasks(_role, [1, 2, 3]);
        Assert.Equal(1, _taskWrites);
        Assert.Equal(2, _accounts.Snapshots.PersistedSkips);
        _roles.SaveTasks(_role, [4]);
        Assert.Equal(2, _taskWrites);
        Assert.Equal(new byte[] { 4 }, Stored());
        _roles.SaveTasks(_role, [1, 2, 3]); // back to an older value: a real change, written
        Assert.Equal(3, _taskWrites);
        Assert.Equal(new byte[] { 1, 2, 3 }, Stored());
    }

    [Fact]
    public void AnotherCommitOfThePartForgetsTheRememberedValue()
    {
        _roles.SaveTasks(_role, [1, 2, 3]);
        // Another path (a transaction) commits the same part with different bytes.
        var other = _roles.NewTransaction();
        _roles.SaveTasks(_role, [9, 9], other);
        _roles.Commit(other, "test/other-tasks-writer");
        Assert.Equal(new byte[] { 9, 9 }, Stored());
        _roles.SaveTasks(_role, [1, 2, 3]); // equal to what this path last wrote, but no longer what is stored
        Assert.Equal(2, _taskWrites);
        Assert.Equal(new byte[] { 1, 2, 3 }, Stored());
    }

    [Fact]
    public void ASaveRacedByAnotherCommitOfThePartIsNotRemembered()
    {
        var token = _accounts.Snapshots.BeginSave(_role, "tasks");
        _accounts.Snapshots.Committed([RevisionStore.Role(_role, "tasks")]); // a concurrent writer's commit
        _accounts.Snapshots.Committed([RevisionStore.Role(_role, "tasks")]); // ours
        _accounts.Snapshots.EndSave(_role, "tasks", token, [5], committed: true);
        Assert.False(_accounts.Snapshots.PersistedEqual(_role, "tasks", [5]));
        var single = _accounts.Snapshots.BeginSave(_role, "tasks");
        _accounts.Snapshots.Committed([RevisionStore.Role(_role, "tasks")]);
        _accounts.Snapshots.EndSave(_role, "tasks", single, [5], committed: true);
        Assert.True(_accounts.Snapshots.PersistedEqual(_role, "tasks", [5]));
        var failed = _accounts.Snapshots.BeginSave(_role, "tasks"); // a failed save forgets, never remembers
        _accounts.Snapshots.EndSave(_role, "tasks", failed, [5], committed: false);
        Assert.False(_accounts.Snapshots.PersistedEqual(_role, "tasks", [5]));
    }

    [Fact]
    public void AnIdentityWriteForgetsEveryRememberedPartOfTheRole()
    {
        _roles.SaveTasks(_role, [1]);
        _roles.SaveTaskRuntime(_role, [2]);
        _roles.TouchLogin(_role);
        _roles.SaveTasks(_role, [1]);
        _roles.SaveTaskRuntime(_role, [2]);
        Assert.Equal(2, _taskWrites);
        Assert.Equal(0, _accounts.Snapshots.PersistedSkips);
    }

    public void Dispose()
    {
        _accounts.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
