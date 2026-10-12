using System.Text.Json;
using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Accounts.Persistence;

internal sealed class GuildChallengeSchemaPlan : DbPlan
{
    internal override int Bytes => 128;
    internal override string Label => "guild-challenge/schema";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, """
            CREATE TABLE IF NOT EXISTS guild_challenge (guild_id INTEGER PRIMARY KEY, state BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS guild_challenge_clear (
                id TEXT PRIMARY KEY, guild_id INTEGER NOT NULL, season INTEGER NOT NULL,
                cycle INTEGER NOT NULL, difficulty INTEGER NOT NULL, pending INTEGER NOT NULL, state BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS challenge_clear_occurrence ON guild_challenge_clear(guild_id,season,cycle,difficulty);
            CREATE INDEX IF NOT EXISTS challenge_clear_pending ON guild_challenge_clear(pending) WHERE pending=1;
            """);
        cmd.ExecuteNonQuery();
        SchemaSql.Execute(db, tx, """
            INSERT OR IGNORE INTO owner_revision(kind,id,part,revision)
                SELECT 'guild_challenge',guild_id,'state',0 FROM guild_challenge
            """);
        SchemaSql.Execute(db, tx, """
            INSERT OR IGNORE INTO owner_revision(kind,id,part,revision)
                SELECT 'guild_challenge_clear',id,'state',0 FROM guild_challenge_clear
            """);
        return 1;
    }
}

internal enum GuildChallengeReadKind { State, Clear, Pending, Occurrence, AllClears }

internal sealed class GuildChallengeReadPlan(GuildChallengeReadKind kind, long guild = 0, int season = 0,
    long cycle = 0, int? difficulty = null, string id = "") : DbReadPlan
{
    internal override int Bytes => 128 + id.Length * 2;
    internal override string Label => "guild-challenge/read-" + kind;
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        if (kind == GuildChallengeReadKind.State)
        {
            using var state = DbSql.Command(db, tx, "SELECT state FROM guild_challenge WHERE guild_id=$id");
            state.Parameters.AddWithValue("$id", guild);
            return state.ExecuteScalar() is byte[] bytes
                ? JsonSerializer.Deserialize<GuildChallengeService.GuildState>(bytes)! : new GuildChallengeService.GuildState();
        }
        string where = kind switch
        {
            GuildChallengeReadKind.Clear => "WHERE id=$id",
            GuildChallengeReadKind.Pending => "WHERE pending=1",
            GuildChallengeReadKind.Occurrence => difficulty.HasValue
                ? "WHERE guild_id=$guild AND season=$season AND cycle=$cycle AND difficulty=$difficulty"
                : "WHERE guild_id=$guild AND season=$season AND cycle=$cycle",
            GuildChallengeReadKind.AllClears => "",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        using var cmd = DbSql.Command(db, tx, $"SELECT state FROM guild_challenge_clear {where} ORDER BY rowid");
        if (kind == GuildChallengeReadKind.Clear) cmd.Parameters.AddWithValue("$id", id);
        if (kind == GuildChallengeReadKind.Occurrence)
        {
            cmd.Parameters.AddWithValue("$guild", guild);
            cmd.Parameters.AddWithValue("$season", season);
            cmd.Parameters.AddWithValue("$cycle", cycle);
            if (difficulty.HasValue) cmd.Parameters.AddWithValue("$difficulty", difficulty.Value);
        }
        using var reader = cmd.ExecuteReader();
        var rows = new List<GuildChallengeService.Clear>();
        while (reader.Read()) rows.Add(JsonSerializer.Deserialize<GuildChallengeService.Clear>((byte[])reader[0])!);
        return rows;
    }
}

internal sealed class GuildChallengeStatePlan(long guild, GuildChallengeService.GuildState state) : DbPlan
{
    private readonly byte[] _state = JsonSerializer.SerializeToUtf8Bytes(state);
    internal override int Bytes => 128 + _state.Length;
    internal override string Label => "guild-challenge/state";
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites => [RevisionStore.Entity("guild_challenge", guild)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx,
            "INSERT INTO guild_challenge(guild_id,state) VALUES($id,$state) ON CONFLICT(guild_id) DO UPDATE SET state=excluded.state");
        cmd.Parameters.AddWithValue("$id", guild);
        cmd.Parameters.AddWithValue("$state", _state);
        return cmd.ExecuteNonQuery();
    }
}
