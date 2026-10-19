using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// 30012 free travel on a host where players are not GM: only to a map the role has been to. Tests and local play
/// (everyone GM) keep travelling anywhere.
/// </summary>
public sealed class TravelTrustTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [ClientDataFact]
    public async Task APlayerTravelsOnlyToMapsItHasBeenTo()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-travel-" + Guid.NewGuid().ToString("N"));
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        var app = ServerHost.Create(["--environment", "Production"], o =>
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
            await using var alice = new BotClient($"http://127.0.0.1:{httpPort}", File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")));
            await alice.LoginAsync("alice", "pw");
            await alice.EnterGateAsync();
            await alice.EnterWorldAsync((await alice.CreateRoleAsync("Alice")).RoleBaseInfo.Id);
            var session = app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice");
            var loop = app.Services.GetRequiredService<GameLoop>();
            const long town = 10100;
            Assert.False(await loop.InvokeAsync(() => Task.FromResult(session.WorldState.Saved.VisitedMaps.Contains(town))));

            await alice.SendGameAsync(SceneService.SceneTeleport, new SceneTeleportReq { TargetMapId = town });
            Assert.Equal(ErrorCodes.FeatureUnavailable, (await Next(alice)).Result);

            await loop.InvokeAsync(() => { session.WorldState.Saved.VisitedMaps.Add(town); return Task.CompletedTask; });
            await alice.SendGameAsync(SceneService.SceneTeleport, new SceneTeleportReq { TargetMapId = town });
            Assert.Equal(0, (await Next(alice)).Result);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dataDir, true); } catch (IOException) { }
        }
    }

    private static async Task<SceneTeleportRsp> Next(BotClient bot) =>
        SceneTeleportRsp.Parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(SceneService.SceneTeleport), Wait)).Body);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
