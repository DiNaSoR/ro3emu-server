using Microsoft.Extensions.Logging.Abstractions;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// #bug-reports "I have 163 Flee ... Roda Frog (Level 13) ... Every single attack hits me": almost every monster's
/// normal attack carries _iSkillHit 10000, which skipped the hit roll, so FLEE never dodged a monster. A normal attack
/// now always draws the FLEE roll; a skill with _iSkillHit 10000 still never misses.
/// </summary>
public sealed class FleeTests
{
    private static GameData Data()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ServerOptions
        { GameDataDir = Path.Combine(ClientData.RepoRoot, "data") });
        return new GameData(options, NullLogger<GameData>.Instance);
    }

    [ClientDataFact]
    public void AMonsterNormalAttackCanBeDodged()
    {
        var frog = Data().Row("SkillConfig", 5002101)!.Value; // Roda Frog's normal attack
        Assert.Equal(10000, GameData.Int(frog, "_iSkillHit"));
        Assert.False(CombatService.NeverMisses(frog));
    }

    [ClientDataFact]
    public void ASkillWithFullSkillHitStillNeverMisses()
    {
        var skill = Data().Row("SkillConfig", 1510205)!.Value; // Sonic Blow
        Assert.Equal(10000, GameData.Int(skill, "_iSkillHit"));
        Assert.Equal(0, GameData.Int(skill, "_iIsNormalAtk"));
        Assert.True(CombatService.NeverMisses(skill));
    }
}
