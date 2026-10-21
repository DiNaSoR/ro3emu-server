using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class WorldCoreService
{
    // FlyBuffTag and MapConfig._iFly are consumed by the client's flight availability checks. A ridden flying mount
    // counts too: FlightStateAsync awaits the AOIAttrChange sends before FlightChanged adds its air buff, and the 1 s tick,
    // queued on the loop, ran in that gap and landed the rider (MountTests failed about one run in four).
    private bool CanFly(GameSession s) => Ready(s) && !s.Dead
        && _data.Row("MapConfig", s.SceneEntry) is { } map && GameData.Int(map, "_iFly") == 1
        && (MountService.FlySkills(s, _data) != null || s.Buffs.Any(b => b.ExpireMs > Now && _data.Row("BuffConfig", b.ConfigId) is { } row
            && row.TryGetProperty("_kBuffCategory", out var tags) && tags.ValueKind == JsonValueKind.Array
            && tags.EnumerateArray().Any(t => GameData.Number(t) == 3100000)));

    /// <summary>
    /// Wing use effects (ActionConfig 101 JumpSameMap / 102 JumpOtherMap, _iEXECHost 1; ItemConfig 1002001 Fly Wing and
    /// 1002000 Butterfly Wing). The Fly Wing moves the player to a random spot of the current map (inferred: one of the
    /// map's authored points or monster spawn points standing on walkable ground, so it never lands outside the play
    /// area); the Butterfly Wing goes to the picked map's Kafra arrival, else its born point (inferred).
    /// </summary>
    private async Task<bool> WingJumpAsync(GameSession s, long scene)
    {
        if (_worldData.Map(scene) is not { } map) return false;
        if (scene != s.SceneEntry) return await this._scene.EnterSceneAsync(s, scene, map.KafraArrival);
        var spots = map.Points.Values.Concat(map.SpawnGroups.SelectMany(g => g.Points).Select(p => new MapPoint(p.X, p.Y, p.Z, p.Face)))
            .Select(map.Grounded).Where(p => map.IsWalkable(p.X, p.Y, p.Z)).ToList();
        if (spots.Count == 0) return false;
        await this._scene.TeleportInMapAsync(s, spots[Random.Shared.Next(spots.Count)]);
        return true;
    }

    /// <summary>31083 toggles authoritative flight only with the configured buff and a valid map.</summary>
    public async Task FlySwitchAsync(GameSession s, FlySwitchReq req)
    {
        if (!Ready(s) || s.Dead || !float.IsFinite(req.Height) || req.Height < 0 || req.Height > 50
            || req.IsFly && (!CanFly(s) || s.WorldState.Flying))
        { await ErrorAsync(s, FlySwitch, ErrorCodes.Parameter); return; }
        if (!req.IsFly)
        {
            if (s.WorldState.Flying) await LandAsync(s, s.Position, s.Face);
            return;
        }
        // (inferred) Height is a desired offset; cap each takeoff to the same one-second flight budget.
        var position = s.Position.Clone();
        position.Y += Math.Min(req.Height, FlightSpeed(s));
        var map = _worldData.Map(s.SceneEntry);
        if (map == null || position.Y > map.Grounded(new MapPoint(position.X, position.Y, position.Z, s.Face)).Y + 50)
        { await ErrorAsync(s, FlySwitch, ErrorCodes.Parameter); return; }
        s.WorldState.Flying = true;
        s.WorldState.LastFlyMs = Now;
        s.WorldState.TakeOff = s.Position.Clone();
        await MoveFlightAsync(s, position, s.Face, new Vec3(), true, MovementTransitionType.MoveTransFlyUp);
        await FlightStateAsync(s);
    }

    private float FlightSpeed(GameSession s) => _data.Table("PublicParamConfig").TryGetValue("MountFlyMaxSpeed", out var value)
        ? Math.Max(1, (float)(GameData.Number(value) ?? s.Speed) / 100) : Math.Max(1, s.Speed / 100f);

    /// <summary>
    /// 31084: AStarAgentNew sends position plus a three-dimensional direction, not a ground yaw. Climbing and diving are
    /// free inside the 50 m band over the ground (a mount's take-off skill climbs on its own, 20 m over 3.5 s for the
    /// Starshatter Dragon, while the client already sends 31084), so the speed check is horizontal, and the ground under
    /// a flyer need not be walkable (water, cliffs): the band counts from its nearest layer of any kind, and a cell with no
    /// layer (off the map's grid) is refused. Flying off a cliff over the sea may hold or lose height above the new band
    /// (no invisible wall at the edge), only not climb further. A move outside these bounds is put back without an error tip: the
    /// client sends 31084 many times a second and showed a "parameter error" for each refusal.
    /// </summary>
    public async Task FlyMoveAsync(GameSession s, FlyMoveReq req)
    {
        if (!Finite(req.Pos) || !Finite(req.Dir) || !float.IsFinite(req.Face) || Distance(new Vec3(), req.Dir) > 2)
        { await ErrorAsync(s, FlyMove, ErrorCodes.Parameter); return; }
        double seconds = Math.Clamp((Now - s.WorldState.LastFlyMs) / 1000.0, 0.05, 2);
        float speed = FlightSpeed(s);
        var map = _worldData.Map(s.SceneEntry);
        var floor = map?.FloorHeight(new MapPoint(req.Pos.X, req.Pos.Y, req.Pos.Z, req.Face));
        if (!s.WorldState.Flying || !CanFly(s) || floor is not { } ground
            || req.Pos.Y < ground - 1 || req.Pos.Y > Math.Max(ground + 50, s.Position.Y)
            || MonsterService.Distance(s.Position.X, s.Position.Z, req.Pos.X, req.Pos.Z) > speed * seconds * 1.5 + 1.5)
        { await CorrectFlightAsync(s); return; }
        s.WorldState.LastFlyMs = Now;
        await MoveFlightAsync(s, req.Pos, req.Face, req.Dir, req.BStop, MovementTransitionType.MoveTransNormal, self: false);
    }

    public async Task FreeFallAsync(GameSession s, FreeFallJumpReq req)
    {
        if (!Ready(s) || s.Dead || !s.WorldState.Flying || !Finite(req.ToPos) || !float.IsFinite(req.Face)
            || Distance(s.Position, req.ToPos) > 50 || req.ToPos.Y > s.Position.Y + 1
            || MonsterService.Distance(s.Position.X, s.Position.Z, req.ToPos.X, req.ToPos.Z) > 3)
        { await ErrorAsync(s, FreeFall, ErrorCodes.Parameter); return; }
        await LandAsync(s, req.ToPos, req.Face);
    }

    /// <param name="forced">The server ends the flight (the mount or the flight buff is gone): over ground with no walkable
    /// layer it puts the rider back where it took off, else on the map's born point (inferred), instead of refusing, or
    /// the flight would never end.</param>
    private async Task LandAsync(GameSession s, Vec3 requested, float face, bool forced = false)
    {
        var map = _worldData.Map(s.SceneEntry);
        if (map == null) return;
        var transition = MovementTransitionType.MoveTransLanding;
        var at = map.Landing(new MapPoint(requested.X, requested.Y, requested.Z, face));
        if (at == null && forced)
        {
            var origin = s.WorldState.TakeOff ?? requested;
            var back = new MapPoint(origin.X, origin.Y, origin.Z, face);
            at = map.Landing(back) ?? (map.Born is { } born ? map.Landing(born) ?? map.Grounded(born) : map.Grounded(back));
            transition = MovementTransitionType.MoveTransForceTeleport;
        }
        if (at is not { } spot) { await ErrorAsync(s, FreeFall, ErrorCodes.Parameter); return; }
        s.WorldState.Flying = false;
        await MoveFlightAsync(s, new Vec3 { X = spot.X, Y = spot.Y, Z = spot.Z }, face, new Vec3(), true, transition);
        // FixLandNtf instructs the client to end the special landing state; immediate landing has zero remaining time.
        await ToSelfAndViewersAsync(s, FixLand, new FixLandNtf { Uuid = s.Role!.Id, FixLandTime = 0 });
        await FlightStateAsync(s);
    }

    private async Task CorrectFlightAsync(GameSession s)
    {
        if (s.Role == null) return;
        await s.SendGameAsync(GameAoiSink.MovementChange, new MovementChangeNtf
        { Uid = s.Role.Id, TransType = MovementTransitionType.MoveTransForceTeleport, MovementInfo = new MovementInfo { Pos = s.Position.Clone(), Face = s.Face, ServerTime = Now } });
    }

    /// <param name="self">Also tell the flyer: a take-off or landing the server makes, not the flyer's own 31084 move,
    /// which it already shows (an echo of each one fought the client's own movement).</param>
    private async Task MoveFlightAsync(GameSession s, Vec3 pos, float face, Vec3 direction, bool stop, MovementTransitionType transition, bool self = true)
    {
        var move = new MovementInfo { Pos = pos.Clone(), Face = face, ServerTime = Now, MoveType = MovementType.MoveTypeNone };
        float length = Distance(new Vec3(), direction);
        if (!stop && length > 0.0001f)
        {
            float speed = FlightSpeed(s);
            move.MoveType = MovementType.MoveTypeMove; move.MoveDuration = 1000;
            move.MovementMove = new MovementMove { StartVelocity = new Vec3 { X = direction.X / length * speed, Y = direction.Y / length * speed, Z = direction.Z / length * speed }, AccVelocity = new Vec3() };
        }
        s.PreviousPosition = s.Position; s.Position = pos.Clone(); s.Face = face; s.LastWalkUtc = DateTime.UtcNow; s.Walking = false;
        _combat.PlayerMoved(s);
        var notify = new MovementChangeNtf { Uid = s.Role!.Id, TransType = transition, MovementInfo = move };
        if (self) await s.SendGameAsync(GameAoiSink.MovementChange, notify);
        await _world.MoveAsync(s, new WalkReq { Pos = pos.Clone(), Face = face, BStop = true, SpeedRate = 1 }, movement: notify);
    }
}
