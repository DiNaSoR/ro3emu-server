using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Ro3Emu.Server.Diagnostics;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class ServerMetricsTests
{
    [Fact]
    public async Task ConcurrentRuntimeMetricOwnersDoNotReconfigureOrDeadlockEventPipe()
    {
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 32; i++)
            {
                using var metrics = new ServerMetrics();
                metrics.Reset();
                if (i % 8 == 0) GC.Collect(0, GCCollectionMode.Optimized, blocking: false);
            }
        }))).WaitAsync(TimeSpan.FromSeconds(30));
    }
    private static JsonElement Snapshot(ServerMetrics metrics) => JsonSerializer.SerializeToElement(metrics.Snapshot(), new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public void HistogramKeepsExactCountsAndConservativeQuantilesUnderContention()
    {
        var histogram = new FixedHistogram([1, 5, 10, 100]);
        Parallel.For(0, 10000, i => histogram.Record(i % 10 + 1));
        var snapshot = histogram.Snapshot();
        Assert.Equal(10000, snapshot.Count);
        Assert.Equal(55000, snapshot.Sum);
        Assert.Equal(1, snapshot.Min);
        Assert.Equal(10, snapshot.Max);
        Assert.Equal(5, snapshot.P50);
        Assert.Equal(10, snapshot.P99);
        Assert.Equal([1000L, 4000L, 5000L, 0L], snapshot.Buckets);
        Assert.Throws<ArgumentOutOfRangeException>(() => histogram.Record(double.NaN));
        Assert.Throws<ArgumentException>(() => new FixedHistogram([2, 1]));
    }

    [Fact]
    public void ResetExchangesCountersWithoutMakingOutstandingEgressNegative()
    {
        using var metrics = new ServerMetrics();
        var connection = metrics.Connect();
        connection.Enqueued(120);
        connection.Outgoing(55, 100);
        var before = Snapshot(metrics);
        metrics.Reset();
        var reset = Snapshot(metrics);
        Assert.NotEqual(before.GetProperty("epoch").GetString(), reset.GetProperty("epoch").GetString());
        Assert.Equal(120, reset.GetProperty("wire").GetProperty("currentEgressQueueBytes").GetInt64());
        Assert.Empty(reset.GetProperty("outbound").EnumerateObject());
        connection.Sending(); connection.Sent(120, 120);
        connection.Closed(); connection.Closed(); connection.Enqueued(10);
        Assert.Equal(0, Snapshot(metrics).GetProperty("wire").GetProperty("currentEgressQueueBytes").GetInt64());
    }

    [Fact]
    public async Task ReplyLatencyIncludesQueueingUntilSocketCompletionAndExcludesOtherRecipients()
    {
        using var metrics = new ServerMetrics();
        var requester = metrics.Connect();
        var viewer = metrics.Connect();
        long received = Stopwatch.GetTimestamp();
        await Task.Delay(15);
        using (var request = metrics.BeginRequest(requester, 42, received))
        {
            viewer.Outgoing(42, 10); viewer.Enqueued(16);
            requester.Outgoing(41, 10); requester.Enqueued(16);
            requester.Outgoing(42, 10); requester.Enqueued(16);
            request.Complete();
        }
        Assert.Equal(0, Snapshot(metrics).GetProperty("inbound").GetProperty("42").GetProperty("wireReplies").GetInt64());
        await Task.Delay(15);
        viewer.Sending(); viewer.Sent(16, 16);
        requester.Sending(); requester.Sent(16, 16);
        requester.Sending(); requester.Sent(16, 16);
        var inbound = Snapshot(metrics).GetProperty("inbound").GetProperty("42");
        Assert.Equal(1, inbound.GetProperty("wireReplies").GetInt64());
        Assert.True(inbound.GetProperty("wireReplyMs").GetProperty("max").GetDouble() >= 20);
        Assert.Equal(1, inbound.GetProperty("completed").GetInt64());
        requester.Closed(); viewer.Closed();
    }

    [Fact]
    public void APartialSocketSendIsCountedButNeverCompletesWireLatency()
    {
        using var metrics = new ServerMetrics();
        var connection = metrics.Connect();
        using (var request = metrics.BeginRequest(connection, 42, Stopwatch.GetTimestamp()))
        {
            connection.Outgoing(42, 20); connection.Enqueued(26); request.Complete();
        }
        connection.Sending(); connection.Sent(3, 26);
        var snapshot = Snapshot(metrics);
        Assert.Equal(1, snapshot.GetProperty("wire").GetProperty("partialSends").GetInt64());
        Assert.Equal(3, snapshot.GetProperty("wire").GetProperty("bytesOut").GetInt64());
        Assert.Equal(0, snapshot.GetProperty("inbound").GetProperty("42").GetProperty("wireReplies").GetInt64());
        connection.Closed();
    }

    [Fact]
    public async Task OptionalBotBarrierWaitsForWireProgressAndFailsOnAStall()
    {
        using var metrics = new ServerMetrics(observeRuntime: false);
        var connection = metrics.Connect();
        connection.Enqueued(16);
        var drain = connection.WaitForDrainAsync(TimeSpan.FromSeconds(2));
        Assert.False(drain.IsCompleted);
        connection.Sending(); connection.Sent(16, 16);
        await drain;
        connection.Enqueued(8);
        await Assert.ThrowsAsync<TimeoutException>(() => connection.WaitForDrainAsync(TimeSpan.FromMilliseconds(30)));
        var closing = connection.WaitForDrainAsync(TimeSpan.FromSeconds(2));
        connection.Closed();
        await Assert.ThrowsAsync<IOException>(() => closing);
        connection.Enqueued(8); // legacy transport ignores a TryWrite after close; the barrier must still refuse it
        await Assert.ThrowsAsync<IOException>(() => connection.WaitForDrainAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task OptionalBotBarrierRejectsPartialSends()
    {
        using var metrics = new ServerMetrics(observeRuntime: false);
        var connection = metrics.Connect();
        connection.Enqueued(16);
        var drain = connection.WaitForDrainAsync(TimeSpan.FromSeconds(2));
        connection.Sending(); connection.Sent(5, 16);
        await Assert.ThrowsAsync<IOException>(() => drain);
        connection.Closed();
    }

    [Fact]
    public async Task SqliteTraceCoversBatchesCommitsAndTheActualCallingThread()
    {
        using var metrics = new ServerMetrics();
        using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        connection.Open();
        SqliteMetrics.Attach(connection, metrics);
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE test_metrics (n INTEGER);";
        command.ExecuteNonQuery();
        metrics.Reset();
        using (var tx = connection.BeginTransaction())
        {
            command.Transaction = tx;
            command.CommandText = "INSERT INTO test_metrics VALUES (1); INSERT INTO test_metrics VALUES (2);";
            command.ExecuteNonQuery();
            command.CommandText = "SELECT n FROM test_metrics;";
            using (var reader = command.ExecuteReader()) while (reader.Read()) { }
            tx.Commit();
        }
        var database = Snapshot(metrics).GetProperty("database");
        Assert.Equal(5, database.GetProperty("statements").GetInt64());
        Assert.Equal(1, database.GetProperty("commits").GetInt64());
        Assert.Equal(0, database.GetProperty("loopStatements").GetInt64());
        Assert.Equal(2, database.GetProperty("operations").GetProperty("test_metrics/INSERT").GetProperty("count").GetInt64());
        using var loop = new GameLoop(NullLogger<GameLoop>.Instance, metrics);
        await loop.StartAsync(default);
        await loop.InvokeAsync(() =>
        {
            command.Transaction = null;
            command.CommandText = "SELECT count(*) FROM test_metrics";
            Assert.Equal(2L, command.ExecuteScalar());
            return Task.CompletedTask;
        });
        await loop.StopAsync(default);
        database = Snapshot(metrics).GetProperty("database");
        Assert.Equal(1, database.GetProperty("loopStatements").GetInt64());
        Assert.Equal(0, database.GetProperty("profileErrors").GetInt64());
        Assert.Equal(2, database.GetProperty("operations").GetProperty("test_metrics/SELECT").GetProperty("threads").EnumerateObject().Count());
        var select = database.GetProperty("operations").GetProperty("test_metrics/SELECT");
        Assert.Equal(1, select.GetProperty("loopDurationMs").GetProperty("count").GetInt64());
        Assert.Equal(1, select.GetProperty("otherDurationMs").GetProperty("count").GetInt64());
        Assert.Equal(1, database.GetProperty("operations").GetProperty("transaction/COMMIT").GetProperty("commits").GetInt64());
    }

    [Fact]
    public void PooledSqliteConnectionIsReattributedToItsNewHost()
    {
        using var first = new ServerMetrics();
        using var second = new ServerMetrics();
        string connectionString = $"Data Source=metrics-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=True";
        void Query(ServerMetrics owner)
        {
            using var db = new SqliteConnection(connectionString);
            db.Open(); SqliteMetrics.Attach(db, owner);
            using var command = db.CreateCommand(); command.CommandText = "SELECT 1"; command.ExecuteScalar();
        }
        Query(first); Query(second);
        Assert.Equal(1, Snapshot(first).GetProperty("database").GetProperty("statements").GetInt64());
        Assert.Equal(1, Snapshot(second).GetProperty("database").GetProperty("statements").GetInt64());
        using var pool = new SqliteConnection(connectionString);
        SqliteConnection.ClearPool(pool);
    }

    [Fact]
    public void SqliteCountsReadOnlyCommitButNotRollbackAsACommit()
    {
        using var metrics = new ServerMetrics();
        using var db = new SqliteConnection("Data Source=:memory:;Pooling=False");
        db.Open(); SqliteMetrics.Attach(db, metrics);
        using var command = db.CreateCommand();
        command.CommandText = "BEGIN; SELECT 1; COMMIT; BEGIN; SELECT 2; ROLLBACK;";
        command.ExecuteNonQuery();
        var database = Snapshot(metrics).GetProperty("database");
        Assert.Equal(6, database.GetProperty("statements").GetInt64());
        Assert.Equal(1, database.GetProperty("commits").GetInt64());
    }

    [Fact]
    public async Task DueTimerFloodCannotStarveQueueAndItsLatenessIsPreserved()
    {
        using var metrics = new ServerMetrics();
        using var loop = new GameLoop(NullLogger<GameLoop>.Instance, metrics);
        int fired = 0;
        // Enqueue from a due timer itself: the queue must progress before all 256 deadlines have drained.
        var observed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        loop.Schedule(TimeSpan.FromMilliseconds(-1), () => { loop.Post(() => { observed.TrySetResult(fired); return Task.CompletedTask; }); return Task.CompletedTask; });
        for (int i = 0; i < 256; i++) loop.Schedule(TimeSpan.Zero, () => { fired++; return Task.CompletedTask; });
        await loop.StartAsync(default);
        int atQueuedTurn = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(atQueuedTurn < 256);
        for (int i = 0; i < 100 && Volatile.Read(ref fired) < 256; i++) await Task.Delay(20);
        Assert.Equal(256, fired);
        await loop.StopAsync(default);
        var loopMetrics = Snapshot(metrics).GetProperty("loop");
        Assert.True(loopMetrics.GetProperty("timerLatenessMs").GetProperty("max").GetDouble() >= 50);
        Assert.True(loopMetrics.GetProperty("cadenceMs").GetProperty("count").GetInt64() >= 4);
    }
}
