using Google.Protobuf;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Micro;

/// <summary>
/// "UserDataService" — the client's global role cache (head names of players, friend lists...). The client asks
/// getAttributeBatchByVersion {"UidFieldVersions": {"&lt;uid&gt;": {"&lt;Type&gt;": version}}} for every player unit,
/// including itself.
/// </summary>
public sealed class UserDataService(SocialOwnerCache cache, OwnerRegistry owners, GameLoop loop, SessionRegistry sessions, GuildService guilds,
    GuildStore guildStore, IOptions<ServerOptions> options, ILogger<UserDataService> log) : IMicroService
{
    // (inferred) Full profile replies use increasing versions, so a previous guild/online snapshot is replaceable.
    // The original per-field version allocation is not exported; this service already returns all requested roles.
    private long version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public string Name => "UserDataService";

    public async ValueTask<object?> InvokeAsync(MicroSession session, string method, object? args) =>
        method == "getAttributeBatchByVersion" ? await loop.InvokeAsync(() => GetAttributesAsync(args)) : null;

    private async Task<object?> GetAttributesAsync(object? args)
    {
        var results = new List<object>();
        if (args is IDictionary<object, object?> map && map.TryGetValue("UidFieldVersions", out var v) && v is IDictionary<object, object?> uids)
        {
            var ids = uids.Keys.Select(key => long.TryParse(key.ToString(), out long id) ? id : 0L)
                .Where(id => id > 0).Distinct().ToArray();
            // Guild state has an entity revision separate from the role vector. Until guild publication
            // reaches this projection, a small worker snapshot validates it on every batch.
            while (true)
            {
                var stamps = await cache.GuildStampsAsync(ids);
                using var scope = await owners.EnterAsync("micro/user-data",
                    ids.Select(id => ReservationKey.Role(id)).Concat(stamps.Values.Where(s => s.Id != 0)
                        .Select(s => ReservationKey.Entity("guild", s.Id.ToString()))).Distinct().ToArray());
                var stable = await cache.GuildStampsAsync(ids);
                if (ids.Any(id => stamps[id] != stable[id])) continue;
                var profiles = await cache.LoadProfilesAsync(ids, stable);
                foreach (var key in uids.Keys)
                {
                    if (!long.TryParse(key.ToString(), out long uid)) continue;
                    if (profiles.TryGetValue(uid, out var profile) && profile is not null) results.Add(Entry(profile));
                    else if (GuildEntry(uid) is { } guild) results.Add(guild);
                }
                break;
            }
        }
        log.LogDebug("getAttributeBatchByVersion -> {Count} role(s)", results.Count);
        return new Dictionary<string, object?> { ["Ret"] = MicroServiceRouter.Ret(true), ["Results"] = results.ToArray() };
    }

    /// <summary>
    /// Guild rows of the leaderboards bind GuildSnap by guild id, not by role (UI_Sys_RankPower_ListCellWidgetCtrl
    /// _bindGuildId 1610-1641 / _onGuildLeaderChanged 2895-2921), through the same global cache request. Guild ids
    /// (guild table, from 1) never meet role ids (role sequence starts at 100000), so an unknown role uid that names a
    /// formal guild is answered with that guild's GuildSnap only; LC_DAL_RolePublic decodes Base only when present
    /// (:627-640).
    /// </summary>
    private Dictionary<string, object?>? GuildEntry(long uid)
    {
        if (uid <= 0 || guildStore.Get(uid) is not { Status: (int)GuildStatus.Normal } guild) return null;
        long snapshotVersion = Interlocked.Increment(ref version);
        return new()
        {
            ["Uid"] = uid,
            ["FieldsVersion"] = new Dictionary<string, object?> { ["GuildSnap"] = snapshotVersion },
            ["Data"] = new Dictionary<string, object?> { ["GuildSnap"] = guilds.Snapshot(guild).ToByteArray() },
        };
    }

    /// <summary>
    /// "Base" (romsg.RoleAdditionBaseInfo bytes) must always be present; Name/Level/Online/ClusterId are plain values.
    /// The guild roster reads PersonalSystem.Base, not GuildMemberInfo's name/level/online fields
    /// (UI_Sys_Guild_Member_Info_ListWidgetCtrl.lua:160-196,309). OfflineTime is milliseconds, -1 when online.
    /// Membership comes from the exclusive guild index; a stale personal 5047 blob cannot restore a departed guild.
    /// GuildSnap bytes also reset after leaving (LC_DAL_RolePublic.lua:960-980).
    /// </summary>
    private Dictionary<string, object?> Entry(Accounts.Persistence.ProfileSnapshot profile)
    {
        var role = profile.Role;
        var session = sessions.All().FirstOrDefault(s => s.Role?.Id == role.Id);
        var live = session?.Progress;
        var guild = profile.Guild;
        var member = guild?.Members.GetValueOrDefault(role.Id);
        var personal = profile.PersonalGuild is { } blob ? DBRoleGuildInfo.Parser.ParseFrom(blob) : null;
        // (inferred) After an unclean stop, use last login if no positive guild logout timestamp was persisted.
        long offline = live != null ? -1L : (member?.OfflineTime is > 0 ? member.OfflineTime : role.LastLoginTime) * 1000;
        var additional = new RoleAdditionBaseInfo
        {
            Career = live?.Career ?? role.Career, Gender = role.Sex, JobLevel = live?.JobLevel ?? 1, BornArea = role.Area,
            GuildID = guild?.Id ?? 0, GuildOfficial = member?.Official ?? 0,
            CantBeInvited = personal?.CantBeInvited ?? false, Offlinetime = offline,
        };
        additional.Appearances.Add(session?.Wardrobe?.CurUseAppearances.ToDictionary(p => p.Key, p => p.Value)
            ?? RoleProto.Appearances(role, profile.Wardrobe));
        var fields = new Dictionary<string, object?>
        {
            ["Base"] = additional.ToByteArray(),
            ["Name"] = role.Name,
            ["Level"] = live?.Level ?? role.Level,
            ["Online"] = live != null,
            ["OfflineTime"] = offline,
            ["ClusterId"] = options.Value.ClusterId,
            ["GuildSnap"] = (guild == null ? new GuildSnapInfo() : guilds.Snapshot(guild)).ToByteArray(),
        };
        long snapshotVersion = Interlocked.Increment(ref version);
        return new()
        {
            ["Uid"] = role.Id,
            ["FieldsVersion"] = fields.Keys.ToDictionary(k => k, _ => (object?)snapshotVersion),
            ["Data"] = fields,
        };
    }
}
