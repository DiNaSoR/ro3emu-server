using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>
/// Guild Hunting Challenge (GuildHuntingService): one row per weekly event, keyed by its candidate start (unix s). data is
/// the original's DBGveHuntingActivityData (MG_Activity.proto:127-135, groups, results, server top, settled flag, phase
/// ends); runtime is this server's JSON bookkeeping beside it (live scores, participants, the mail flag). Neither holds an
/// asset: rewards leave as ordinary template mail with their own ledger entries.
/// </summary>
internal sealed class GuildHuntingSchemaPlan : DbPlan
{
    internal override int Bytes => 128;
    internal override string Label => "guild-hunting/schema";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx,
            "CREATE TABLE IF NOT EXISTS guild_hunting (cycle_start INTEGER PRIMARY KEY, data BLOB NOT NULL, runtime BLOB NOT NULL)");
        return cmd.ExecuteNonQuery();
    }
}

/// <summary>The newest events, newest first (the current one and, for 43516, the last settled one before it).</summary>
internal sealed class GuildHuntingReadPlan(int limit) : DbReadPlan
{
    internal override int Bytes => 128;
    internal override string Label => "guild-hunting/read";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx,
            "SELECT cycle_start,data,runtime FROM guild_hunting ORDER BY cycle_start DESC LIMIT $limit");
        cmd.Parameters.AddWithValue("$limit", Math.Max(1, limit));
        using var rows = cmd.ExecuteReader();
        var result = new List<(long Cycle, byte[] Data, byte[] Runtime)>();
        while (rows.Read()) result.Add((rows.GetInt64(0), (byte[])rows.GetValue(1), (byte[])rows.GetValue(2)));
        return result;
    }
}

internal sealed class GuildHuntingSavePlan(long cycle, byte[] data, byte[] runtime) : DbPlan
{
    private readonly byte[] _data = (byte[])data.Clone(), _runtime = (byte[])runtime.Clone();
    internal override int Bytes => 128 + _data.Length + _runtime.Length;
    internal override string Label => "guild-hunting/save";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, """
            INSERT INTO guild_hunting(cycle_start,data,runtime) VALUES($cycle,$data,$runtime)
            ON CONFLICT(cycle_start) DO UPDATE SET data=excluded.data, runtime=excluded.runtime
            """);
        cmd.Parameters.AddWithValue("$cycle", cycle);
        cmd.Parameters.AddWithValue("$data", _data);
        cmd.Parameters.AddWithValue("$runtime", _runtime);
        return cmd.ExecuteNonQuery();
    }
}
