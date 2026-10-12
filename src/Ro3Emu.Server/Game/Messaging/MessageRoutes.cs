using System.Diagnostics;

namespace Ro3Emu.Server.Game.Messaging;

public delegate Task MessageHandler(GameSession session, uint msgId, ReadOnlyMemory<byte> body);

/// <summary>
/// Game-gate message routes (audit F4.1): one owner per message id, declared where the message is handled.
/// A duplicate id or an overlapping range is a startup error instead of a silently shadowed switch arm. Every route
/// counts its calls, loop time and failures (the per-message handling-time view).
/// </summary>
public sealed class MessageRoutes
{
    public sealed class Route(uint from, uint to, string owner, MessageHandler handler)
    {
        public uint From { get; } = from;
        public uint To { get; } = to;
        public string Owner { get; } = owner;
        private long _calls, _failures, _ticks;
        public long Calls => Interlocked.Read(ref _calls);
        public long Failures => Interlocked.Read(ref _failures);
        public double TotalMs => Interlocked.Read(ref _ticks) * 1000d / Stopwatch.Frequency;

        public async Task InvokeAsync(GameSession session, uint msgId, ReadOnlyMemory<byte> body)
        {
            Interlocked.Increment(ref _calls);
            long started = Stopwatch.GetTimestamp();
            try { await handler(session, msgId, body); }
            catch { Interlocked.Increment(ref _failures); throw; }
            finally { Interlocked.Add(ref _ticks, Stopwatch.GetTimestamp() - started); }
        }
    }

    private readonly Dictionary<uint, Route> _exact = new();
    private readonly List<Route> _ranges = new();

    public IReadOnlyCollection<Route> Exact => _exact.Values;
    public IReadOnlyList<Route> Ranges => _ranges;

    public void Map(uint id, string owner, MessageHandler handler)
    {
        if (_exact.TryGetValue(id, out var existing))
            throw new InvalidOperationException($"Message {id} is routed twice ({existing.Owner}, {owner}).");
        if (_ranges.FirstOrDefault(r => id >= r.From && id <= r.To) is { } range)
            throw new InvalidOperationException($"Message {id} ({owner}) falls in {range.Owner}'s range {range.From}-{range.To}.");
        _exact.Add(id, new Route(id, id, owner, handler));
        Diagnostics.MessageCoverage.Route(id, id, owner);
    }

    public void MapRange(uint from, uint to, string owner, MessageHandler handler)
    {
        if (from > to) throw new ArgumentException($"Empty range {from}-{to} for {owner}.");
        if (_exact.Keys.FirstOrDefault(id => id >= from && id <= to) is var id && id != 0)
            throw new InvalidOperationException($"Range {from}-{to} ({owner}) covers routed message {id} ({_exact[id].Owner}).");
        if (_ranges.FirstOrDefault(r => from <= r.To && r.From <= to) is { } overlap)
            throw new InvalidOperationException($"Range {from}-{to} ({owner}) overlaps {overlap.Owner}'s {overlap.From}-{overlap.To}.");
        _ranges.Add(new Route(from, to, owner, handler));
        Diagnostics.MessageCoverage.Route(from, to, owner);
    }

    public bool TryGet(uint id, out Route route)
    {
        if (_exact.TryGetValue(id, out route!)) return true;
        foreach (var range in _ranges)
            if (id >= range.From && id <= range.To) { route = range; return true; }
        return false;
    }

    /// <summary>Slowest routes by total loop time (diagnostics).</summary>
    public object Snapshot(int top = 20) => _exact.Values.Concat(_ranges)
        .Where(r => r.Calls > 0).OrderByDescending(r => r.TotalMs).Take(top)
        .Select(r => new { id = r.From == r.To ? r.From.ToString() : $"{r.From}-{r.To}", owner = r.Owner, calls = r.Calls,
            totalMs = Math.Round(r.TotalMs, 1), avgMs = Math.Round(r.TotalMs / r.Calls, 3), failures = r.Failures })
        .ToArray();
}
