using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Tests;

/// <summary>Stage G: a role's selection stages its login blobs and commits them together (AccountStore.Staging).</summary>
public sealed class LoginStagingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3-staging-" + Guid.NewGuid().ToString("N"));
    private readonly AccountStore _accounts;
    private readonly RoleStore _roles;

    public LoginStagingTests()
    {
        _accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = _dir }));
        _roles = new RoleStore(_accounts);
    }

    public void Dispose()
    {
        _accounts.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private long NewRole(string name) => _roles.Create("acct-" + name, 1, name, 1, 1000, 1)!.Id;
    private byte[]? Stored(long role, string column) =>
        _accounts.ReadSync<byte[]?>(new RoleBlobReadPlan(role, column));

    [Fact]
    public async Task StagedBlobsAreReadBackAndCommittedTogether()
    {
        long role = NewRole("Stager");
        _roles.BeginLoginStaging(role);
        _roles.SaveTasks(role, [1, 2, 3]);
        _roles.SaveDaily(role, [4]);
        Assert.Equal(new byte[] { 1, 2, 3 }, _roles.LoadTasks(role)); // reads see the stage
        Assert.Null(Stored(role, "tasks"));                            // nothing written yet
        Assert.Equal(2, _accounts.StagedCount(role));
        await _roles.CommitLoginStagingAsync(role);
        Assert.Equal(new byte[] { 1, 2, 3 }, Stored(role, "tasks"));
        Assert.Equal(new byte[] { 4 }, Stored(role, "daily"));
        Assert.Equal(-1, _accounts.StagedCount(role));                // staging ended
        _roles.SaveTasks(role, [9]);                                   // later saves write directly again
        Assert.Equal(new byte[] { 9 }, Stored(role, "tasks"));
    }

    [Fact]
    public void AnotherWriterOfAStagedColumnWritesTheStageFirstAndWins()
    {
        // The ordering risk of batching login writes: an older staged value must never land after a newer write of
        // the same part, and the other writer's revision capture must stay exact (no conflict).
        long role = NewRole("Orderly");
        _roles.BeginLoginStaging(role);
        _roles.SaveTasks(role, [1]);
        var builder = _roles.NewTransaction();
        builder.RoleBlob(role, "tasks", [2]);
        builder.Commit(_accounts, "test/tasks");
        Assert.Equal(new byte[] { 2 }, Stored(role, "tasks"));
        Assert.Equal(-1, _accounts.StagedCount(role)); // the stage was written and staging ended
    }

    [Fact]
    public void AProgressBoundWriteOfAStagedColumnCapturesTheStageRevision()
    {
        // A login's daily reset pinned the role through its progress source while the stage still held the quests
        // accepted at login; the stage was written at Prepare, after the capture, and the reset hit a revision
        // conflict that dropped the connection (WildAfkTests pending old-day award, evenings only).
        long role = NewRole("Pinned");
        var progress = _roles.LoadProgress(role);
        Assert.NotNull(progress.SourceRevision);
        _roles.BeginLoginStaging(role);
        _roles.SaveTasks(role, [1]);
        var builder = _roles.NewTransaction();
        builder.BindProgressSource(role, progress);
        builder.Add(new RoleBlobWritePlan(role, "tasks", [2]));
        builder.Commit(_accounts, "test/progress-bound-tasks");
        Assert.Equal(new byte[] { 2 }, Stored(role, "tasks"));
        Assert.Equal(-1, _accounts.StagedCount(role));
    }

    [Fact]
    public void AReadThroughATransactionSeesTheStagedValue()
    {
        long role = NewRole("Reader");
        _roles.BeginLoginStaging(role);
        _roles.SaveTasks(role, [7]);
        var builder = _roles.NewTransaction();
        Assert.Equal(new byte[] { 7 }, builder.ReadRoleBlob(_accounts, role, "tasks"));
    }

    [Fact]
    public async Task AnUnchangedSaveIsNotStaged()
    {
        long role = NewRole("Same");
        _roles.SaveTasks(role, [5]);
        _roles.BeginLoginStaging(role);
        _roles.SaveTasks(role, [5]);
        Assert.Equal(0, _accounts.StagedCount(role));
        _roles.SaveTasks(role, [6]);
        _roles.SaveTasks(role, [5]); // once staged, a change back is staged too
        Assert.Equal(1, _accounts.StagedCount(role));
        await _roles.CommitLoginStagingAsync(role);
        Assert.Equal(new byte[] { 5 }, Stored(role, "tasks"));
    }
}
