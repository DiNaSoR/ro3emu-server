using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>A caravan member left the guild, or the row moved on, while the save waited.</summary>
internal sealed class CaravanMembershipChangedException : Exception;

internal sealed class CaravanSchemaPlan : DbPlan
{
    internal override int Bytes => 128;
    internal override string Label => "guild-caravan/schema";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, """
            CREATE TABLE IF NOT EXISTS guild_caravan_member (role_id INTEGER PRIMARY KEY, state BLOB NOT NULL, revision INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS guild_caravan_version (guild_id INTEGER PRIMARY KEY, day INTEGER NOT NULL, revision INTEGER NOT NULL);
            """);
        cmd.ExecuteNonQuery();
        SchemaSql.Execute(db, tx, """
            INSERT OR IGNORE INTO owner_revision(kind,id,part,revision) SELECT 'guild_caravan_member',role_id,'state',0 FROM guild_caravan_member;
            INSERT OR IGNORE INTO owner_revision(kind,id,part,revision) SELECT 'guild_caravan_version',guild_id,'state',0 FROM guild_caravan_version;
            """);
        return 1;
    }
}

internal sealed class CaravanMemberReadPlan<T>(long roleId) : DbReadPlan where T : class
{
    internal override int Bytes => 128;
    internal override string Label => "guild-caravan/read-Member";
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, "SELECT state FROM guild_caravan_member WHERE role_id=$role");
        cmd.Parameters.AddWithValue("$role", roleId);
        return cmd.ExecuteScalar() is byte[] blob ? JsonSerializer.Deserialize<T>(blob) : null;
    }
}

internal sealed class CaravanVersionReadPlan(long guildId, long day) : DbReadPlan
{
    internal override int Bytes => 128;
    internal override string Label => "guild-caravan/read-Version";
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, "SELECT revision FROM guild_caravan_version WHERE guild_id=$guild AND day=$day");
        cmd.Parameters.AddWithValue("$guild", guildId);
        cmd.Parameters.AddWithValue("$day", day);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }
}

internal sealed class CaravanRoleIdsReadPlan : DbReadPlan
{
    internal override int Bytes => 128;
    internal override string Label => "guild-caravan/read-RoleIds";
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, "SELECT role_id FROM guild_caravan_member");
        using var reader = cmd.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids;
    }
}

internal sealed class CaravanMemberWritePlan(long roleId, long guildId, bool requireMembership,
    long expected, long next, byte[] state) : DbPlan
{
    private readonly byte[] _state = (byte[])state.Clone();
    internal override int Bytes => 128 + _state.Length;
    internal override string Label => "guild-caravan/member";
    internal override IReadOnlyList<long> RolePins => [roleId];
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites => [RevisionStore.Entity("guild_caravan_member", roleId)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        if (requireMembership)
        {
            using var member = DbSql.Command(db, tx, "SELECT guild_id FROM guild_member WHERE role_id=$role");
            member.Parameters.AddWithValue("$role", roleId);
            if (Convert.ToInt64(member.ExecuteScalar() ?? 0L) != guildId) throw new CaravanMembershipChangedException();
        }
        using var cmd = DbSql.Command(db, tx, "INSERT INTO guild_caravan_member(role_id,state,revision) VALUES($role,$state,$next) " +
            "ON CONFLICT(role_id) DO UPDATE SET state=excluded.state,revision=excluded.revision " +
            "WHERE guild_caravan_member.revision=$expected");
        cmd.Parameters.AddWithValue("$role", roleId);
        cmd.Parameters.AddWithValue("$state", _state);
        cmd.Parameters.AddWithValue("$next", next);
        cmd.Parameters.AddWithValue("$expected", expected);
        if (cmd.ExecuteNonQuery() != 1) throw new CaravanMembershipChangedException();
        return next;
    }
}

internal sealed class CaravanVersionPlan(long guildId, long day) : DbPlan
{
    internal override int Bytes => 128;
    internal override string Label => "guild-caravan/version";
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites => [RevisionStore.Entity("guild_caravan_version", guildId)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, """
            INSERT INTO guild_caravan_version(guild_id,day,revision) VALUES($guild,$day,1)
            ON CONFLICT(guild_id) DO UPDATE SET revision=CASE WHEN day=$day THEN revision+1 ELSE 1 END,day=$day
            """);
        cmd.Parameters.AddWithValue("$guild", guildId);
        cmd.Parameters.AddWithValue("$day", day);
        return cmd.ExecuteNonQuery();
    }
}
