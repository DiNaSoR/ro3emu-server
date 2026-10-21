using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// How a scripted step moves its players besides walking:
/// <list type="bullet">
/// <item>A slide (step "slide"): the client's own LC_Movement_Controller_Slide, which the player's movement
/// (LC_Movement_AStarAgentNew.SetCustomData) creates for the local player on its first slide custom data. The server
/// pushes Player_SlideSpeed (5400003, ListLong [side, down] in cm/s) and then Player_SlideState (5400001, Int 1) in
/// 31018 CustomDataChange; the client then slides the player downhill, the A/D keys steer and the position is
/// reported as walks every 0.1 s. State 0 stops it. The pushes are not stored as the role's custom data: a slide never
/// outlives its step.</item>
/// <item>A vehicle ride: an interaction whose GameplayStatusConfig is type 3 on a vehicle scene actor
/// (DungeonCatalog.VehicleSeat) seats the player, as WorldInteractionService does for world NPCs: 32702
/// InteractChangeNtf (EType 3, the status, the interaction and the actor's uid) and 32704 Change3CNtf (the seat's
/// _iChange3C controls). The seat's button (_kGameplayBtnId, 32703) or its exit interaction (_iExitNpcAction, 32700)
/// gets the player off; so does the end of the step.</item>
/// <item>A fallback (step "fallback"): the step completes by itself after its time, so a ride or slide that never
/// reaches its region cannot block the run.</item>
/// <item>A won scene with a "next" scene goes straight on to it (DungeonConfig _iSameDupLoadingType 2 on that scene),
/// as the client's quest tracker entry would.</item>
/// </list>
/// </summary>
public sealed partial class DungeonService
{
    internal sealed record Ride(SceneActorUnit Vehicle, int Interaction, int StatusId, JsonElement Seat);

    /// <summary>LC_Movement_Controller_Slide's custom data ids (enum_CustomData Player_SlideState, Player_SlideSpeed).</summary>
    internal const long SlideStateData = 5400001, SlideSpeedData = 5400003;
    /// <summary>The pause before a won scene goes on to its next one, so its last line is read (inferred).</summary>
    private static readonly TimeSpan ContinueDelay = TimeSpan.FromSeconds(3);

    private async Task StartSlideAsync(Attempt a, ReconstructedDungeons.StepSlide slide)
    {
        foreach (var s in Present(a).Where(m => a.Loaded.Contains(m) && m.Role != null))
        {
            var speed = new CustomParam { Tp = CustomParam.Types.ParamType.ListLong };
            speed.ListLongParam.Add([(long)MathF.Round(slide.Side * 100), (long)MathF.Round(slide.Down * 100)]);
            // Speed first: the controller enters the slide on state 1 with the speed it already has.
            await SendSlideAsync(a, s, new CustomData { Id = SlideSpeedData, CustomParam = speed }, SlideState(1));
            a.Sliding.Add(s);
        }
    }

    private static CustomData SlideState(long state) =>
        new() { Id = SlideStateData, CustomParam = new CustomParam { Tp = CustomParam.Types.ParamType.Int, IntParam = state } };

    private async Task SendSlideAsync(Attempt a, GameSession s, params CustomData[] values)
    {
        var change = new CustomDataChange { EntityId = s.Role!.Id, CustomDatas = { values } };
        await SendQuietAsync(s, WorldCoreService.CustomChange, change);
        foreach (var viewer in Present(a).Where(v => v != s && a.Loaded.Contains(v)))
            await SendQuietAsync(viewer, WorldCoreService.CustomChange, change);
    }

    private async Task StopSlideAsync(Attempt a, GameSession s)
    {
        if (!a.Sliding.Remove(s) || s.Role == null) return;
        await SendSlideAsync(a, s, SlideState(0));
    }

    /// <summary>A step ends: its slides stop and its riders get off.</summary>
    private async Task EndStepMovementAsync(Attempt a)
    {
        foreach (var s in a.Sliding.ToArray()) await StopSlideAsync(a, s);
        foreach (var s in a.Riders.Keys.ToArray()) await DismountAsync(a, s, interrupt: true);
    }

    /// <summary>A participant leaves the room: nothing of the run's movement follows them out.</summary>
    private async Task EndMovementAsync(Attempt a, GameSession s)
    {
        await StopSlideAsync(a, s);
        await DismountAsync(a, s, interrupt: true);
    }

    /// <summary>
    /// After a successful 32700 on a scene actor: true when the interaction was a ride (getting on or off), which is
    /// not a step event of its own.
    /// </summary>
    private async Task<bool> RideInteractionAsync(Attempt a, GameSession s, SceneActorUnit actor, int interaction)
    {
        if (a.Riders.TryGetValue(s, out var ride))
        {
            if (ride.Vehicle == actor && interaction == GameData.Int(ride.Seat, "_iExitNpcAction")) await DismountAsync(a, s, false);
            return interaction == GameData.Int(ride.Seat, "_iExitNpcAction") || _catalog.VehicleSeat(actor.NpcId, interaction) != null;
        }
        if (_catalog.VehicleSeat(actor.NpcId, interaction) is not { } seat) return false;
        // One driver seat, and only while the step opens the ride: the actor carries the buff its condition asks for,
        // as the client checks before it offers the button.
        if (a.Riders.Values.Any(r => r.Vehicle == actor)
            || !_catalog.BuffGates(interaction).All(group => actor.Buffs.Any(b => _data.Row("BuffConfig", b.ConfigId) is { } buff
                && GameData.Int(buff, "_iBuffID") == group))) return true;
        ride = new Ride(actor, interaction, seat.StatusId, seat.SeatRow);
        a.Riders[s] = ride;
        _log.LogInformation("{Role} rides scene actor {Npc} in scene {Scene} step {Step}", s.Role?.Id, actor.NpcId,
            a.Plan.SceneEntry, a.Wave + 1);
        await BroadcastAsync(a, WorldInteractionService.Change, new InteractChangeNtf
        {
            Uid = s.Role!.Id,
            DataInfo = new InteractInfo
            {
                EType = (InteractInfo.Types.EState)3, StateId = seat.StatusId, InteractConfigId = interaction, ActiveObjId = actor.Uid,
            },
        });
        await SendQuietAsync(s, WorldInteractionService.Change3C, new Change3CNtf { CfgId = (int)GameData.Int(seat.SeatRow, "_iChange3C") });
        if (Step(a) is { Fallback: { FromMount: true } fallback }) ScheduleFallback(a, fallback);
        return true;
    }

    private async Task DismountAsync(Attempt a, GameSession s, bool interrupt)
    {
        if (!a.Riders.Remove(s) || s.Role == null) return;
        var off = new InteractChangeNtf
        {
            Uid = s.Role.Id, DataInfo = new InteractInfo { EType = InteractInfo.Types.EState.Idle }, Interrupt = interrupt,
        };
        await SendQuietAsync(s, WorldInteractionService.Change, off);
        foreach (var viewer in Present(a).Where(v => v != s && a.Loaded.Contains(v)))
            await SendQuietAsync(viewer, WorldInteractionService.Change, off);
    }

    /// <summary>32703 while riding a scene actor: the seat's own buttons get the player off. False when not riding one.</summary>
    public async Task<bool> TryUseActionAsync(GameSession s, UseInteractActionReq req)
    {
        if (s.DungeonAttempt is not { } a || !a.Riders.TryGetValue(s, out var ride)) return false;
        bool exit = ride.Seat.TryGetProperty("_kGameplayBtnId", out var buttons) && buttons.ValueKind == JsonValueKind.Array
            && buttons.EnumerateArray().Any(b => (long)(GameData.Number(b) ?? 0) == req.ButtonId);
        await s.SendGameAsync(WorldInteractionService.Action, new UseInteractActionRes { Result = exit ? 0 : ErrorCodes.NpcParameter });
        if (exit) await DismountAsync(a, s, false);
        return true;
    }

    /// <summary>31074 while riding a scene actor: the driver turns. False when not riding one.</summary>
    public async Task<bool> TrySyncVehicleAsync(GameSession s, SyncVehicleReq req)
    {
        if (s.DungeonAttempt is not { } a || !a.Riders.ContainsKey(s)) return false;
        if (float.IsFinite(req.Face) && req.Face >= 0 && req.Face <= 360) await _world.TurnAsync(s, req.Face);
        return true;
    }

    private void ScheduleFallback(Attempt a, ReconstructedDungeons.StepFallback fallback)
    {
        int wave = a.Wave;
        _loop.ScheduleLegacy(TimeSpan.FromMilliseconds(fallback.Ms), async () =>
        {
            if (a.Wave != wave || a.StepDone || a.Settled || a.Room.Closed || Step(a) is not { } step) return;
            if (Present(a).FirstOrDefault(a.Loaded.Contains) is not { } s) return;
            _log.LogInformation("Scripted scene {Scene} step {Step}: fallback after {Ms} ms", a.Plan.SceneEntry, wave + 1, fallback.Ms);
            await EndStepMovementAsync(a);
            if (fallback.Teleport != 0 && _catalog.ScenePoint(a.Plan.SceneEntry, fallback.Teleport) is { } to)
                foreach (var member in Present(a).Where(m => a.Loaded.Contains(m) && !m.SceneLoading))
                    await _scenes.TeleportInMapAsync(member, to);
            var t = step.Complete[0];
            await StepEventAsync(a, s, [(t.Kind, t.Id)]);
        });
    }

    /// <summary>
    /// A won scripted scene with a next scene: the players go straight on to it instead of being sent out. When the
    /// next scene cannot be entered now (DungeonService.Admission), the usual countdown sends them out.
    /// </summary>
    private bool TryContinue(Attempt a)
    {
        if (_reconstructed?.Scripted(a.Plan.SceneEntry) is not { Next: > 0 } scene || a.Plan.Multi != null) return false;
        var members = Present(a).Where(a.Loaded.Contains).ToArray();
        var leader = members.FirstOrDefault(m => m.Role?.Id == a.Leader) ?? members.FirstOrDefault();
        var req = new EnterDungeonReq { SceneEntry = scene.Next };
        if (leader == null || _catalog.Resolve(req) is not { } plan || members.Any(m => Admission(m, plan, continuation: true) != 0))
            return false;
        _loop.ScheduleLegacy(ContinueDelay, async () =>
        {
            if (!ReferenceEquals(leader.DungeonAttempt, a) || leader.InstanceId != a.Room.Id || leader.SceneLoading || leader.Role == null) return;
            _log.LogInformation("Scripted scene {Scene} won: {Role} goes on to {Next}", a.Plan.SceneEntry, leader.Role.Id, scene.Next);
            try { await EnterAsync(leader, req); }
            catch (Exception e) { _log.LogWarning(e, "Scripted scene {Scene}: going on to {Next} failed", a.Plan.SceneEntry, scene.Next); }
            // Anyone still in this room (the entry was refused after all) is sent out as usual.
            foreach (var s in members)
                if (ReferenceEquals(s.DungeonAttempt, a) && s.InstanceId == a.Room.Id && !s.SceneLoading && s.Role != null)
                    try { await LeaveAsync(s); }
                    catch (Exception e) { _log.LogWarning(e, "Dungeon leave after a refused continuation failed for {Role}", s.Role?.Id); }
        });
        return true;
    }
}
