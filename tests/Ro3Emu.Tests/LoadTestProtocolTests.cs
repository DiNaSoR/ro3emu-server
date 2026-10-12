using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Ro3Emu.Bot;
using Ro3Emu.Server;

namespace Ro3Emu.Tests;

public sealed class LoadTestProtocolTests
{
    [Fact]
    public async Task Typed_expectation_reports_reply_time_from_registration_once()
    {
        var inbox = new BotInbox();
        var observed = new List<(uint Id, double Ms)>();
        inbox.OnExpectedReply = (e, ms) => observed.Add((e.MsgId, ms));
        inbox.ConfigureBounded(4, _ => { });
        using var expected = inbox.Expect(e => e.IsGame(42));
        await Task.Delay(20);
        inbox.Add(new BotEvent(BotEventKind.Game) { MsgId = 41 });
        inbox.Add(new BotEvent(BotEventKind.Game) { MsgId = 42 });
        Assert.Equal((uint)42, (await expected.WaitAsync(TimeSpan.FromSeconds(1))).MsgId);
        Assert.Single(observed);
        Assert.Equal((uint)42, observed[0].Id);
        Assert.True(observed[0].Ms >= 10, $"observed {observed[0].Ms} ms");
    }

    [Fact]
    public async Task Historical_inbox_delivery_does_not_invoke_typed_reply_hook()
    {
        var inbox = new BotInbox();
        int callbackCount = 0;
        inbox.OnExpectedReply = (_, _) => callbackCount++;
        inbox.Add(new BotEvent(BotEventKind.Game) { MsgId = 9 });
        Assert.Equal((uint)9, (await inbox.WaitAsync(e => e.IsGame(9), TimeSpan.FromSeconds(1))).MsgId);
        Assert.Equal(0, callbackCount);
    }

    [Fact]
    public async Task Failed_sdk_stage_finishes_once_with_error()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var bot = new BotClient($"http://127.0.0.1:{port}", "");
        var started = new List<string>();
        var finished = new List<(string Name, double Ms, Exception? Error)>();
        bot.OnStageStarted = started.Add;
        bot.OnStageFinished = (name, ms, error) => finished.Add((name, ms, error));
        await Assert.ThrowsAnyAsync<Exception>(() => bot.LoginAsync("missing", "missing"));
        Assert.Equal(["sdk-login"], started);
        Assert.Single(finished);
        Assert.Equal("sdk-login", finished[0].Name);
        Assert.True(finished[0].Ms >= 0);
        Assert.NotNull(finished[0].Error);
        Assert.Equal(0, bot.WireInboundBytes);
    }

    [Fact]
    public async Task Rejected_create_role_keeps_default_response_and_marks_load_stage_refused_over_wire()
    {
        var dataDir = Path.Combine(RepoRoot(), ".loadtest-temp", "load-refuse-" + Guid.NewGuid().ToString("N"));
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        var app = ServerHost.Create([], o =>
        {
            o.DataDir = dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
        });
        await app.StartAsync();
        try
        {
            var url = $"http://127.0.0.1:{httpPort}";
            var pem = File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem"));
            await using (var normal = new BotClient(url, pem))
            {
                await normal.LoginAsync("load_refuse_normal", "pw");
                await normal.EnterGateAsync();
                var response = await normal.CreateRoleAsync("X"); // server rejects a one-character role name
                Assert.NotEqual(0, response.Result);
                Assert.Empty(normal.Roles);
            }
            await using (var load = new BotClient(url, pem))
            {
                load.EnableLoadMode(32, _ => { });
                var finished = new List<(string Name, Exception? Error)>();
                load.OnStageFinished = (name, _, error) => finished.Add((name, error));
                await load.LoginAsync("load_refuse_measured", "pw");
                await load.EnterGateAsync();
                var refused = await Assert.ThrowsAsync<BotRefusedException>(() => load.CreateRoleAsync("X"));
                Assert.Equal("create-role", refused.Operation);
                Assert.NotEqual("0", refused.Result);
                Assert.Empty(load.Roles);
                var stage = Assert.Single(finished, x => x.Name == "create-role");
                Assert.Same(refused, stage.Error);
            }
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dataDir, true); } catch (IOException) { }
        }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
