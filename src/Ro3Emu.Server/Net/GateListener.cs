using System.Net;
using System.Net.Sockets;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Net;

/// <summary>Accepts TCP connections on one port and runs a <see cref="PhonestChannel"/> per client.</summary>
public abstract class GateListener(IPAddress address, int port, ILogger log, Diagnostics.ServerMetrics? metrics = null) : BackgroundService
{
    private const int MaxServeTasks = 4096;
    private readonly object _servesGate = new();
    private readonly Dictionary<long, (Socket Socket, Task Task)> _serves = new();
    private readonly AsyncLocal<GameLoop.CompletionReservation?> _serveCompletion = new();
    private long _nextServeId;
    private bool _stopping;
    public bool DrainSucceeded { get; private set; } = true;
    public int OutstandingServeTasks { get { lock (_servesGate) return _serves.Count; } }
    protected ILogger Log { get; } = log;
    protected Diagnostics.ServerMetrics? Metrics { get; } = metrics;

    protected abstract string Name { get; }

    protected abstract ValueTask OnFrameAsync(PhonestChannel channel, ushort index, ReadOnlyMemory<byte> body);

    protected virtual ValueTask OnDisconnectedAsync(PhonestChannel channel) => ValueTask.CompletedTask;
    protected virtual GameLoop.CompletionReservation? ReserveServeCompletion() => null;
    protected virtual void OnCleanupFailed(PhonestChannel channel, Exception error) { }
    protected GameLoop.CompletionReservation? CurrentServeCompletion => _serveCompletion.Value;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var listener = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(address, port));
        listener.Listen(512); // a realm start is a burst of connects (Kestrel's default backlog too)
        Log.LogInformation("{Name} listening on {Address}:{Port}", Name, address, port);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var socket = await listener.AcceptAsync(ct);
                socket.NoDelay = true;
                lock (_servesGate)
                {
                    if (_stopping || _serves.Count >= MaxServeTasks)
                    {
                        socket.Dispose();
                        continue;
                    }
                    long id = ++_nextServeId;
                    GameLoop.CompletionReservation? completion;
                    try { completion = ReserveServeCompletion(); }
                    catch (InvalidOperationException)
                    {
                        socket.Dispose();
                        continue;
                    }
                    // No cancellation token on Task.Run: a canceled dispatch must still dispose its socket.
                    var task = Task.Run(() => ServeAsync(socket, ct, completion));
                    _serves.Add(id, (socket, task));
                    _ = task.ContinueWith(_ => { lock (_servesGate) _serves.Remove(id); },
                        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_servesGate) _stopping = true;
        await base.StopAsync(cancellationToken);
        Task[] outstanding;
        lock (_servesGate)
        {
            // RunAsync observes the canceled receive token; each channel's existing DisposeAsync policy
            // must retain its chance to flush queued output before the socket closes.
            outstanding = _serves.Values.Select(s => s.Task).ToArray();
        }
        try { await Task.WhenAll(outstanding).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
        catch (Exception e) when (e is TimeoutException || (e is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            DrainSucceeded = false;
            Log.LogError("{Name} serve drain incomplete: {Count} connection tasks remain", Name, OutstandingServeTasks);
        }
        catch (Exception e)
        {
            DrainSucceeded = false;
            Log.LogError(e, "{Name} serve drain failed", Name);
        }
    }

    private async Task ServeAsync(Socket socket, CancellationToken ct, GameLoop.CompletionReservation? completion)
    {
        _serveCompletion.Value = completion;
        try
        {
        await using var channel = new PhonestChannel(socket, Log) { Metrics = Metrics?.Connect() };
        Log.LogInformation("{Name}: {Remote} connected", Name, channel.Remote);
        try
        {
            await channel.RunAsync(OnFrameAsync, ct);
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or IOException)
        {
        }
        catch (Exception e)
        {
            Log.LogError(e, "{Name}: {Remote} failed", Name, channel.Remote);
        }
        finally
        {
            try { await OnDisconnectedAsync(channel); }
            catch (Exception e)
            {
                DrainSucceeded = false;
                OnCleanupFailed(channel, e);
                Log.LogError(e, "{Name}: {Remote} disconnect cleanup failed", Name, channel.Remote);
            }
            Log.LogInformation("{Name}: {Remote} disconnected", Name, channel.Remote);
        }
        }
        finally { _serveCompletion.Value = null; completion?.Dispose(); }
    }
}
