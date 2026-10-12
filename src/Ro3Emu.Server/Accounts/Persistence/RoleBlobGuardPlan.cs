using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

internal sealed record RoleBlobSnapshot(bool Exists, byte[]? Value, RoleRevisionVector? Vector = null);

internal sealed class RoleBlobSnapshotRead(long role, string column) : DbReadPlan
{
    internal override int Bytes => 128;
    internal override string Label => "role/capture-blob";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var source = Read(db, tx, role, column);
        var vector = (RoleRevisionVector)new RoleRevisionReadPlan(role).Execute(db, tx);
        return source with { Vector = vector };
    }

    internal static RoleBlobSnapshot Read(SqliteConnection db, SqliteTransaction tx, long role, string column)
    {
        RoleColumns.Validate(column);
        using var cmd = DbSql.Command(db, tx, $"SELECT {column} FROM role WHERE id=$id");
        cmd.Parameters.AddWithValue("$id", role);
        using var row = cmd.ExecuteReader();
        return row.Read() ? new(true, row.IsDBNull(0) ? null : (byte[])row.GetValue(0)) : new(false, null);
    }
}

/// <summary>Compare the caller's source snapshot inside the same transaction as its complete mutation.</summary>
internal sealed class RoleBlobGuardPlan : DbPlan
{
    private readonly long _role;
    private readonly string _column;
    private readonly bool _exists;
    private readonly byte[]? _value;
    internal RoleRevisionVector? CapturedVector { get; }
    internal string Column => _column;
    internal RoleBlobGuardPlan(long role, string column, RoleBlobSnapshot original)
    {
        RoleColumns.Validate(column);
        _role = role; _column = column; _exists = original.Exists;
        CapturedVector = original.Vector ?? throw new InvalidOperationException("Role blob guard needs the read's captured revision vector.");
        _value = original.Value is null ? null : (byte[])original.Value.Clone();
    }
    internal override int Bytes => 128 + (_value?.Length ?? 0);
    internal override string Label => "role/guard-blob";
    internal override IReadOnlyList<long> RolePins => [_role];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var current = RoleBlobSnapshotRead.Read(db, tx, _role, _column);
        if (current.Exists != _exists || (current.Value is null) != (_value is null) ||
            current.Value is not null && !current.Value.AsSpan().SequenceEqual(_value))
            throw new InvalidOperationException($"Role {_role} {_column} changed during transaction capture.");
        return true;
    }
}
