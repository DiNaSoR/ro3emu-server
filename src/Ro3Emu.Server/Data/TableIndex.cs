using System.Text.Json;

namespace Ro3Emu.Server.Data;

/// <summary>
/// A config table's rows grouped by a key (<see cref="GameData.Index{TKey}"/>), each group in table order, so
/// <see cref="First"/> returns the row a <c>Values.FirstOrDefault(...)</c> scan would. The index holds row ids and reads
/// the rows from the table, so a row replaced in place is seen as it is now; a table that gained or lost rows (tests
/// add some) is indexed again.
/// </summary>
public sealed class TableIndex<TKey> where TKey : notnull
{
    private readonly IReadOnlyDictionary<string, JsonElement> _table;
    private readonly int _count;
    private readonly Dictionary<TKey, string[]> _ids;

    internal TableIndex(IReadOnlyDictionary<string, JsonElement> table, Func<JsonElement, TKey> key)
    {
        _table = table;
        _count = table.Count;
        _ids = table.GroupBy(r => key(r.Value), r => r.Key).ToDictionary(g => g.Key, g => g.ToArray());
    }

    internal bool Fresh(IReadOnlyDictionary<string, JsonElement> table) => ReferenceEquals(table, _table) && table.Count == _count;

    /// <summary>The first row with this key, or null.</summary>
    public JsonElement? First(TKey key)
    {
        if (_ids.TryGetValue(key, out var ids))
            foreach (var id in ids)
                if (_table.TryGetValue(id, out var row)) return row;
        return null;
    }

    /// <summary>Every row with this key, in table order.</summary>
    public IEnumerable<JsonElement> All(TKey key)
    {
        if (!_ids.TryGetValue(key, out var ids)) yield break;
        foreach (var id in ids)
            if (_table.TryGetValue(id, out var row)) yield return row;
    }

    public bool Contains(TKey key) => _ids.ContainsKey(key);
}
