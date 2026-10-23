using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    // The airship's cannons (inferred): 'Board the Cannon' (5300006) on a Cannon fires 5365001 at the nearest enemy
    // airship, three boardings a ship; sinking both (5300015 at point 24, 5300053 at 61) ends stage 2 and the boarders come.
    [ClientDataFact]
    public async Task TheAirshipCannonsSinkTheEnemyShips()
    {
        const long scene = 1000501;
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = scene }));
        long instance = await Load(alice, scene);
        await Stage(alice, 1, quest: 1);
        await Gm(alice, "!actors use"); // the chitchat (all four)
        await Stage(alice, 2, quest: 2);
        var cannons = await ActorsIn(alice, 3, a => a.NpcId == 5300022);
        var ships = await WaveIn(instance, 2);
        Assert.Equal([5300015, 5300053], ships.Select(m => m.Template.NpcId).Order());
        var cannon = cannons[0];
        await Gm(alice, "!actors 5300022");
        int boardings = 0;
        for (; boardings < 20 && ships.Any(m => !m.Dead); boardings++)
        {
            Assert.Equal(0, await Interact(alice, cannon, 5300006));
            await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 5365001);
            await OnLoop(() =>
            {
                foreach (var ship in ships.Where(m => !m.Dead)) ship.Hp = ship.Template.MaxHp;
                return Task.CompletedTask;
            }); // a far ship's leash reset
        }
        Assert.All(ships, m => Assert.True(m.Dead));
        Assert.InRange(boardings, 6, 8); // three a ship (a miss costs one more)
        await Stage(alice, 3, quest: 3); // the boarding
    }

}
