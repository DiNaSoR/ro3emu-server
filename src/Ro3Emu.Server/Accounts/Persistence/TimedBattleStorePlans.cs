using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

internal sealed class TimedBattleSchemaPlan : DbPlan
{
    internal override int Bytes => 256;
    internal override string Label => "timed-battle/schema";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        SchemaSql.Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS timed_battle_obligation (
                source_key TEXT PRIMARY KEY, match_id INTEGER NOT NULL, entry INTEGER NOT NULL,
                role_id INTEGER NOT NULL, grants BLOB NOT NULL, costs BLOB NOT NULL,
                next_state BLOB NOT NULL, result BLOB NOT NULL,
                delivered INTEGER NOT NULL DEFAULT 0 CHECK(delivered IN (0,1)));
            CREATE INDEX IF NOT EXISTS timed_battle_pending_role
                ON timed_battle_obligation(role_id,delivered);
            INSERT OR IGNORE INTO owner_revision(kind,id,part,revision)
                SELECT 'timed_battle_obligation',source_key,'state',0 FROM timed_battle_obligation;
            """);
        return 1;
    }
}

internal sealed record TimedBattleObligationRow(string SourceKey, long MatchId, int Entry, long RoleId,
    byte[] Grants, byte[] Costs, byte[] NextState, byte[] Result);

internal sealed class TimedBattleObligationReadPlan(long role) : DbReadPlan
{
    internal override int Bytes => 256;
    internal override string Label => "timed-battle/read-pending";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, """
            SELECT source_key,match_id,entry,role_id,grants,costs,next_state,result
            FROM timed_battle_obligation WHERE role_id=$role AND delivered=0 ORDER BY match_id,source_key
            """);
        cmd.Parameters.AddWithValue("$role", role);
        using var reader = cmd.ExecuteReader();
        var rows = new List<TimedBattleObligationRow>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2),
            reader.GetInt64(3), (byte[])reader.GetValue(4), (byte[])reader.GetValue(5),
            (byte[])reader.GetValue(6), (byte[])reader.GetValue(7)));
        return rows;
    }
}

internal sealed class TimedBattleObligationCreatePlan : DbPlan
{
    private readonly TimedBattleObligationRow[] rows;
    internal TimedBattleObligationCreatePlan(IEnumerable<TimedBattleObligationRow> rows)
    {
        this.rows = rows.Select(row => row with
        {
            Grants = (byte[])row.Grants.Clone(), Costs = (byte[])row.Costs.Clone(),
            NextState = (byte[])row.NextState.Clone(), Result = (byte[])row.Result.Clone(),
        }).ToArray();
        if (this.rows.Select(row => row.SourceKey).Distinct(StringComparer.Ordinal).Count() != this.rows.Length)
            throw new ArgumentException("Duplicate timed battle obligation source.");
    }
    internal override int Bytes => 256 + rows.Sum(row => row.SourceKey.Length * 2 + row.Grants.Length
        + row.Costs.Length + row.NextState.Length + row.Result.Length);
    internal override string Label => "timed-battle/create-obligations";
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites =>
        rows.Select(row => RevisionStore.Entity("timed_battle_obligation", row.SourceKey)).ToArray();
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        foreach (var row in rows)
        {
            using var cmd = DbSql.Command(db, tx, """
                INSERT INTO timed_battle_obligation(source_key,match_id,entry,role_id,grants,costs,next_state,result)
                VALUES($source,$match,$entry,$role,$grants,$costs,$next,$result)
                """);
            cmd.Parameters.AddWithValue("$source", row.SourceKey);
            cmd.Parameters.AddWithValue("$match", row.MatchId);
            cmd.Parameters.AddWithValue("$entry", row.Entry);
            cmd.Parameters.AddWithValue("$role", row.RoleId);
            cmd.Parameters.AddWithValue("$grants", row.Grants);
            cmd.Parameters.AddWithValue("$costs", row.Costs);
            cmd.Parameters.AddWithValue("$next", row.NextState);
            cmd.Parameters.AddWithValue("$result", row.Result);
            if (cmd.ExecuteNonQuery() != 1) throw new OwnerRevisionConflictException();
        }
        return rows.Length;
    }
}

internal sealed class TimedBattleObligationDeliverPlan(string source, long role) : DbPlan
{
    internal override int Bytes => 128 + source.Length * 2;
    internal override string Label => "timed-battle/deliver-obligation";
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites =>
        [RevisionStore.Entity("timed_battle_obligation", source)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, """
            UPDATE timed_battle_obligation SET delivered=1
            WHERE source_key=$source AND role_id=$role AND delivered=0
            """);
        cmd.Parameters.AddWithValue("$source", source);
        cmd.Parameters.AddWithValue("$role", role);
        if (cmd.ExecuteNonQuery() != 1) throw new OwnerRevisionConflictException();
        return 1;
    }
}
