namespace Ro3Emu.Patcher;

/// <summary>Chunk size limits of FastCDC. Publisher and client must use the same values (the manifest carries them).</summary>
public sealed record ChunkingParameters(int Min, int Avg, int Max)
{
    public static readonly ChunkingParameters Default = new(256 * 1024, 1024 * 1024, 4 * 1024 * 1024);

    public void Validate()
    {
        if (Min < 64 || Min >= Avg || Avg >= Max)
        {
            throw new ArgumentException($"Invalid chunking parameters {Min}/{Avg}/{Max}.");
        }
        if ((Avg & (Avg - 1)) != 0)
        {
            throw new ArgumentException($"Average chunk size {Avg} must be a power of two.");
        }
    }
}

/// <summary>
/// FastCDC content-defined chunking (Xia et al. 2016/2020) with normalized chunking level 2: a gear rolling hash,
/// a stricter mask before the average size and a looser one after it. Boundaries depend only on the bytes around
/// them, so an insertion in a file changes only the chunks next to it.
/// </summary>
public static class FastCdc
{
    static readonly ulong[] Gear = BuildGear();

    /// <summary>Length of the next chunk at the start of <paramref name="data"/>; <paramref name="data"/> holds all remaining bytes
    /// or at least <see cref="ChunkingParameters.Max"/> of them.</summary>
    public static int Cut(ReadOnlySpan<byte> data, ChunkingParameters p)
    {
        int n = data.Length;
        if (n <= p.Min)
        {
            return n;
        }
        if (n > p.Max)
        {
            n = p.Max;
        }

        int bits = System.Numerics.BitOperations.Log2((uint)p.Avg);
        // The gear hash shifts left, so its top bits cover the last 64 bytes; test those.
        ulong maskStrict = ulong.MaxValue << (64 - (bits + 2));
        ulong maskLoose = ulong.MaxValue << (64 - (bits - 2));

        int normal = Math.Min(p.Avg, n);
        ulong hash = 0;
        int i = p.Min;
        for (; i < normal; i++)
        {
            hash = (hash << 1) + Gear[data[i]];
            if ((hash & maskStrict) == 0)
            {
                return i + 1;
            }
        }
        for (; i < n; i++)
        {
            hash = (hash << 1) + Gear[data[i]];
            if ((hash & maskLoose) == 0)
            {
                return i + 1;
            }
        }
        return n;
    }

    static ulong[] BuildGear()
    {
        // Fixed seed: the table is part of the on-disk format.
        var table = new ulong[256];
        ulong state = 0x46617465_73656E64UL; // fixed gear seed; changing it changes every chunk boundary
        for (int i = 0; i < table.Length; i++)
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            table[i] = z ^ (z >> 31);
        }
        return table;
    }
}

/// <summary>Reads a stream as a sequence of FastCDC chunks. The returned memory is valid until the next call.</summary>
public sealed class ChunkReader
{
    readonly Stream _stream;
    readonly ChunkingParameters _p;
    readonly byte[] _buffer;
    int _start;
    int _end;
    bool _eof;
    long _offset;

    public ChunkReader(Stream stream, ChunkingParameters p)
    {
        p.Validate();
        _stream = stream;
        _p = p;
        _buffer = new byte[Math.Max(p.Max * 2, 8 * 1024 * 1024)];
    }

    public bool Next(out long offset, out ReadOnlyMemory<byte> chunk)
    {
        if (_end - _start < _p.Max && !_eof)
        {
            Fill();
        }
        if (_end == _start)
        {
            offset = _offset;
            chunk = ReadOnlyMemory<byte>.Empty;
            return false;
        }
        int length = FastCdc.Cut(_buffer.AsSpan(_start, _end - _start), _p);
        offset = _offset;
        chunk = new ReadOnlyMemory<byte>(_buffer, _start, length);
        _start += length;
        _offset += length;
        return true;
    }

    void Fill()
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }
        while (_end < _buffer.Length)
        {
            int read = _stream.Read(_buffer, _end, _buffer.Length - _end);
            if (read == 0)
            {
                _eof = true;
                break;
            }
            _end += read;
        }
    }
}
