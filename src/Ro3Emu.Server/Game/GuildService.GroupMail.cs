using System.Text;
using Romsg;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Guild Group Mail (32241 GuildBroadEmailReq, the Members tab's Mail button): one letter in the Guild mailbox tab
/// (category 3) to every member. Right 6 (GuildSendEmailPermission), GuildMassEmailNum letters a guild day,
/// GuildMassEmailCD seconds between them, GuildEmailContentNumNew characters. The 32231 tp=6 push and the 32213 full
/// info carry the next allowed time in ms and the count used today, which the button reads (docs/systems/guild-group-mail.md).
/// </summary>
public sealed partial class GuildService
{
    public const uint BroadEmail = 32241;

    public Task BroadEmailAsync(GameSession s, GuildBroadEmailReq req) => RunGuildAsync(s, [], [], () => BroadEmailCoreAsync(s, req));

    /// <summary>Today's 05:00 local, unix seconds: (inferred) the guild's daily boundary, as <see cref="WeekStart"/> is weekly.</summary>
    private long DayStart
    {
        get
        {
            var local = clock.GetLocalNow();
            var day = local.AddHours(-5).Date;
            return new DateTimeOffset(day.AddHours(5), local.Offset).ToUnixTimeSeconds();
        }
    }

    private int BroadEmailUsed(GuildStore.State g) => g.BroadEmailDay == DayStart ? g.BroadEmailCount : 0;

    private GuildBaseInfoChangeNtf BroadEmailChange(GuildStore.State g) => new()
    {
        Tp = GuildBaseInfoChangeNtf.Types.ChangeType.ChangeBroadEmail,
        NextBroadEmailTime = g.NextBroadEmailMs, BroadEmailLimit = BroadEmailUsed(g),
    };

    private async Task BroadEmailCoreAsync(GameSession s, GuildBroadEmailReq req)
    {
        var g = Own(s);
        string content = (req.Req?.Content ?? "").Trim();
        long now = clock.GetUtcNow().ToUnixTimeMilliseconds(); // the same clock as the guild day
        int code = g == null ? ErrorCodes.NotInGuild
            : g.Status != (int)GuildStatus.Normal ? ErrorCodes.GuildPermission
            : !HasRight(g, s.Role!.Id, (int)Setting("GuildSendEmailPermission", 6)[0]) ? ErrorCodes.GuildPermission
            : BroadEmailUsed(g) >= Setting("GuildMassEmailNum", 10)[0] ? ErrorCodes.GuildMailDailyLimit
            // (inferred) the client counts the cooldown down itself (170096); the server answers the generic cooldown.
            : now < g.NextBroadEmailMs ? ErrorCodes.Cooldown
            : content.Length == 0 ? ErrorCodes.MailBodyEmpty
            : content.Length > Setting("GuildEmailContentNumNew", 0, 80)[1]
              || Encoding.UTF8.GetByteCount(content) > Setting("GuildMassEmailMaxContent", 200)[0] ? ErrorCodes.GuildTextLength
            : Sensitive(content) ? ErrorCodes.MailBodySensitive : 0;
        if (code == 0)
        {
            int used = BroadEmailUsed(g!);
            long day = g!.BroadEmailDay, next = g.NextBroadEmailMs;
            g.BroadEmailDay = DayStart;
            g.BroadEmailCount = used + 1;
            g.NextBroadEmailMs = now + Setting("GuildMassEmailCD", 3600)[0] * 1000;
            if (!await SaveGuildAsync(g))
            {
                (g.BroadEmailDay, g.BroadEmailCount, g.NextBroadEmailMs) = (day, used, next);
                code = ErrorCodes.GuildMissing;
            }
        }
        await s.SendGameAsync(BroadEmail, code != 0 ? new GuildBroadEmailResp { Result = code }
            : new GuildBroadEmailResp { NextBroadEmailTime = g!.NextBroadEmailMs, BroadEmailLimit = BroadEmailUsed(g) });
        if (code != 0) return;

        // The client titles the letter with the guild name; the server uses its own copy of that name.
        // (inferred) every member gets it, the sender too, for MailConfig 1019's 30 days.
        var expires = DateTimeOffset.FromUnixTimeMilliseconds(now).AddDays(30);
        foreach (long role in g.Members.Keys.Order().ToArray())
        {
            try { await mail.SendRawAsync(role, g.Name, content, sender: s.Role!.Name, expiresAt: expires, category: 3); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Guild {Guild} group mail to role {Role} failed", g.Id, role);
            }
        }
        await PushBase(g, BroadEmailChange(g));
    }
}
