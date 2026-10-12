using System.Collections.Concurrent;
using System.Diagnostics;

namespace Ro3Emu.Server.Diagnostics;

/// <summary>Counting sidecar for the existing transport. It neither changes framing nor repairs partial sends.
/// A FIFO of small value stamps follows the transport FIFO; there is no retained message body or formatting.</summary>
public sealed class ConnectionMetrics(ServerMetrics owner, long id)
{
    [ThreadStatic] private static RequestMeasurement? _replyCandidate;
    private readonly ConcurrentQueue<FrameStamp> _frames = new();
    private FrameStamp _sending;
    private long _currentBytes, _peakBytes;
    private int _closed;
    private long _enqueuedFrames, _completedFrames;
    private long _receivedBytes, _sentBytes;
    private Exception? _sendFailure;
    private TaskCompletionSource? _sendProgress;
    public long Id => id;
    public long CurrentBytes => Math.Max(0, Volatile.Read(ref _currentBytes));
    public long PeakBytes => Volatile.Read(ref _peakBytes);
    public long BytesReceived => Volatile.Read(ref _receivedBytes);
    public long BytesSent => Volatile.Read(ref _sentBytes);
    private readonly record struct FrameStamp(int Bytes, uint ReplyId, long ReceivedAt);

    public void ReceivedBytes(int bytes)
    {
        Interlocked.Add(ref _receivedBytes, bytes);
        Interlocked.Add(ref owner.Current.BytesIn, bytes);
    }
    public void ReceivedFrame() => Interlocked.Increment(ref owner.Current.FramesIn);
    public ReplyScope Outgoing(uint messageId, int bytes)
    {
        var e = owner.Current;
        if (!e.Outbound.TryGetValue(messageId, out var counter))
            counter = e.Outbound.GetOrAdd(e.Outbound.Count < 4096 ? messageId : uint.MaxValue, static _ => new());
        Interlocked.Increment(ref counter.Count);
        Interlocked.Add(ref counter.Bytes, bytes);
        var request = ServerMetrics.Request.Value;
        var previous = _replyCandidate;
        _replyCandidate = request is { Active: true } && ReferenceEquals(request.Connection, this) && request.Id == messageId ? request : null;
        return new ReplyScope(previous);
    }
    /// <summary>The synchronous serialization/enqueue call owns this thread-local candidate. Another execution
    /// context sending on this connection cannot steal its reply tag, even if it inherited the request context.</summary>
    public readonly struct ReplyScope : IDisposable
    {
        private readonly RequestMeasurement? _previous;
        internal ReplyScope(RequestMeasurement? previous) => _previous = previous;
        public void Dispose() => _replyCandidate = _previous;
    }
    // Called inside the transport's existing enqueue ordering boundary, before publishing the frame to its writer.
    public void Enqueued(int bytes)
    {
        long old = Volatile.Read(ref _currentBytes);
        while (true)
        {
            if (old < 0) return;
            long previous = Interlocked.CompareExchange(ref _currentBytes, old + bytes, old);
            if (previous == old) break;
            old = previous;
        }
        owner.EgressDelta(bytes);
        FixedHistogram.UpdateMax(ref _peakBytes, old + bytes);
        owner.ConnectionPeak(old + bytes);
        var request = _replyCandidate;
        _replyCandidate = null;
        long received = 0;
        uint replyId = 0;
        if (request is { Active: true } && ReferenceEquals(request.Connection, this) && Interlocked.CompareExchange(ref request.ReplyStamped, 1, 0) == 0)
        {
            received = request.ReceivedAt; replyId = request.Id;
        }
        _frames.Enqueue(new(bytes, replyId, received));
        Interlocked.Increment(ref _enqueuedFrames);
    }
    public void Sending() => _frames.TryDequeue(out _sending);
    public void Sent(int bytes, int expected)
    {
        var e = owner.Current;
        Interlocked.Increment(ref e.SocketSends);
        Interlocked.Add(ref e.BytesOut, bytes);
        Interlocked.Add(ref _sentBytes, bytes);
        if (bytes == expected)
        {
            Interlocked.Increment(ref e.FramesOut);
            if (_sending.ReceivedAt != 0) e.Message(_sending.ReplyId).WireReplyMs.RecordSince(_sending.ReceivedAt);
        }
        else
        {
            Interlocked.Increment(ref e.PartialSends);
            Volatile.Write(ref _sendFailure, new IOException("Partial socket send"));
        }
        Release(_sending.Bytes);
        _sending = default;
        Interlocked.Increment(ref _completedFrames);
        Interlocked.Exchange(ref _sendProgress, null)?.TrySetResult();
    }
    /// <summary>
    /// One coalesced write of <paramref name="frames"/> whole queued frames, fully accepted by the socket in
    /// <paramref name="calls"/> send calls. The transport finishes partial sends itself, so they are counted, not failures.
    /// </summary>
    public void SentBatch(int frames, int bytes, int calls, int partial)
    {
        var e = owner.Current;
        Interlocked.Add(ref e.SocketSends, calls);
        Interlocked.Add(ref e.PartialSends, partial);
        Interlocked.Add(ref e.BytesOut, bytes);
        Interlocked.Add(ref _sentBytes, bytes);
        for (int i = 0; i < frames; i++)
        {
            if (!_frames.TryDequeue(out var frame)) break;
            Interlocked.Increment(ref e.FramesOut);
            if (frame.ReceivedAt != 0) e.Message(frame.ReplyId).WireReplyMs.RecordSince(frame.ReceivedAt);
            Release(frame.Bytes);
            Interlocked.Increment(ref _completedFrames);
        }
        Interlocked.Exchange(ref _sendProgress, null)?.TrySetResult();
    }
    public void WriterFailed()
    {
        Interlocked.Increment(ref owner.Current.WriterErrors);
        Volatile.Write(ref _sendFailure, new IOException("Socket writer failed"));
        Interlocked.Exchange(ref _sendProgress, null)?.TrySetResult();
    }
    public void Closed()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        owner.EgressDelta(-Math.Max(0, Interlocked.Exchange(ref _currentBytes, -1)));
        owner.Disconnect(id);
        Interlocked.Exchange(ref _sendProgress, null)?.TrySetResult();
    }
    /// <summary>Optional bot-only send barrier, driven by existing transport counting hooks. Production never calls
    /// this method. It waits for all frames already enqueued, and fails on short writes, closure or deadline.</summary>
    public async Task WaitForDrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        long target = Volatile.Read(ref _enqueuedFrames);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            while (true)
            {
                if (Volatile.Read(ref _sendFailure) is { } failure) throw new IOException("Socket send did not complete", failure);
                if (Volatile.Read(ref _closed) != 0) throw new IOException("Connection closed before send completed");
                if (Volatile.Read(ref _completedFrames) >= target) return;
                var signal = Volatile.Read(ref _sendProgress);
                if (signal == null)
                {
                    var fresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    signal = Interlocked.CompareExchange(ref _sendProgress, fresh, null) ?? fresh;
                }
                // Register before rechecking counters: completion cannot disappear between check and await.
                if (Volatile.Read(ref _completedFrames) >= target || Volatile.Read(ref _sendFailure) != null || Volatile.Read(ref _closed) != 0) continue;
                await signal.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Socket send deadline expired", e); }
    }
    private void Release(int bytes)
    {
        long old = Volatile.Read(ref _currentBytes);
        while (old > 0)
        {
            long delta = Math.Min(old, bytes);
            long previous = Interlocked.CompareExchange(ref _currentBytes, old - delta, old);
            if (previous == old) { owner.EgressDelta(-delta); break; }
            old = previous;
        }
    }
}
