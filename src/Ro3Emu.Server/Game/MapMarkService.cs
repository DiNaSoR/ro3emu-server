using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Big-map flags in a battle instance (MG_Dungeon 30407 DUNGEON_SET_MARKPOS, 30408 DUNGEON_MARKPOS_NTF,
/// 30431 DUNGEON_GATHER_MARKPOS, 30438 DUNGEON_GATHER_MARKPOS_NTF). The siege and push-up maps
/// (UI_Sys_SiegeBigMapWidgetBaseCtrl) place them; UI_Sys_BigMap_MarkTipPanelCtrl deletes and gathers.
/// <para>Who may manage a flag type is LC_DunGeon_Helper.CheckManageMarkPermission: a TeamLeader flag needs the leader of
/// a 40-member team (LC_TeamHelper.IsGroupLeader), a Commander flag the guild right BattleFieldCommand (enum_GuildPermission
/// 12). The client keeps flags in slots 1..GVEMapMarkMaxNum (LC_DAL_Dungeon._processDungeonMarkPosNtf) and replaces a
/// type's whole list on each 30408, so an add carries no id: the server picks the first free slot.</para>
/// <para>(inferred) Commander flags are shared with the guild and TeamLeader flags with the team, each only within the
/// same battle instance; a gather goes to the same people. Flags live in memory and end with the instance.</para>
/// </summary>
public sealed class MapMarkService(GameData data, TeamService teams, GuildStore guildStore, GuildService guilds,
    SessionRegistry sessions)
{
    public const uint SetMark = 30407, MarkNtf = 30408, Gather = 30431, GatherNtf = 30438;
    /// <summary>enum_GuildPermission.BattleFieldCommand (Lua_GameDefine).</summary>
    public const int CommandRight = 12;
    private const int DefaultMaxMarks = 10; // PublicParamConfig GVEMapMarkMaxNum in the client data

    private readonly record struct Board(Maps.RuntimeMapKey Map, long Audience, DungeonMarkType Type);

    private readonly Lock _gate = new();
    private readonly Dictionary<Board, SortedDictionary<int, DungeonMarkPos>> _boards = [];

    private int MaxMarks => data.Table("PublicParamConfig").TryGetValue("GVEMapMarkMaxNum", out var v)
        && GameData.Number(v) is > 0 and var n ? (int)n : DefaultMaxMarks;

    /// <summary>The guild (Commander) or team (TeamLeader) a role manages flags for, or 0 without the permission.</summary>
    private long Audience(long role, DungeonMarkType type) => type switch
    {
        DungeonMarkType.DungeonMarkCommander when guildStore.ForRole(role) is { } g && guilds.HasRight(g, role, CommandRight) => g.Id,
        DungeonMarkType.DungeonMarkTeamLeader when teams.IsFortyLeader(role) => teams.TeamOf(role).TeamId,
        _ => 0,
    };

    private long AudienceOf(long role, DungeonMarkType type) => type == DungeonMarkType.DungeonMarkCommander
        ? guildStore.ForRole(role)?.Id ?? 0 : teams.TeamOf(role).TeamId;

    /// <summary>The slot a delete or gather names. UI_Sys_BigMap_MarkTipPanelCtrl sends m_iFlagIndex, the index in the
    /// client's list of all types ((type - 1) * GVEMapMarkMaxNum + slot); only its escort branch subtracts the offset.
    /// A plain slot is accepted as well.</summary>
    private int Slot(int id, DungeonMarkType type) => id > MaxMarks ? id - ((int)type - 1) * MaxMarks : id;

    private static bool InBattle(GameSession s) => s.Role != null && s.InstanceId != 0 && !s.SceneLoading;

    public async Task SetMarkAsync(GameSession s, DungeonSetMarkPosReq req)
    {
        var type = req.Pos?.MarkType ?? DungeonMarkType.DungeonMarkNone;
        long audience = InBattle(s) && s.Role is { } self ? Audience(self.Id, type) : 0;
        int result = !InBattle(s) ? ErrorCodes.FeatureUnavailable
            : type == DungeonMarkType.DungeonMarkNone ? ErrorCodes.Parameter
            : audience == 0 ? ErrorCodes.NoPermission
            : !req.BDel && req.Pos?.Pos == null ? ErrorCodes.Parameter : 0;
        DungeonMarkPos? placed = null;
        DungeonSetMarkPosNtf? ntf = null;
        var board = new Board(s.RuntimeMap, audience, type);
        if (result == 0)
            lock (_gate)
            {
                Prune();
                _boards.TryGetValue(board, out var marks);
                if (req.BDel) result = marks != null && marks.Remove(Slot(req.Pos?.Id ?? 0, type)) ? 0 : ErrorCodes.Parameter;
                else if (Enumerable.Range(1, MaxMarks).FirstOrDefault(i => marks?.ContainsKey(i) != true) is > 0 and var slot
                    && req.Pos?.Pos is { } at)
                    (marks ??= _boards[board] = [])[slot] = placed = new DungeonMarkPos { Id = slot, Pos = at.Clone(), MarkType = type };
                else result = ErrorCodes.Parameter; // (inferred) every slot is taken
                if (result == 0 && marks != null) ntf = new DungeonSetMarkPosNtf { MarkType = type, Pos = { marks.Values.Select(m => m.Clone()) } };
            }
        await s.SendGameAsync(SetMark, new DungeonSetMarkPosRsp
        {
            Result = result, BDel = req.BDel, Pos = placed?.Clone() ?? req.Pos?.Clone(),
        });
        if (ntf != null) await SendAsync(board, MarkNtf, ntf);
    }

    public async Task GatherAsync(GameSession s, DungeonGatherMarkPosReq req)
    {
        long role = s.Role?.Id ?? 0;
        var type = req.Pos?.MarkType ?? DungeonMarkType.DungeonMarkNone;
        Board? board = null;
        int result = !InBattle(s) ? ErrorCodes.FeatureUnavailable : ErrorCodes.Parameter;
        if (InBattle(s) && req.GatherType == DungeonGatherType.DungeonGatherMark && req.Pos != null)
        {
            long audience = Audience(role, type);
            var mark = new Board(s.RuntimeMap, AudienceOf(role, type), type);
            if (audience == 0) result = ErrorCodes.NoPermission;
            else
                lock (_gate)
                    if (_boards.TryGetValue(mark, out var marks) && marks.ContainsKey(Slot(req.Pos.Id, type))) { result = 0; board = mark; }
        }
        else if (InBattle(s) && req.GatherType == DungeonGatherType.DungeonGatherBuilding && req.GatherGuid != 0
            && req.Pos?.Pos != null)
        {
            // (inferred) a building gather goes to the guild for a commander, else to the leader's team.
            var asType = Audience(role, DungeonMarkType.DungeonMarkCommander) != 0 ? DungeonMarkType.DungeonMarkCommander
                : DungeonMarkType.DungeonMarkTeamLeader;
            long audience = Audience(role, asType);
            if (audience == 0) result = ErrorCodes.NoPermission;
            else { result = 0; board = new Board(s.RuntimeMap, audience, asType); }
        }
        await s.SendGameAsync(Gather, new DungeonGatherMarkPosRsp
        {
            Result = result, GatherType = req.GatherType, Pos = req.Pos?.Clone(), GatherGuid = req.GatherGuid,
        });
        if (board is { } b && req.Pos is { } pos)
            await SendAsync(b, GatherNtf, new DungeonGatherMarkPosNtf
            {
                GatherType = req.GatherType, Pos = pos.Clone(), GatherGuid = req.GatherGuid,
            });
    }

    private Task SendAsync(Board board, uint id, Google.Protobuf.IMessage body) =>
        PreparedMessage.SendToAsync(sessions.All().Where(p => p.Role != null && !p.SceneLoading && p.RuntimeMap == board.Map
            && AudienceOf(p.Role.Id, board.Type) == board.Audience), id, body);

    /// <summary>Drops the flags of instances nobody is in any more. Called under <see cref="_gate"/>.</summary>
    private void Prune()
    {
        var live = sessions.All().Where(p => p.Role != null).Select(p => p.RuntimeMap).ToHashSet();
        foreach (var key in _boards.Keys.Where(k => !live.Contains(k.Map)).ToArray()) _boards.Remove(key);
    }
}
