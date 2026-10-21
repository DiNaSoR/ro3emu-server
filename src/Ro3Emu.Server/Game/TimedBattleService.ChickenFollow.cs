using Romsg;
using static Ro3Emu.Server.Game.ChickenRules;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Survivor Battle team follow and head signs. Follow (UI_Sys_Chicken_MapPanelModel.lua:86-192 and the team tips):
/// 82628 SelectFollowReq {type, roleId, followedRoleId} where Invite (1) asks roleId (0 = every teammate) to follow the
/// sender, Followed (2) makes roleId follow followedRoleId (accepting an invite, or "follow" on a teammate) and Cancle
/// (0) ends it. The team learns the relations from 82673 (LC_DAL_Chicken.lua:254-273: followedMap follower -> followed,
/// inviteCd invitee -> invite cooldown end in seconds) and an invitee from 82674 (followedRoleId = the inviter; the
/// client gives it InviteAwaitChicken 15 s). A follower lands with the one it follows. Head signs: 82672 with
/// EChickenOpenPanel (the custom data ids 5000008-10 ChickenOpenShop / Adventure / Ambulance) shows the sign over the
/// player to the others while the panel is open (LC_CustomDataHandler_Chicken.lua:52-89, value 1 shows, 0 hides).
/// </summary>
public sealed partial class TimedBattleService
{
    public const uint SelectFollow = 82628, OpenPanel = 82672, FollowNotify = 82673, InviteFollowNotify = 82674;

    private long InviteCdSeconds() => PublicValue("InviteCdChicken") is var cd and > 0 ? cd : 5;

    public async Task SelectFollowAsync(GameSession session, SelectFollowReq req)
    {
        int result = ErrorCodes.Parameter;
        if (Chicken(session) is (var match, var run, _))
            await RunMatchTransitionAsync(match, "chicken follow", async () =>
            {
                long self = match.Participants[session].Id;
                int faction = match.Faction[session];
                var mates = Team(match, faction).ToDictionary(p => match.Participants[p].Id);
                bool Mate(long id) => mates.ContainsKey(id);
                switch (req.Type)
                {
                    case EChickenFollowNotifyType.Invite:
                        {
                            if (req.FollowedRoleId != self || (req.RoleId != 0 && (!Mate(req.RoleId) || req.RoleId == self))) return;
                            long now = _time.GetUtcNow().ToUnixTimeSeconds();
                            var invited = (req.RoleId == 0 ? mates.Keys.Where(id => id != self) : [req.RoleId])
                                .Where(id => run.InviteCd.GetValueOrDefault((self, id)) <= now).ToArray();
                            if (invited.Length == 0) { result = ErrorCodes.DungeonBusy; return; }
                            foreach (long id in invited)
                            {
                                run.InviteCd[(self, id)] = now + InviteCdSeconds();
                                if (InRoom(match).Contains(mates[id]))
                                    await mates[id].SendGameAsync(InviteFollowNotify, new ChickenInviteFollowNotify { FollowedRoleId = self });
                            }
                            break;
                        }
                    case EChickenFollowNotifyType.Followed:
                        // The follower itself, or (inferred) the one followed calling its teammates (the map's
                        // "everyone follow me", UI_Sys_Chicken_MapPanelModel.lua:86-98).
                        if (!Mate(req.RoleId) || !Mate(req.FollowedRoleId) || req.RoleId == req.FollowedRoleId
                            || (req.RoleId != self && req.FollowedRoleId != self)) return;
                        // No circles: whoever the target follows, directly or not, may not be the follower.
                        long at = req.FollowedRoleId;
                        for (int hops = 0; hops < mates.Count && run.Follow.TryGetValue(at, out long next); hops++)
                            if ((at = next) == req.RoleId) return;
                        run.Follow[req.RoleId] = req.FollowedRoleId;
                        break;
                    case EChickenFollowNotifyType.Cancle:
                        if (!run.Follow.TryGetValue(req.RoleId, out long followed) || (req.RoleId != self && followed != self)) return;
                        run.Follow.Remove(req.RoleId);
                        break;
                    default: return;
                }
                result = 0;
                await SendFollowAsync(match, run, faction);
            });
        await session.SendGameAsync(SelectFollow, new SelectFollowResp { Result = result });
    }

    private ChickenSelectFollowResp FollowState(Match match, ChickenRun run, GameSession player)
    {
        int faction = match.Faction[player];
        var team = Team(match, faction).Select(p => match.Participants[p].Id).ToHashSet();
        long self = match.Participants[player].Id;
        var state = new ChickenSelectFollowResp();
        foreach (var (follower, followed) in run.Follow)
            if (team.Contains(follower)) state.FollowedMap[follower] = followed;
        long now = _time.GetUtcNow().ToUnixTimeSeconds();
        foreach (var ((inviter, invitee), end) in run.InviteCd)
            if (inviter == self && end > now) state.InviteCd[invitee] = end;
        return state;
    }

    private Task SendFollowAsync(Match match, ChickenRun run, int faction) =>
        RoleFanout.EachAsync(Team(match, faction).Where(p => InRoom(match).Contains(p)),
            p => p.SendGameAsync(FollowNotify, FollowState(match, run, p)));

    /// <summary>The landing of the one a player follows (to the end of the chain), if it picked one.</summary>
    private ChickenPlayer? Leader(Match match, ChickenRun run, GameSession player)
    {
        var byId = match.Players.ToDictionary(p => match.Participants[p].Id);
        long at = match.Participants[player].Id;
        for (int hops = 0; hops < match.Players.Length && run.Follow.TryGetValue(at, out long next); hops++)
        {
            at = next;
            if (byId.TryGetValue(at, out var leader) && run.Players[leader].LandChosen) return run.Players[leader];
        }
        return null;
    }

    // ---- 82672 head signs ----

    public async Task OpenPanelAsync(GameSession session, ChickenOpenPanelReq req)
    {
        if (req.PanelNum is EChickenOpenPanel.Shop or EChickenOpenPanel.Adventure or EChickenOpenPanel.Ambulance
            && Chicken(session) is (var match, _, _) && session.Role != null)
        {
            var sign = new CustomData
            {
                Id = (long)req.PanelNum,
                CustomParam = new CustomParam { Tp = CustomParam.Types.ParamType.Int, IntParam = req.IsOpen ? 1 : 0 },
            };
            var change = new CustomDataChange { EntityId = session.Role.Id, CustomDatas = { sign } };
            await RoleFanout.EachAsync(InRoom(match).Where(p => p != session),
                p => p.SendGameAsync(WorldCoreService.CustomChange, change));
        }
        await session.SendGameAsync(OpenPanel, new ChickenOpenPanelResp { Req = req });
    }
}
