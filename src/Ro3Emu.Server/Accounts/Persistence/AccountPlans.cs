using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

internal static class AccountHash
{
    internal static byte[] Hash(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 100_000, HashAlgorithmName.SHA256, 32);
}

internal sealed record AccountCredentials(byte[] Hash, byte[] Salt);

internal sealed class AccountCredentialsRead(string name) : DbReadPlan
{
    internal override int Bytes => 128 + Encoding.UTF8.GetByteCount(name);
    internal override string Label => "account/credentials";
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var select = DbSql.Command(db, tx, "SELECT password_hash,salt FROM account WHERE name=$name");
        select.Parameters.AddWithValue("$name", name);
        using var row = select.ExecuteReader();
        return row.Read() ? new AccountCredentials((byte[])((byte[])row[0]).Clone(), (byte[])((byte[])row[1]).Clone()) : null;
    }
}

internal sealed class AccountRegisterPlan : DbPlan
{
    private readonly string _name, _createdUtc;
    private readonly byte[] _hash, _salt;
    internal AccountRegisterPlan(string name, byte[] hash, byte[] salt, string createdUtc)
    {
        _name = name; _createdUtc = createdUtc;
        _hash = (byte[])hash.Clone(); _salt = (byte[])salt.Clone();
    }
    internal override int Bytes => 256 + Encoding.UTF8.GetByteCount(_name) + _hash.Length + _salt.Length;
    internal override string Label => "account/register";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var insert = DbSql.Command(db, tx, "INSERT OR IGNORE INTO account(name,password_hash,salt,created_utc) VALUES($name,$hash,$salt,$now)");
        insert.Parameters.AddWithValue("$name", _name);
        insert.Parameters.AddWithValue("$hash", _hash);
        insert.Parameters.AddWithValue("$salt", _salt);
        insert.Parameters.AddWithValue("$now", _createdUtc);
        return insert.ExecuteNonQuery() == 1;
    }
}

internal sealed class AccountTokenPlan(string kind, string account, string token, long now, long expires) : DbPlan
{
    internal override int Bytes => 192 + Encoding.UTF8.GetByteCount(account) + token.Length;
    internal override string Label => "auth_token/issue";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var command = DbSql.Command(db, tx, "DELETE FROM auth_token WHERE expires_utc < $now; INSERT INTO auth_token(token,kind,account,expires_utc) VALUES($token,$kind,$account,$expires)");
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$token", token);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$expires", expires);
        command.ExecuteNonQuery();
        return token;
    }
}

internal sealed class AccountTokenRead(string kind, string? token, long now) : DbReadPlan
{
    internal override int Bytes => 128 + (token?.Length ?? 0);
    internal override string Label => "auth_token/resolve";
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        if (string.IsNullOrEmpty(token)) return null;
        using var cmd = DbSql.Command(db, tx, "SELECT account FROM auth_token WHERE token=$token AND kind=$kind AND expires_utc >= $now");
        cmd.Parameters.AddWithValue("$token", token);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$now", now);
        return cmd.ExecuteScalar() as string;
    }
}
