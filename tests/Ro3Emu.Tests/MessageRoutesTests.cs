using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Messaging;

namespace Ro3Emu.Tests;

/// <summary>Audit F4.1: one owner per game message id, checked when the table is built.</summary>
public sealed class MessageRoutesTests
{
    private static Task Noop(GameSession s, uint id, ReadOnlyMemory<byte> b) => Task.CompletedTask;

    [Fact]
    public void ADuplicateIdOrOverlappingRangeIsRefusedWhenTheTableIsBuilt()
    {
        var routes = new MessageRoutes();
        routes.Map(100, "a", Noop);
        routes.MapRange(200, 210, "b", Noop);
        Assert.Throws<InvalidOperationException>(() => routes.Map(100, "c", Noop));
        Assert.Throws<InvalidOperationException>(() => routes.Map(205, "c", Noop));
        Assert.Throws<InvalidOperationException>(() => routes.MapRange(95, 105, "c", Noop));
        Assert.Throws<InvalidOperationException>(() => routes.MapRange(210, 220, "c", Noop));
        Assert.True(routes.TryGet(205, out var range) && range.Owner == "b");
        Assert.False(routes.TryGet(300, out _));
    }

    [Fact]
    public async Task TheServersTableHasEveryFormerSwitchArmOnce()
    {
        var (hp, mp, gp) = TestPorts.FreePorts();
        string dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-routes-" + Guid.NewGuid().ToString("N"));
        var app = ServerHost.Create([], o =>
        {
            o.DataDir = dataDir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = hp; o.MicroGatePort = mp; o.GameGatePort = gp;
        });
        try
        {
            var routes = app.Services.GetRequiredService<GameMessageHandler>().Routes; // throws on any duplicate
            Assert.True(routes.Exact.Count >= 347, $"{routes.Exact.Count} exact routes");
            Assert.True(routes.TryGet(MsgId.SceneLoaded, out var loaded) && loaded.Owner == "scene");
            Assert.True(routes.TryGet(31183, out var wardrobe) && wardrobe.Owner == "wardrobe");
            Assert.True(routes.TryGet(82365, out var totem) && totem.Owner == "totems");
            Assert.Equal(totem.From, totem.To);
            Assert.True(routes.TryGet(82813, out var genre) && genre.Owner == "genre");
            Assert.Equal(genre.From, genre.To);
        }
        finally
        {
            await app.DisposeAsync();
            try { Directory.Delete(dataDir, true); } catch (IOException) { }
        }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new InvalidOperationException("repo root not found");
    }
}
