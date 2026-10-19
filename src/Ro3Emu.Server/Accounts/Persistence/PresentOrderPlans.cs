using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>Every role's Fashion gift blobs (present_sent, present_received) for the realm's gift index at startup.</summary>
internal sealed class PresentOrderReadPlan : DbReadPlan
{
    internal override int Bytes => 128;
    internal override string Label => "shop/present-orders";

    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var result = new List<(long Role, byte[]? Sent, byte[]? Received)>();
        using var cmd = DbSql.Command(db, tx,
            "SELECT id,present_sent,present_received FROM role WHERE present_sent IS NOT NULL OR present_received IS NOT NULL");
        using var row = cmd.ExecuteReader();
        while (row.Read())
            result.Add((row.GetInt64(0), row.IsDBNull(1) ? null : (byte[])row[1], row.IsDBNull(2) ? null : (byte[])row[2]));
        return result;
    }
}
