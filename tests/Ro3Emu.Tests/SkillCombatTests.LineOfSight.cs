using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>
    /// A target in range behind an unwalkable cell: the client's sight check (CheckIndirectPass) lets a skill through any
    /// cell whose GridLevel is at most the skill's _iPassBlock, so it sends the cast. The server refused it with 3026
    /// because the cells were not walkable; the client never repositions on that code, and Auto Battle retried forever.
    /// </summary>
    [ClientDataFact]
    public async Task ASkillOverAnUnwalkableCellTheClientSeesThroughIsAccepted()
    {
        await using var alice = await InWorld("Alice");
        var s = Session("alice");
        var nav = _app.Services.GetRequiredService<WorldData>().Map(s.SceneEntry)!.Nav!;
        var (from, to) = BlockedPair(nav, s.Position);
        Assert.False(nav.CanWalk(from.X, from.Y, from.Z, to.X, to.Z));
        Assert.True(nav.CanPass(from.X, from.Z, to.X, to.Z, 2));
        Assert.False(nav.CanPass(from.X, from.Z, to.X, to.Z, -1));

        var target = await Loop.InvokeAsync(async () =>
        {
            await _app.Services.GetRequiredService<SceneService>().TeleportInMapAsync(s, new MapPoint(from.X, from.Y, from.Z, 0));
            var monsters = _app.Services.GetRequiredService<MonsterService>();
            var template = monsters.Template(5000001)! with { RoamDistance = 0 };
            var spawned = await monsters.SpawnSingleAsync(s.SceneEntry, 5000001, new MapPoint(to.X, to.Y, to.Z, 0),
                s.InstanceId, customTemplate: template);
            Assert.NotNull(spawned);
            spawned.Hp = 1_000_000_000;
            return spawned;
        });

        // Normal Attack (_iPassBlock 2, 3 m).
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }

    /// <summary>Two walkable cell centres 2.8-3 m apart at the same height with an unwalkable cell between them.</summary>
    private static (Vec3 From, Vec3 To) BlockedPair(NavGrid nav, Vec3 near)
    {
        int cx = (int)MathF.Floor(near.X), cz = (int)MathF.Floor(near.Z);
        // Longer than two cells: the client's ray checks only the end cell of shorter segments.
        (int, int)[] offsets = [(3, 0), (0, 3), (2, 2), (-2, 2)];
        for (int r = 0; r < 200; r++)
            for (int x = cx - r; x <= cx + r; x++)
                for (int z = cz - r; z <= cz + r; z++)
                {
                    if (Math.Max(Math.Abs(x - cx), Math.Abs(z - cz)) != r) continue;
                    if (nav.WalkHeight(x + 0.5f, z + 0.5f, near.Y) is not { } h || MathF.Abs(h - near.Y) > 30) continue;
                    foreach (var (dx, dz) in offsets)
                    {
                        float tx = x + dx + 0.5f, tz = z + dz + 0.5f;
                        if (nav.WalkHeight(tx, tz, h) is not { } th || MathF.Abs(th - h) > 0.5f) continue;
                        if (!nav.CanWalk(x + 0.5f, h, z + 0.5f, tx, tz) && nav.CanPass(x + 0.5f, z + 0.5f, tx, tz, 2))
                            return (new Vec3 { X = x + 0.5f, Y = h, Z = z + 0.5f }, new Vec3 { X = tx, Y = th, Z = tz });
                    }
                }
        throw new InvalidOperationException("no blocked pair near the spawn point");
    }
}
