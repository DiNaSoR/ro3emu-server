using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The Tavern (client "BarRoom", 酒馆): player-run chat and voice rooms opened from the chat window (function 2410, chat
/// channel 10). Client: Logic/ChatBarroom (LC_BarRoomHelper, LC_BarRoomServer, LC_BarRoomTalk), LC_DAL_BarRoom and the
/// UI_Sys_Chat_BarRoom* widgets; protocol MG_Barroom.proto 82481-82514. docs/systems/tavern.md has the whole picture.
/// <list type="bullet">
/// <item>Rooms live in memory (a restart clears them). A room has an owner, members, a mic list (curSpeaks), requests to
/// speak, a mute list, a block list and Popularity (hotValue). The owner is always on the mic.</item>
/// <item>Every change pushes 82506 BarRoomInfoUpdateNotify and the room's 82508 BarRoomGetResp to every member: the
/// client asks for 82508 on 82506 only while the tavern window is open (UI_Sys_Chat_BarRoomWindowCtrl), and its 82508
/// handler (LC_DAL_BarRoom.OnGetBarRoomPartyInfo -> LC_BarRoomHelper.OnPartyGetClient) replaces BarRoomTotalDetail.</item>
/// <item>The room list is PartyService.getPartysSummary with partyType 4, each summary a BarRoomSummary
/// (LC_BarRoomHelper.OnPartyQuerySummary); see <see cref="Directory"/>.</item>
/// <item>Text chat is IM channel "10_&lt;room&gt;" (enum_EPublicChatChannelType.EChannelBarRoom, LC_ChatHelper
/// ChooseMessageTargetChannelId); only members may join it (<see cref="InRoom"/>). Voice is the relay room of the
/// same id (LC_BarRoomTalk.JoinChannel -> VoiceCall_Util.JoinChannel(tostring(room))), see <see cref="MayTalk"/>.</item>
/// <item>5043 DBBarRoomInfo: barRoomId (LC_BarRoomHelper.OnGetBarRoomID re-joins the room's chat at login), the
/// Star Power received from gifts (popularity, weekPopularity; achievement 106008), today's seconds in a room
/// (inBarRoomTime; quest target 106010 of the Daily Tavern quest 2000) and the days it was done (actDays; 106007).</item>
/// </list>
/// Leaving the world leaves the room (inferred: the client never re-enters on login, and an offline member could not
/// be removed or talk).
/// </summary>
public sealed partial class TavernService(GameData data, RoleStore roles, CharacterService character, SessionRegistry sessions, GameLoop loop,
    IOptions<ServerOptions> options, TimeProvider clock, ILogger<TavernService> log) : IGameTicker
{
    public const uint Create = 82481, Enter = 82482, Exit = 82483, Invite = 82484, InviteNotify = 82485, InviteResponse = 82486,
        ApplyMic = 82487, DownMic = 82488, Transfer = 82489, MicMode = 82490, Gift = 82491, HandleMic = 82492, MicOrder = 82493,
        SetInfo = 82494, Forbid = 82498, CancelForbid = 82499, Kick = 82500, CancelKick = 82501, ShutUpAll = 82504,
        CancelShutUpAll = 82505, InfoNotify = 82506, UserEnter = 82507, Get = 82508, GmSetInfo = 82513, MutePlayer = 82514,
        Sync = 5043;

    /// <summary>LC_BarRoomServer / LC_DAL_BarRoom partyType of the room list (PartyService).</summary>
    public const int PartyType = 4;

    // ErrorCodeConfig 2551-2580 (the Tavern block).
    internal const int ErrCleared = 2551, ErrNoRoom = 2552, ErrBlocked = 2553, ErrNoPermission = 2554, ErrGiftLeft = 2555,
        ErrData = 2556, ErrTransferBlocked = 2557, ErrTargetMuted = 2558, ErrMicFull = 2559, ErrOnMic = 2560,
        ErrOwnerMic = 2561, ErrNotOnMic = 2562, ErrGiftOffline = 2563, ErrTooFast = 2573, ErrGiftSelf = 2574,
        ErrNotBlocked = 2566, ErrFreeMic = 2567, ErrApplied = 2568, ErrNotApplied = 2569, ErrNotMuted = 2570,
        ErrInRoom = 2571, ErrFull = 2572, ErrTransferCd = 2575, ErrNameLong = 2576, ErrName = 2577, ErrNotInRoom = 2578,
        ErrType = 2579, ErrLanguage = 2580;

    /// <summary>ItemConfig _iType 2 / _iSubType 213: the four Tavern gifts (UI_Sys_Chat_BarRoom_GiveawayWidgetCtrl).
    /// _iUseEffect 110 is the room's Popularity, 111 the recipient's Star Power, per gift (item texts 12390100369/70).</summary>
    private const int GiftType = 2, GiftSubType = 213, EffectPopularity = 110, EffectStar = 111;

    private readonly GameData _data = data;
    private readonly RoleStore _roles = roles;
    private readonly CharacterService _character = character;
    private readonly SessionRegistry _sessions = sessions;
    private readonly GameLoop _loop = loop;
    private readonly IOptions<ServerOptions> _options = options;
    private readonly TimeProvider _clock = clock;
    private readonly ILogger<TavernService> _log = log;

    internal sealed class Room
    {
        public long Id;
        public int Type, Language, Hot, Region;
        public string Name = "";
        public long Owner, Created, TransferCd, InfoSetAt;
        public SpeakAuthorityType Mode = SpeakAuthorityType.Free;
        public bool MicClosed;
        public int GmSpecial;
        /// <summary>Members in join order: role, area, unix seconds of the join.</summary>
        public readonly List<(long Role, long Area, long Joined)> Members = new();
        public readonly HashSet<long> Blocked = new(), Muted = new();
        public readonly Dictionary<long, string> Requests = new();
        /// <summary>The mic list in order (micOrder 1..n); the owner is always on it.</summary>
        public readonly List<long> Speakers = new();
        public bool Has(long role) => Members.Any(m => m.Role == role);
    }

    private readonly Dictionary<long, Room> _rooms = new();
    private readonly Dictionary<long, Room> _byMember = new();
    private readonly Dictionary<(long Target, long Room), long> _invites = new();
    /// <summary>Room ids sit far above team ids (TeamService starts at 2^24), so the voice relay keys never collide.</summary>
    private long _nextId = 1L << 40;
    private TimeSpan _sinceCount;

    /// <summary>A role's Star Power or completed days changed (achievements 106007 / 106008 re-read them).</summary>
    public List<Action<GameSession>> Changed { get; } = new();

    /// <summary>A role left room (the chat channel stops reaching it).</summary>
    public List<Action<GameSession, long>> Left { get; } = new();

    /// <summary>Seconds a role spent in a room since the last count (quest target 106010, TaskService).</summary>
    public List<Action<GameSession, int>> TimeSpent { get; } = new();

    private long Now => _clock.GetUtcNow().ToUnixTimeSeconds();

    private long Param(string key, int index = -1, long fallback = 0)
    {
        if (!_data.Table("PublicParamConfig").TryGetValue(key, out var value)) return fallback;
        if (index >= 0)
            return value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > index && GameData.Number(value[index]) is { } n
                ? (long)n : fallback;
        return GameData.Number(value) is { } number ? (long)number : fallback;
    }

    private int NameLength => (int)Param("BarRoomNameLen", fallback: 18);
    private int Capacity => (int)Param("BarroomPersonNum", fallback: 1000);
    private int SpeakerLimit => (int)Param("BarroomSpeakNum", fallback: 10);
    private long InfoCooldown => Param("BarRoomSetInfoCd", fallback: 5);
    private long TransferCooldown => Param("BarRoomTransferRoomCd", fallback: 30);

    private GameSession? Online(long role) => _sessions.All().FirstOrDefault(s => s.Role?.Id == role && s.Tavern != null);

    // ------------------------------------------------------------------ queries for chat, voice and the directory

    /// <summary>The room a role is in, 0 when none.</summary>
    public long RoomOf(long role) => _byMember.TryGetValue(role, out var room) ? room.Id : 0;

    /// <summary>The role is a member of room <paramref name="room"/> (chat channel 10 and the voice relay).</summary>
    public bool InRoom(long role, long room) => room != 0 && RoomOf(role) == room;

    /// <summary>
    /// Whether the role may use its mic in its room (LC_BarRoomHelper.CheckMemberCanOpenMic): the owner always; nobody
    /// muted or while "mute all" is on; anyone in Free mode, else only those on the mic list.
    /// </summary>
    public bool MayTalk(long role)
    {
        if (!_byMember.TryGetValue(role, out var room)) return false;
        if (room.Owner == role) return true;
        if (room.MicClosed || room.Muted.Contains(role)) return false;
        return room.Mode == SpeakAuthorityType.Free || room.Speakers.Contains(role);
    }

    internal Room? RoomById(long id) => _rooms.GetValueOrDefault(id);

    /// <summary>
    /// PartyService getPartysSummary for partyType 4: one GameSummaryData (a BarRoomSummary) per room, busiest first
    /// (inferred order). LC_BarRoomServer.PartyQuerySummaryRequest's "Discover" query adds PartyIntCondition
    /// BaroomCreateTime [now - 1800, now + 1800] and BaroomHeadCount [2, 30] (PublicParamConfig.BarroomFindBarRoom) and
    /// joins a random room of the answer (TryJoinRandomRoom).
    /// </summary>
    public object[] Directory(IReadOnlyDictionary<string, (long Min, long Max)> ranges, int offset, int limit)
    {
        IEnumerable<Room> rooms = _rooms.Values;
        if (ranges.TryGetValue("BaroomCreateTime", out var created)) rooms = rooms.Where(r => r.Created >= created.Min && r.Created <= created.Max);
        if (ranges.TryGetValue("BaroomHeadCount", out var heads)) rooms = rooms.Where(r => r.Members.Count >= heads.Min && r.Members.Count <= heads.Max);
        return rooms.OrderByDescending(r => r.Hot).ThenByDescending(r => r.Members.Count).ThenBy(r => r.Id)
            .Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 0, 100))
            .Select(r => (object)new Dictionary<string, object?> { ["GameSummaryData"] = Summary(r).ToByteArray() }).ToArray();
    }

    /// <summary>getSubPartysCountByType for partyType 4: rooms per room type (BarRoomAllTargetCount).</summary>
    public object[] TypeCounts() => _rooms.Values.GroupBy(r => r.Type).OrderBy(g => g.Key)
        .Select(g => (object)new Dictionary<string, object?> { ["SubType"] = g.Key, ["Count"] = g.Count() }).ToArray();

    // ------------------------------------------------------------------ wire views

    private static BarRoomSummary Summary(Room room)
    {
        var summary = new BarRoomSummary
        {
            BarRoomGuid = room.Id, BarRoomType = room.Type, LanguageType = room.Language, BarRoomName = room.Name,
            HotValue = room.Hot, RegionId = room.Region,
        };
        summary.MemberList.Add(room.Members.Select(m => new MemberInfo { MemberId = m.Role, AreaId = m.Area, JoinTime = m.Joined }));
        return summary;
    }

    internal static BarRoomTotalDetail Detail(Room room)
    {
        var detail = new BarRoomTotalDetail
        {
            BarRoomSummary = Summary(room), ControlMicType = room.Mode, IsCloseMic = room.MicClosed, OwnerGuid = room.Owner,
            CreateTime = room.Created, TransferCd = room.TransferCd, HistoryHot = new HistoryHotInfo { LastHot = room.Hot },
            GmSpRoomCount = room.GmSpecial,
        };
        // LC_BarRoomHelper.CheckIsForbiddenUser compares the map's values with the role id; keys and values are both
        // the role for both lists (inferred for the keys).
        foreach (long role in room.Blocked) detail.BlackList[role] = role;
        foreach (long role in room.Muted) detail.ForbiddenSpeaker[role] = role;
        foreach (var (role, reason) in room.Requests) detail.ApplyUsers[role] = reason;
        for (int i = 0; i < room.Speakers.Count; i++) detail.CurSpeaks.Add(new CurSpeakUsers { UserId = room.Speakers[i], MicOrder = i + 1 });
        return detail;
    }

    /// <summary>82506 and the room's 82508 to every member online (but <paramref name="except"/>).</summary>
    private async Task PublishAsync(Room room, long except = 0)
    {
        var get = new BarRoomGetResp { Result = 0, Info = Detail(room) };
        var notify = new BarRoomInfoUpdateNotify { BarRoomGuid = room.Id };
        foreach (var (role, _, _) in room.Members.ToArray())
            if (role != except && Online(role) is { } member)
            {
                await member.SendGameAsync(InfoNotify, notify);
                await member.SendGameAsync(Get, get);
            }
    }

    // ------------------------------------------------------------------ login, leave, 5043

    public async Task LoginAsync(GameSession s)
    {
        var bytes = _roles.LoadTavern(s.Role!.Id);
        var info = bytes is { Length: > 0 } ? DBBarRoomInfo.Parser.ParseFrom(bytes) : new DBBarRoomInfo();
        RollDay(info, Now);
        // Rooms live in memory: whatever room the blob names is gone, unless the role is still listed in one.
        info.BarRoomId = RoomOf(s.Role.Id);
        s.Tavern = info;
        s.TavernCountedAt = info.BarRoomId != 0 ? Now : 0;
        await SyncAsync(s);
    }

    private static Task SyncAsync(GameSession s) => CharacterService.SyncAsync(s, Sync, s.Tavern!.Clone());

    public async Task LeaveRoleAsync(GameSession s)
    {
        if (s.Role is not { } role || s.Tavern == null) return;
        if (_byMember.TryGetValue(role.Id, out var room)) await LeaveRoomAsync(s, room, notifySelf: false);
        await SaveAsync(s);
    }

    /// <summary>Awaited by every caller: a dropped commit resumes on the loop after its caller's work (a role
    /// handover's cleanup) has finished, as an untracked orphan continuation.</summary>
    private Task SaveAsync(GameSession s)
    {
        if (s.Role == null || s.Tavern == null) return Task.CompletedTask;
        var stored = s.Tavern.Clone();
        stored.BarRoomId = 0;
        var transaction = _roles.NewTransaction();
        transaction.RoleBlob(s.Role.Id, "barroom", stored.ToByteArray());
        return _roles.CommitAsync(transaction, "tavern/save");
    }

    /// <summary>The counters of a new day (05:00 realm time) and week (Monday 05:00, as Popularity), inferred.
    /// beginBarRoomTime holds the start of the day inBarRoomTime counts.</summary>
    private void RollDay(DBBarRoomInfo info, long now)
    {
        long day = DayStart(now);
        if (info.BeginBarRoomTime != day)
        {
            if (info.BeginBarRoomTime < PersonalService.WeekStartMs(_clock) / 1000) info.WeekPopularity = 0;
            info.BeginBarRoomTime = day;
            info.InBarRoomTime = 0;
            info.IsToDayActComplete = false;
        }
    }

    private long DayStart(long now)
    {
        var local = DateTimeOffset.FromUnixTimeSeconds(now).ToOffset(_clock.GetLocalNow().Offset);
        var day = local.AddHours(-5).Date.AddHours(5);
        return new DateTimeOffset(day, local.Offset).ToUnixTimeSeconds();
    }

    /// <summary>PublicParamConfig.BarroomTaskID's quest (2000 "Spend @{1} minutes in any Tavern room") needs this many
    /// seconds (its _kProgressTarget [106010, 1200]).</summary>
    private long DailySeconds()
    {
        long task = Param("BarroomTaskID", fallback: 2000);
        if (_data.Row("TaskConfig", task) is { } row && row.TryGetProperty("_kProgressTarget", out var targets)
            && targets.ValueKind == JsonValueKind.Array)
            foreach (var target in targets.EnumerateArray())
                if (target.ValueKind == JsonValueKind.Array && target.GetArrayLength() == 2
                    && GameData.Number(target[0]) == 106010 && GameData.Number(target[1]) is { } seconds) return (long)seconds;
        return 1200;
    }

    // ------------------------------------------------------------------ time in a room

    /// <summary>Today's seconds in a room (quest target 106010); 0 when the stored day is over.</summary>
    public long SecondsToday(GameSession s) =>
        s.Tavern is { } info && info.BeginBarRoomTime == DayStart(Now) ? info.InBarRoomTime : 0;

    /// <summary>Star Power received (achievement 106008).</summary>
    public static long StarPower(GameSession s) => s.Tavern?.Popularity ?? 0;

    /// <summary>Days the Tavern quest time was reached (achievement 106007).</summary>
    public static long QuestDays(GameSession s) => s.Tavern?.ActDays ?? 0;

    private static readonly TimeSpan CountEvery = TimeSpan.FromSeconds(10);

    public void Tick(GameTime time)
    {
        _sinceCount += time.Delta;
        if (_sinceCount < CountEvery) return;
        _sinceCount = TimeSpan.Zero;
        foreach (var room in _rooms.Values.ToArray())
            foreach (var (role, _, _) in room.Members.ToArray())
                if (Online(role) is { } member) _loop.PostOwned(member, () => CountAsync(member), "tavern time");
    }

    private async Task CountAsync(GameSession s)
    {
        if (s.Role == null || !_byMember.ContainsKey(s.Role.Id)) return;
        await CountCoreAsync(s, sync: true);
    }

    /// <summary>Adds the seconds since the last count to today's time; the day's quest time done marks the day.</summary>
    private async Task CountCoreAsync(GameSession s, bool sync)
    {
        if (s.Tavern is not { } info || s.Role == null || s.TavernCountedAt == 0) return;
        long now = Now;
        long since = Math.Max(s.TavernCountedAt, DayStart(now));
        RollDay(info, now);
        int seconds = (int)Math.Clamp(now - since, 0, 3600);
        s.TavernCountedAt = now;
        if (seconds == 0) return;
        info.InBarRoomTime = (int)Math.Min(int.MaxValue, (long)info.InBarRoomTime + seconds);
        bool done = !info.IsToDayActComplete && info.InBarRoomTime >= DailySeconds();
        if (done)
        {
            info.IsToDayActComplete = true;
            info.ActDays++;
            await SaveAsync(s);
        }
        foreach (var handler in TimeSpent) handler(s, seconds);
        if (done) foreach (var handler in Changed) handler(s);
        if (sync) await SyncAsync(s);
    }
}
