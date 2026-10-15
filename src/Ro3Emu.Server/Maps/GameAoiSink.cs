using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Maps;

/// <summary>
/// AOI through the client's own messages: SYNC_AOI_VIEW (31037) creates and removes remote units,
/// MOVEMENT_CHANGE (31081) moves them, ENTITY_FACCETOALL (31014) turns them.
/// </summary>
public sealed class GameAoiSink : IAoiSink
{
    public const uint SyncAoiView = 31037;
    public const uint MovementChange = 31081;
    public const uint EntityFaceToAllId = 31014;

    /// <summary>A WALK further than this from the previous one is shown as a teleport, not a walk.</summary>
    private const float TeleportDistance = 10f;

    /// <summary>
    /// The extrapolated walk covers this long; WALK keep-alives come every 0.9 s, so a late update overshoots
    /// little and the next one re-targets smoothly.
    /// </summary>
    private const int WalkDurationMs = 1000;

    public Task EnterAsync(GameSession viewer, GameSession subject)
    {
        var view = new AoIViewChangeData();
        view.Bases.Add(RoleSessionService.PlayerEntity(subject));
        return viewer.SendGameAsync(SyncAoiView, view);
    }

    public Task MoveAsync(GameSession viewer, GameSession subject, WalkReq walk) =>
        viewer.SendGameAsync(MovementChange, Movement(subject, walk));

    public Task TurnAsync(GameSession viewer, GameSession subject, float face) =>
        viewer.SendGameAsync(EntityFaceToAllId, new EntityFaceToAll { EntityID = (ulong)subject.Role!.Id, Face = face });

    public Task LeaveAsync(GameSession viewer, GameSession subject)
    {
        var view = new AoIViewChangeData();
        view.LeaveAoi.Add((ulong)subject.Role!.Id);
        return viewer.SendGameAsync(SyncAoiView, view);
    }

    public Task UnitsChangedAsync(GameSession viewer, IReadOnlyList<WorldUnit> entered, IReadOnlyList<long> left)
    {
        var view = new AoIViewChangeData();
        foreach (var unit in entered) view.Bases.Add(unit.Entity());
        foreach (var uid in left) view.LeaveAoi.Add((ulong)uid);
        return viewer.SendGameAsync(SyncAoiView, view);
    }

    public Task UnitMoveAsync(GameSession viewer, MovementChangeNtf movement) => viewer.SendGameAsync(MovementChange, movement);

    public async Task UnitMoveManyAsync(IReadOnlyList<GameSession> viewers, MovementChangeNtf movement)
    {
        var prepared = new PreparedMessage(MovementChange, movement);
        foreach (var viewer in viewers)
        {
            try { await viewer.SendPreparedAsync(prepared); }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or ObjectDisposedException) { }
        }
    }

    /// <summary>One 31081 built and serialized once for all viewers of the move (was one build + serialize per viewer).</summary>
    public async Task MoveManyAsync(IReadOnlyList<GameSession> viewers, GameSession subject, WalkReq walk, MovementChangeNtf? movement)
    {
        var prepared = new PreparedMessage(MovementChange, movement ?? Movement(subject, walk));
        foreach (var viewer in viewers)
        {
            try { await viewer.SendPreparedAsync(prepared); }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or ObjectDisposedException) { }
        }
    }

    /// <summary>31037 pet base follows the owner's base, as required by LC_Unit_Pet.OnCreateOver.</summary>
    public Task PetEnterAsync(GameSession viewer, PetFollowerActor pet)
    {
        var view = new AoIViewChangeData();
        view.Bases.Add(pet.Entity());
        return viewer.SendGameAsync(SyncAoiView, view);
    }

    public Task PetLeaveAsync(GameSession viewer, PetFollowerActor pet)
    {
        var view = new AoIViewChangeData();
        view.LeaveAoi.Add((ulong)pet.Uid);
        return viewer.SendGameAsync(SyncAoiView, view);
    }

    /// <summary>31081 follows the pet's own displacement and finite movement duration (inferred).</summary>
    public Task PetMoveAsync(GameSession viewer, GameSession owner, PetFollowerActor pet, WalkReq walk)
        => viewer.SendGameAsync(MovementChange, PetMovement(owner, pet, walk));

    public Task PetTurnAsync(GameSession viewer, PetFollowerActor pet, float face) =>
        viewer.SendGameAsync(EntityFaceToAllId, new EntityFaceToAll { EntityID = (ulong)pet.Uid, Face = face });

    public static MovementChangeNtf PetMovement(GameSession owner, PetFollowerActor pet, WalkReq walk)
    {
        var info = new MovementInfo
        {
            Pos = pet.Position.Clone(),
            ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Face = pet.Face,
            MoveType = MovementType.MoveTypeNone,
        };
        float speed = owner.Speed / 100f * Math.Clamp(walk.SpeedRate, 0f, 1f);
        if (!walk.BStop && !pet.Teleported && speed > 0f)
        {
            // The accepted pet position is the accepted owner position plus a fixed offset. Project both
            // with the same finite velocity so their paths remain separated, including on reversal (inferred).
            info.MoveType = MovementType.MoveTypeWalk;
            info.MoveDuration = WalkDurationMs;
            info.MovementWalk = new MovementWalk
            {
                Dir = walk.Dir,
                Speed = speed,
                Acc = 0f,
                EndHeight = pet.Position.Y,
            };
        }
        return new MovementChangeNtf
        {
            Uid = pet.Uid,
            TransType = pet.Teleported ? MovementTransitionType.MoveTransForceTeleport : MovementTransitionType.MoveTransNormal,
            MovementInfo = info,
        };
    }

    /// <summary>WalkReq of the subject -> MovementChangeNtf for viewers.</summary>
    public static MovementChangeNtf Movement(GameSession subject, WalkReq walk)
    {
        var info = new MovementInfo
        {
            Pos = walk.Pos.Clone(),
            ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), // same clock as Pong.serverTimeMs
            Face = walk.Face,
        };
        var transition = MovementTransitionType.MoveTransNormal;
        float speed = subject.Speed / 100f * Math.Clamp(walk.SpeedRate, 0f, 1f);
        if (Distance(subject.PreviousPosition, walk.Pos) > TeleportDistance)
        {
            transition = MovementTransitionType.MoveTransForceTeleport;
            info.MoveType = MovementType.MoveTypeNone;
        }
        else if (walk.BStop || speed <= 0f)
        {
            info.MoveType = MovementType.MoveTypeNone;
        }
        else
        {
            info.MoveType = MovementType.MoveTypeWalk;
            info.MoveDuration = WalkDurationMs;
            info.MovementWalk = new MovementWalk { Dir = walk.Dir, Speed = speed, Acc = 0, EndHeight = walk.Pos.Y };
        }
        return new MovementChangeNtf { Uid = subject.Role!.Id, TransType = transition, MovementInfo = info };
    }

    private static float Distance(Vec3 a, Vec3 b)
    {
        float dx = a.X - b.X, dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
