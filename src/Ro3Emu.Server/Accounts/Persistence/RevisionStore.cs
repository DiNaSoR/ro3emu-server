using System.Collections.Frozen;
using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

internal readonly record struct RoleRevisionKey(long RoleId, string Part);
internal readonly record struct OwnerRevisionKey(string Kind, string Id, string Part);
internal readonly record struct OwnerRevisionValue(OwnerRevisionKey Key, long Revision, bool Exists);

/// <summary>A detached, durable snapshot captured before a role mutation is staged.</summary>
public sealed record RoleRevisionVector(long RoleId, long Epoch, bool Deleted, IReadOnlyDictionary<string, long> Parts)
{
    public long Revision(string part) => Parts.TryGetValue(part, out long revision) ? revision
        : throw new InvalidOperationException($"Missing revision for role {RoleId}, part {part}.");
    internal RoleRevisionVector Advance(string part)
        => WithRevision(part, checked(Revision(part) + 1));
    internal RoleRevisionVector WithRevision(string part, long revision)
    {
        // Stage G: binding a source part the vector already holds at that revision copies nothing; a change copies
        // once into a plain dictionary (freezing ~80 parts per bound part was a top allocation of a login storm).
        if (Parts.TryGetValue(part, out long current) && current == revision) return this;
        var values = new Dictionary<string, long>(Parts, StringComparer.Ordinal) { [part] = revision };
        return this with { Parts = values };
    }
}

internal sealed class RoleRevisionReadPlan(long roleId) : DbReadPlan
{
    internal override int Bytes => 128;
    internal override string Label => "role/read-revisions";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var lifecycle = DbSql.Command(db, tx, "SELECT epoch,deleted FROM role_lifecycle WHERE role_id=$role");
        lifecycle.Parameters.AddWithValue("$role", roleId);
        using var row = lifecycle.ExecuteReader();
        if (!row.Read()) throw new OwnerRevisionConflictException();
        long epoch = row.GetInt64(0);
        bool deleted = row.GetInt64(1) != 0;
        row.Close();
        using var revisions = DbSql.Command(db, tx,
            "SELECT part,revision FROM owner_revision WHERE kind='role' AND id=$role");
        revisions.Parameters.AddWithValue("$role", roleId);
        using var parts = revisions.ExecuteReader();
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        while (parts.Read()) values.Add(parts.GetString(0), parts.GetInt64(1));
        return new RoleRevisionVector(roleId, epoch, deleted, values.ToFrozenDictionary(StringComparer.Ordinal));
    }
}

internal sealed class OwnerRevisionReadPlan(OwnerRevisionKey key) : DbReadPlan
{
    internal override int Bytes => 128;
    internal override string Label => "owner/read-revision";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx,
            "SELECT revision FROM owner_revision WHERE kind=$kind AND id=$id AND part=$part");
        cmd.Parameters.AddWithValue("$kind", key.Kind);
        cmd.Parameters.AddWithValue("$id", key.Id);
        cmd.Parameters.AddWithValue("$part", key.Part);
        return cmd.ExecuteScalar() is long revision
            ? new OwnerRevisionValue(key, revision, true) : new OwnerRevisionValue(key, 0, false);
    }
}

internal static class RevisionStore
{
    internal const string Kind = "role";
    internal const string Progress = "progress";
    internal const string Inventory = "inventory";
    internal const string Position = "public_position";
    internal const string Identity = "identity";
    internal const string FriendMetadata = "friend_metadata";
    internal const string FriendRelation = "friend_relation";
    internal const string AuctionOrder = "auction_order";
    internal const string AuctionBonus = "auction_bonus";
    internal const string GuildMembership = "guild_membership";
    internal const string FestivalReward = "festival_reward";
    internal const string ActivityDonation = "activity_donation";
    internal const string MvpCounter = "mvp_counter";
    internal static readonly string[] AdditionalParts = [Progress, Inventory, Position, Identity,
        FriendMetadata, FriendRelation, AuctionOrder, AuctionBonus, GuildMembership,
        FestivalReward, ActivityDonation, MvpCounter];

    internal static RoleRevisionKey Role(long id, string part) => new(id, part);
    internal static OwnerRevisionKey Entity(string kind, object id, string part = "state") =>
        new(kind, Convert.ToString(id, System.Globalization.CultureInfo.InvariantCulture)!, part);

    internal static IReadOnlyDictionary<OwnerRevisionKey, OwnerRevisionValue> Check(SqliteConnection db, SqliteTransaction tx, DbPlan plan)
    {
        var deltaRevisions = new Dictionary<OwnerRevisionKey, OwnerRevisionValue>();
        var keys = plan.RoleWrites.Distinct().ToArray();
        var pins = plan.RolePins.Concat(keys.Select(key => key.RoleId)).Distinct().ToArray();
        if (pins.Length != 0)
        {
            var expected = plan.ExpectedRevisions ?? throw new InvalidOperationException($"Uncaptured role lifecycle: {plan.Label}");
            foreach (long role in pins)
            {
                if (!expected.TryGetValue(role, out var vector) || vector.Deleted) throw new OwnerRevisionConflictException();
                using var lifecycle = DbSql.Command(db, tx,
                    "SELECT 1 FROM role_lifecycle WHERE role_id=$role AND epoch=$epoch AND deleted=0");
                lifecycle.Parameters.AddWithValue("$role", role);
                lifecycle.Parameters.AddWithValue("$epoch", vector.Epoch);
                if (lifecycle.ExecuteScalar() is null) throw new OwnerRevisionConflictException();
            }
            foreach (var key in keys)
            {
                using var revision = DbSql.Command(db, tx,
                    "SELECT 1 FROM owner_revision WHERE kind='role' AND id=$role AND part=$part AND revision=$revision");
                revision.Parameters.AddWithValue("$role", key.RoleId);
                revision.Parameters.AddWithValue("$part", key.Part);
                revision.Parameters.AddWithValue("$revision", expected[key.RoleId].Revision(key.Part));
                if (revision.ExecuteScalar() is null) throw new OwnerRevisionConflictException();
            }
        }
        var entities = plan.EntityWrites.Distinct().ToArray();
        if (entities.Length != 0)
        {
            var expectedEntities = plan.ExpectedEntities ?? throw new InvalidOperationException($"Uncaptured entity revisions: {plan.Label}");
            foreach (var key in entities)
            {
                if (!expectedEntities.TryGetValue(key, out var expected))
                    throw new InvalidOperationException($"Missing entity expectation for {key}.");
                using var revision = DbSql.Command(db, tx,
                    "SELECT revision FROM owner_revision WHERE kind=$kind AND id=$id AND part=$part");
                revision.Parameters.AddWithValue("$kind", key.Kind);
                revision.Parameters.AddWithValue("$id", key.Id);
                revision.Parameters.AddWithValue("$part", key.Part);
                var current = revision.ExecuteScalar();
                if (expected.Exists ? current is not long value || value != expected.Revision : current is not null)
                    throw new OwnerRevisionConflictException();
            }
        }
        foreach (var key in plan.EntityDeltaWrites.Distinct().Except(entities))
            deltaRevisions.Add(key, (OwnerRevisionValue)new OwnerRevisionReadPlan(key).Execute(db, tx));
        return deltaRevisions;
    }

    internal static void Bump(SqliteConnection db, SqliteTransaction tx, DbPlan plan, object? result,
        IReadOnlyDictionary<OwnerRevisionKey, OwnerRevisionValue> deltaRevisions)
    {
        foreach (var key in plan.MutatedRoleWrites(result).Distinct())
        {
            var vector = plan.ExpectedRevisions![key.RoleId];
            using var bump = DbSql.Command(db, tx, """
                UPDATE owner_revision SET revision=revision+1
                WHERE kind='role' AND id=$role AND part=$part AND revision=$revision
                """);
            bump.Parameters.AddWithValue("$role", key.RoleId);
            bump.Parameters.AddWithValue("$part", key.Part);
            bump.Parameters.AddWithValue("$revision", vector.Revision(key.Part));
            if (bump.ExecuteNonQuery() != 1) throw new OwnerRevisionConflictException();
        }
        foreach (var key in plan.MutatedEntityWrites(result)
            .Concat(plan.MutatedEntityDeltaWrites(result)).Distinct())
        {
            var expected = plan.ExpectedEntities is { } captured && captured.TryGetValue(key, out var staged)
                ? staged : deltaRevisions[key];
            using var bump = DbSql.Command(db, tx, expected.Exists ? """
                UPDATE owner_revision SET revision=revision+1
                WHERE kind=$kind AND id=$id AND part=$part AND revision=$revision
                """ : """
                INSERT INTO owner_revision(kind,id,part,revision) VALUES($kind,$id,$part,1)
                """);
            bump.Parameters.AddWithValue("$kind", key.Kind);
            bump.Parameters.AddWithValue("$id", key.Id);
            bump.Parameters.AddWithValue("$part", key.Part);
            if (expected.Exists) bump.Parameters.AddWithValue("$revision", expected.Revision);
            if (bump.ExecuteNonQuery() != 1) throw new OwnerRevisionConflictException();
        }
    }
}
