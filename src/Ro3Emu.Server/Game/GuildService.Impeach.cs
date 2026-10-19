using Romsg;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Leader impeachment (client "Peach"; UI_Sys_Guild_ImpeachPresidentPanel). A member of at least ActiveImpeachJoinDay
/// minutes pays ActiveImpeachMoney to start one against a leader offline ActiveImpeachChairmanOffineTimeLimit days
/// (32235 isCancle=false). Members respond (32236) within ActiveImpeachTime minutes; once ActiveImpeachOnlineScale % of
/// the members seen in the last ActiveImpeachOnlineDayNum days responded, the initiator becomes leader and the old
/// leader a regular member. The initiator can cancel (32235 isCancle=true); leaving or being kicked cancels too.
/// Every end puts ActiveImpeachCD hours of cooldown on the guild. GuildPeachInfo goes in the 32213 full info and in
/// 32231 tp=4 pushes; mails 1001-1006 tell the members (docs/systems/guild-impeachment.md).
/// </summary>
public sealed partial class GuildService
{
    public const uint Peach = 32235, PeachApply = 32236;
    private const int EvImpeach = 7; // GuildEventConfig 7 GuildImpeachLeader: "${1} impeached ${2}"
    private const int ImpeachDoneMail = 1001, ImpeachCancelMail = 1002, ImpeachLeftMail = 1003, ImpeachFailMail = 1004;
    private const int ImpeachStartMail = 1005, ImpeachCallMail = 1006;

    public Task PeachAsync(GameSession s, GuildPeachReq req) => RunGuildAsync(s, [], [], () => PeachCoreAsync(s, req),
        characterGate: !req.IsCancle && !req.OnlyCheck);
    public Task PeachApplyAsync(GameSession s) => RunGuildAsync(s, [], [], () => PeachApplyCoreAsync(s));

    private long ImpeachCooldownMs => Setting("ActiveImpeachCD", 24)[0] * 3600_000;
    private long ImpeachHours => Math.Max(1, (Setting("ActiveImpeachTime", 60)[0] + 59) / 60);

    /// <summary>The leader is offline for ActiveImpeachChairmanOffineTimeLimit days. (inferred) The other ground, a
    /// leader below ActiveImpeachWeekContribution a week for ActiveImpeachChairmanWeekNum weeks, needs a weekly
    /// contribution count the server does not keep, so only the offline ground applies.</summary>
    private bool LeaderImpeachable(GuildStore.State g)
    {
        var leader = g.Members.Values.Single(m => m.Official == 1);
        return Online(leader.RoleId) == null && leader.OfflineTime > 0
            && NowSec() - leader.OfflineTime >= Setting("ActiveImpeachChairmanOffineTimeLimit", 7)[0] * 86400;
    }

    /// <summary>canPeach shows the Members tab's Impeach button, so it stays true while an impeachment runs: that is how
    /// the other members reach the Respond button (inferred).</summary>
    private GuildPeachInfo ImpeachView(GuildStore.State g)
    {
        var view = new GuildPeachInfo { NextPeachTime = g.NextImpeachMs, CanPeach = g.Impeach != null || LeaderImpeachable(g) };
        if (g.Impeach is { } i)
        {
            view.IsPeaching = true; view.PeachOwnerID = i.OwnerId; view.NeedCount = i.NeedCount; view.PeachEndTime = i.EndMs;
            view.PeachPlayerIDs.AddRange(i.Responders);
        }
        return view;
    }

    private Task PushImpeach(GuildStore.State g) =>
        PushBase(g, new GuildBaseInfoChangeNtf { Tp = GuildBaseInfoChangeNtf.Types.ChangeType.ChangePeach, Peach = ImpeachView(g) });

    /// <summary>(inferred) responses needed: half (rounded up) of the members online or seen within a day, at least one.</summary>
    private long ImpeachNeed(GuildStore.State g)
    {
        long within = Setting("ActiveImpeachOnlineDayNum", 1)[0] * 86400, now = NowSec();
        int active = g.Members.Values.Count(m => Online(m.RoleId) != null || m.OfflineTime == -1
            || m.OfflineTime > 0 && now - m.OfflineTime <= within);
        long scale = Setting("ActiveImpeachOnlineScale", 50)[0];
        return Math.Max(1, (active * scale + 99) / 100);
    }

    private async Task PeachCoreAsync(GameSession s, GuildPeachReq req)
    {
        var g = Own(s);
        long me = s.Role!.Id, now = NowMs();
        int code = g == null ? ErrorCodes.NotInGuild
            : g.Status != (int)GuildStatus.Normal ? ErrorCodes.GuildPermission
            : req.IsCancle ? g.Impeach == null ? ErrorCodes.ImpeachNotRunning
                : g.Impeach.OwnerId != me ? ErrorCodes.ImpeachCancelState : 0
            : g.Impeach != null ? ErrorCodes.ImpeachRunning
            : g.Members[me].Official == 1 ? ErrorCodes.ImpeachSelf
            : now < g.NextImpeachMs ? ErrorCodes.ImpeachCooldown
            : now - g.Members[me].EnterTime < Setting("ActiveImpeachJoinDay", 10080)[0] * 60_000 ? ErrorCodes.ImpeachRequirements
            : !LeaderImpeachable(g) ? ErrorCodes.ImpeachNoGrounds : 0;
        if (code != 0 || req.OnlyCheck)
        {
            await s.SendGameAsync(Peach, new GuildPeachResp { Result = code, IsCancle = req.IsCancle });
            return;
        }
        var leader = g!.Members.Values.Single(m => m.Official == 1);
        var owner = g.Members[me];
        if (req.IsCancle)
        {
            var prior = g.Impeach;
            g.Impeach = null;
            long next = g.NextImpeachMs;
            g.NextImpeachMs = now + ImpeachCooldownMs;
            if (!await SaveGuildAsync(g)) { g.Impeach = prior; g.NextImpeachMs = next; code = ErrorCodes.GuildMissing; }
            await s.SendGameAsync(Peach, new GuildPeachResp { Result = code, IsCancle = true });
            if (code != 0) return;
            await ImpeachMailAsync(g.Members.Keys, ImpeachCancelMail, owner.Name, leader.Name);
            await PushImpeach(g);
            return;
        }

        g.Impeach = new GuildStore.Impeachment
        {
            OwnerId = me, BeginMs = now, EndMs = now + Setting("ActiveImpeachTime", 60)[0] * 60_000,
            NeedCount = ImpeachNeed(g), Responders = [me],
        };
        var cost = Setting("ActiveImpeachMoney", 3, 30);
        bool paid;
        try
        {
            paid = cost.Length < 2 || cost[0] <= 0 || cost[1] <= 0 ? await SaveGuildAsync(g)
                : await character.ExchangeLockedAsync(s, [((int)cost[0], cost[1])], [],
                    persist: tx => { store.EnlistSave(g, tx, "guild disappeared during impeachment"); EnlistViews(g, tx);
                        tx.WithEconomy(GuildPayment(s, "guild-impeach", (int)cost[0], cost[1])); }, asyncCommit: true) != null;
            if (!paid) code = cost.Length >= 2 && cost[0] > 0 ? ErrorCodes.ItemsShort : ErrorCodes.GuildMissing;
        }
        catch (InvalidOperationException e) when (e.Message == "guild disappeared during impeachment") { code = ErrorCodes.GuildMissing; }
        if (code != 0)
        {
            g.Impeach = null;
            await s.SendGameAsync(Peach, new GuildPeachResp { Result = code, IsCancle = req.IsCancle });
            return;
        }
        await s.SendGameAsync(Peach, new GuildPeachResp { Result = 0 });
        long hours = ImpeachHours, need = g.Impeach.NeedCount;
        await ImpeachMailAsync([me], ImpeachStartMail, hours, need);
        await ImpeachMailAsync(g.Members.Keys.Where(id => id != me), ImpeachCallMail, owner.Name, leader.Name, hours, need, owner.Name);
        await PushImpeach(g);
        if (g.Impeach.Responders.Count >= need) await ImpeachSucceedAsync(g);
    }

    private async Task PeachApplyCoreAsync(GameSession s)
    {
        var g = Own(s);
        long me = s.Role!.Id;
        int code = g == null ? ErrorCodes.NotInGuild
            : g.Impeach == null ? ErrorCodes.ImpeachNotRunning
            : g.Impeach.Responders.Contains(me) ? ErrorCodes.ImpeachRepeat
            // (inferred) the leader cannot vote against themselves.
            : g.Members[me].Official == 1 ? ErrorCodes.ImpeachRequirements : 0;
        if (code == 0)
        {
            g!.Impeach!.Responders.Add(me);
            if (!await SaveGuildAsync(g)) { g.Impeach.Responders.Remove(me); code = ErrorCodes.GuildMissing; }
        }
        await s.SendGameAsync(PeachApply, new GuildPeachApplyResp { Result = code });
        if (code != 0) return;
        await PushImpeach(g!);
        if (g!.Impeach!.Responders.Count >= g.Impeach.NeedCount) await ImpeachSucceedAsync(g);
    }

    /// <summary>(inferred) the swap happens as soon as enough members responded, not at the end of the hour.</summary>
    private async Task ImpeachSucceedAsync(GuildStore.State g)
    {
        var owner = g.Members[g.Impeach!.OwnerId];
        var leader = g.Members.Values.Single(m => m.Official == 1);
        var prior = g.Impeach;
        long next = g.NextImpeachMs;
        leader.Official = 20;
        owner.Official = 1;
        g.Impeach = null;
        // (inferred) the cooldown follows every end, as it follows a cancel.
        g.NextImpeachMs = NowMs() + ImpeachCooldownMs;
        Journal(g, EvImpeach, [owner.RoleId, leader.RoleId], Str(owner.Name), Str(leader.Name));
        if (!await SaveGuildAsync(g))
        {
            leader.Official = 1; owner.Official = 20; g.Impeach = prior; g.NextImpeachMs = next; g.PendingEvents.Clear();
            return;
        }
        log.LogInformation("Guild {Guild}: {Owner} impeached leader {Leader}", g.Id, owner.RoleId, leader.RoleId);
        await RefreshMembers(g);
        await PushImpeach(g);
        await ImpeachMailAsync(g.Members.Keys, ImpeachDoneMail, owner.Name, leader.Name);
    }

    /// <summary>Ended by time with too few responses: mail 1004 and the cooldown.</summary>
    private async Task ExpireImpeachmentsAsync()
    {
        long now = NowMs();
        foreach (var g in store.List().Where(g => g.Impeach is { } i && i.EndMs <= now))
        {
            var owner = g.Members.GetValueOrDefault(g.Impeach!.OwnerId);
            g.Impeach = null;
            g.NextImpeachMs = now + ImpeachCooldownMs;
            if (!await SaveGuildAsync(g)) continue;
            await PushImpeach(g);
            await ImpeachMailAsync(g.Members.Keys, ImpeachFailMail, owner?.Name ?? "");
        }
    }

    /// <summary>Before a leave or kick saves: the initiator leaving ends the impeachment (25103); a responder leaving takes
    /// back the response (inferred). Returns the mail to send once the save committed.</summary>
    private Func<Task>? ImpeachMemberLeaving(GuildStore.State g, GuildStore.Member member)
    {
        if (g.Impeach is not { } impeach) return null;
        if (impeach.OwnerId != member.RoleId)
        {
            if (!impeach.Responders.Remove(member.RoleId)) return null;
            return () => PushImpeach(g);
        }
        string leader = g.Members.Values.Single(m => m.Official == 1).Name;
        g.Impeach = null;
        g.NextImpeachMs = NowMs() + ImpeachCooldownMs;
        return async () =>
        {
            await PushImpeach(g);
            await ImpeachMailAsync(g.Members.Keys.Append(member.RoleId), ImpeachLeftMail, member.Name, leader);
        };
    }

    private async Task ImpeachMailAsync(IEnumerable<long> roles, int template, params object[] values)
    {
        foreach (long role in roles.Distinct().Order().ToArray())
        {
            try { await mail.SendTemplateAsync(role, template, [], contentParams: values); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Impeachment mail {Template} to role {Role} failed", template, role);
            }
        }
    }
}
