using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Micro;

namespace Ro3Emu.Tests;

/// <summary>Detached friend projections share durable owner revisions with game and store writers.</summary>
public sealed class SocialOwnerTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(RepoRoot(), ".loadtest-temp",
        "social-owner-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    private static string RepoRoot()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path != null; path = path.Parent)
            if (File.Exists(Path.Combine(path.FullName, "Ro3Emu.slnx"))) return path.FullName;
        throw new InvalidOperationException("repository root");
    }

    private async Task DisposeDetachedAsync(BotClient bot, long role)
    {
        var loop = Service<GameLoop>();
        var handover = Service<RoleHandover>();
        var selected = await loop.InvokeAsync(() => Task.FromResult(Service<SessionRegistry>().All()
            .Single(session => session.Role?.Id == role)));
        var detached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handover.PhaseForTest = (phase, session) =>
        {
            if (phase == "Detached" && ReferenceEquals(session, selected)) detached.TrySetResult();
        };
        try { await bot.DisposeAsync(); await detached.Task.WaitAsync(Wait); }
        finally { handover.PhaseForTest = null; }
    }

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<(BotClient Bot, long Id)> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        return (bot, id);
    }

    private static Dictionary<string, object?> Args(long self, string? key = null, long target = 0)
    {
        var args = new Dictionary<string, object?> { ["Uid"] = self.ToString() };
        if (key != null) args[key] = target.ToString();
        return args;
    }
    private static string[] Ids(Dictionary<object, object?> response, string field) =>
        ((object?[])response[field]!).Select(value => value is Dictionary<object, object?> row
            ? (string)row["uid"]! : (string)value!).ToArray();

    [Fact]
    public async Task OfflineOwnerAndExternalFriendWriteInvalidateWarmProjection()
    {
        var (alice, a) = await InWorld("OwnerAlice");
        var (bob, b) = await InWorld("OwnerBob");
        await using var _ = alice;
        await DisposeDetachedAsync(bob, b);
        Assert.Equal(RoleOwnerState.Detached,
            await Service<GameLoop>().InvokeAsync(() => Task.FromResult(Service<OwnerRegistry>().Cached(b)?.State)));

        var cache = Service<SocialOwnerCache>();
        Assert.Empty(Ids(await alice.MicroAsync("FriendService", "fetchApplicants", Args(b)), "Applicants"));
        long loaded = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(cache.SocialSnapshotLoads));
        Assert.Empty(Ids(await alice.MicroAsync("FriendService", "fetchApplicants", Args(b)), "Applicants"));
        Assert.Equal(loaded, await Service<GameLoop>().InvokeAsync(() => Task.FromResult(cache.SocialSnapshotLoads)));

        var owners = Service<OwnerRegistry>();
        await Service<GameLoop>().InvokeAsync(() => owners.RunAsync("test/external-friend",
            [ReservationKey.Role(a), ReservationKey.Role(b)], () => Service<FriendStore>().ApplyAsync(b, a)));
        Assert.Equal([a.ToString()], Ids(await alice.MicroAsync("FriendService", "fetchApplicants", Args(b)), "Applicants"));
        var accepted = await alice.MicroAsync("FriendService", "accept", Args(b, "Applicant", a));
        Assert.Equal(a.ToString(), accepted["Applicant"]);
        Assert.Equal([b.ToString()], Ids(await alice.MicroAsync("FriendService", "fetchFriendsInfo", Args(a)), "Friends"));
        Assert.Equal([a.ToString()], Ids(await alice.MicroAsync("FriendService", "fetchFriendsInfo", Args(b)), "Friends"));
    }

    [Fact]
    public async Task FriendReadWaitsForCommittedApplyAndThenSeesBothOwnerProjections()
    {
        var (alice, a) = await InWorld("RaceAlice");
        var (bob, b) = await InWorld("RaceBob");
        var (reader, _) = await InWorld("RaceReader");
        await using var aliceLease = alice;
        await using var bobLease = bob;
        await using var readerLease = reader;
        Assert.Empty(Ids(await reader.MicroAsync("FriendService", "fetchApplicants", Args(b)), "Applicants"));
        var accounts = Service<AccountStore>();
        var owners = Service<OwnerRegistry>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        accounts.BeforeOwnedApplyForTest = label =>
        {
            if (label != "micro/friend/apply") return Task.CompletedTask;
            entered.TrySetResult(); return release.Task;
        };
        owners.DeferredForTest = origin => { if (origin == "micro/friend/read") deferred.TrySetResult(); };
        Task<Dictionary<object, object?>>? query = null;
        try
        {
            var apply = alice.MicroAsync("FriendService", "apply", Args(a, "Target", b));
            await entered.Task.WaitAsync(Wait);
            query = reader.MicroAsync("FriendService", "fetchApplicants", Args(b));
            await deferred.Task.WaitAsync(Wait);
            Assert.False(query.IsCompleted);
            release.TrySetResult();
            await apply.WaitAsync(Wait);
            Assert.Equal([a.ToString()], Ids(await query.WaitAsync(Wait), "Applicants"));
        }
        finally
        {
            release.TrySetResult(); accounts.BeforeOwnedApplyForTest = null; owners.DeferredForTest = null;
        }
    }

    [Fact]
    public async Task WriterReceiptPreservesBlacklistOrderingAndSelfBlockOnWarmReads()
    {
        var (alice, a) = await InWorld("ListAlice");
        var (bob, b) = await InWorld("ListBob");
        var (charlie, c) = await InWorld("ListCharlie");
        await using var aliceLease = alice;
        await using var bobLease = bob;
        await using var charlieLease = charlie;
        await alice.MicroAsync("FriendService", "addBlacklist", Args(a, "Target", c));
        await alice.MicroAsync("FriendService", "addBlacklist", Args(a, "Target", b));
        await alice.MicroAsync("FriendService", "addBlacklist", Args(a, "Target", a));
        var warm = Ids(await alice.MicroAsync("FriendService", "fetchBlacklist", Args(a)), "Blacklist");
        Assert.Equal(Service<FriendStore>().Blacklist(a).Select(id => id.ToString()), warm);
        Assert.Equal([a.ToString()], Ids(await alice.MicroAsync("FriendService", "fetchBeBlacklist", Args(a)), "BeBlacklist"));
        await Service<GameLoop>().InvokeAsync(() => Service<SocialOwnerCache>().LoadSocialAsync([a], force: true));
        Assert.Equal(warm, Ids(await alice.MicroAsync("FriendService", "fetchBlacklist", Args(a)), "Blacklist"));
    }

    [Fact]
    public async Task DeletedApplicantStopsBulkAfterCommittedPrefixAndTombstoneFetchKeepsRows()
    {
        var (alice, a) = await InWorld("BulkAlice");
        var (valid, v) = await InWorld("ValidDonor");
        var (deleted, d) = await InWorld("DeletedDonor");
        await using var aliceLease = alice;
        await using var validLease = valid;
        await valid.MicroAsync("FriendService", "apply", Args(v, "Target", a));
        await valid.MicroAsync("FriendService", "apply", Args(v, "Target", d));
        await deleted.MicroAsync("FriendService", "apply", Args(d, "Target", a));
        Assert.Equal([v.ToString(), d.ToString()],
            Ids(await alice.MicroAsync("FriendService", "fetchApplicants", Args(a)), "Applicants"));

        await DisposeDetachedAsync(deleted, d);
        using (var http = new HttpClient())
        using (var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["identity"] = "deleteddonor", ["credential"] = "pw", ["role"] = d.ToString(),
        }))
        {
            var response = await http.PostAsync(_http + "/ro3emu/account/delete-role", form);
            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(0, body.RootElement.GetProperty("code").GetInt32());
        }
        Assert.Equal([v.ToString()],
            Ids(await alice.MicroAsync("FriendService", "fetchApplicants", Args(d)), "Applicants"));

        await Assert.ThrowsAsync<OwnerRevisionConflictException>(async () =>
        {
            await Service<FriendService>().InvokeAsync(null!, "refuseAll",
                new Dictionary<object, object?> { ["Uid"] = a.ToString() });
        });
        Assert.Equal([d.ToString()], Service<FriendStore>().Applicants(a).Select(id => id.ToString()));
        Assert.Equal([d.ToString()],
            Ids(await alice.MicroAsync("FriendService", "fetchApplicants", Args(a)), "Applicants"));
    }
}
