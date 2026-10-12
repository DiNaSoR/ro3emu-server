using System.Threading.Channels;

namespace Ro3Emu.LoadTest;

/// <summary>One bounded, serialized sender per bot. A stalled send fails within its deadline and closes the bot.</summary>
public sealed class BotSendQueue : IAsyncDisposable
{
    private readonly Channel<SendWork> _channel;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly Func<ValueTask> _close;
    private readonly TimeSpan _timeout;
    private int _failed;
    private sealed record SendWork(Func<Task> Send, TaskCompletionSource Done);

    public BotSendQueue(Func<ValueTask> close, int capacity = 32, TimeSpan? timeout = null)
    {
        _close = close;
        _timeout = timeout ?? TimeSpan.FromSeconds(3);
        _channel = Channel.CreateBounded<SendWork>(new BoundedChannelOptions(capacity)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        _worker = Task.Run(WorkAsync);
    }

    public async Task SendAsync(Func<Task> send, CancellationToken ct)
    {
        if (Volatile.Read(ref _failed) != 0) throw new IOException("bot sender failed");
        var work = new SendWork(send, new(TaskCreationOptions.RunContinuationsAsynchronously));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        deadline.CancelAfter(_timeout);
        try
        {
            await _channel.Writer.WriteAsync(work, deadline.Token);
            await work.Done.Task.WaitAsync(_timeout, ct);
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            work.Done.TrySetException(new TimeoutException("bot send queue admission timed out", e));
            throw new TimeoutException("bot send queue admission timed out", e);
        }
        catch (TimeoutException)
        {
            _ = CloseAfterStallAsync();
            throw;
        }
    }

    private async Task WorkAsync()
    {
        try
        {
            await foreach (var work in _channel.Reader.ReadAllAsync(_stop.Token))
            {
                try { await work.Send().WaitAsync(_timeout, _stop.Token); work.Done.TrySetResult(); }
                catch (Exception e)
                {
                    work.Done.TrySetException(e);
                    _ = CloseAfterStallAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            while (_channel.Reader.TryRead(out var queued)) queued.Done.TrySetException(new IOException("bot sender stopped"));
        }
    }

    private async Task CloseAfterStallAsync()
    {
        if (Interlocked.Exchange(ref _failed, 1) != 0) return;
        _channel.Writer.TryComplete();
        _stop.Cancel();
        try { await _close(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _stop.Cancel();
        try { await _worker.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}
