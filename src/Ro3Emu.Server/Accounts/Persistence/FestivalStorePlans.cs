using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

internal sealed class FestivalSchemaPlan : DbPlan
{
    internal override int Bytes => 128;
    internal override string Label => "festival/schema";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, """
            CREATE TABLE IF NOT EXISTS festival_round (cycle INTEGER PRIMARY KEY, state BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS festival_reward (
                cycle INTEGER NOT NULL, role_id INTEGER NOT NULL, source INTEGER NOT NULL,
                award INTEGER NOT NULL, claimed INTEGER NOT NULL DEFAULT 0, grants BLOB,
                PRIMARY KEY(cycle,role_id,source));
            """);
        cmd.ExecuteNonQuery();
        // Legacy rows have no resolved result and remain visible as recovery debt to the economy decoder.
        SchemaSql.AddColumn(db, tx, "festival_reward", "grants", "BLOB");
        SchemaSql.Execute(db, tx, """
            INSERT OR IGNORE INTO owner_revision(kind,id,part,revision)
                SELECT 'festival_round',cycle,'state',0 FROM festival_round
            """);
        return 1;
    }
}

internal enum FestivalReadKind { LatestRound, Round, PendingRewards }

internal sealed record FestivalPendingReward(long Cycle, int Source, long Award, byte[] Grants);

internal sealed class FestivalReadPlan(FestivalReadKind kind, long id = 0) : DbReadPlan
{
    internal override int Bytes => 128;
    internal override string Label => "festival/read-" + kind;
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        switch (kind)
        {
            case FestivalReadKind.LatestRound:
            case FestivalReadKind.Round:
                using (var cmd = DbSql.Command(db, tx, kind == FestivalReadKind.LatestRound
                    ? "SELECT state FROM festival_round ORDER BY cycle DESC LIMIT 1"
                    : "SELECT state FROM festival_round WHERE cycle=$cycle"))
                {
                    if (kind == FestivalReadKind.Round) cmd.Parameters.AddWithValue("$cycle", id);
                    return cmd.ExecuteScalar() is byte[] bytes ? (byte[])bytes.Clone() : null;
                }
            case FestivalReadKind.PendingRewards:
                using (var cmd = DbSql.Command(db, tx,
                    "SELECT cycle,source,award,grants FROM festival_reward WHERE role_id=$role AND claimed=0 AND grants IS NOT NULL"))
                {
                    cmd.Parameters.AddWithValue("$role", id);
                    using var reader = cmd.ExecuteReader();
                    var rows = new List<FestivalPendingReward>();
                    while (reader.Read()) rows.Add(new FestivalPendingReward(reader.GetInt64(0), reader.GetInt32(1),
                        reader.GetInt64(2), (byte[])reader.GetValue(3)));
                    return rows;
                }
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }
}

internal sealed class FestivalRoundWritePlan(long cycle, byte[] state) : DbPlan
{
    private readonly byte[] _state = (byte[])state.Clone();
    internal override int Bytes => 128 + _state.Length;
    internal override string Label => "festival/round";
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites => [RevisionStore.Entity("festival_round", cycle)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx,
            "INSERT INTO festival_round(cycle,state) VALUES($cycle,$state) ON CONFLICT(cycle) DO UPDATE SET state=excluded.state");
        cmd.Parameters.AddWithValue("$cycle", cycle);
        cmd.Parameters.AddWithValue("$state", _state);
        return cmd.ExecuteNonQuery();
    }
}

internal sealed class FestivalRewardInsertPlan(long cycle, long role, int source, long award, byte[] grants) : DbPlan
{
    private readonly byte[] _grants = (byte[])grants.Clone();
    internal override int Bytes => 128 + _grants.Length;
    internal override string Label => "festival/reward";
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => [RevisionStore.Role(role, RevisionStore.FestivalReward)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx,
            "INSERT OR IGNORE INTO festival_reward(cycle,role_id,source,award,grants) VALUES($cycle,$role,$source,$award,$grants)");
        cmd.Parameters.AddWithValue("$cycle", cycle);
        cmd.Parameters.AddWithValue("$role", role);
        cmd.Parameters.AddWithValue("$source", source);
        cmd.Parameters.AddWithValue("$award", award);
        cmd.Parameters.AddWithValue("$grants", _grants);
        return cmd.ExecuteNonQuery();
    }
}
