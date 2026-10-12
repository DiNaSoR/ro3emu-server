using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

internal sealed class RewardSourceReadPlan(string? source = null) : DbReadPlan
{
    internal override int Bytes => 1024;
    internal override string Label => "reward/read-source";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var command = DbSql.Command(db, tx, source == null
            ? "SELECT realm_id FROM economy_meta WHERE singleton=1"
            : "SELECT 1 FROM operation_source WHERE source_key=$source");
        if (source != null) command.Parameters.AddWithValue("$source", source);
        var result = command.ExecuteScalar();
        return source == null ? result as string ?? throw new InvalidDataException("Missing reward realm") : result != null;
    }
}
