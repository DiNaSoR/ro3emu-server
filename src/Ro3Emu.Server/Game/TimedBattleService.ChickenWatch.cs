using Romsg;
using static Ro3Emu.Server.Game.ChickenRules;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Survivor Battle spectating. A player out of the battle (no revive map open) watches a standing teammate: the client
/// binds its camera to the unit named by the custom data Unit_CameraBindUnitCustomData (1002) on its own player and
/// opens UI_Sys_Chicken_SpectatingWidget (LC_DunGeon_Chicken.lua:960-1004, LC_ChickenHelper.lua:899-1019). 82675
/// ChickenWatchNotify {autoWatchTime, seconds} shows "auto spectate in N s" while the player is dead and watches nobody;
/// the server then picks the first standing teammate. 82640 ChickenChangeWatchPlayerReq {player_id} switches (the
/// spectating widget's arrows and the team list; the client allows only a teammate in war, CheckCanWatchPlayer;
/// the response is NullArg). (inferred) Server side the spectator's view follows the watched teammate: its position
/// moves there and World hides it from every other player (its Death crate and ID tag stay where it fell). A revive,
/// a revive map, or nobody left to watch ends it and puts the player back where it fell.
/// </summary>
public sealed partial class TimedBattleService
{
    public const uint ChangeWatch = 82640, WatchNotify = 82675;
    public const long CameraBindData = 1002;

    private bool Standing(Match match, ChickenRun run, GameSession player) =>
        run.Players.TryGetValue(player, out var state) && state.State == RoundRoleStatus.InWar && !player.Dead
        && !match.Fallen.Contains(player) && !match.Absent.Contains(player) && InRoom(match).Contains(player);

    /// <summary>The next standing teammate after <paramref name="after"/> in team order (or the first one).</summary>
    private GameSession? NextWatchTarget(Match match, ChickenRun run, GameSession player, GameSession? after)
    {
        var mates = Team(match, match.Faction[player]).Where(p => p != player)
            .OrderBy(p => run.Players[p].TeamIndex).ToList();
        int start = after == null ? 0 : mates.IndexOf(after) + 1;
        for (int i = 0; i < mates.Count; i++)
        {
            var mate = mates[(start + i) % mates.Count];
            if (Standing(match, run, mate)) return mate;
        }
        return null;
    }

    private bool CanSpectate(Match match, ChickenPlayer me, GameSession player) =>
        player.Dead && match.Fallen.Contains(player) && me.RelifeUntilMs == 0 && !me.GaveUp;

    /// <summary>After an elimination: the countdown to watching a teammate, if one still stands.</summary>
    private async Task OfferWatchAsync(Match match, ChickenRun run, GameSession player, ChickenPlayer me, long now)
    {
        if (!CanSpectate(match, me, player) || me.Watching != null || NextWatchTarget(match, run, player, null) == null) return;
        me.AutoWatchAtMs = now + AutoWatchMs;
        await player.SendGameAsync(WatchNotify, new ChickenWatchNotify { AutoWatchTime = me.AutoWatchAtMs / 1000 });
    }

    /// <summary>82640.</summary>
    public async Task ChangeWatchAsync(GameSession session, ChickenChangeWatchPlayerReq req)
    {
        if (Chicken(session) is not (var match, var run, var me)) return;
        await RunMatchTransitionAsync(match, "chicken watch", async () =>
        {
            if (!CanSpectate(match, me, session)) return;
            var target = Team(match, match.Faction[session])
                .FirstOrDefault(p => p != session && match.Participants[p].Id == req.PlayerId);
            if (target == null || !Standing(match, run, target)) return;
            await WatchAsync(match, session, me, target);
        });
    }

    private async Task WatchAsync(Match match, GameSession player, ChickenPlayer me, GameSession target)
    {
        me.AutoWatchAtMs = 0;
        if (me.Watching == target) return;
        if (me.Watching == null) me.FellAt = (player.Position.Clone(), player.Face);
        me.Watching = target;
        await _world.SetHiddenAsync(player, true);
        await ViewFromAsync(player, target.Position);
        await SendCameraBindAsync(match, player, match.Participants[target].Id);
    }

    /// <summary>Ends spectating: the camera back on the player's own unit, its view back where it fell.</summary>
    private async Task StopWatchAsync(Match match, GameSession player, ChickenPlayer me)
    {
        me.AutoWatchAtMs = 0;
        if (me.Watching == null && !player.AoiHidden) return;
        me.Watching = null;
        await SendCameraBindAsync(match, player, 0);
        if (me.FellAt is var (at, face))
        {
            me.FellAt = null;
            player.Face = face;
            await ViewFromAsync(player, at);
        }
        await _world.SetHiddenAsync(player, false);
    }

    /// <summary>Moves only the server's view point: the spectator's client keeps its own unit where it fell.</summary>
    private async Task ViewFromAsync(GameSession player, Vec3 at)
    {
        player.PreviousPosition = player.Position;
        player.Position = at.Clone();
        await _world.MoveAsync(player, new WalkReq { Pos = at.Clone(), Face = player.Face, BStop = true, SpeedRate = 1 }, forceTeleport: true);
    }

    private static Task SendCameraBindAsync(Match match, GameSession player, long target)
    {
        var bind = new CustomData
        {
            Id = CameraBindData,
            CustomParam = new CustomParam { Tp = CustomParam.Types.ParamType.Int, IntParam = target },
        };
        return player.SendGameAsync(WorldCoreService.CustomChange,
            new CustomDataChange { EntityId = match.Participants[player].Id, CustomDatas = { bind } });
    }

    /// <summary>Per tick: due auto-watches, a watched teammate that fell or left (the next one, or the end), and the
    /// spectator's view following the watched one.</summary>
    private async Task TickWatchAsync(Match match, ChickenRun run, long now)
    {
        foreach (var (player, me) in run.Players.ToArray())
        {
            if (me.Watching == null && me.AutoWatchAtMs == 0) continue;
            if (!CanSpectate(match, me, player) || !InRoom(match).Contains(player))
            {
                await StopWatchAsync(match, player, me);
                continue;
            }
            if (me.Watching == null)
            {
                if (now < me.AutoWatchAtMs) continue;
                me.AutoWatchAtMs = 0;
                if (NextWatchTarget(match, run, player, null) is { } first) await WatchAsync(match, player, me, first);
                continue;
            }
            var watched = me.Watching;
            if (!Standing(match, run, watched))
            {
                if (NextWatchTarget(match, run, player, watched) is { } next) await WatchAsync(match, player, me, next);
                else await StopWatchAsync(match, player, me);
                continue;
            }
            if (MonsterService.Distance(player.Position.X, player.Position.Z, watched.Position.X, watched.Position.Z) > WatchFollowStep)
                await ViewFromAsync(player, watched.Position);
        }
    }
}
