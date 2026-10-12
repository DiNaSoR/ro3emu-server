using System.IO.Compression;
using ICSharpCode.SharpZipLib.Zip.Compression;

namespace Ro3Emu.Server.Net;

/// <summary>
/// Mirror of Phonest.Rpc.Convertor: one persistent zlib stream per direction (sync-flushed after every frame)
/// wrapped in RC4. Inbound = RC4 decrypt, then inflate; outbound = deflate + sync flush, then RC4 encrypt.
/// </summary>
public sealed class StreamConvertor
{
    private readonly Rc4 _decrypt;
    private readonly Rc4 _encrypt;
    private readonly Inflater _inflater = new(noHeader: false);
    // Outbound: the runtime's native zlib (Flush = Z_SYNC_FLUSH) into a reused buffer. SharpZipLib's deflater rebuilt
    // its Huffman trees with fresh int[] arrays on every per-frame flush: 7.3 GB a minute at 500 players and about
    // 29 gen0 collections a second, each pausing the game loop (stage G).
    private readonly MemoryStream _packed = new();
    private readonly ZLibStream _deflater;
    // Input runs on the connection reader, output on its writer task, concurrently: each direction has its own state.
    private readonly byte[] _inScratch = new byte[16 * 1024];

    public StreamConvertor(byte[] sessionKey)
    {
        _decrypt = new Rc4(sessionKey);
        _encrypt = new Rc4(sessionKey);
        _deflater = new ZLibStream(_packed, CompressionLevel.Optimal, leaveOpen: true);
    }

    /// <summary>Decrypts and inflates a chunk of received bytes; returns the plain frame bytes it yields (possibly none).
    /// More than <paramref name="maxOutput"/> plain bytes from one chunk is refused (a decompression bomb).</summary>
    public byte[] ConvertInput(ReadOnlySpan<byte> received, int maxOutput = int.MaxValue)
    {
        var buf = received.ToArray();
        _decrypt.Process(buf);
        _inflater.SetInput(buf);
        using var plain = new MemoryStream();
        while (true)
        {
            int n = _inflater.Inflate(_inScratch);
            if (n > 0)
            {
                if (plain.Length + n > maxOutput) throw new InvalidDataException($"inflated input exceeds {maxOutput} bytes");
                plain.Write(_inScratch, 0, n);
                continue;
            }
            if (_inflater.IsNeedingInput || _inflater.IsFinished) break;
            if (_inflater.IsNeedingDictionary) throw new InvalidDataException("zlib stream wants a preset dictionary");
        }
        return plain.ToArray();
    }

    /// <summary>Deflates one or more whole frames with a sync flush and encrypts the result.</summary>
    public byte[] ConvertOutput(ReadOnlySpan<byte> frames)
    {
        var output = new MemoryStream();
        ConvertOutput(frames, output);
        return output.ToArray();
    }

    /// <summary>Deflates, sync-flushes and encrypts <paramref name="frames"/>, appending the wire bytes to
    /// <paramref name="destination"/> without an intermediate array.</summary>
    public void ConvertOutput(ReadOnlySpan<byte> frames, Stream destination)
    {
        _packed.SetLength(0);
        _deflater.Write(frames);
        _deflater.Flush();
        var wire = _packed.GetBuffer().AsSpan(0, (int)_packed.Length);
        _encrypt.Process(wire);
        destination.Write(wire);
    }
}
