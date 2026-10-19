using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Ro3Emu.Server.Net;

namespace Ro3Emu.Tests;

/// <summary>Directed player visibility with the client's own cap (QS_LodLevelCtrl 39/49/79/99/149).</summary>
public sealed class AoiCapTests
{
    private sealed class RecordingSink : IAoiSink
    {
        public List<string> Events { get; } = new();
        public Task EnterAsync(GameSession viewer, GameSession subject) => Add($"{viewer.Role!.Name}+{subject.Role!.Name}");
        public Task MoveAsync(GameSession viewer, GameSession subject, WalkReq walk) => Add($"{viewer.Role!.Name}~{subject.Role!.Name}");
        public Task TurnAsync(GameSession viewer, GameSession subject, float face) => Add($"{viewer.Role!.Name}^{subject.Role!.Name}");
        public Task LeaveAsync(GameSession viewer, GameSession subject) => Add($"{viewer.Role!.Name}-{subject.Role!.Name}");
        public Task UnitsChangedAsync(GameSession viewer, IReadOnlyList<WorldUnit> entered, IReadOnlyList<long> left) => Task.CompletedTask;
        public Task UnitMoveAsync(GameSession viewer, MovementChangeNtf movement) => Task.CompletedTask;
        private Task Add(string e) { lock (Events) Events.Add(e); return Task.CompletedTask; }
    }

    private readonly RecordingSink sink = new();
    private readonly World world;
    private int nextId = 1;

    public AoiCapTests() => world = new World(sink, new GameLoop(NullLogger<GameLoop>.Instance), NullLogger<World>.Instance);

    private GameSession Player(string name, float x, float z, int limit = World.MaxViewLimit)
    {
        var channel = new PhonestChannel(new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp), NullLogger.Instance);
        return new GameSession(channel, null!)
        {
            Role = new Role(nextId++, "acc", 1, name, 1, 1000, 1, 0, 0, 0, 10111, x, 0, z, 0),
            SceneEntry = 10111,
            Position = new Vec3 { X = x, Z = z },
            ViewLimit = limit,
        };
    }

    private static string Names(IEnumerable<GameSession> players) => string.Join(",", players.Select(p => p.Role!.Name).Order());

    [Fact]
    public async Task LowCapViewerShowsTheNearestWhileEveryoneStillSeesIt()
    {
        var others = new[] { Player("B", 105, 100), Player("C", 110, 100), Player("D", 120, 100), Player("E", 130, 100) };
        foreach (var o in others) await world.JoinAsync(o);
        var low = Player("A", 100, 100, limit: 2);
        await world.JoinAsync(low);

        Assert.Equal("B,C", Names(low.Visible));                 // the two nearest
        Assert.Equal("B,C,D,E", Names(low.Observers));           // directed: all of them still show A
        Assert.Equal("B,C,D,E", Names(world.Viewers(low)));      // broadcasts about A reach its observers
        Assert.All(others, o => Assert.Contains(low, o.Visible));
        Assert.DoesNotContain("A+D", sink.Events);
        Assert.Contains("D+A", sink.Events);
    }

    [Fact]
    public async Task FullViewSwapsItsFarthestForANearerPlayerBeyondTheMargin()
    {
        var b = Player("B", 125, 100);
        var c = Player("C", 130, 100);
        foreach (var o in new[] { b, c }) await world.JoinAsync(o);
        var low = Player("A", 100, 100, limit: 2);
        await world.JoinAsync(low);
        var near = Player("D", 101, 100);   // arrives while A is full: A only gets its name board
        var edge = Player("E", 126, 100);   // nearer than C, but within the margin of B once C is gone
        await world.JoinAsync(near);
        await world.JoinAsync(edge);
        Assert.Equal("B,C", Names(low.Visible));
        sink.Events.Clear();

        Assert.Equal(1, await world.RebalanceAsync(_ => true));
        Assert.Equal("B,D", Names(low.Visible));
        Assert.Equal(new[] { "A-C", "A+D" }, sink.Events.ToArray());
        Assert.Contains(low, near.Observers);
        Assert.DoesNotContain(low, c.Observers);

        // Stable afterwards: E (26) is not 5 units nearer than B (25), and nobody else qualifies.
        sink.Events.Clear();
        Assert.Equal(0, await world.RebalanceAsync(_ => true));
        Assert.Empty(sink.Events);
    }

    [Fact]
    public async Task HiddenPlayerSeesWithoutBeingSeen()
    {
        var a = Player("A", 100, 100);
        var b = Player("B", 110, 100);
        var far = Player("F", 400, 100);
        foreach (var p in new[] { a, b, far }) await world.JoinAsync(p);
        sink.Events.Clear();

        await world.SetHiddenAsync(a, true);
        Assert.Equal(new[] { "B-A" }, sink.Events.ToArray());   // B loses A, A keeps showing B
        Assert.Equal("B", Names(a.Visible));
        Assert.Empty(a.Observers);

        // A's view moves to F: it shows F, F never shows A.
        sink.Events.Clear();
        a.Position = new Vec3 { X = 405, Z = 100 };
        await world.MoveAsync(a, new WalkReq { Pos = a.Position, BStop = true, SpeedRate = 1 }, forceTeleport: true);
        Assert.Equal("F", Names(a.Visible));
        Assert.Empty(far.Visible);
        Assert.DoesNotContain("F+A", sink.Events);

        // Shown again where it stands.
        sink.Events.Clear();
        await world.SetHiddenAsync(a, false);
        Assert.Equal(new[] { "F+A" }, sink.Events.ToArray());
        Assert.Contains(a, far.Visible);
    }

    [Fact]
    public async Task RebalanceLeavesViewsWithRoomAlone()
    {
        var a = Player("A", 100, 100, limit: 3);
        var b = Player("B", 140, 100);
        await world.JoinAsync(a);
        await world.JoinAsync(b);
        Assert.Equal(0, await world.RebalanceAsync(_ => true));
        Assert.Equal("B", Names(a.Visible));
    }

    [Fact]
    public async Task ViewerRefillsWhenAShownPlayerLeaves()
    {
        var b = Player("B", 105, 100);
        var c = Player("C", 110, 100);
        var d = Player("D", 120, 100);
        foreach (var o in new[] { b, c, d }) await world.JoinAsync(o);
        var low = Player("A", 100, 100, limit: 2);
        await world.JoinAsync(low);
        sink.Events.Clear();

        await world.LeaveAsync(b);
        Assert.Equal("C,D", Names(low.Visible));
        // The leaver's observers are told first, then the viewer that had a free slot takes the next nearest.
        Assert.Equal(4, sink.Events.Count);
        Assert.Equal(new[] { "A-B", "C-B", "D-B" }, sink.Events.Take(3).Order().ToArray());
        Assert.Equal("A+D", sink.Events[^1]);

        // Walking away from C: C leaves both ways, A refills with nobody new (D is still shown).
        sink.Events.Clear();
        c.Position = new Vec3 { X = 300, Z = 100 };
        await world.MoveAsync(c, new WalkReq { Pos = c.Position });
        Assert.Equal("D", Names(low.Visible));
        Assert.DoesNotContain(low, c.Observers);
    }

    [Fact]
    public async Task ChangingTheCapDropsTheFarthestAndRaisingItRefills()
    {
        var players = new[] { Player("B", 105, 100), Player("C", 110, 100), Player("D", 120, 100) };
        foreach (var p in players) await world.JoinAsync(p);
        var a = Player("A", 100, 100);
        await world.JoinAsync(a);
        Assert.Equal("B,C,D", Names(a.Visible));

        sink.Events.Clear();
        await world.SetViewLimitAsync(a, 1);
        Assert.Equal("B", Names(a.Visible));
        Assert.Equal(new[] { "A-D", "A-C" }, sink.Events);
        Assert.Equal("B,C,D", Names(a.Observers)); // others' views are not the viewer's business

        sink.Events.Clear();
        await world.SetViewLimitAsync(a, 149);
        Assert.Equal("B,C,D", Names(a.Visible));
        Assert.Equal(new[] { "A+C", "A+D" }, sink.Events);

        await world.SetViewLimitAsync(a, 0); // absent: keep the current cap
        Assert.Equal(149, a.ViewLimit);
    }

    [Fact]
    public async Task GridFindsPlayersAcrossCellBordersAndNotBeyondTheLeaveRadius()
    {
        // 68-unit cells: 67/69 straddle a border.
        var a = Player("A", 67, 67);
        var b = Player("B", 69, 69);
        var far = Player("F", 400, 400);
        await world.JoinAsync(a);
        await world.JoinAsync(b);
        await world.JoinAsync(far);
        Assert.Equal("B", Names(a.Visible));
        Assert.Empty(far.Visible);

        // F walks next to A over several cells: it appears both ways once in range.
        sink.Events.Clear();
        foreach (var step in new[] { 300f, 200f, 120f, 80f })
        {
            far.Position = new Vec3 { X = step, Z = step };
            await world.MoveAsync(far, new WalkReq { Pos = far.Position });
        }
        Assert.Contains(far, a.Visible);
        Assert.Contains(a, far.Visible);
        Assert.Contains("A+F", sink.Events);
        Assert.Contains("F+A", sink.Events);

        // A moving player's observers get its movement, nobody else.
        sink.Events.Clear();
        a.Position = new Vec3 { X = 70, Z = 70 };
        await world.MoveAsync(a, new WalkReq { Pos = a.Position });
        Assert.Equal(new[] { "B~A", "F~A" }, sink.Events.Order().ToArray());
    }
}
