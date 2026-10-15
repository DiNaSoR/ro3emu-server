using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>Resolve a committed natural mail delivery even after its letter leaves the mailbox.</summary>
internal sealed class MailDeliveryReceiptReadPlan(string sourceKey, long role, string deliveryKey,
    string semanticHash) : DbReadPlan
{
    internal override int Bytes => 256 + sourceKey.Length * 2 + deliveryKey.Length * 2;
    internal override string Label => "mail/delivery-replay";

    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var command = DbSql.Command(db, tx, """
            SELECT s.realm_id,s.operation_id,r.realm_id,r.actor_account,r.actor_role,r.command_kind,
                   r.request_fingerprint,r.intent_hash,r.intent_payload,r.source_key,r.outcome,
                   r.child_receipts,r.leg_count
            FROM operation_source s LEFT JOIN operation_receipt r
              ON r.operation_id=s.operation_id AND r.realm_id=s.realm_id
            WHERE s.realm_id=(SELECT realm_id FROM economy_meta WHERE singleton=1)
              AND s.source_key=$source
            """);
        command.Parameters.AddWithValue("$source", sourceKey);
        using var row = command.ExecuteReader();
        if (!row.Read()) return null;
        if (row.IsDBNull(2)) throw new InvalidDataException("Mail delivery source lost its receipt.");
        string realm = row.GetString(0);
        string operation = row.GetString(1);
        byte[] payload = (byte[])row.GetValue(8);
        byte[] outcome = (byte[])row.GetValue(10);
        byte[] children = (byte[])row.GetValue(11);
        int legs = row.GetInt32(12);
        if (payload.Length > 16 * 1024 * 1024 || outcome.Length > 64 * 1024 * 1024 || legs < 0 ||
            row.GetString(2) != realm || row.GetString(3) != "" || row.GetInt64(4) != 0 ||
            row.GetString(5) != "mail/deliver" || row.GetString(9) != sourceKey ||
            !outcome.AsSpan().SequenceEqual(children))
            throw new InvalidDataException("Committed mail delivery receipt is invalid.");
        string hash = Convert.ToHexString(SHA256.HashData(payload));
        if (row.GetString(6) != hash || row.GetString(7) != hash)
            throw new InvalidDataException("Committed mail delivery payload hash differs from receipt.");
        EscrowEconomyReceiptCodec.Decode(children);
        row.Close();

        using (var meta = DbSql.Command(db, tx, "SELECT realm_id FROM economy_meta WHERE singleton=1"))
            if (meta.ExecuteScalar() is not string actual || actual != realm)
                throw new InvalidDataException("Committed mail delivery has a foreign realm.");
        FamilyEconomyTransactionPlan.ValidateLegCount(db, tx, operation, legs);

        using var intent = JsonDocument.Parse(payload);
        var root = intent.RootElement;
        if (root.GetProperty("OperationId").GetString() != operation ||
            root.GetProperty("SourceKey").GetString() != sourceKey ||
            root.GetProperty("CommandKind").GetString() != "mail/deliver" ||
            root.GetProperty("ActorAccount").GetString() != "" ||
            root.GetProperty("ActorRoleId").GetInt64() != 0)
            throw new InvalidDataException("Committed mail delivery intent differs from source claim.");

        foreach (var evidence in root.GetProperty("Evidence").EnumerateArray())
        {
            if (!evidence.TryGetProperty("Json", out var stored) || stored.ValueKind != JsonValueKind.String)
                continue;
            string json = stored.GetString()!;
            using var detail = JsonDocument.Parse(json);
            if (!detail.RootElement.TryGetProperty("DeliverySemanticHash", out var storedHash)) continue;
            byte[] evidenceBytes = Encoding.UTF8.GetBytes(json);
            if (evidence.GetProperty("Bytes").GetInt32() != evidenceBytes.Length ||
                evidence.GetProperty("Hash").GetString() != Convert.ToHexString(SHA256.HashData(evidenceBytes)))
                throw new InvalidDataException("Committed mail delivery evidence hash differs from receipt.");
            var claim = detail.RootElement;
            if (claim.GetProperty("Role").GetInt64() != role ||
                claim.GetProperty("DeliveryKey").GetString() != deliveryKey ||
                storedHash.GetString() != semanticHash)
                throw new EconomyOperationConflictException("Mail delivery source was already committed with another reward.");
            string id = claim.GetProperty("LetterId").GetString() ?? "";
            if (id.Length is < 1 or > 32 || !id.All(char.IsAsciiDigit))
                throw new InvalidDataException("Committed mail delivery ID is invalid.");
            return id;
        }
        throw new InvalidDataException("Committed mail delivery has no replay evidence.");
    }
}
