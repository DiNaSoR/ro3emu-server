using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Diagnostics;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>Two bounded read-only workers. Each result is detached before its short snapshot ends.</summary>
public sealed class DbReadPool : IDisposable
{
    public const int WorkerCount = 2, MaxWaitingJobs = 64;
    private readonly object _gate = new();
    private readonly Queue<Job> _queue = new();
    private readonly Thread[] _threads = new Thread[WorkerCount];
    private readonly TaskCompletionSource[] _exited = new TaskCompletionSource[WorkerCount];
    private readonly string _connectionString;
    private readonly DbAccessMetrics _metrics;
    private readonly ServerMetrics? _serverMetrics;
    private readonly ILogger? _log;
    private readonly TimeSpan _stopTimeout;
    private readonly DbReadPlan?[] _active = new DbReadPlan?[WorkerCount];
    private int _waiting;
    private bool _stopping;
    private Exception? _terminalError;

    private sealed record Job(DbReadPlan Plan, TaskCompletionSource<object?> Done);

    public DbReadPool(string databasePath, DbAccessMetrics metrics, ServerMetrics? serverMetrics = null, ILogger? log = null,
        TimeSpan? stopTimeout = null)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        _metrics = metrics; _serverMetrics = serverMetrics; _log = log; _stopTimeout = stopTimeout ?? TimeSpan.FromSeconds(5);
        for (int i = 0; i < WorkerCount; i++)
        {
            int worker = i;
            _exited[i] = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _threads[i] = new Thread(() => Run(worker)) { IsBackground = true, Name = $"ro3-db-read-{i}" };
            using (ExecutionContext.SuppressFlow()) _threads[i].Start();
        }
    }

    public Task WorkersExited => Task.WhenAll(_exited.Select(x => x.Task));

    public Task<T> ExecuteAsync<T>(DbReadPlan plan) => Cast<T>(Enqueue(plan));

    private Task<object?> Enqueue(DbReadPlan plan)
    {
        var done = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_stopping)
            {
                _metrics.ReaderRefused();
                return Task.FromException<object?>(_terminalError is { } error
                    ? new InvalidOperationException("Database reader pool failed and cannot admit more work.", error)
                    : new ObjectDisposedException(nameof(DbReadPool)));
            }
            if (_waiting >= MaxWaitingJobs)
            {
                _metrics.ReaderRefused();
                return Task.FromException<object?>(new DbQueueFullException("Database reader admission closed or full."));
            }
            _waiting++;
            _metrics.ReaderAdmitted();
            _queue.Enqueue(new(plan, done));
            Monitor.Pulse(_gate);
        }
        return done.Task;
    }

    private static async Task<T> Cast<T>(Task<object?> task) => (T)(await task.ConfigureAwait(false))!;

    // Bypass the async result-cast continuation for callers that already block on the read worker.
    internal T ExecuteLegacySync<T>(DbReadPlan plan) => (T)Enqueue(plan).GetAwaiter().GetResult()!;

    private void Run(int worker)
    {
        try
        {
            using var db = new SqliteConnection(_connectionString);
            using (var measurement = _serverMetrics?.MeasureDatabaseOpen()) db.Open();
            SqliteMetrics.Attach(db, _serverMetrics);
            using (var queryOnly = db.CreateCommand()) { queryOnly.CommandText = "PRAGMA query_only=ON"; queryOnly.ExecuteNonQuery(); }
            DbWriteGuard.AttachRead(db, _metrics, "reader/snapshot");
            while (true)
            {
                Job job;
                lock (_gate)
                {
                    while (_queue.Count == 0 && !_stopping) Monitor.Wait(_gate);
                    if (_queue.Count == 0) break;
                    job = _queue.Dequeue(); _waiting--; _active[worker] = job.Plan;
                }
                bool success = false;
                object? result = null;
                Exception? error = null;
                try
                {
                    DbWriteGuard.ReadPlan(db, job.Plan.Label);
                    using var tx = db.BeginTransaction(deferred: true);
                    result = job.Plan.Execute(db, tx);
                    tx.Commit();
                    success = true;
                }
                catch (Exception caught) { error = caught; }
                finally
                {
                    lock (_gate) _active[worker] = null;
                    _metrics.ReaderFinished(success);
                }
                if (error is null) job.Done.TrySetResult(result);
                else job.Done.TrySetException(error);
            }
        }
        catch (Exception error)
        {
            _log?.LogError(error, "Database reader worker failed; queued snapshots will fail");
            lock (_gate)
            {
                _stopping = true; _terminalError = error;
                while (_queue.TryDequeue(out var job))
                {
                    job.Done.TrySetException(error);
                    _metrics.ReaderFinished(false);
                }
                _waiting = 0;
                Monitor.PulseAll(_gate);
            }
        }
        finally { _exited[worker].TrySetResult(); }
    }

    public void Dispose()
    {
        lock (_gate) { _stopping = true; Monitor.PulseAll(_gate); }
        if (!_threads.Contains(Thread.CurrentThread) && !WorkersExited.Wait(_stopTimeout))
        {
            _metrics.WorkerStopTimedOut(writer: false);
            (int active, int waiting, int bytes, string labels) debt;
            lock (_gate)
                debt = (_active.Count(x => x != null), _waiting,
                    _active.Where(x => x != null).Sum(x => x!.Bytes) + _queue.Sum(x => x.Plan.Bytes),
                    string.Join(",", _active.Where(x => x != null).Cast<DbReadPlan>()
                    .Concat(_queue.Select(x => x.Plan))
                    .Select(x => x.Label.Length > 64 ? x.Label[..64] : x.Label).Distinct().Take(8)));
            _log?.LogError("Database reader shutdown timed out with activeJobs={ActiveJobs}, queuedJobs={QueuedJobs}, pendingBytes={PendingBytes}, labels={Labels}; active snapshots retain worker connections until completion",
                debt.active, debt.waiting, debt.bytes, debt.labels);
        }
    }
}
