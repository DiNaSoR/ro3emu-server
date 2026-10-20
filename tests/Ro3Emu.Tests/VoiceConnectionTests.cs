using System.Net;
using System.Net.Sockets;
using Ro3Emu.Server.Voice;

namespace Ro3Emu.Tests;

/// <summary>VoiceConnection's writer on a real loopback socket pair, without the gate around it.</summary>
public sealed class VoiceConnectionTests
{
    [Fact]
    public async Task AKickWhileTheWriterIsMidFrameStillSendsTheKickedFrame()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ReceiveBufferSize = 4096 };
        await client.ConnectAsync(listener.LocalEndPoint!);
        var accepted = await listener.AcceptAsync();
        accepted.SendBufferSize = 4096;
        using var connection = new VoiceConnection(accepted);
        var writer = Task.Run(connection.RunWriterAsync);

        // A frame far larger than both socket buffers: the writer has dequeued it, found the queue empty, and is
        // blocked writing it while the client reads nothing.
        connection.SendAudio(new byte[60000]);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (client.Available == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(client.Available > 0);

        // The kick lands mid-write: the Kicked frame still has to follow before the writer ends the stream.
        connection.Kick(VoiceKickCode.Flood);
        using var stream = new NetworkStream(client, ownsSocket: false);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = await VoiceFrame.ReadAsync(stream, ushort.MaxValue, cts.Token);
        Assert.Equal(VoiceDown.Audio, first.Type);
        var second = await VoiceFrame.ReadAsync(stream, ushort.MaxValue, cts.Token);
        Assert.Equal(VoiceDown.Kicked, second.Type);
        Assert.Equal(VoiceKickCode.Flood, second.Payload[0]);
        await writer.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
