using Ro3Emu.Server.Net;

namespace Ro3Emu.Tests;

/// <summary>The connection's deflate + RC4 streams: input and output run on different threads at the same time.</summary>
public sealed class StreamConvertorTests
{
    [Fact]
    public async Task BothDirectionsAtOnceStayInSync()
    {
        var key = Enumerable.Range(1, 20).Select(i => (byte)i).ToArray();
        var server = new StreamConvertor(key);
        var client = new StreamConvertor(key); // the peer: its output is our input
        var random = new Random(7);
        var frames = Enumerable.Range(0, 300).Select(_ => { var b = new byte[random.Next(10, 20000)]; random.NextBytes(b); return b; }).ToList();

        var packed = frames.Select(f => client.ConvertOutput(f)).ToList();
        var outbound = Task.Run(() =>
        {
            for (int i = 0; i < 300; i++) server.ConvertOutput(frames[i]); // our writer, concurrently
        });
        var received = new List<byte[]>();
        foreach (var p in packed) received.Add(server.ConvertInput(p));
        await outbound;
        Assert.Equal(frames.SelectMany(f => f), received.SelectMany(r => r));
    }

    [Fact]
    public void OutboundFramesReuseTheirBuffers()
    {
        // Stage G: SharpZipLib rebuilt Huffman trees with fresh int[] arrays on every per-frame flush (7.3 GB a minute
        // at 500 players). The native zlib stream into a reused buffer allocates almost nothing per frame.
        var key = Enumerable.Range(1, 20).Select(i => (byte)i).ToArray();
        var server = new StreamConvertor(key);
        var client = new StreamConvertor(key);
        var frame = Enumerable.Range(0, 300).Select(i => (byte)(i * 7)).ToArray();
        var batch = new MemoryStream();
        for (int i = 0; i < 50; i++) server.ConvertOutput(frame, batch); // warm-up: stream state, buffer growth
        Assert.Equal(Enumerable.Repeat(frame, 50).SelectMany(f => f), client.ConvertInput(batch.ToArray()));
        batch.SetLength(0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) { batch.SetLength(0); server.ConvertOutput(frame, batch); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 200_000, $"{allocated} bytes for 1000 frames");
    }
}
