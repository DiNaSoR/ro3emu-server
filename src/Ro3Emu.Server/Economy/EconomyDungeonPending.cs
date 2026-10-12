using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Economy;

/// <summary>Materialized dungeon entitlements are owned Pending holdings until the delivery commit.</summary>
internal static class EconomyDungeonPending
{
    private static readonly JsonSerializerOptions StrictJson = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    internal static string EarnedSource(string deliverySource) => deliverySource + "/earned";

    internal static DungeonRewardObligation Decode(byte[] state) =>
        JsonSerializer.Deserialize<DungeonRewardObligation>(state, StrictJson)
        ?? throw new InvalidDataException("Frozen dungeon source has no state.");

    internal static IReadOnlyList<EconomyHolding> Holdings(DungeonRewardObligation obligation)
    {
        if (string.IsNullOrWhiteSpace(obligation.Key) || obligation.RoleId <= 0)
            throw new InvalidDataException("Invalid frozen dungeon source identity.");
        var owner = new EconomyOwner(EconomyOwnerKind.Role,
            obligation.RoleId.ToString(CultureInfo.InvariantCulture));
        var result = new List<EconomyHolding>();
        foreach (var group in obligation.Goods.Where(g => g.Entry is not (100 or 101))
            .GroupBy(g => (g.Kind, g.Entry, g.Bind)).OrderBy(g => (int)g.Key.Kind)
            .ThenBy(g => g.Key.Entry).ThenBy(g => g.Key.Bind))
        {
            var (kind, entry, bind) = group.Key;
            if (kind is not (EconomyAssetKind.Item or EconomyAssetKind.Currency) || entry <= 0 ||
                bind < 0 || kind == EconomyAssetKind.Currency && bind != 0 ||
                group.Any(g => g.Count <= 0))
                throw new InvalidDataException("Invalid frozen dungeon denomination or quantity.");
            long count = group.Aggregate(0L, (sum, g) => checked(sum + g.Count));
            string key = $"dungeon/{obligation.Key}/entry/{entry}/bind/{bind}";
            string fingerprint = kind == EconomyAssetKind.Item
                ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"DungeonPending|{entry}|{bind}")))
                : "";
            result.Add(new(new(kind, entry), new(owner, EconomyCompartment.Pending, key), count, fingerprint));
        }
        return result;
    }

    internal static IReadOnlyList<EconomyHolding> Read(SqliteConnection db, SqliteTransaction tx,
        long? roleId = null)
    {
        using (var exists = db.CreateCommand())
        {
            exists.Transaction = tx;
            exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='dungeon_reward_obligation'";
            if (exists.ExecuteScalar() is null) return [];
        }
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = roleId is null
            ? "SELECT source_key,role_id,state FROM dungeon_reward_obligation WHERE activated=1 AND completed=0 ORDER BY source_key"
            : "SELECT source_key,role_id,state FROM dungeon_reward_obligation WHERE activated=1 AND completed=0 AND role_id=$role ORDER BY source_key";
        if (roleId is not null) command.Parameters.AddWithValue("$role", roleId.Value);
        using var row = command.ExecuteReader();
        var result = new List<EconomyHolding>();
        while (row.Read())
        {
            var obligation = Decode((byte[])row.GetValue(2));
            if (obligation.Key != row.GetString(0) || obligation.RoleId != row.GetInt64(1))
                throw new InvalidDataException("Frozen dungeon source row differs from its state.");
            if (obligation.RequiresKill && string.IsNullOrWhiteSpace(obligation.KillSourceKey))
                throw new InvalidDataException("Activated dungeon recipient has no terminal kill source.");
            result.AddRange(Holdings(obligation));
        }
        return result;
    }
}
