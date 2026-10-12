using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>One source-keyed financial letter, serialized before enqueue and appended to the current mailbox.</summary>
internal sealed class FinancialMailAppendPlan : DbPlan
{
    private readonly long _role, _utcTicks;
    private readonly string _key;
    private readonly byte[] _letter;
    internal FinancialMailAppendPlan(long role, string key, MailService.Letter letter, long utcTicks)
    {
        _role = role; _key = key; _utcTicks = utcTicks;
        _letter = JsonSerializer.SerializeToUtf8Bytes(letter);
    }
    internal override int Bytes => 128 + _key.Length * 2 + _letter.Length;
    internal override string Label => "mail/financial-append";
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => [RevisionStore.Role(_role, "mailbox")];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var read = DbSql.Command(db, tx, "SELECT mailbox FROM role WHERE id=$role AND deleted_time=0");
        read.Parameters.AddWithValue("$role", _role);
        var raw = read.ExecuteScalar();
        if (raw is null) throw new InvalidOperationException("Invalid auction mail recipient, template or attachment.");
        var box = raw is byte[] bytes ? JsonSerializer.Deserialize<MailService.Mailbox>(bytes) ?? new MailService.Mailbox()
            : new MailService.Mailbox();
        foreach (var letter in box.Letters)
        {
            if (letter.DeliveryKey.Length == 0 && letter.SourceKey is { Length: > 0 })
                letter.DeliveryKey = letter.SourceKey;
            letter.SourceKey = null;
        }
        if (box.Letters.Any(letter => letter.DeliveryKey == _key)) return 0;
        var prepared = JsonSerializer.Deserialize<MailService.Letter>(_letter)!;
        box.NextId = Math.Max(box.NextId + 1, _utcTicks);
        prepared.Id = box.NextId.ToString("D18", CultureInfo.InvariantCulture);
        box.Letters.Add(prepared);
        using var write = DbSql.Command(db, tx, "UPDATE role SET mailbox=$mailbox WHERE id=$role AND deleted_time=0");
        write.Parameters.AddWithValue("$role", _role);
        write.Parameters.AddWithValue("$mailbox", JsonSerializer.SerializeToUtf8Bytes(box));
        if (write.ExecuteNonQuery() != 1) throw new InvalidOperationException("Invalid auction mail recipient, template or attachment.");
        return 1;
    }
}
