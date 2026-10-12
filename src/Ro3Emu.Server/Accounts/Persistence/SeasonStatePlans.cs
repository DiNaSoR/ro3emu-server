using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

internal sealed class SeasonStateSchemaPlan : DbPlan
{
    internal override int Bytes => 128;
    internal override string Label => "season/schema";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx,
            "CREATE TABLE IF NOT EXISTS server_state (key TEXT PRIMARY KEY, value TEXT NOT NULL)");
        int changed = cmd.ExecuteNonQuery();
        SchemaSql.Execute(db, tx, """
            INSERT OR IGNORE INTO owner_revision(kind,id,part,revision)
                SELECT 'server_state',key,'state',0 FROM server_state
            """);
        return changed;
    }
}

internal sealed class SeasonStateReadPlan(string key) : DbReadPlan
{
    internal override int Bytes => 128 + key.Length * 2;
    internal override string Label => "season/read";
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, "SELECT value FROM server_state WHERE key=$key");
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }
}

internal sealed class SeasonStateWritePlan(string key, string value) : DbPlan
{
    internal override int Bytes => 128 + (key.Length + value.Length) * 2;
    internal override string Label => "season/write";
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites => [RevisionStore.Entity("server_state", key)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx,
            "INSERT INTO server_state(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=$value");
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        return cmd.ExecuteNonQuery();
    }
}
