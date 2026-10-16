using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>
/// 32228 guild journal: append GuildEventData rows (protobuf bytes) with the next per-guild index, keeping the newest
/// <c>cap</c> (GuildSetting GuildEventDateMaxNum). Enlisted in the transaction of the change it records; a guild created
/// in the same transaction (id 0) is found by its unique name.
/// </summary>
internal sealed class GuildEventAppendPlan(long guild, string name, byte[][] events, int cap) : DbPlan
{
    internal override int Bytes => 128 + name.Length * 2 + events.Sum(e => e.Length + 16);
    internal override string Label => "guild/event-append";

    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        long id = guild;
        if (id == 0)
        {
            using var find = DbSql.Command(db, tx, "SELECT id FROM guild WHERE name=$name");
            find.Parameters.AddWithValue("$name", name);
            id = find.ExecuteScalar() is long found ? found : throw new InvalidOperationException("Journal guild vanished.");
        }
        long last;
        using (var max = DbSql.Command(db, tx, "SELECT COALESCE(MAX(idx),0) FROM guild_event WHERE guild_id=$id"))
        {
            max.Parameters.AddWithValue("$id", id);
            last = (long)max.ExecuteScalar()!;
        }
        foreach (byte[] data in events)
        {
            using var insert = DbSql.Command(db, tx, "INSERT INTO guild_event(guild_id,idx,data) VALUES($id,$idx,$data)");
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$idx", ++last);
            insert.Parameters.AddWithValue("$data", data);
            insert.ExecuteNonQuery();
        }
        using (var trim = DbSql.Command(db, tx, "DELETE FROM guild_event WHERE guild_id=$id AND idx<=$floor"))
        {
            trim.Parameters.AddWithValue("$id", id);
            trim.Parameters.AddWithValue("$floor", last - Math.Max(1, cap));
            trim.ExecuteNonQuery();
        }
        return last;
    }
}

/// <summary>The whole journal of one guild, newest first (at most GuildEventDateMaxNum rows).</summary>
internal sealed class GuildEventReadPlan(long guild) : DbReadPlan
{
    internal override int Bytes => 128;
    internal override string Label => "guild/event-read";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, "SELECT idx,data FROM guild_event WHERE guild_id=$id ORDER BY idx DESC");
        cmd.Parameters.AddWithValue("$id", guild);
        using var rows = cmd.ExecuteReader();
        var result = new List<(long Index, byte[] Data)>();
        while (rows.Read()) result.Add((rows.GetInt64(0), (byte[])rows.GetValue(1)));
        return result;
    }
}
