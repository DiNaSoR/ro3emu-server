using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Tests;

/// <summary>C/D 19: backup cut while writes advance, atomic completed files, offline restore.</summary>
public sealed class DatabaseBackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3-backup-" + Guid.NewGuid().ToString("N"));
    private ServerOptions Options() => new() { DataDir = _dir };
    private AccountStore Store() => new(Microsoft.Extensions.Options.Options.Create(Options()));
    private DatabaseBackup Backup(AccountStore accounts) =>
        new(Microsoft.Extensions.Options.Options.Create(Options()), accounts, NullLogger<DatabaseBackup>.Instance);

    private static (double X, long Revision) PositionAndRevision(string path, long role)
    {
        using var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT r.pos_x, COALESCE((SELECT revision FROM owner_revision WHERE kind='role' AND id=$id AND part=$part), 0)
            FROM role r WHERE r.id=$role
            """;
        command.Parameters.AddWithValue("$id", role.ToString());
        command.Parameters.AddWithValue("$part", RevisionStore.Position);
        command.Parameters.AddWithValue("$role", role);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetDouble(0), reader.GetInt64(1));
    }

    [Fact]
    public async Task EveryBackupTakenWhileTheWriterAdvancesIsOneCommittedCut()
    {
        using var accounts = Store();
        var roles = new RoleStore(accounts);
        var role = roles.Create("cut", 1, "Cut", 1, 1000, 1)!;
        roles.SavePosition(role.Id, 1, 1, 0, 0, 0);
        var first = PositionAndRevision(accounts.DatabasePath, role.Id);
        Assert.True(first.Revision > 0, "the position write must bump its revision in the same transaction");
        long offset = (long)first.X - first.Revision;

        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            int x = 1;
            while (!stop.IsCancellationRequested) roles.SavePosition(role.Id, 1, ++x, 0, 0, 0);
            return x;
        });
        var backup = Backup(accounts);
        var copies = new List<string>();
        for (int i = 0; i < 6; i++) { copies.Add(backup.Backup()); await Task.Delay(20); }
        stop.Cancel();
        int last = await writer;

        long previous = 0;
        foreach (var copy in copies)
        {
            var (x, revision) = PositionAndRevision(copy, role.Id);
            Assert.Equal(offset, (long)x - revision); // the row and its revision come from the same commit
            Assert.True(revision >= previous, "later backups never go back in time");
            previous = revision;
        }
        Assert.True(PositionAndRevision(copies[^1], role.Id).X <= last);
        Assert.Equal(copies.Count, copies.Distinct().Count());
        Assert.Empty(Directory.GetFiles(backup.Directory, "*" + DatabaseBackup.PartialExtension));
    }

    [Fact]
    public void PruningCountsOnlyCompletedBackupsAndStartupClearsUnfinishedOnes()
    {
        using var accounts = Store();
        new RoleStore(accounts).Create("prune", 1, "Prune", 1, 1000, 1);
        var backup = Backup(accounts);
        Directory.CreateDirectory(backup.Directory);
        // A crash mid-copy leaves a partial file; it must never count as one of the kept backups.
        string stale = Path.Combine(backup.Directory, "ro3emu-20000101-000000.db" + DatabaseBackup.PartialExtension);
        File.WriteAllText(stale, "half a database");
        var made = Enumerable.Range(0, 22).Select(_ => backup.Backup()).ToList();
        var kept = DatabaseBackup.Completed(backup.Directory).ToList();
        Assert.Equal(20, kept.Count);
        Assert.Equal(made.TakeLast(20).OrderBy(f => f), kept.OrderBy(f => f));
        foreach (var file in kept) DatabaseRestore.Validate(file);
        Assert.True(File.Exists(stale)); // only the service's startup removes it (no backup can be running then)
    }

    [Fact]
    public void RestoreRefusesWhileTheRealmIsOpenThenReplacesItAndKeepsTheOldFile()
    {
        string copy;
        long id;
        using (RealmLock.Acquire(_dir)) // what a running server holds
        using (var accounts = Store())
        {
            var roles = new RoleStore(accounts);
            id = roles.Create("restore", 1, "Before", 1, 1000, 1)!.Id;
            copy = Backup(accounts).Backup();
            roles.Create("restore", 1, "After", 1, 1000, 1);
            var refused = Assert.Throws<InvalidOperationException>(() => DatabaseRestore.Restore(_dir, copy));
            Assert.Contains("in use", refused.Message);
            Assert.Throws<InvalidOperationException>(() => RealmLock.Acquire(_dir)); // nor may a second server start
            Assert.Single(new RoleStore(accounts).ForAccount("restore"), r => r.Name == "After");
        }
        SqliteConnection.ClearAllPools();

        string? saved = DatabaseRestore.Restore(_dir, copy);
        Assert.NotNull(saved);
        Assert.True(File.Exists(Path.Combine(saved!, "ro3emu.db")));
        Assert.False(File.Exists(Path.Combine(_dir, "ro3emu.db.restoring")));
        using var restored = Store();
        var names = new RoleStore(restored).ForAccount("restore").Select(r => r.Name).ToList();
        Assert.Equal(["Before"], names);
        Assert.NotNull(new RoleStore(restored).Get(id));
    }

    [Fact]
    public async Task ARunningServerHoldsItsDataDirAndItsStartupBackupClearsUnfinishedCopies()
    {
        string stale = Path.Combine(_dir, "backups", "ro3emu-20000101-000000.db" + DatabaseBackup.PartialExtension);
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        File.WriteAllText(stale, "half a database");
        var (http, micro, game) = TestPorts.FreePorts();
        void Configure(ServerOptions o) { o.DataDir = _dir; o.HttpPort = http; o.MicroGatePort = micro; o.GameGatePort = game; }
        var app = ServerHost.Create([], Configure);
        await app.StartAsync();
        string copy;
        try
        {
            var backup = app.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<DatabaseBackup>().Single();
            copy = backup.Backup();
            Assert.False(File.Exists(stale));
            Assert.Contains("in use", Assert.Throws<InvalidOperationException>(() => ServerHost.Create([], Configure)).Message);
            Assert.Contains("in use", Assert.Throws<InvalidOperationException>(() => DatabaseRestore.Restore(_dir, copy)).Message);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
        SqliteConnection.ClearAllPools();
        Assert.NotNull(DatabaseRestore.Restore(_dir, copy));
    }

    [Fact]
    public async Task LoopWaitInventoryCountsOnlySynchronousWaitsOnTheLoopByLabel()
    {
        var (http, micro, game) = TestPorts.FreePorts();
        await using var app = ServerHost.Create([], o => { o.DataDir = _dir; o.HttpPort = http; o.MicroGatePort = micro; o.GameGatePort = game; });
        await app.StartAsync();
        var accounts = app.Services.GetRequiredService<AccountStore>();
        var roles = app.Services.GetRequiredService<RoleStore>();
        long id = roles.Create("inventory", 1, "Inventory", 1, 1000, 1)!.Id;
        var before = accounts.AccessMetrics.LoopWaitCounts();
        roles.Get(id); // off the loop: not a loop wait
        var mid = accounts.AccessMetrics.LoopWaitCounts();
        await app.Services.GetRequiredService<Ro3Emu.Server.Game.GameLoop>().InvokeAsync(() =>
        {
            for (int i = 0; i < 3; i++) roles.Get(id);
            roles.SaveSettings(id, [7]);
            return Task.CompletedTask;
        });
        var after = accounts.AccessMetrics.LoopWaitCounts();
        // The server's own loop may read in the background: assert on the label these calls use, not on the whole map.
        string read = Assert.Single(after.Where(p => p.Key.StartsWith("R ", StringComparison.Ordinal)
            && p.Value - mid.GetValueOrDefault(p.Key) >= 3).Select(p => p.Key));
        Assert.Equal(before.GetValueOrDefault(read), mid.GetValueOrDefault(read)); // the off-loop call did not count
        Assert.Contains(after, p => p.Key.StartsWith("W ", StringComparison.Ordinal) && p.Value > mid.GetValueOrDefault(p.Key));
        Assert.NotEmpty(accounts.AccessMetrics.LoopWaits(5));
        await app.StopAsync();
    }

    [Fact]
    public void RestoreRefusesACorruptOrForeignFileAndLeavesTheRealmAlone()
    {
        using (var accounts = Store()) new RoleStore(accounts).Create("keep", 1, "Keep", 1, 1000, 1);
        SqliteConnection.ClearAllPools();
        string junk = Path.Combine(_dir, "junk.db");
        File.WriteAllBytes(junk, Enumerable.Repeat((byte)0x5A, 8192).ToArray());
        Assert.ThrowsAny<Exception>(() => DatabaseRestore.Restore(_dir, junk));
        string foreign = Path.Combine(_dir, "foreign.db");
        using (var db = new SqliteConnection($"Data Source={foreign};Pooling=False"))
        {
            db.Open();
            using var create = db.CreateCommand();
            create.CommandText = "CREATE TABLE something(id INTEGER)";
            create.ExecuteNonQuery();
        }
        Assert.Contains("not a realm database", Assert.Throws<InvalidDataException>(() => DatabaseRestore.Restore(_dir, foreign)).Message);
        Assert.False(Directory.Exists(Path.Combine(_dir, "restore-saved")));
        using var accounts2 = Store();
        Assert.Single(new RoleStore(accounts2).ForAccount("keep"));
    }

    [Fact]
    public void RestoreCliReportsTheOutcome()
    {
        string copy;
        using (var accounts = Store()) { new RoleStore(accounts).Create("cli", 1, "Cli", 1, 1000, 1); copy = Backup(accounts).Backup(); }
        SqliteConnection.ClearAllPools();
        var output = new StringWriter();
        Assert.Equal(0, DatabaseRestore.RunCli(copy, Options(), output));
        Assert.Contains("Restored", output.ToString());
        output = new StringWriter();
        Assert.Equal(1, DatabaseRestore.RunCli(Path.Combine(_dir, "missing.db"), Options(), output));
        Assert.Contains("Restore refused", output.ToString());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
