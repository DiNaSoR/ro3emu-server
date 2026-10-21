using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// A won solo story run (an ordinary dungeon, not a multi/team one) sends its players back out after a countdown.
/// The client has no story-scene exit script and shows no result window for these scenes, so without this the player
/// had to find the exit button on their own. The delay is the client's own team-dungeon close time
/// (PublicParamConfig FiveGamePlay_CloseTime, 15 s; the victory widget counts it down there); using it for story runs,
/// the countdown push (LC_DunGeon_Actions.SetCountDown -> DungeonCountDownTimeStamp, shown by
/// UI_Sys_Dungeon_TopInfo_TimeWidget) and the notice text (UIWords 69053 "Leave the dungeon in ${1} sec.") are
/// (inferred).
/// </summary>
public sealed partial class DungeonService
{
    internal int AutoLeaveSeconds => (int)Math.Clamp(
        _data.Table("PublicParamConfig").TryGetValue("FiveGamePlay_CloseTime", out var v) ? GameData.Number(v) ?? 15 : 15, 3, 120);

    private async Task ScheduleAutoLeaveAsync(Attempt a)
    {
        if (a.Plan.Multi != null || a.GuildChallenge != null || a.Dracula != null || a.Room.Closed) return;
        int seconds = AutoLeaveSeconds;
        long leaveAt = _clock.GetUtcNow().ToUnixTimeSeconds() + seconds;
        var members = Present(a).Where(a.Loaded.Contains).ToArray();
        await BroadcastAsync(a, Event, new DungeonEventNotify
        {
            DungeonEntry = a.Plan.SceneEntry, EventType = (DungeonEventType)6, StageNum = a.Wave + 1, LevelNum = 1,
            CliFuncname = "SetCountDown", Params = { new EventParams { IntParams = { leaveAt } } },
        });
        foreach (var s in members)
            try { await GmCommands.ReplyAsync(s, $"Leave the dungeon in {seconds} sec."); }
            catch (Exception e) { _log.LogWarning(e, "Dungeon leave notice failed for {Role}", s.Role?.Id); }
        _loop.ScheduleLegacy(TimeSpan.FromSeconds(seconds), async () =>
        {
            foreach (var s in members)
            {
                // Only a player still in this run's room, settled and not loading, is sent out (the same path as
                // pressing the exit button).
                if (!ReferenceEquals(s.DungeonAttempt, a) || s.InstanceId != a.Room.Id || s.SceneLoading || s.Role == null) continue;
                try { await LeaveAsync(s); }
                catch (Exception e) { _log.LogWarning(e, "Dungeon auto leave failed for {Role}, instance {Instance}", s.Role?.Id, a.Room.Id); }
            }
        });
    }
}
