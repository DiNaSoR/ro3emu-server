using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts;

/// <summary>
/// Offline restore of a backup made by <see cref="DatabaseBackup"/> (C/D 19): <c>Ro3Emu.Server --restore-backup &lt;file&gt;</c>.
/// - The backup must pass <c>PRAGMA quick_check</c> and hold the realm's core tables.
/// - The realm must not be open: the restore takes the data dir's <see cref="RealmLock"/>, which a running server holds.
/// - The live database with its WAL is moved to <c>&lt;data&gt;/restore-saved/</c>, never deleted, and the backup is
///   copied in through a temporary name, so an interrupted restore leaves either the old or the new file.
/// </summary>
public static class DatabaseRestore
{
    private static readonly string[] CoreTables = ["account", "role"];

    public static int RunCli(string backup, ServerOptions options, TextWriter output)
    {
        try
        {
            var saved = Restore(options.DataDir, backup);
            output.WriteLine($"Restored {backup} into {Path.Combine(options.DataDir, "ro3emu.db")}");
            if (saved != null) output.WriteLine($"The replaced database is kept in {saved}");
            return 0;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or SqliteException)
        {
            output.WriteLine("Restore refused: " + e.Message);
            return 1;
        }
    }

    /// <summary>Restores <paramref name="backup"/>; returns the folder holding the replaced database (null if none).</summary>
    public static string? Restore(string dataDir, string backup)
    {
        backup = Path.GetFullPath(backup);
        if (!File.Exists(backup)) throw new IOException($"No backup at {backup}");
        Validate(backup);
        var live = Path.Combine(dataDir, "ro3emu.db");
        if (string.Equals(Path.GetFullPath(live), backup, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The backup is the live database.");
        using var held = RealmLock.Acquire(dataDir); // a running server holds it; none may start mid-restore
        string? saved = null;
        if (File.Exists(live))
        {
            saved = Path.Combine(dataDir, "restore-saved", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(saved);
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(live + suffix)) File.Move(live + suffix, Path.Combine(saved, "ro3emu.db" + suffix));
        }
        var incoming = live + ".restoring";
        File.Copy(backup, incoming, overwrite: true);
        File.Move(incoming, live);
        return saved;
    }

    /// <summary>Throws unless the file passes quick_check and holds the realm's core tables.</summary>
    public static void Validate(string path)
    {
        using var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        db.Open();
        if (DatabaseBackup.QuickCheck(db) is { } problem) throw new InvalidDataException($"{path} fails its integrity check: {problem}");
        using var tables = db.CreateCommand();
        tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        var present = new HashSet<string>(StringComparer.Ordinal);
        using (var reader = tables.ExecuteReader()) while (reader.Read()) present.Add(reader.GetString(0));
        var missing = CoreTables.Where(t => !present.Contains(t)).ToArray();
        if (missing.Length != 0) throw new InvalidDataException($"{path} is not a realm database (missing {string.Join(", ", missing)})");
    }
}
