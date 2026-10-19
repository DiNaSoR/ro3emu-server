using Romsg;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Guild merging (Manage > Merge tab, right 21). The inviter asks a target guild to merge into it (32319). The target
/// accepts or refuses (32318) and, once accepted, picks which of its members move (32317), up to the inviter's free
/// places. At the next 05:00 the chosen members join the inviter as regular members and the target is disbanded
/// (170288-170290, 170349). 32314 lists the invites, 32315 recommends guilds and 32316 compares the two guilds
/// (docs/systems/guild-merge.md).
/// </summary>
public sealed partial class GuildService
{
    public const uint MergeLists = 32314, MergeRecommend = 32315, MergeDetail = 32316, MergeSaveMembers = 32317,
        MergeAnswer = 32318, MergeInvite = 32319;
    private const int MergeRight = 21; // GuildPermissionConfig 21 "Merge Guild"
    private const int MergeDoneMail = 1121; // GuildMail21: "Guild 【${1}】 has successfully completed a Merge Guild with this guild"

    public Task MergeListsAsync(GameSession s) => RunGuildAsync(s, [], [], () => MergeListsCoreAsync(s));
    public Task MergeRecommendAsync(GameSession s) => RunGuildAsync(s, [], [], () => MergeRecommendCoreAsync(s));
    public Task MergeDetailAsync(GameSession s, MergeGuildDetailDataReq req) =>
        RunGuildAsync(s, [req.Guildid, Own(s)?.PendingMergeGuildId ?? 0], [], () => MergeDetailCoreAsync(s, req));
    public Task MergeSaveMembersAsync(GameSession s, SaveMergeMemberReq req) =>
        RunGuildAsync(s, [req.Guildid], [], () => MergeSaveMembersCoreAsync(s, req));
    public Task MergeAnswerAsync(GameSession s, MergeGuildOpeReq req) =>
        RunGuildAsync(s, [req.Guildid], [], () => MergeAnswerCoreAsync(s, req));
    public Task MergeInviteAsync(GameSession s, InviteMergeGuildReq req) =>
        RunGuildAsync(s, [req.Guildid], [], () => MergeInviteCoreAsync(s, req));

    private long MergeNowMs => clock.GetUtcNow().ToUnixTimeMilliseconds();
    private long MergeInviteLifeMs => Setting("GuildMergeLapseTimeLimit", 24)[0] * 3600_000;
    private bool LiveInvite(GuildStore.MergeInvite invite) => invite.SentMs + MergeInviteLifeMs > MergeNowMs;
    private long MergeRoom(GuildStore.State inviter) => Math.Max(0, MaxMembers(inviter) - inviter.Members.Count);

    private IEnumerable<GuildStore.State> SentInvites(GuildStore.State g) => store.List()
        .Where(o => o.Id != g.Id && o.MergeInvites.TryGetValue(g.Id, out var i) && LiveInvite(i));

    private int MergeActor(GameSession s, out GuildStore.State g)
    {
        g = Own(s)!;
        return g == null ? ErrorCodes.NotInGuild : g.Status != (int)GuildStatus.Normal || !HasRight(g, s.Role!.Id, MergeRight)
            ? ErrorCodes.GuildPermission : 0;
    }

    /// <summary>The refusals both sides share: an impeachment, the merge cooldown, a merge already agreed.
    /// (inferred) The "new guild" (10064/10076) and "low activity" (10065/10077) refusals are not applied: the client
    /// names no threshold.</summary>
    private int MergeBlocked(GuildStore.State own, GuildStore.State other)
    {
        long now = MergeNowMs / 1000, cd = Setting("GuildMergeTimeCD", 604800)[0];
        return other.Status != (int)GuildStatus.Normal ? ErrorCodes.GuildMissing
            : own.Impeach != null ? ErrorCodes.MergeOwnImpeach
            : other.Impeach != null ? ErrorCodes.MergeOtherImpeach
            : own.LastMergeSec > 0 && now < own.LastMergeSec + cd ? ErrorCodes.MergeOwnCooldown
            : other.LastMergeSec > 0 && now < other.LastMergeSec + cd ? ErrorCodes.MergeOtherCooldown
            // (inferred) a guild with an agreed merge takes part in no other.
            : own.PendingMergeGuildId != 0 || other.PendingMergeGuildId != 0 ? ErrorCodes.GuildPermission : 0;
    }

    private async Task MergeListsCoreAsync(GameSession s)
    {
        int code = MergeActor(s, out var g);
        var resp = new PullMergeGuildDataRes { Result = code };
        if (code == 0)
        {
            resp.RecvInvite.AddRange(g.MergeInvites.Where(i => LiveInvite(i.Value)).OrderByDescending(i => i.Value.SentMs)
                .Select(i => store.Get(i.Key)).OfType<GuildStore.State>().Select(o => Snapshot(o)));
            resp.SendInvite.AddRange(SentInvites(g).OrderByDescending(o => o.MergeInvites[g.Id].SentMs).Select(o => Snapshot(o)));
        }
        await s.SendGameAsync(MergeLists, resp);
    }

    /// <summary>(inferred) other open guilds without an agreed merge that fit into this one's free places, by level, then
    /// last week's activity (the panel sorts the same way), at most GuildRecommendRankNum.</summary>
    private async Task MergeRecommendCoreAsync(GameSession s)
    {
        int code = MergeActor(s, out var g);
        var resp = new PullRecommendMergeGuildDataRes { Result = code };
        if (code == 0)
        {
            long room = MergeRoom(g);
            resp.Data.AddRange(store.List()
                .Where(o => o.Id != g.Id && o.Status == (int)GuildStatus.Normal && o.PendingMergeGuildId == 0 && room > 0)
                .OrderByDescending(o => o.Level).ThenByDescending(o => LastWeekActivity(o)).ThenBy(o => o.Id)
                .Take((int)Setting("GuildRecommendRankNum", 20)[0]).Select(o => Snapshot(o)));
        }
        await s.SendGameAsync(MergeRecommend, resp);
    }

    /// <summary>One guild's column. The three columns are compared building by building by index, so every column lists
    /// the same buildings in the same order.</summary>
    private static GuildMergeInfo MergeColumn(GuildStore.State g, long[] buildings, long memberCount, long activity)
    {
        var info = new GuildMergeInfo { Guid = g.Id, Name = g.Name, Level = g.Level, MemberCount = (int)memberCount,
            Activity = (int)Math.Min(int.MaxValue, activity), IconInfo = Icon(g) };
        info.Buildind.AddRange(buildings.Select(b => new GuildBuilding { BuildingID = b, Level = g.Buildings.GetValueOrDefault(b) }));
        return info;
    }

    private async Task MergeDetailCoreAsync(GameSession s, MergeGuildDetailDataReq req)
    {
        int code = MergeActor(s, out var g);
        GuildStore.State? inviter = null, target = null;
        if (code == 0)
        {
            if (req.Guildid == 0)
            {
                // The merging state: the agreed pair.
                var other = g.PendingMergeGuildId != 0 ? store.Get(g.PendingMergeGuildId) : null;
                if (other == null) code = ErrorCodes.MergeInviteMissing;
                else (inviter, target) = g.MergeInviter ? (g, other) : (other, g);
            }
            else if (store.Get(req.Guildid) is not { } other || other.Id == g.Id) code = ErrorCodes.GuildMissing;
            // A sent invite or a recommended guild: this guild invites. A received invite: the other guild does.
            else (inviter, target) = req.IsInvite ? (g, other) : (other, g);
        }
        var resp = new MergeGuildDetailDataRes { Result = code };
        if (code == 0)
        {
            bool agreed = target!.PendingMergeGuildId == inviter!.Id;
            long room = MergeRoom(inviter);
            long[] buildings = inviter.Buildings.Keys.Union(target.Buildings.Keys).Order().ToArray();
            resp.TargetGuildData = MergeColumn(target, buildings, target.Members.Count, LastWeekActivity(target));
            resp.InvitedGuildData = MergeColumn(inviter, buildings, inviter.Members.Count, LastWeekActivity(inviter));
            // (inferred) the result keeps the inviter's level and buildings and adds the members moving in.
            long moving = agreed ? target.MergeMembers.Count(target.Members.ContainsKey) : Math.Min(room, target.Members.Count);
            resp.NewGuildData = MergeColumn(inviter, buildings, inviter.Members.Count + moving, LastWeekActivity(inviter));
            resp.CanMergeCount = (int)room;
            resp.Announce = agreed ? target.MergeAnnounce : target.MergeInvites.GetValueOrDefault(inviter.Id)?.Announce ?? "";
            if (agreed)
            {
                resp.Memberids.AddRange(target.MergeMembers.Where(target.Members.ContainsKey));
                resp.LastMergeTime = target.MergeExecuteSec; // "After ${1}, the Merge Guild process will be complete" (s)
            }
        }
        await s.SendGameAsync(MergeDetail, resp);
    }

    private async Task MergeInviteCoreAsync(GameSession s, InviteMergeGuildReq req)
    {
        int code = MergeActor(s, out var g);
        var target = code == 0 ? store.Get(req.Guildid) : null;
        if (code == 0 && (target == null || target.Id == g.Id)) code = ErrorCodes.GuildMissing;
        string announce = req.Announce.Trim();
        if (code == 0 && !req.IsAgree)
            code = target!.MergeInvites.Remove(g.Id) ? 0 : ErrorCodes.MergeInviteMissing;
        else if (code == 0)
        {
            long now = MergeNowMs;
            code = MergeBlocked(g, target!);
            if (code == 0)
                code = target!.MergeInvites.TryGetValue(g.Id, out var prior) && LiveInvite(prior) ? ErrorCodes.MergeInviteRepeat
                    : target.MergeRefusals.TryGetValue(g.Id, out long refused) && now < refused + MergeRefusalMs ? ErrorCodes.MergeRefused
                    : SentInvites(g).Count() >= Setting("GuildMergeListNumLimit", 5)[0] ? ErrorCodes.MergeSentLimit
                    : target.MergeInvites.Values.Count(LiveInvite) >= Setting("GuildMergeListNumLimit", 5)[0] ? ErrorCodes.MergeReceivedLimit
                    : announce.Length > Setting("GuildMergeNoticeLimitNew", 0, 50)[1] || Sensitive(announce) ? ErrorCodes.MergeAnnounce
                    // (inferred) an inviter with no free place has nobody to take in.
                    : MergeRoom(g) == 0 ? ErrorCodes.GuildFull : 0;
            if (code == 0) target!.MergeInvites[g.Id] = new GuildStore.MergeInvite { SentMs = now, Announce = announce };
        }
        if (code == 0 && !await SaveGuildAsync(target!)) code = ErrorCodes.GuildMissing;
        await s.SendGameAsync(MergeInvite, new InviteMergeGuildRes { Guildid = req.Guildid, IsAgree = req.IsAgree, Result = code });
    }

    /// <summary>ErrorCodeConfig 10058 "will not accept your invites for @{1} minutes" carries 30.</summary>
    private long MergeRefusalMs => 30 * 60_000;

    private async Task MergeAnswerCoreAsync(GameSession s, MergeGuildOpeReq req)
    {
        int code = MergeActor(s, out var g);
        var inviter = code == 0 ? store.Get(req.Guildid) : null;
        if (code == 0 && (inviter == null || inviter.Id == g.Id)) code = ErrorCodes.MergeInviteMissing;
        bool cancel = false;
        if (code == 0 && !req.IsAgree)
        {
            // (inferred) "Cancel Merge" (170311) has no request of its own; a refusal of the agreed guild cancels it.
            if (g.PendingMergeGuildId == inviter!.Id) cancel = true;
            else if (!g.MergeInvites.Remove(inviter.Id)) code = ErrorCodes.MergeInviteMissing;
            else g.MergeRefusals[inviter.Id] = MergeNowMs;
        }
        else if (code == 0)
        {
            code = !g.MergeInvites.TryGetValue(inviter!.Id, out var invite) ? ErrorCodes.MergeAcceptMissing
                : !LiveInvite(invite) ? ErrorCodes.MergeInviteExpired : MergeBlocked(g, inviter);
            if (code == 0)
            {
                long room = MergeRoom(inviter);
                if (room == 0) code = ErrorCodes.GuildFull;
                else
                {
                    g.MergeInvites.Clear();
                    g.MergeAnnounce = invite!.Announce;
                    (g.PendingMergeGuildId, g.MergeInviter) = (inviter.Id, false);
                    (inviter.PendingMergeGuildId, inviter.MergeInviter) = (g.Id, true);
                    inviter.MergeInvites.Remove(g.Id);
                    // "the guilds will be merged at 5:00 the following morning"
                    g.MergeExecuteSec = inviter.MergeExecuteSec = DayStart + 86400;
                    // (inferred) preselected: officers first, then by join time, up to the free places.
                    g.MergeMembers = g.Members.Values.OrderBy(m => m.Official).ThenBy(m => m.EnterTime)
                        .Take((int)room).Select(m => m.RoleId).ToList();
                }
            }
        }
        if (cancel) ClearMerge(g, inviter!);
        if (code == 0 && !await SavePairAsync(g, req.IsAgree || cancel ? inviter : null)) code = ErrorCodes.GuildMissing;
        await s.SendGameAsync(MergeAnswer, new MergeGuildOpeRes { Result = code, Guildid = req.Guildid });
        if (code == 0 && (req.IsAgree || cancel))
        {
            await PushBase(g, BaseInfoChange(g));
            await PushBase(inviter!, BaseInfoChange(inviter!));
        }
    }

    private async Task MergeSaveMembersCoreAsync(GameSession s, SaveMergeMemberReq req)
    {
        int code = MergeActor(s, out var g);
        var inviter = code == 0 && g.PendingMergeGuildId == req.Guildid && !g.MergeInviter ? store.Get(req.Guildid) : null;
        var chosen = req.Memberids.Distinct().ToList();
        if (code == 0 && inviter == null) code = ErrorCodes.MergeInviteMissing;
        if (code == 0 && chosen.Any(id => !g.Members.ContainsKey(id))) code = ErrorCodes.GuildMemberMissing;
        if (code == 0 && chosen.Count > MergeRoom(inviter!)) code = ErrorCodes.MergeTooMany;
        if (code == 0)
        {
            var prior = g.MergeMembers;
            g.MergeMembers = chosen;
            if (!await SaveGuildAsync(g)) { g.MergeMembers = prior; code = ErrorCodes.GuildMissing; }
        }
        var resp = new SaveMergeMemberRes { Guildid = req.Guildid, Result = code };
        resp.Memberids.AddRange(code == 0 ? chosen : req.Memberids);
        await s.SendGameAsync(MergeSaveMembers, resp);
    }

    private static void ClearMerge(params GuildStore.State[] guilds)
    {
        foreach (var g in guilds)
        {
            g.PendingMergeGuildId = 0; g.MergeInviter = false; g.MergeMembers = new(); g.MergeExecuteSec = 0; g.MergeAnnounce = "";
        }
    }

    private async Task<bool> SavePairAsync(GuildStore.State g, GuildStore.State? other)
    {
        if (other == null) return await SaveGuildAsync(g);
        var transaction = roles.NewTransaction();
        store.EnlistSave(g, transaction);
        store.EnlistSave(other, transaction);
        try { await roles.CommitAsync(transaction, "guild/merge-state"); return true; }
        catch (Microsoft.Data.Sqlite.SqliteException e) when (e.SqliteExtendedErrorCode is 1555 or 2067) { return false; }
    }

    /// <summary>Heartbeat: expired invites leave, and agreed merges whose 05:00 has come run.</summary>
    private async Task MergeDueAsync()
    {
        long now = MergeNowMs;
        foreach (var g in store.List().Where(g => g.MergeInvites.Values.Any(i => !LiveInvite(i))
            || g.MergeRefusals.Values.Any(t => now >= t + MergeRefusalMs)))
        {
            foreach (long id in g.MergeInvites.Where(i => !LiveInvite(i.Value)).Select(i => i.Key).ToArray()) g.MergeInvites.Remove(id);
            foreach (long id in g.MergeRefusals.Where(r => now >= r.Value + MergeRefusalMs).Select(r => r.Key).ToArray())
                g.MergeRefusals.Remove(id);
            await SaveGuildAsync(g);
        }
        foreach (var inviter in store.List().Where(g => g.MergeInviter && g.PendingMergeGuildId != 0 && now / 1000 >= g.MergeExecuteSec))
        {
            var target = store.Get(inviter.PendingMergeGuildId);
            if (target == null || target.PendingMergeGuildId != inviter.Id)
            {
                ClearMerge(inviter);
                if (await SaveGuildAsync(inviter)) await PushBase(inviter, BaseInfoChange(inviter));
                continue;
            }
            var moving = target.MergeMembers.Where(target.Members.ContainsKey).Distinct().ToArray();
            // "If too many members are selected, the merge fails and nothing changes" (inferred: the agreement ends).
            if (inviter.Members.Count + moving.Length > MaxMembers(inviter) || moving.Length == 0)
            {
                log.LogInformation("Guild merge {Target} into {Inviter} failed: {Moving} members for {Room} places",
                    target.Id, inviter.Id, moving.Length, MergeRoom(inviter));
                ClearMerge(inviter, target);
                if (await SavePairAsync(inviter, target))
                {
                    await PushBase(inviter, BaseInfoChange(inviter));
                    await PushBase(target, BaseInfoChange(target));
                }
                continue;
            }
            await ExecuteMergeAsync(inviter, target, moving);
        }
    }

    /// <summary>One commit: the target is disbanded (its treasury and market stock go to recovery, as for any disband),
    /// the moving members join the inviter as regular members (inferred: "each new Member's position must be
    /// reassigned"), the others are left without a guild.</summary>
    private async Task ExecuteMergeAsync(GuildStore.State inviter, GuildStore.State target, long[] moving)
    {
        long nowMs = NowMs();
        foreach (long id in moving)
        {
            var from = target.Members[id];
            inviter.Members[id] = new GuildStore.Member
            {
                RoleId = id, Name = from.Name, Level = from.Level, Career = from.Career, Official = 20, EnterTime = nowMs,
                OfflineTime = Online(id) != null ? -1 : from.OfflineTime, HistoryContribute = from.HistoryContribute,
            };
            Journal(inviter, EvJoined, [id], Str(from.Name));
        }
        ClearMerge(inviter);
        inviter.LastMergeSec = MergeNowMs / 1000;
        long leader = target.Members.Values.Single(m => m.Official == 1).RoleId;
        long[] prior = target.Members.Keys.OrderBy(id => id != leader).ToArray();
        var transaction = roles.NewTransaction();
        var economy = EnlistDisband(target, transaction, prior, "guild-merge", keepViews: moving);
        store.EnlistSave(inviter, transaction);
        EnlistViews(inviter, transaction);
        transaction.WithEconomy(economy);
        try { await roles.CommitAsync(transaction, "guild/merge"); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "Guild merge {Target} into {Inviter} failed to commit", target.Id, inviter.Id);
            return;
        }
        log.LogInformation("Guild {Target} merged into {Inviter} with {Count} members", target.Id, inviter.Id, moving.Length);
        foreach (long id in prior.Except(moving))
            if (Online(id) is { } left) { await SyncRole(left); await SendMemberClear(left, target); }
        foreach (long id in moving)
            if (Online(id) is { } moved) { await SyncRole(moved); await SendFull(moved, inviter, true); }
        await RefreshMembers(inviter);
        foreach (long role in inviter.Members.Keys.Order().ToArray())
        {
            try { await mail.SendTemplateAsync(role, MergeDoneMail, [], contentParams: [target.Name]); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Guild merge mail to role {Role} failed", role);
            }
        }
    }
}
