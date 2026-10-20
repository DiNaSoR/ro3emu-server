using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Flying mounts (MountsConfig _iType 1: the Starshatter Dragon 60060010 and 60040033) take off and land by their
/// _kFlySwitchSkills, not by 31083: no client Lua sends 31083, while LC_Movement_AStarAgentNew sends 31084 only once
/// the unit is m_bFlying, which LC_UnitBase sets from the status word's eObjectStateFly (43) bit
/// (LC_UnitSpecialStatus.CheckFlyMask). So an accepted take-off sets that bit and an accepted landing clears it (inferred:
/// the client registers SK_EndSkill _onFlyLandingSkillEnd as the bit drops, so the bit leads the landing skill). The
/// skills move the model themselves (Lua_Config_EffectData: 3112121 rises 20 m, 3112221 drops 20 m) and the client
/// sends 31084 during the climb, so the server keeps following those moves rather than jumping ahead of them.
/// </summary>
public sealed partial class WorldCoreService
{
    private const long FlyBit = 1L << 42; // eObjectStateFly (43)

    /// <summary>After flight starts or ends: the mount's ground/air buffs (CombatService) and its wheel slot (SkillService).</summary>
    public List<Func<GameSession, Task>> FlightChanged { get; } = new();

    /// <summary>CombatService.BeforeCast: the ridden flying mount's take-off on the ground, its landing in the air.</summary>
    internal async Task MountFlightCastAsync(GameSession s, int skillId)
    {
        if (MountService.FlySkills(s, data) is not { } fly) return;
        if (skillId == fly.Up && !s.WorldState.Flying && worldData.Map(s.SceneEntry) != null)
        {
            s.WorldState.Flying = true;
            s.WorldState.LastFlyMs = Now;
            await FlightStateAsync(s);
        }
        else if (skillId == fly.Down && s.WorldState.Flying)
        {
            s.WorldState.Flying = false;
            if (worldData.Map(s.SceneEntry) is { } here)
            {
                var at = here.Grounded(new MapPoint(s.Position.X, s.Position.Y, s.Position.Z, s.Face));
                s.PreviousPosition = s.Position; s.Position = new Vec3 { X = at.X, Y = at.Y, Z = at.Z };
            }
            await FlightStateAsync(s);
        }
    }

    /// <summary>MountService.Changed: getting off (or swapping) the mount in the air lands at once.</summary>
    internal async Task MountsChangedAsync(GameSession s)
    {
        if (s.WorldState.Flying && !CanFly(s)) await LandAsync(s, s.Position, s.Face);
    }

    /// <summary>The eObjectStateFly bit to the flyer and its viewers, then <see cref="FlightChanged"/>.</summary>
    private async Task FlightStateAsync(GameSession s)
    {
        long bits = s.WorldState.Flying ? s.StatusBits | FlyBit : s.StatusBits & ~FlyBit;
        if (bits != s.StatusBits && s.Role != null)
        {
            s.StatusBits = bits;
            await ToSelfAndViewersAsync(s, CombatService.AoiAttrChange, new AOIAttrChange
            {
                EntityUuid = (ulong)s.Role.Id,
                StateInfo = new AoiObjStateInfo { Data = new CommonBigMask { Values = { (ulong)bits } } },
            });
        }
        foreach (var handler in FlightChanged) await handler(s);
    }
}
