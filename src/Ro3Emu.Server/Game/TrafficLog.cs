using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Microsoft.Extensions.Options;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Every game message in both directions as JSON lines in &lt;data&gt;/logs/traffic-yyyyMMdd.jsonl:
/// {"t":..., "acc":..., "dir":"c2s"|"s2c", "id":..., "name":..., "body":{...}}. The main RE aid for new systems.
/// <para><see cref="ServerOptions.TrafficLog"/>: "off" (default), "on", or a comma-separated account list. The game loop
/// only copies the body bytes into a bounded queue; parsing, JSON formatting and buffered file writes run on a
/// background thread. A full queue drops the entry and counts it (<see cref="Dropped"/>) instead of stalling the
/// loop; the next written line reports the gap.</para>
/// </summary>
public sealed class TrafficLog : IDisposable
{
    private readonly record struct Entry(DateTime At, string? Account, string Direction, uint MsgId,
        MessageDescriptor? Type, ReadOnlyMemory<byte> Body, string? Text);

    private const int Capacity = 65536;
    private readonly MsgCatalog _catalog;
    private readonly string _dir;
    private readonly bool _all;
    private readonly HashSet<string>? _accounts;
    private readonly Channel<Entry>? _queue;
    private readonly Task? _writerTask;
    private long _dropped, _reportedDropped;

    public TrafficLog(IOptions<ServerOptions> options, MsgCatalog catalog)
    {
        _catalog = catalog;
        _dir = Path.Combine(options.Value.DataDir, "logs");
        string mode = options.Value.TrafficLog?.Trim() ?? "off";
        if (mode.Equals("off", StringComparison.OrdinalIgnoreCase) || mode.Length == 0) return;
        _all = mode.Equals("on", StringComparison.OrdinalIgnoreCase);
        if (!_all)
            _accounts = mode.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(_dir);
        _queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false,
        }, _ => Interlocked.Increment(ref _dropped));
        _writerTask = Task.Factory.StartNew(() => WriteLoopAsync(_queue.Reader), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    /// <summary>Entries discarded because the background writer fell behind.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public bool Enabled(string? account) =>
        _queue != null && (_all || account != null && _accounts!.Contains(account));

    /// <summary><paramref name="body"/> may be a reused receive buffer: it is copied when the account is logged.</summary>
    public void ClientToServer(string? account, uint msgId, ReadOnlyMemory<byte> body)
    {
        if (!Enabled(account) || msgId == MsgId.Heartbeat) return;
        Enqueue(new Entry(DateTime.Now, account, "c2s", msgId, null, body.ToArray(), null));
    }

    /// <summary><paramref name="bytes"/> is the already serialized <paramref name="type"/> body (not copied again).</summary>
    public void ServerToClient(string? account, uint msgId, MessageDescriptor type, byte[] bytes)
    {
        if (!Enabled(account) || msgId == MsgId.Heartbeat) return;
        Enqueue(new Entry(DateTime.Now, account, "s2c", msgId, type, bytes, null));
    }

    public void ServerToClientRaw(string? account, uint msgId, int length)
    {
        if (!Enabled(account)) return;
        Enqueue(new Entry(DateTime.Now, account, "s2c", msgId, null, default, $"{length} bytes"));
    }

    public void Rpc(string? account, string direction, string method, Func<string> args)
    {
        if (!Enabled(account)) return;
        Enqueue(new Entry(DateTime.Now, account, direction, 0, null, default, method + "\n" + args()));
    }

    private void Enqueue(in Entry entry) => _queue!.Writer.TryWrite(entry);

    private async Task WriteLoopAsync(ChannelReader<Entry> reader)
    {
        StreamWriter? writer = null;
        string? day = null;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var e))
                {
                    var d = e.At.ToString("yyyyMMdd");
                    if (d != day)
                    {
                        writer?.Dispose();
                        writer = new StreamWriter(new FileStream(Path.Combine(_dir, $"traffic-{d}.jsonl"), FileMode.Append,
                            FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false), 1 << 16);
                        day = d;
                    }
                    long dropped = Interlocked.Read(ref _dropped);
                    if (dropped != _reportedDropped)
                    {
                        writer!.WriteLine($"{{\"t\":\"{e.At:HH:mm:ss.fff}\",\"acc\":null,\"dir\":\"log\",\"id\":0,\"name\":\"DROPPED\",\"body\":{dropped - _reportedDropped}}}");
                        _reportedDropped = dropped;
                    }
                    writer!.WriteLine(Format(e));
                }
                writer?.Flush(); // once per drained batch, not per line
            }
        }
        catch (Exception) { /* logging must never take the server down */ }
        finally { writer?.Dispose(); }
    }

    private string Format(in Entry e)
    {
        string name, body;
        if (e.Text != null && e.MsgId == 0)
        {
            int split = e.Text.IndexOf('\n');
            name = e.Text[..split];
            body = JsonSerializer.Serialize(e.Text[(split + 1)..]);
        }
        else
        {
            name = _catalog.Name(e.MsgId);
            body = e.Text ?? (e.Type != null ? Parse(e.Type, e.Body) : _catalog.Describe(e.MsgId, e.Body));
            if (!(body.StartsWith('{') || body.StartsWith('"'))) body = JsonSerializer.Serialize(body);
        }
        return $"{{\"t\":\"{e.At:HH:mm:ss.fff}\",\"acc\":{JsonSerializer.Serialize(e.Account)},\"dir\":\"{e.Direction}\",\"id\":{e.MsgId},\"name\":{JsonSerializer.Serialize(name)},\"body\":{body}}}";
    }

    private static string Parse(MessageDescriptor type, ReadOnlyMemory<byte> bytes)
    {
        try { return JsonFormatter.Default.Format(type.Parser.ParseFrom(bytes.Span)); }
        catch (InvalidProtocolBufferException) { return $"{bytes.Length} bytes (unparsable as {type.Name})"; }
    }

    public void Dispose()
    {
        if (_queue == null) return;
        _queue.Writer.TryComplete();
        try { _writerTask!.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
    }
}
