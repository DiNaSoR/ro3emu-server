using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

internal sealed class CampfireSchemaPlan : DbPlan
{
    internal override int Bytes => 128;
    internal override string Label => "guild-campfire/schema";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx,
            "CREATE TABLE IF NOT EXISTS guild_campfire (guild_id INTEGER PRIMARY KEY, state BLOB NOT NULL)");
        cmd.ExecuteNonQuery();
        SchemaSql.Execute(db, tx, "INSERT OR IGNORE INTO owner_revision(kind,id,part,revision) SELECT 'guild_campfire',guild_id,'state',0 FROM guild_campfire");
        return 1;
    }
}

internal sealed class CampfireReadPlan(long guild) : DbReadPlan
{
    internal override int Bytes => 128;
    internal override string Label => "guild-campfire/read";
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, "SELECT state FROM guild_campfire WHERE guild_id=$guild");
        cmd.Parameters.AddWithValue("$guild", guild);
        return cmd.ExecuteScalar() is byte[] value ? (byte[])value.Clone() : null;
    }
}

internal sealed class CampfireWritePlan(long guild, byte[] state) : DbPlan
{
    private readonly byte[] _state = (byte[])state.Clone();
    internal override int Bytes => 128 + _state.Length;
    internal override string Label => "guild-campfire/save";
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites => [RevisionStore.Entity("guild_campfire", guild)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx,
            "INSERT INTO guild_campfire(guild_id,state) VALUES($guild,$state) ON CONFLICT(guild_id) DO UPDATE SET state=excluded.state");
        cmd.Parameters.AddWithValue("$guild", guild);
        cmd.Parameters.AddWithValue("$state", _state);
        return cmd.ExecuteNonQuery();
    }
}
