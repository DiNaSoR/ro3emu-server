using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Accounts.Persistence;

internal sealed record EconomyLedgerCommit(string OperationId, string ActorAccount, long ActorRoleId,
    string CommandKind, string RequestFingerprint, string IntentHash, byte[] IntentPayload,
    string SourceKey, long EffectiveUtcMs, byte[] Outcome, byte[] ChildReceipts,
    string BeforeRevisions, string AfterRevisions, IReadOnlyList<EconomyLeg> Legs);

/// <summary>Appends a complete semantic outcome inside the caller's existing writer transaction.</summary>
internal static class EconomyLedgerWrite
{
    internal static long Append(SqliteConnection db, SqliteTransaction tx, EconomyLedgerCommit commit)
    {
        string realm;
        using (var meta = DbSql.Command(db, tx, "SELECT realm_id FROM economy_meta WHERE singleton=1"))
            realm = meta.ExecuteScalar() as string ?? throw new InvalidOperationException("Missing economy realm identity.");
        long sequence;
        using (var advance = DbSql.Command(db, tx,
            "UPDATE economy_meta SET next_sequence=next_sequence+1 WHERE singleton=1 RETURNING next_sequence"))
            sequence = (long?)advance.ExecuteScalar() ?? throw new InvalidOperationException("Missing economy watermark.");
        long recorded = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using (var receipt = DbSql.Command(db, tx, """
            INSERT INTO operation_receipt(operation_id,realm_id,commit_seq,actor_account,actor_role,command_kind,
                request_fingerprint,intent_hash,intent_payload,source_key,recorded_utc_ms,effective_utc_ms,outcome,child_receipts,
                before_revisions,after_revisions,leg_count)
            VALUES($op,$realm,$seq,$account,$role,$kind,$fingerprint,$intent,$payload,$source,$recorded,$effective,$outcome,$children,
                $before,$after,$count)
            """))
        {
            receipt.Parameters.AddWithValue("$op", commit.OperationId);
            receipt.Parameters.AddWithValue("$realm", realm);
            receipt.Parameters.AddWithValue("$seq", sequence);
            receipt.Parameters.AddWithValue("$account", commit.ActorAccount);
            receipt.Parameters.AddWithValue("$role", commit.ActorRoleId);
            receipt.Parameters.AddWithValue("$kind", commit.CommandKind);
            receipt.Parameters.AddWithValue("$fingerprint", commit.RequestFingerprint);
            receipt.Parameters.AddWithValue("$intent", commit.IntentHash);
            receipt.Parameters.AddWithValue("$payload", commit.IntentPayload);
            receipt.Parameters.AddWithValue("$source", commit.SourceKey);
            receipt.Parameters.AddWithValue("$recorded", recorded);
            receipt.Parameters.AddWithValue("$effective", commit.EffectiveUtcMs);
            receipt.Parameters.AddWithValue("$outcome", commit.Outcome);
            receipt.Parameters.AddWithValue("$children", commit.ChildReceipts);
            receipt.Parameters.AddWithValue("$before", commit.BeforeRevisions);
            receipt.Parameters.AddWithValue("$after", commit.AfterRevisions);
            receipt.Parameters.AddWithValue("$count", commit.Legs.Count);
            receipt.ExecuteNonQuery();
        }
        using (var source = DbSql.Command(db, tx,
            "INSERT INTO operation_source(realm_id,source_key,operation_id) VALUES($realm,$source,$op)"))
        {
            source.Parameters.AddWithValue("$realm", realm);
            source.Parameters.AddWithValue("$source", commit.SourceKey);
            source.Parameters.AddWithValue("$op", commit.OperationId);
            source.ExecuteNonQuery();
        }
        for (int index = 0; index < commit.Legs.Count; index++)
        {
            var leg = commit.Legs[index];
            using var entry = DbSql.Command(db, tx, """
                INSERT INTO economy_entry(commit_seq,operation_id,leg_no,recorded_utc_ms,effective_utc_ms,
                    owner_kind,owner_id,compartment_kind,compartment_key,instance_key,asset_kind,asset_id,asset_epoch,
                    delta,reason_code,leg_kind,edge_no,counterparty_owner_kind,counterparty_owner_id,
                    counterparty_compartment_kind,counterparty_compartment_key,counterparty_instance_key,
                    before_fingerprint,after_fingerprint,source_key)
                VALUES($seq,$op,$no,$recorded,$effective,$ownerKind,$owner,$compartment,$key,$instance,$assetKind,$asset,
                    $epoch,$delta,$reason,$legKind,$edge,$counterOwnerKind,$counterOwner,$counterCompartment,
                    $counterKey,$counterInstance,$before,$after,$source)
                """);
            entry.Parameters.AddWithValue("$seq", sequence);
            entry.Parameters.AddWithValue("$op", commit.OperationId);
            entry.Parameters.AddWithValue("$no", index);
            entry.Parameters.AddWithValue("$recorded", recorded);
            entry.Parameters.AddWithValue("$effective", commit.EffectiveUtcMs);
            entry.Parameters.AddWithValue("$ownerKind", (int)leg.Location.Owner.Kind);
            entry.Parameters.AddWithValue("$owner", leg.Location.Owner.Id);
            entry.Parameters.AddWithValue("$compartment", (int)leg.Location.Compartment);
            entry.Parameters.AddWithValue("$key", leg.Location.Key);
            entry.Parameters.AddWithValue("$instance", leg.Location.Instance);
            entry.Parameters.AddWithValue("$assetKind", (int)leg.Asset.Kind);
            entry.Parameters.AddWithValue("$asset", leg.Asset.Id);
            entry.Parameters.AddWithValue("$epoch", leg.Asset.Epoch);
            entry.Parameters.AddWithValue("$delta", leg.Delta);
            entry.Parameters.AddWithValue("$reason", (int)leg.Reason);
            entry.Parameters.AddWithValue("$legKind", (int)leg.Kind);
            entry.Parameters.AddWithValue("$edge", leg.EdgeNumber);
            entry.Parameters.AddWithValue("$counterOwnerKind", leg.Counterparty is { } c ? (object)(int)c.Owner.Kind : DBNull.Value);
            entry.Parameters.AddWithValue("$counterOwner", (object?)leg.Counterparty?.Owner.Id ?? DBNull.Value);
            entry.Parameters.AddWithValue("$counterCompartment", leg.Counterparty is { } c2 ? (object)(int)c2.Compartment : DBNull.Value);
            entry.Parameters.AddWithValue("$counterKey", (object?)leg.Counterparty?.Key ?? DBNull.Value);
            entry.Parameters.AddWithValue("$counterInstance", (object?)leg.Counterparty?.Instance ?? DBNull.Value);
            entry.Parameters.AddWithValue("$before", (object?)leg.BeforeFingerprint ?? DBNull.Value);
            entry.Parameters.AddWithValue("$after", (object?)leg.AfterFingerprint ?? DBNull.Value);
            entry.Parameters.AddWithValue("$source", commit.SourceKey);
            entry.ExecuteNonQuery();
        }
        return sequence;
    }
}
