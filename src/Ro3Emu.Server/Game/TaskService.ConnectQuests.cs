using System.Globalization;
using System.Text.Json;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Connect Quests (TaskConfig type 19, 1901000-1905110, "Connect Quest - Defeat the MVP Angeling with a Party" and 52
/// more): the friendship system's activity sources ("Connect" is the English client's word for the social system,
/// Localization 10500000002). Localization 10960000069 says friends gain Intimacy by gifts and by "participating in
/// activities with friends", and that the activity amount is limited each day.
/// <para>How players receive them: never by hand. enum_TaskType (Lua_GameDefine) has no 19, and both the quest log
/// (UI_Sys_TaskWidgetCtrl._refreshTaskInfo) and the HUD tracker (UI_Sys_Main_TeamUp_TaskWidgetBaseCtrl via
/// LC_TaskHelper.GetCurrentMainTrackTask) list only the types LC_TaskHelper.CheckShowMainTask allows (Main, Side, Guide,
/// OnePiece, Dungeon, Delivery, Explore, Branch, RingTask), so the client never shows them. Every row is Auto accept and
/// Auto delivery (_kAcceptTask [1], _kDeliverTask [1]) with no pre-task, condition, award or EXP, and its only effect is
/// the server-host action 116 UpdateSocialityScore [116, SocialRelationsConfig id]. The server therefore keeps all 53 of
/// them running for every role, and a delivery re-arms the quest at once. Repeating is inferred: _iIfRepeat is 0, but a
/// single pass over all 53 (670 Intimacy in all) could not fill a scale that runs to 180,000 (FriendlinessConfig).</para>
/// <para>Targets: these four ids are not in the client's enum_Progress (LC_ConditionDefine); their meaning is the quest
/// text, so every rule below is inferred. "In a party" is a party of at least two (TeamService).
/// - 106012 [106012, n, npc] "Defeat MVP Angeling in a Party": n field MVP kills of that NPCConfig id (MvpChallengeConfig)
///   credited by MvpService's participant settlement while in a party.
/// - 107005 [107005, n, boss] "Defeat Golden Thief Bug (Normal) in a party", "Defeat Normal Ishti in a party": n committed
///   clears of that TenGameplayBossConfig._iBoss with at least one other player in the run (DungeonService).
/// - 106013 [106013, n] "Participate in a battle royale as a party": n settled Kafra Survivor matches (MatchConfig 2)
///   while in a party.
/// - 106014 [106014, minutes] "Auto-battle in a party for @{1} minutes": Field Bonus Time seconds (WildAfkService, the
///   field auto-battle clock) consumed while in a party; R is counted in seconds, minutes x 60.</para>
/// </summary>
public sealed partial class TaskService
{
    private const int ConnectType = 19;
    private const int TPartyMvp = 106012, TPartyBattleRoyale = 106013, TPartyAutoBattle = 106014, TPartyBoss = 107005;
    private const int UpdateSocialityScore = 116;

    /// <summary>A role's party roster, itself included (TeamService.MembersOf); a solo role returns only itself.</summary>
    public Func<long, IReadOnlyList<long>>? PartyMembers { get; set; }

    /// <summary>
    /// A Connect Quest's 116 UpdateSocialityScore committed for the role: the friend Intimacy its SocialRelationsConfig
    /// row grants and the party members at delivery (the role excluded). Runs on the role's owner loop after the quest
    /// transaction (FriendExtraService.GrantActivityIntimacyAsync).
    /// </summary>
    public Func<GameSession, int, IReadOnlyList<long>, Task>? SocialityScored { get; set; }

    private List<long>? _connectQuests;

    /// <summary>Type 19 rows the server can run: no pre-task, Auto accept and delivery, and supported targets.</summary>
    private List<long> ConnectQuests() => _connectQuests ??= Defs().Values
        .Where(d => d.Type == ConnectType && d.Pre.Length == 0 && d.Accept == ReceiveAuto && d.Deliver == ReceiveAuto
            && TargetsSupported(d.Id))
        .Select(d => d.Id).OrderBy(id => id).ToList();

    private bool InParty(GameSession session) => session.Role is { } role && PartyMembers?.Invoke(role.Id) is { Count: > 1 };

    /// <summary>The role's online party members, itself excluded: an offline member took no part (inferred).</summary>
    private IReadOnlyList<long> PartyOthers(GameSession session)
    {
        if (session.Role is not { } role || PartyMembers?.Invoke(role.Id) is not { } members) return [];
        var online = _sessions.All().Where(s => s.Role != null).Select(s => s.Role!.Id).ToHashSet();
        return members.Where(id => id != role.Id && online.Contains(id)).Distinct().ToArray();
    }

    /// <summary>Keeps every Connect Quest running (they are hidden and repeat); returns how many were accepted.</summary>
    private int AcceptConnectQuests(GameSession session)
    {
        int accepted = 0;
        foreach (long id in ConnectQuests())
        {
            if (Active(session, id) || !Ready(session, Defs()[id])) continue;
            session.Tasks!.Infos[id] = NewInfo(id, Running); // no Accept log: 53 hidden quests per role
            accepted++;
        }
        return accepted;
    }

    /// <summary>The SocialRelationsConfig row of a 116 action, null when the action or the row is malformed.</summary>
    private long? SocialityScore(string[] action) =>
        action.Length == 2 && long.TryParse(action[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)
        && _data.Row("SocialRelationsConfig", id) != null ? id : null;

    /// <summary>
    /// SocialRelationsConfig._iFriendValue [[107 GainIntimacy, amount, 1]]: the Intimacy a friend gains. The trailing 1 has
    /// no recovered meaning. The sworn sibling, couple and mentor columns are empty in every row and are not read.
    /// </summary>
    internal int FriendIntimacyOf(long score)
    {
        if (_data.Row("SocialRelationsConfig", score) is not { } row) return 0;
        foreach (var action in Actions(row, "_iFriendValue"))
            if (action is ["107", var amount, ..] && int.TryParse(amount, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0)
                return n;
        return 0;
    }

    /// <summary>After a committed delivery: hand each 116 score to the friendship owner with the current party.</summary>
    private void PublishSociality(GameSession session, IReadOnlyList<long> scores)
    {
        if (scores.Count == 0 || SocialityScored is not { } scored) return;
        var others = PartyOthers(session);
        if (others.Count == 0) return;
        foreach (long score in scores)
            if (FriendIntimacyOf(score) is var amount and > 0)
                // The friends' rows are written too: their roles are reserved with the role's own (FriendExtraService pins them).
                _loop.PostOwned(session, others.Select(id => Ownership.ReservationKey.Role(id)).ToArray(),
                    () => scored(session, amount, others), "connect quest intimacy");
    }

    /// <summary>106014 counts Field Bonus Time seconds consumed while the role is in a party.</summary>
    public bool CountsPartyAutoBattle(GameSession session) => session.Tasks != null && InParty(session);

    /// <summary>True when <paramref name="seconds"/> more would complete a running 106014 target.</summary>
    public bool PartyAutoBattleCompletes(GameSession session, long seconds)
    {
        if (session.Tasks == null || seconds <= 0) return false;
        foreach (var info in session.Tasks.Infos.Values)
        {
            if (info.Status != Running || !Defs().TryGetValue(info.TaskId, out var def)) continue;
            for (int i = 0; i < def.Targets.Length && i < info.CompleteNum.Count; i++)
                if (def.Targets[i] is [TPartyAutoBattle, _] && Required(session, def.Targets[i]) is var required and > 0
                    && seconds >= required - Math.Clamp(info.CompleteNum[i], 0, required))
                    return true;
        }
        return false;
    }

    /// <summary>
    /// One quest pass for a Field Bonus Time tick (WildAfkService): <paramref name="partySeconds"/> batched 106014 seconds,
    /// then the passive re-read when <paramref name="refresh"/>. Both share a single settle, so the tick saves and syncs
    /// the quests at most once.
    /// </summary>
    public async Task OnAutoBattleTickAsync(GameSession session, long partySeconds, bool refresh)
    {
        if (partySeconds <= 0 && !refresh) return;
        using var gate = await LockTasksAsync(session);
        if (BeforeProgress is { } before) await before(session);
        if (session.Tasks == null || session.Progress == null) return;
        bool changed = partySeconds > 0 && AddProgress(session, TPartyAutoBattle, t => t.Length == 2 ? partySeconds : 0);
        if (refresh) await RefreshCoreAsync(session);
        else if (changed) await SettleAsync(session);
    }
}
