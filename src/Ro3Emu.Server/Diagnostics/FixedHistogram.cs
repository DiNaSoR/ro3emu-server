using System.Diagnostics;

namespace Ro3Emu.Server.Diagnostics;

/// <summary>Fixed, non-cumulative buckets. Record is allocation-free and uses only interlocked operations.
/// Snapshots are weakly consistent while writers run; subtract bucket counts, never percentiles.</summary>
public sealed class FixedHistogram
{
    // Milliseconds for time metrics; raw counts for queue lengths. Upper bounds, including an overflow bucket.
    public static readonly double[] DefaultBounds = [.001, .002, .005, .01, .02, .05, .1, .2, .5, 1, 2, 3, 5, 8, 10, 15, 20, 30, 40, 50, 60, 80, 100, 150, 200, 300, 500, 750, 1000, 2000, 3000, 5000, 10000, 30000, 60000, double.MaxValue];
    private readonly double[] _bounds;
    private readonly long[] _buckets;
    private long _sumMicros, _minMicros = long.MaxValue, _maxMicros;

    public FixedHistogram() : this(DefaultBounds) { }
    public FixedHistogram(double[] bounds)
    {
        if (bounds.Length == 0 || bounds.Any(x => !double.IsFinite(x) || x <= 0) || !bounds.SequenceEqual(bounds.Order().Distinct()))
            throw new ArgumentException("Bounds must be positive, finite and strictly increasing.", nameof(bounds));
        _bounds = (double[])bounds.Clone();
        _buckets = new long[bounds.Length];
    }

    public void Record(double value)
    {
        if (!double.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        var micros = (long)Math.Min(value * 1000, long.MaxValue / 1024.0);
        int bucket = Array.BinarySearch(_bounds, value);
        if (bucket < 0) bucket = ~bucket;
        Interlocked.Increment(ref _buckets[Math.Min(bucket, _buckets.Length - 1)]);
        Interlocked.Add(ref _sumMicros, micros);
        UpdateMin(ref _minMicros, micros);
        UpdateMax(ref _maxMicros, micros);
    }

    public void RecordSince(long start) => Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    internal static void UpdateMax(ref long target, long value)
    {
        long old = Volatile.Read(ref target);
        while (value > old)
        {
            long previous = Interlocked.CompareExchange(ref target, value, old);
            if (previous == old) break;
            old = previous;
        }
    }
    private static void UpdateMin(ref long target, long value)
    {
        long old = Volatile.Read(ref target);
        while (value < old)
        {
            long previous = Interlocked.CompareExchange(ref target, value, old);
            if (previous == old) break;
            old = previous;
        }
    }

    public HistogramSnapshot Snapshot()
    {
        var counts = new long[_buckets.Length];
        for (int i = 0; i < counts.Length; i++) counts[i] = Volatile.Read(ref _buckets[i]);
        long count = counts.Sum();
        double max = Volatile.Read(ref _maxMicros) / 1000.0;
        double Quantile(double q)
        {
            if (count == 0) return 0;
            long target = (long)Math.Ceiling(count * q), n = 0;
            for (int i = 0; i < counts.Length; i++) if ((n += counts[i]) >= target) return Math.Min(_bounds[i], max);
            return max;
        }
        return new(count, Volatile.Read(ref _sumMicros) / 1000.0, count == 0 ? 0 : Volatile.Read(ref _minMicros) / 1000.0,
            max, Quantile(.5), Quantile(.99), (double[])_bounds.Clone(), counts);
    }
}

public sealed record HistogramSnapshot(long Count, double Sum, double Min, double Max, double P50, double P99, double[] Bounds, long[] Buckets);
