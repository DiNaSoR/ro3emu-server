using System.Text.Json;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Season talents: 5050 is the durable global/plan view; 83550 pays the next
/// SeasonTalentEffectConfig level, 83552 selects a plan, 83553 renames one. The original server's path and
/// plan policies are not captured: active-plan max-parent activation, five zero-based plans, and active-plan
/// total levels for tree thresholds are inferred from the client graph and PublicParamConfig.SchemeNum=5.
///
/// Job branches (enum_SeasonTalentType SkillPoint 5, Profession_L 6, Profession_M 7): a branch point has no level
/// group and is never sent by the client; its widget opens UI_Sys_Skill_SeasonTalent_SelectPanel, whose Finish sends
/// 83550 with a job node under one of its large branch nodes. LC_SeasonTalentHelper.FindSelectedChild shows the large
/// node at max level as the chosen branch, and the panel's auto-advance needs the branch point itself at max level
/// before its ordinary child. Choosing one (SeasonTalentService.Profession.cs) sets the branch point, one large node
/// and, when the large node has options for the role's job, one small node to level 1, and clears any other choice of
/// that branch point in this plan. Job nodes open while their branch point would open and (inferred) count toward the
/// total talent level of tree and node thresholds: every tree's _iNeedLevel equals all ordinary levels of the trees
/// before it plus 2 per branch point (SeasonTalentTreeConfig: 126 = 120 + 3 x 2, 228 = 220 + 4 x 2, up to 1590).
/// </summary>
public sealed partial class SeasonTalentService
{
    public const uint TalentSync = 5050, Upgrade = 83550, SetPlan = 83552, RenamePlan = 83553;
    private const long TalentFunction = 1003, ParentFunction = 10, TalentTask = 10103160, ParentTask = 10101170;
    // (inferred) server-side limits: five zero-based plans, eight Unicode scalars, three-second switch cooldown.
    private const int PlanCount = 5, NameMaxRunes = 8, SwitchCooldownSeconds = 3;
    private const int PvpNodeType = 4, BranchPointType = 5, BranchType = 6, BranchPassiveType = 7;
    private readonly GameData _data;
    private readonly CharacterService _character;
    private readonly FunctionService _functions;
    private readonly SeasonService _seasons;
    private readonly SkillService _skills;
    private readonly RoleStore _roles;
    private readonly ILogger<SeasonTalentService> _log;

    /// <summary>Run after the role's effective talents may have changed (purchase, plan switch, unlock change),
    /// outside the role lock. SeasonTalentEffects reconciles job node buffs and skill modifiers here.</summary>
    public List<Func<GameSession, Task>> Changed { get; } = new();

    public SeasonTalentService(GameData data, CharacterService character, FunctionService functions,
        SeasonService seasons, SkillService skills, RoleStore roles, ILogger<SeasonTalentService> log)
    {
        this._data = data;
        this._character = character;
        this._functions = functions;
        this._seasons = seasons;
        this._skills = skills;
        this._roles = roles;
        this._log = log;
        character.TalentAttributes = Attributes;
    }

    private async Task ChangedAsync(GameSession session)
    {
        foreach (var handler in Changed) await handler(session);
    }

    /// <summary>Called before the first login stat snapshot; (inferred) saved plans carry across seasons without reset.</summary>
    public void Load(GameSession session)
    {
        session.SeasonTalent = _roles.LoadSeasonTalent(session.Role!.Id) is { } bytes
            ? DBSeasonTalent.Parser.ParseFrom(bytes) : new DBSeasonTalent();
        session.TalentEffectsEnabled = false; // TaskService has not loaded this role's quest state yet.
        session.TalentPlanSwitchUtc = default;
        Seed(session.SeasonTalent);
    }

    /// <summary>Called after 5027 and before 5056, also when quests or progress change.</summary>
    public async Task SyncAsync(GameSession session)
    {
        if (session.SeasonTalent == null) return;
        // The DAL expects configured tree and node keys in 5050. An empty map can crash its overlay.
        if (!HasTalentGraph()) return;
        bool before = session.TalentEffectsEnabled;
        var oldTrees = session.SeasonTalent.UnlockedTrees.ToDictionary(kv => kv.Key, kv => kv.Value);
        var oldNodes = session.SeasonTalent.UnlockedNode.ToDictionary(kv => kv.Key, kv => kv.Value);
        session.TalentEffectsEnabled = Available(session);
        BuildUnlocks(session);
        await CharacterService.SyncAsync(session, TalentSync, session.SeasonTalent, session.SeasonTalent.Clone);
        // Seeding false map keys at login cannot alter stats; avoid an extra pre-world 31008.
        if (before != session.TalentEffectsEnabled || (session.TalentEffectsEnabled
            && (!Same(oldTrees, session.SeasonTalent.UnlockedTrees)
                || !Same(oldNodes, session.SeasonTalent.UnlockedNode))))
        {
            await _character.RefreshStatsAsync(session);
            await ChangedAsync(session);
        }
    }

    private static bool Same(IReadOnlyDictionary<long, bool> old, IDictionary<long, bool> now) =>
        old.Count == now.Count && old.All(kv => now.TryGetValue(kv.Key, out bool value) && value == kv.Value);

    private bool Available(GameSession s) => s.Role != null && s.Progress != null && s.Tasks != null
        && HasTalentGraph()
        && TaskService.IsDelivered(s, ParentTask) && TaskService.IsDelivered(s, TalentTask)
        && _functions.IsOpen(s, ParentFunction) && _functions.IsOpen(s, TalentFunction)
        && Trees().Any(Applies);

    /// <summary>Use 83552's unlock and cooldown policy when Genre changes the active talent plan.</summary>
    internal int ValidateGenrePlan(GameSession session, DBSeasonTalent state, int index, bool replaceCurrent = false)
    {
        if (index == state.CurPlanId && !replaceCurrent) return 0;
        if (!Available(session)) return ErrorCodes.FeatureUnavailable;
        if (index is < 0 or >= PlanCount || !state.Plan.ContainsKey(index)) return ErrorCodes.Parameter;
        if (index != state.CurPlanId && DateTime.UtcNow - session.TalentPlanSwitchUtc < TimeSpan.FromSeconds(SwitchCooldownSeconds)) return ErrorCodes.Cooldown;
        state.CurPlanId = index;
        BuildUnlocks(session, state);
        return 0;
    }

    internal async Task SyncGenrePlanAsync(GameSession session)
    {
        await CharacterService.SyncAsync(session, TalentSync, session.SeasonTalent!, session.SeasonTalent!.Clone);
        await ChangedAsync(session);
    }

    private bool HasTalentGraph() => Trees().Any(tree => tree.Id > 0) && Nodes().Count > 0;

    private bool Applies(JsonElement row) => row.TryGetProperty("_kMultiverseArray", out var books)
        && books.ValueKind == JsonValueKind.Array
        && books.EnumerateArray().Any(v => GameData.Number(v) == _seasons.PlayBook);
    private bool Applies(TalentTree tree) => tree.Books.Any(book => book == _seasons.PlayBook);

    private void Seed(DBSeasonTalent state)
    {
        // (inferred) plan keys 0..4, default 0: the client labels a key by key+1 (LC_DAL_SeasonTalent.GetPlanNameByPlan).
        if (state.CurPlanId is < 0 or >= PlanCount) state.CurPlanId = 0;
        for (int planId = 0; planId < PlanCount; planId++)
        {
            if (!state.Plan.TryGetValue(planId, out var plan)) state.Plan[planId] = plan = new SeasonTalentPlan();
            foreach (var node in Nodes())
                if (!plan.TalentNodes.ContainsKey(node.Id)) plan.TalentNodes[node.Id] = new DBSeasonTalentNode { TalentId = node.Id };
        }
    }

    private void BuildUnlocks(GameSession s, DBSeasonTalent? candidate = null)
    {
        var state = candidate ?? s.SeasonTalent!;
        s.TalentEffectsEnabled = Available(s);
        Seed(state);
        var active = state.Plan[state.CurPlanId];
        long total = Total(state, active);
        state.UnlockedTrees.Clear();
        long? oathMax = null;
        foreach (var tree in Trees())
        {
            if (tree.Id <= 0) continue;
            bool unlocked = s.TalentEffectsEnabled && Applies(tree)
                && _functions.ConditionMet(s, tree.Condition) && total >= tree.NeedLevel;
            state.UnlockedTrees[tree.Id] = unlocked;
            // (inferred) the oath cap is the largest cap marker on an eligible, unlocked tree.
            if (unlocked) oathMax = Math.Max(oathMax ?? long.MinValue, tree.PvpLimit);
        }
        state.BattleOathMaxLevel = oathMax ?? 0;
        // Every configured node is rewritten below, so existing entries are updated in place (a login used to
        // rebuild both maps several times: ~22% of the loop's allocations in a login storm); entries of nodes the
        // table no longer has are dropped as the old clear did.
        var nodes = Nodes();
        var plans = state.Plan.Values.ToArray(); // once, not two map views and enumerators per node
        foreach (var node in nodes)
        {
            long id = node.Id, tree = node.Tree;
            bool pvp = node.Type == PvpNodeType;
            bool activated = state.UnlockedTrees.GetValueOrDefault(tree) && (pvp
                ? state.BattleOathMaxLevel > 0 : Opens(active, node, total));
            state.UnlockedNode[id] = activated;
            // (inferred) the global node view exposes highest paid level across plans; selected-plan levels remain separate.
            long level = 0;
            if (pvp) level = state.BattleOathLevel;
            else foreach (var p in plans) if (p.TalentNodes.TryGetValue(id, out var n) && n.CurrentLevel > level) level = n.CurrentLevel;
            if (state.TalentNodes.TryGetValue(id, out var global)) { global.TalentId = id; global.CurrentLevel = level; global.IsActivated = activated; }
            else state.TalentNodes[id] = new DBSeasonTalentNode { TalentId = id, CurrentLevel = level, IsActivated = activated };
            foreach (var plan in plans)
            {
                plan.TalentNodes[id].IsActivated = activated;
                // The DAL overlays selected plan nodes after global nodes, including the shared oath node.
                if (pvp) plan.TalentNodes[id].CurrentLevel = state.BattleOathLevel;
            }
        }
        if (state.UnlockedNode.Count != _nodeIds!.Count)
            foreach (long stale in state.UnlockedNode.Keys.Where(k => !_nodeIds.Contains(k)).ToArray()) state.UnlockedNode.Remove(stale);
        if (state.TalentNodes.Count != _nodeIds.Count)
            foreach (long stale in state.TalentNodes.Keys.Where(k => !_nodeIds.Contains(k)).ToArray()) state.TalentNodes.Remove(stale);
    }

    private sealed record TalentNode(long Id, long Tree, long Type, long NeedLevel, bool Start, long MaxLevel, long Group);

    /// <summary>(inferred) Tree and node thresholds count the total the talent panel shows
    /// (UI_Sys_Skill_SeasonTalentPanelCtrl._refreshGrade): every node of every tree but the SkillPoint branch points,
    /// so the selected plan's ordinary and job node levels plus the shared oath level.</summary>
    private long Total(DBSeasonTalent state, SeasonTalentPlan plan)
    {
        Nodes();
        long total = Math.Max(0, state.BattleOathLevel);
        foreach (var (id, node) in plan.TalentNodes)
            if (_countedIds!.Contains(id)) total += Math.Max(0, node.CurrentLevel);
        return total;
    }

    /// <summary>The node's own activation in this plan, its tree's unlock aside. A job node opens with its branch
    /// point (the large node's parent of type 5).</summary>
    private bool Opens(SeasonTalentPlan plan, TalentNode node, long total)
    {
        Nodes();
        if (node.Type == BranchPassiveType)
            return _branchOf!.TryGetValue(node.Id, out long large) && _byId!.TryGetValue(large, out var branch)
                && Opens(plan, branch, total);
        if (node.Type == BranchType)
            return _branchPoint!.TryGetValue(node.Id, out long point) && _byId!.TryGetValue(point, out var parent)
                && Opens(plan, parent, total);
        return total >= node.NeedLevel && (node.Start || ParentMaxed(plan, node.Id, node.Tree));
    }
    private sealed record TalentTree(long Id, double?[] Books, long Condition, long NeedLevel, long PvpLimit);

    /// <summary>SeasonTalentTreeConfig's rows (table order), read once: a login rebuilt the unlocks several times,
    /// each time looking every field up in the JSON rows (stage G).</summary>
    private IReadOnlyList<TalentTree> Trees() => _trees ??= _data.Table("SeasonTalentTreeConfig").Values.Select(row =>
        new TalentTree(GameData.Int(row, "_iId"),
            row.TryGetProperty("_kMultiverseArray", out var books) && books.ValueKind == JsonValueKind.Array
                ? books.EnumerateArray().Select(GameData.Number).ToArray() : [],
            GameData.Int(row, "_iTreeCondition"), GameData.Int(row, "_iNeedLevel"), GameData.Int(row, "_iUpPvPLvLimit"))).ToArray();
    private TalentTree[]? _trees;

    /// <summary>SeasonTalentConfig's nodes (id &gt; 0, table order) and each node's same-tree parents with their caps,
    /// indexed once: ParentMaxed scanned all 232 rows for every node of every rebuild.</summary>
    private IReadOnlyList<TalentNode> Nodes()
    {
        if (_nodes != null) return _nodes;
        var rows = _data.Table("SeasonTalentConfig").Values.ToArray();
        var parents = new Dictionary<(long Node, long Tree), List<(long Parent, long Max)>>();
        foreach (var candidate in rows)
        {
            if (!candidate.TryGetProperty("_kAfterids", out var after) || after.ValueKind != JsonValueKind.Array) continue;
            long tree = GameData.Int(candidate, "_iTalentTreeID"), parent = GameData.Int(candidate, "_iId"), max = GameData.Int(candidate, "_iMaxLevel");
            foreach (var v in after.EnumerateArray())
                if (GameData.Number(v) is { } child)
                {
                    var key = ((long)child, tree);
                    if (!parents.TryGetValue(key, out var list)) parents[key] = list = new();
                    list.Add((parent, max));
                }
        }
        _parents = parents.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
        var nodes = rows.Select(r => new TalentNode(GameData.Int(r, "_iId"), GameData.Int(r, "_iTalentTreeID"), GameData.Int(r, "_iType"),
            GameData.Int(r, "_iNeedLevel"), GameData.Int(r, "_iIsStartPoint") == 1, GameData.Int(r, "_iMaxLevel"),
            GameData.Int(r, "_iLevelGroupId"))).Where(n => n.Id > 0).ToArray();
        var byId = nodes.ToDictionary(n => n.Id);
        // A large job node's branch point is its type-5 parent in the same tree; a small node's large node its type-6 parent.
        var branchPoint = new Dictionary<long, long>();
        var branchOf = new Dictionary<long, long>();
        foreach (var node in nodes)
        {
            long want = node.Type == BranchType ? BranchPointType : node.Type == BranchPassiveType ? BranchType : 0;
            if (want == 0 || !_parents!.TryGetValue((node.Id, node.Tree), out var incoming)) continue;
            foreach (var (parent, _) in incoming)
                if (byId.TryGetValue(parent, out var p) && p.Type == want)
                {
                    (node.Type == BranchType ? branchPoint : branchOf)[node.Id] = parent;
                    break;
                }
        }
        _byId = byId;
        _branchPoint = branchPoint;
        _branchOf = branchOf;
        _countedIds = nodes.Where(n => n.Type is not (PvpNodeType or BranchPointType)) // the oath is added once, from its shared level
            .Select(n => n.Id).ToHashSet();
        _nodeIds = nodes.Select(n => n.Id).ToHashSet();
        return _nodes = nodes;
    }

    private TalentNode[]? _nodes;
    private HashSet<long>? _nodeIds, _countedIds;
    private Dictionary<long, TalentNode>? _byId;
    private Dictionary<long, long>? _branchPoint, _branchOf;
    private Dictionary<(long Node, long Tree), (long Parent, long Max)[]>? _parents;

    private bool ParentMaxed(SeasonTalentPlan active, long nodeId, long tree)
    {
        Nodes();
        if (_parents!.TryGetValue((nodeId, tree), out var parents))
            foreach (var (parent, max) in parents)
                if (max > 0 && active.TalentNodes.TryGetValue(parent, out var n) && n.CurrentLevel >= max) return true;
        return false; // (inferred) one maxed incoming parent opens a branch, never an unearned child.
    }

    private JsonElement? Effect(long group, long level, int career)
    {
        JsonElement? generic = null;
        _effects ??= _data.Table("SeasonTalentEffectConfig").Values
            .GroupBy(row => (GameData.Int(row, "_iGroup"), GameData.Int(row, "_iLevel")))
            .ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var row in _effects.GetValueOrDefault((group, level)) ?? [])
        {
            if (!row.TryGetProperty("_iJobId", out var jobs) || jobs.ValueKind != JsonValueKind.Array || jobs.GetArrayLength() == 0)
                generic = row;
            else if (jobs.EnumerateArray().Any(j => GameData.Number(j) == career)) return row;
            else if (jobs.EnumerateArray().Any(j => GameData.Number(j) == 0)) generic = row;
        }
        return generic; // Lua CFG GetCfgByJob falls back to job 0.
    }

    private Dictionary<(long Group, long Level), JsonElement[]>? _effects; // table order within each key

    private static bool HasEntries(JsonElement row, string key) => row.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0;

    private static bool Supported(JsonElement row) => HasEntries(row, "_kAttrs") && HasEntries(row, "_kCosts")
        && !HasEntries(row, "_kSpecialEffectList") && !HasEntries(row, "_kProfessionID")
        && GameData.Int(row, "_iSkillPoint") == 0 && GameData.Int(row, "_kSkillPointCosts") == 0;

    /// <summary>A job node row: special effects and/or attributes, no skill-point economy (unused by the export).
    /// _kProfessionID only names the schools the client lists as suited (UI_Sys_Skill_SeasonTalent_Select_ListWidget).</summary>
    private static bool BranchSupported(JsonElement row) => (HasEntries(row, "_kSpecialEffectList") || HasEntries(row, "_kAttrs"))
        && GameData.Int(row, "_iSkillPoint") == 0 && GameData.Int(row, "_kSkillPointCosts") == 0;

    private static bool IsJobNode(long type) => type is BranchType or BranchPassiveType;

    private List<(int Entry, long Count)>? Costs(JsonElement effect)
    {
        var costs = new List<(int, long)>();
        foreach (var (id, amount) in GameData.Pairs(effect, "_kCosts"))
        {
            if (id <= 0 || id > int.MaxValue || amount <= 0 || amount != Math.Truncate(amount)
                || amount > long.MaxValue || _data.Row("ItemConfig", id) == null) return null;
            costs.Add(((int)id, (long)amount));
        }
        return costs.Count > 0 ? costs : null;
    }

    /// <summary>83550: lock validation, payment, plan/oath mutation and durable save as one role operation.</summary>
    public async Task UpgradeAsync(GameSession s, SeasonTalentUpgradeReq req)
    {
        if (_data.Row("SeasonTalentConfig", req.TalentId) is { } requested
            && GameData.Int(requested, "_iType") is BranchType or BranchPassiveType)
        {
            await SelectProfessionAsync(s, req);
            return;
        }
        int result;
        bool changed = false;
        using (await CharacterService.Lock(s))
        {
            result = UpgradeLocked(s, req.TalentId, out var cost, out var node, out var isOath);
            if (result == 0)
            {
                var next = s.SeasonTalent!.Clone();
                if (isOath) next.BattleOathLevel++;
                else next.Plan[next.CurPlanId].TalentNodes[node].CurrentLevel++; // (inferred) ordinary purchases belong to one plan
                BuildUnlocks(s, next);
                if (cost!.Count == 0)
                    result = await SaveFreeAsync(s, next, node);
                else
                {
                    List<InvItem>? paid = null;
                    try
                    {
                        var economy = _character.ProgressionEconomy(s, "cd13c/season-talent-upgrade", cost, [],
                            new
                            {
                                Node = node, Oath = isOath, Level = isOath ? next.BattleOathLevel
                                : next.Plan[next.CurPlanId].TalentNodes[node].CurrentLevel, Plan = next.CurPlanId
                            });
                        paid = await _character.ExchangeLockedAsync(s, cost, [], popup: false, sync: false,
                            persist: tx => { tx.RoleBlob(s.Role!.Id, "season_talent", next.ToByteArray()); tx.WithEconomy(economy); },
                            committed: () => s.SeasonTalent = next, asyncCommit: true);
                    }
                    catch (Exception e) when (e is SqliteException or InvalidOperationException)
                    {
                        _log.LogError(e, "Talent purchase {Node} for role {Role} rolled back", node, s.Role!.Id);
                        result = ErrorCodes.Unknown;
                    }
                    if (result == 0 && paid == null) result = _character.ShortageOf(s, cost);
                    if (paid != null)
                    {
                        await _character.ItemsChangedAsync(s, stats: false); // 5009 before 5050 and the reply
                        await CharacterService.SyncAsync(s, TalentSync, next.Clone());
                        await _character.RefreshStatsAsync(s);
                    }
                }
                changed = result == 0;
            }
        }
        _log.LogInformation("{Name} talent {Node}: {Result}", s.Role?.Name, req.TalentId, result);
        await s.SendGameAsync(Upgrade, new SeasonTalentUpgradeResp { Result = result, TalentId = req.TalentId });
        if (changed) await ChangedAsync(s);
    }

    /// <summary>A job node costs nothing in the export: save the plan without an economy operation.</summary>
    private async Task<int> SaveFreeAsync(GameSession s, DBSeasonTalent next, long node)
    {
        try
        {
            var transaction = _roles.NewTransaction();
            transaction.RoleBlob(s.Role!.Id, "season_talent", next.ToByteArray());
            await _roles.CommitAsync(transaction, "season-talent/choose-branch", _ => s.SeasonTalent = next);
        }
        catch (Exception e) when (e is SqliteException or InvalidOperationException)
        {
            _log.LogError(e, "Talent branch {Node} for role {Role} failed", node, s.Role!.Id);
            return ErrorCodes.Unknown;
        }
        await CharacterService.SyncAsync(s, TalentSync, next.Clone());
        await _character.RefreshStatsAsync(s);
        return 0;
    }

    private int UpgradeLocked(GameSession s, long id, out List<(int Entry, long Count)>? cost, out long node, out bool oath)
    {
        cost = null; node = id; oath = false;
        if (s.SeasonTalent == null || s.Progress == null || !Available(s)) return ErrorCodes.FeatureUnavailable;
        if (_data.Row("SeasonTalentConfig", id) is not { } row || !_byId!.TryGetValue(id, out var config)) return ErrorCodes.TalentInvalid;
        if (config.Type == BranchPointType) return ErrorCodes.Parameter; // chosen through a job node, never bought itself
        BuildUnlocks(s);
        var state = s.SeasonTalent;
        if (!state.UnlockedNode.GetValueOrDefault(id)) return ErrorCodes.TalentLocked;
        oath = config.Type == PvpNodeType;
        var plan = state.Plan[state.CurPlanId];
        long current = oath ? state.BattleOathLevel : plan.TalentNodes[id].CurrentLevel;
        long cap = oath ? state.BattleOathMaxLevel : GameData.Int(row, "_iMaxLevel");
        if (cap <= 0 || current >= cap) return ErrorCodes.SkillMaxLevel;
        long group = GameData.Int(row, "_iLevelGroupId");
        if (group <= 0 || Effect(group, current + 1, s.Progress.Career) is not { } effect || !Supported(effect))
            return ErrorCodes.FeatureUnavailable; // no invented connector or missing group
        cost = Costs(effect);
        if (cost == null) return ErrorCodes.FeatureUnavailable;
        return cost.GroupBy(c => c.Entry).Any(g => _character.CountOf(s, g.Key) < g.Sum(c => c.Count))
            ? _character.ShortageOf(s, cost) : 0;
    }

    /// <summary>A job node's level-1 row for this job (the export has no generic rows for them).</summary>
    private JsonElement? JobRow(TalentNode node, int career) =>
        node.Group > 0 && Effect(node.Group, 1, career) is { } row && BranchSupported(row) ? row : null;

    /// <summary>83552: all plans have complete node maps so the client's overlay clears old levels; (inferred) 3s cooldown.</summary>
    public async Task SetPlanAsync(GameSession s, SeasonTalentSetCurPlanReq req)
    {
        int result;
        using (await CharacterService.Lock(s))
        {
            result = !Available(s) || s.SeasonTalent == null ? ErrorCodes.FeatureUnavailable
                : req.PlanId is < 0 or >= PlanCount ? ErrorCodes.Parameter
                : DateTime.UtcNow - s.TalentPlanSwitchUtc < TimeSpan.FromSeconds(SwitchCooldownSeconds) ? ErrorCodes.Cooldown : 0;
            if (result == 0)
            {
                var next = s.SeasonTalent!.Clone();
                next.CurPlanId = req.PlanId;
                BuildUnlocks(s, next);
                try
                {
                    var transaction = _roles.NewTransaction();
                    transaction.RoleBlob(s.Role!.Id, "season_talent", next.ToByteArray());
                    await _roles.CommitAsync(transaction, "season-talent/set-plan", _ =>
                    {
                        s.SeasonTalent = next;
                        s.TalentPlanSwitchUtc = DateTime.UtcNow;
                    });
                    await CharacterService.SyncAsync(s, TalentSync, next.Clone());
                    await _character.RefreshStatsAsync(s);
                }
                catch (Exception e) when (e is SqliteException or InvalidOperationException)
                {
                    _log.LogError(e, "Talent plan switch for role {Role} failed", s.Role!.Id);
                    result = ErrorCodes.Unknown;
                }
            }
        }
        await s.SendGameAsync(SetPlan, new SeasonTalentSetCurPlanResp { Result = result, Req = req });
        if (result == 0) await ChangedAsync(s);
    }

    /// <summary>83553: (inferred) trim, nonempty/no-control and eight-Unicode-scalar server validation from PublicParamConfig.SchemeNameNumMax=8.</summary>
    public async Task RenameAsync(GameSession s, SeasonTalentSetPlanNameReq req)
    {
        int result;
        using (await CharacterService.Lock(s))
        {
            string name = req.PlanName.Trim();
            result = !Available(s) || s.SeasonTalent == null ? ErrorCodes.FeatureUnavailable
                : req.PlanId is < 0 or >= PlanCount || name.Length == 0 || name.EnumerateRunes().Count() > NameMaxRunes
                    || name.Any(char.IsControl) ? ErrorCodes.Parameter : 0;
            if (result == 0)
            {
                var next = s.SeasonTalent!.Clone();
                next.Plan[req.PlanId].PlanName = name;
                try
                {
                    var transaction = _roles.NewTransaction();
                    transaction.RoleBlob(s.Role!.Id, "season_talent", next.ToByteArray());
                    await _roles.CommitAsync(transaction, "season-talent/rename", _ => s.SeasonTalent = next);
                    await CharacterService.SyncAsync(s, TalentSync, next.Clone());
                }
                catch (Exception e) when (e is SqliteException or InvalidOperationException)
                {
                    _log.LogError(e, "Talent plan rename for role {Role} failed", s.Role!.Id);
                    result = ErrorCodes.Unknown;
                }
            }
        }
        await s.SendGameAsync(RenamePlan, new SeasonTalentSetPlanNameResp { Result = result, Req = req });
    }

    /// <summary>The selected plan's bought nodes whose effect currently applies, with their target-level effect row
    /// (job nodes: the role's job row). The oath is the shared global level.</summary>
    private IEnumerable<(long Node, JsonElement Row)> Effective(GameSession s)
    {
        if (!Available(s) || s.SeasonTalent is not { } state || s.Progress == null
            || !state.Plan.TryGetValue(state.CurPlanId, out var plan)) yield break;
        Nodes();
        long total = Total(state, plan);
        foreach (var (id, saved) in plan.TalentNodes)
        {
            if (saved.CurrentLevel <= 0 || !_byId!.TryGetValue(id, out var node)
                || node.Type is PvpNodeType or BranchPointType) continue;
            if (!TreeEligible(s, node.Tree, total) || !Opens(plan, node, total)) continue;
            // A small node applies only with its own large node chosen.
            if (node.Type == BranchPassiveType && plan.TalentNodes.GetValueOrDefault(_branchOf![id])?.CurrentLevel is not > 0) continue;
            var effect = IsJobNode(node.Type) ? JobRow(node, s.Progress.Career) : Effect(node.Group, saved.CurrentLevel, s.Progress.Career);
            if (effect is { } row && (IsJobNode(node.Type) || Supported(row))) yield return (id, row);
        }
        if (state.BattleOathLevel > 0 && _data.Row("SeasonTalentConfig", 10100000) is { } oath
            && TreeEligible(s, GameData.Int(oath, "_iTalentTreeID"), total))
        {
            var effect = Effect(GameData.Int(oath, "_iLevelGroupId"), state.BattleOathLevel, s.Progress.Career);
            if (effect is { } row && Supported(row)) yield return (10100000, row);
        }
    }

    /// <summary>The SpecialEffectConfig tuples of the effective job nodes, with the node that owns each.</summary>
    internal IEnumerable<(long Node, long[] Op)> SpecialEffects(GameSession s) => SpecialEffects(Effective(s));

    private IEnumerable<(long Node, long[] Op)> SpecialEffects(IEnumerable<(long Node, JsonElement Row)> effective)
    {
        foreach (var (node, row) in effective)
        {
            if (!row.TryGetProperty("_kSpecialEffectList", out var list) || list.ValueKind != JsonValueKind.Array) continue;
            foreach (var id in list.EnumerateArray())
                if (GameData.Number(id) is { } effectId && _data.Row("SpecialEffectConfig", (long)effectId) is { } effect)
                    foreach (var op in EquipmentAffixes.Rows(effect, "_kSpecialEffect")) yield return (node, op);
        }
    }

    /// <summary>Only bought target-level totals contribute once. Job nodes add their attribute tuples
    /// (SpecialEffectConfig opcodes 1, 16, 19 and 22); their buffs and skill changes are SeasonTalentEffects'.</summary>
    public IEnumerable<(long Id, double Value)> Attributes(GameSession s)
    {
        var effective = Effective(s).ToList();
        foreach (var (_, row) in effective)
            foreach (var pair in GameData.Pairs(row, "_kAttrs")) yield return pair;
        if (s.Progress == null) yield break;
        (int SubType, bool Dual)? weapon = null;
        foreach (var (_, op) in SpecialEffects(effective))
        {
            switch (op)
            {
                case [1, var attr, var value]:
                    yield return (attr, value);
                    break;
                // (inferred) [16, weapon subtype, attribute, value]: "When a dagger or katar is equipped, PATK +20"
                // is one tuple per subtype; the main hand's ItemConfig._iSubType decides (SkillService.Weapon).
                case [16, var sub, var attr, var value]:
                    weapon ??= _skills.Weapon(s);
                    if (weapon.Value.SubType == sub) yield return (attr, value);
                    break;
                // (inferred) [19, skill group, level, attribute, value]: "When Mace Mastery reaches Lv. 10, STR +5".
                case [19, var group, var level, var attr, var value]:
                    if (_skills.Level(s.Progress, (int)group) >= Math.Max(1, level)) yield return (attr, value);
                    break;
                // (inferred) [22, weapon subtype, attribute, per level, skill group]: "When a sword-type weapon is
                // equipped, PATK +2 for each learned level of Sword Quicken".
                case [22, var sub, var attr, var per, var group]:
                    weapon ??= _skills.Weapon(s);
                    if (weapon.Value.SubType == sub) yield return (attr, per * _skills.Level(s.Progress, (int)group));
                    break;
            }
        }
    }

    private bool TreeEligible(GameSession s, long treeId, long total) =>
        _data.Row("SeasonTalentTreeConfig", treeId) is { } tree && Applies(tree)
        && _functions.ConditionMet(s, GameData.Int(tree, "_iTreeCondition"))
        && total >= GameData.Int(tree, "_iNeedLevel");

}
