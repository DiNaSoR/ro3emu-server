using System.Text.Json;
using Romsg;
using Google.Protobuf;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Skills of a role: the 5025 DBSkillData dataset (learnable list, learned levels, skill points per
/// job rank, the normal attack and the skill bar), learning (31151) and the bar (31153). Skill ids are group ids
/// (SkillConfig._iSkillID) here; casts use the row id of a level (group*100 + level, see RealId).
/// </summary>
public sealed class SkillService
{
    public const uint SkillDataSync = 5025;
    public const uint PlanUpdate = 31151;
    public const uint SkillLoad = 31153;
    public const uint SwitchPlan = 31152, RenamePlan = 31157, PlanNameChange = RenamePlan;
    public const uint SkillReset = 31156, QueueUpdate = 31159, SwitchGenre = 31160;
    private const int PlanCount = 5; // (inferred) PublicParamConfig.SchemeNum.
    private const int BarSlots = 6;
    private const long FightBit = 1L << 15;

    private readonly GameData _data;
    private readonly CharacterService _character;
    private readonly RoleStore _roles;
    private readonly ILogger<SkillService> _log;
    private Dictionary<(int Group, int Level), int>? _rowIds;
    private Dictionary<int, int>? _upgradedGroupByBase;

    /// <summary>Combat-owned dynamic queue eligibility (LC_DynamicParamMgr).</summary>
    public Func<GameSession, int, bool>? QueueEligibility { get; set; }
    /// <summary>Ordinary pet guardian projection; LC_System_Skill consumes whellSkills[SkillWheelPet].</summary>
    internal Func<GameSession, SkillWheelSlot?>? PetWheel { get; set; }

    public SkillService(GameData data, CharacterService character, RoleStore roles, ILogger<SkillService> log)
    {
        this._data = data;
        this._character = character;
        this._roles = roles;
        this._log = log;
        character.ProgressHandlers.Add(SyncAsync);
        character.EquipmentChanged.Add(WeaponChangedAsync);
    }

    /// <summary>SkillConfig row id of a group at a level (mode 0), or 0.</summary>
    public int RealId(int group, int level)
    {
        _rowIds ??= _data.Table("SkillConfig").Values
            .Where(r => GameData.Int(r, "_iMode") == 0)
            .GroupBy(r => ((int)GameData.Int(r, "_iSkillID"), (int)GameData.Int(r, "_iLevel")))
            .ToDictionary(g => g.Key, g => (int)GameData.Int(g.First(), "_iID"));
        return _rowIds.GetValueOrDefault((group, level));
    }

    /// <summary>SkillConfig._iMaxLevel is the learnable cap.</summary>
    public int MaxLevel(int group) => _data.Row("SkillConfig", RealId(group, 1)) is { } row
        ? (int)GameData.Int(row, "_iMaxLevel") : 0;

    /// <summary>The career's normal attack without a weapon rule: JobConfig._kSkillNormalID (a row id).</summary>
    public int NormalAttack(int career) =>
        _data.Row("JobConfig", career) is { } job && job.TryGetProperty("_kSkillNormalID", out var n) && n.ValueKind == JsonValueKind.Array && n.GetArrayLength() > 0
            ? n[0].GetInt32()
            : 1000001;

    /// <summary>
    /// The role's normal attack now, as the client's Lua_CFG_JobAttackConfig.GetNormalSkillID picks it: JobAttackConfig
    /// by job and weapon (ItemConfig._iSubType of the main hand * 10 + dual wield), then the job's any-weapon row
    /// (_kWeaponType empty), then JobConfig._kSkillNormalID. A Thief with a Dagger attacks with 1005001 "Basic Attack"
    /// (ATK x 50% + 45), with a Katar 1005101, with two Daggers 1005201, otherwise 1500001 "Normal Attack" (ATK x 50%);
    /// a Swordman with 1001001, a Knight or Crusader with 1001101. All of a career's rows count as learned, because the
    /// skill window shows GetNormalSkillID's group in the Novice tree's Normal Attack cell
    /// (UI_Sys_Skill_AddBitWidgetBaseCtrl). Each row also names the battle mount it applies on (_iMountID, 0 on foot):
    /// _getData reads ActivationMounts[Battle] while it is ridden, so a Ranger on the Demonic Wolf attacks with Warg Strike
    /// 1003201, a Mechanic in the Madogear with Normal Punch 1631001 and on foot with Basic Attack 1600001, a Rune Knight
    /// on the dragon with 1100001; a row without that mount falls back to the weapon's on-foot row.
    /// </summary>
    public int NormalAttack(GameSession session)
    {
        int career = session.Progress!.Career;
        var (subType, dual) = Weapon(session);
        return JobAttack(career, subType * 10 + (dual ? 1 : 0), MountService.RidingBattle(session)) is int id && _data.Row("SkillConfig", id) != null
            ? id : NormalAttack(career);
    }

    private const int MainHandGrid = 1, ViceHandGrid = 2;

    /// <summary>
    /// LC_UnitBase weapon state: the main hand's ItemConfig._iSubType (the off hand's when the main hand is empty;
    /// 0 bare-handed) and dual wield (both hands hold the same subtype). Subtype 1 is a Dagger, 12 a Katar.
    /// </summary>
    internal (int SubType, bool Dual) Weapon(GameSession session)
    {
        int main = session.Items.FirstOrDefault(i => i.Grid == MainHandGrid)?.Entry ?? 0;
        int vice = session.Items.FirstOrDefault(i => i.Grid == ViceHandGrid)?.Entry ?? 0;
        int mainSub = SubType(main), viceSub = SubType(vice);
        return (main > 0 ? mainSub : viceSub, main > 0 && vice > 0 && mainSub == viceSub);
    }

    private int SubType(int entry) =>
        entry > 0 && _data.Row("ItemConfig", entry) is { } row ? (int)GameData.Int(row, "_iSubType") : 0;

    private Dictionary<int, Dictionary<int, Dictionary<long, int>>>? _jobAttacks;

    /// <summary>JobAttackConfig keyed as Lua_CFG_JobAttackConfig.ctor: job -> weapon key -> battle mount (0 on foot) -> row.</summary>
    private Dictionary<int, Dictionary<int, Dictionary<long, int>>> JobAttacks => _jobAttacks ??= BuildJobAttacks();

    private Dictionary<int, Dictionary<int, Dictionary<long, int>>> BuildJobAttacks()
    {
        var map = new Dictionary<int, Dictionary<int, Dictionary<long, int>>>();
        foreach (var row in _data.Table("JobAttackConfig").Values)
        {
            int[] weapon = row.TryGetProperty("_kWeaponType", out var w) && w.ValueKind == JsonValueKind.Array
                ? w.EnumerateArray().Select(v => (int)(GameData.Number(v) ?? 0)).ToArray() : [];
            int key = weapon.Length == 0 ? -1 : weapon[0] * 10 + (weapon.Length > 1 ? weapon[1] : 0);
            int job = (int)GameData.Int(row, "_iJobID");
            if (!map.TryGetValue(job, out var byWeapon)) map[job] = byWeapon = new();
            if (!byWeapon.TryGetValue(key, out var byMount)) byWeapon[key] = byMount = new();
            byMount[GameData.Int(row, "_iMountID")] = (int)GameData.Int(row, "_iSkillID");
        }
        return map;
    }

    /// <summary>Lua_CFG_JobAttackConfig._getData: the weapon's rows, else the job's any-weapon rows; of those, the ridden
    /// battle mount's row, else the on-foot row.</summary>
    private int? JobAttack(int career, int weaponKey, long mount = 0) =>
        JobAttacks.TryGetValue(career, out var byWeapon)
            && (byWeapon.TryGetValue(weaponKey, out var byMount) || byWeapon.TryGetValue(-1, out byMount))
            && (byMount.TryGetValue(mount, out int id) || byMount.TryGetValue(0, out id)) ? id : null;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, HashSet<int>> _normalGroups = new();

    /// <summary>Every normal attack group a career can hold (any weapon or battle mount, and JobConfig's): all count as learned.</summary>
    internal HashSet<int> NormalGroups(int career) => _normalGroups.GetOrAdd(career, c =>
        JobAttacks.GetValueOrDefault(c)?.Values.SelectMany(byMount => byMount.Values).Append(NormalAttack(c)).Distinct()
            .Select(id => _data.Row("SkillConfig", id) is { } row ? (int)GameData.Int(row, "_iSkillID") : 0)
            .Where(g => g > 0).ToHashSet()
        ?? (_data.Row("SkillConfig", NormalAttack(c)) is { } only ? [(int)GameData.Int(only, "_iSkillID")] : []));

    private int GroupOf(int rowId) => _data.Row("SkillConfig", rowId) is { } row ? (int)GameData.Int(row, "_iSkillID") : 0;

    /// <summary>
    /// A bar slot's group as cast. Past Novice the skill window shows the Novice tree's Normal Attack cell as the
    /// current normal attack (UI_Sys_Skill_AddBitWidgetBaseCtrl replaces the Novice attack's group with
    /// GetNormalSkillID's), so a bar slot left holding that or another normal attack casts the current one.
    /// </summary>
    private int WheelGroup(RoleProgress p, int group, int atkGroup) =>
        p.Career != 1000 && atkGroup > 0 && (NormalGroups(1000).Contains(group) || NormalGroups(p.Career).Contains(group))
            ? atkGroup : group;

    /// <summary>A weapon change or mounting a battle mount can change the normal attack: resend 5025 (atkSkillId, the
    /// learned attack group).</summary>
    internal async Task WeaponChangedAsync(GameSession session)
    {
        if (session.SkillPlans is null || session.Progress is null) return;
        await CharacterService.SyncAsync(session, SkillDataSync, ClientState(session));
    }

    internal int Rank(int career) => (int)(_data.Row("JobConfig", career) is { } job ? Math.Max(1, GameData.Int(job, "_iJobRank")) : 1);

    /// <summary>The career and its parents (JobConfig._iParentId), novice first.</summary>
    private List<int> CareerChain(int career)
    {
        var chain = new List<int>();
        for (int c = career; c > 0 && chain.Count < 8 && _data.Row("JobConfig", c) is { } job; c = (int)GameData.Int(job, "_iParentId")) chain.Insert(0, c);
        return chain;
    }

    // Tree/Replacements are pure functions of the career and static game data. Level() asked for the tree of every
    // passive group on every stat sheet (~60 full SkillTreeConfig scans per Stats, ~90% of a siege's loop time).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, IReadOnlyDictionary<int, int>> _replacements = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, IReadOnlyList<(int Group, int Career)>> _trees = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, HashSet<int>> _treeGroups = new();

    private IReadOnlyDictionary<int, int> Replacements(int career) => _replacements.GetOrAdd(career, c => _data.Row("JobConfig", c) is { } row
        ? GameData.Pairs(row, "_kSkillReplace").ToDictionary(p => (int)p.Id, p => (int)p.Value)
        : new Dictionary<int, int>());

    /// <summary>Learnable skill groups of a career chain (SkillTreeConfig rows _iX1.._iX7) with the career that teaches them.</summary>
    internal IReadOnlyList<(int Group, int Career)> Tree(int career) => _trees.GetOrAdd(career, BuildTree);

    internal bool InTree(int career, int group) => _treeGroups.GetOrAdd(career, c => Tree(c).Select(t => t.Group).ToHashSet()).Contains(group);

    private List<(int Group, int Career)> BuildTree(int career)
    {
        var list = new List<(int Group, int Career)>();
        var replacements = Replacements(career);
        foreach (int c in CareerChain(career))
            foreach (var row in _data.Table("SkillTreeConfig").Values.Where(r => GameData.Int(r, "_iJob") == c).OrderBy(r => GameData.Int(r, "_iY")))
                for (int x = 1; x <= 7; x++)
                    if (GameData.Int(row, $"_iX{x}") is var g and > 0)
                        list.Add((replacements.GetValueOrDefault((int)g, (int)g), c));
        return list.DistinctBy(t => t.Group).ToList();
    }

    /// <summary>
    /// Free starting level (SkillConfig._iInitialLevel), gated by _iActivateCondition like the client's GetInitLevel.
    /// A free skill with its own _kPreSkillRequired (only Wizard Sight Sprout 12110, which needs Magician Sight 12002)
    /// is free only while those prerequisites hold: the client resets it to 0 together with Sight and saves that.
    /// </summary>
    private int Initial(RoleProgress p, int group, bool requirements = true)
    {
        if (_data.Row("SkillConfig", RealId(group, 1)) is not { } row) return 0;
        int activation = (int)GameData.Int(row, "_iActivateCondition");
        int initial = activation <= 0 || p.Level >= activation ? (int)GameData.Int(row, "_iInitialLevel") : 0;
        return initial > 0 && requirements && !RequirementsMet(p, row, InTree) ? 0 : initial;
    }

    /// <summary>_kPreSkillRequired [group, level] pairs: level -1 means the group must stay unlearned.</summary>
    private bool RequirementsMet(RoleProgress p, JsonElement row, Func<int, int, bool> inTree)
    {
        if (!row.TryGetProperty("_kPreSkillRequired", out var pre) || pre.ValueKind != JsonValueKind.Array) return true;
        foreach (var req in pre.EnumerateArray())
            if (req.ValueKind == JsonValueKind.Array && req.GetArrayLength() >= 2 && GameData.Number(req[0]) is { } g
                && GameData.Number(req[1]) is { } l)
            {
                int actual = inTree(p.Career, (int)g) ? Level(p, (int)g) : 0;
                if (l == -1 ? actual > 0 : l > 0 && actual < l) return false;
            }
        return true;
    }

    private int Parameter(string key, int fallback) => _data.Table("PublicParamConfig").TryGetValue(key, out var value)
        && GameData.Number(value) is { } number && number > 0 ? (int)number : fallback;

    /// <summary>Skill points per job rank: earned from JobLvConfig._iSkillPoint (whole earlier ranks, current rank up to the job level) minus spent.</summary>
    private Dictionary<int, int> SkillPoints(RoleProgress p)
    {
        var points = new Dictionary<int, int>();
        int currentRank = Rank(p.Career);
        for (int rank = 1; rank <= currentRank; rank++)
        {
            int earned = 0;
            for (int lv = 1; lv <= 100; lv++)
            {
                if (rank == currentRank && lv > p.JobLevel) break;
                if (_data.Row("JobLvConfig", rank * 100L + lv) is not { } row) break;
                earned += (int)GameData.Int(row, "_iSkillPoint");
            }
            points[rank] = earned;
        }
        foreach (var (group, career) in Tree(p.Career))
        {
            int rank = Rank(career);
            if (points.ContainsKey(rank) && p.Skills.TryGetValue(group, out int level))
                points[rank] -= Math.Max(0, level - Initial(p, group));
        }
        return points;
    }

    /// <summary>Effective learned level including configured initial skills and the normal attack.</summary>
    public int Level(RoleProgress p, int group)
    {
        int initial = InTree(p.Career, group) ? Initial(p, group) : 0;
        if (NormalGroups(p.Career).Contains(group)) initial = Math.Max(1, initial);
        int saved = Math.Max(p.Skills.GetValueOrDefault(group), p.GrantedSkills.GetValueOrDefault(group));
        foreach (var (old, replacement) in Replacements(p.Career))
            if (replacement == group) saved = Math.Max(saved, p.Skills.GetValueOrDefault(old));
        return Math.Max(saved, initial);
    }

    private DBSkillData BuildFresh(GameSession session)
    {
        var p = session.Progress!;
        int atk = NormalAttack(session);
        int atkGroup = GroupOf(atk);
        var tree = Tree(p.Career);
        var skillData = new DBSkillData { MaxPlanCount = PlanCount, CurrentPlanIndex = 0, CurrentAtkSkillId = atk };
        if (atkGroup > 0) skillData.SkillList.Add(atkGroup);
        skillData.SkillList.Add(tree.Select(t => t.Group).Where(g => g != atkGroup).Distinct());

        var plan = new SkillPlan { IsUnlock = true };
        foreach (int group in skillData.SkillList)
            plan.SkillAssigns[group] = new SkillData { SkillId = group, SkillLevel = Level(p, group) };
        plan.SkillQueue[0] = new SkillData(); // the client pairs() it unguarded
        plan.ManualLoadSkill.Add(new SkillData()); // repeated load lists are read by the client even when empty
        plan.AutoLoadSkill.Add(new SkillData());
        foreach (var (rank, points) in SkillPoints(p)) plan.SkillPoints[rank] = Math.Max(0, points);
        skillData.SkillAssignPlans.Add(plan);
        for (int index = 1; index < PlanCount; index++)
        {
            var blank = new SkillPlan { IsUnlock = true };
            var blankProgress = CharacterService.GenreCloneProgress(p);
            blankProgress.Skills.Clear();
            foreach (int group in skillData.SkillList)
                blank.SkillAssigns[group] = new SkillData
                {
                    SkillId = group,
                    SkillLevel = group == atkGroup ? 1 : Initial(blankProgress, group)
                };
            blank.SkillQueue[0] = new SkillData(); // paired by the client even in a fresh plan.
            blank.ManualLoadSkill.Add(new SkillData());
            blank.AutoLoadSkill.Add(new SkillData());
            foreach (var (rank, points) in SkillPoints(blankProgress)) blank.SkillPoints[rank] = Math.Max(0, points);
            skillData.SkillAssignPlans.Add(blank);
        }

        var wheel = new SkillWheel { AtkSkillId = atk };
        foreach (var (slot, saved) in Bar(p))
            if (WheelGroup(p, saved, atkGroup) is var group && RealId(group, Level(p, group)) is var id and > 0)
                wheel.WhellSkills[slot] = new SkillWheelSlot { SkillConfigId = id, Enable = true };
        skillData.SkillWheel = wheel;
        skillData.OriginSkillWheel = wheel.Clone();
        return skillData;
    }

    /// <summary>Load 5025 plans once per selected role, preserving the former single active progression plan.</summary>
    private void StageLoad(GameSession session, byte[]? saved)
    {
        DBSkillData state;
        if (saved is null) state = BuildFresh(session);
        else if (LegacyJson(saved))
        {
            try { state = ImportLegacyJson(session, saved); }
            catch (JsonException ex)
            {
                _log.LogWarning(ex, "Invalid legacy skill plans for role {Role}; reconstructing from progress", session.Role!.Id);
                state = BuildFresh(session);
            }
        }
        else state = DBSkillData.Parser.ParseFrom(saved);
        var fresh = BuildFresh(session);
        while (state.SkillAssignPlans.Count > PlanCount) state.SkillAssignPlans.RemoveAt(state.SkillAssignPlans.Count - 1);
        while (state.SkillAssignPlans.Count < PlanCount) state.SkillAssignPlans.Add(fresh.SkillAssignPlans[state.SkillAssignPlans.Count].Clone());
        foreach (var plan in state.SkillAssignPlans)
            foreach (var (group, free) in fresh.SkillAssignPlans[1].SkillAssigns)
                if (!plan.SkillAssigns.ContainsKey(group)) plan.SkillAssigns[group] = free.Clone();
        state.MaxPlanCount = PlanCount;
        if (state.CurrentPlanIndex is < 0 or >= PlanCount) state.CurrentPlanIndex = 0;
        state.SkillList.Clear();
        state.SkillList.Add(fresh.SkillList);
        state.CurrentAtkSkillId = fresh.CurrentAtkSkillId;
        // The role progress columns remain the selected plan's authoritative combat values.
        // A stale saved plan must not clear live skills before StageSkillPlan reports a refusal.
        if (saved is not null)
        {
            var staged = CharacterService.GenreCloneProgress(session.Progress!);
            int result = StageSkillPlan(session, state, staged, state.CurrentPlanIndex);
            if (result == 0) CopySkills(staged, session.Progress!);
            else
            {
                _log.LogWarning("{Name} rebuilds invalid saved plan {Plan} from authoritative progress: {Result}",
                    session.Role!.Name, state.CurrentPlanIndex, result);
                session.SkillPlans = state;
                state = Build(session);
                var rebuilt = CharacterService.GenreCloneProgress(session.Progress!);
                if (StageSkillPlan(session, state, rebuilt, state.CurrentPlanIndex) == 0)
                    CopySkills(rebuilt, session.Progress!);
            }
        }
        session.SkillPlans = state;
        session.SkillPlans = Build(session); // persist the selected plan's authoritative wheel after legacy import/job changes
    }

    private static bool LegacyJson(byte[] blob)
    {
        int i = 0;
        while (i < blob.Length && blob[i] is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t') i++;
        return i < blob.Length && blob[i] == (byte)'{';
    }

    private static int JsonInt(JsonElement obj, string name, int fallback = 0) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int number) ? number : fallback;

    private static Dictionary<int, int> JsonMap(JsonElement obj, string name)
    {
        var result = new Dictionary<int, int>();
        if (!obj.TryGetProperty(name, out var map) || map.ValueKind != JsonValueKind.Object) return result;
        foreach (var entry in map.EnumerateObject())
            if (int.TryParse(entry.Name, out int key) && entry.Value.ValueKind == JsonValueKind.Number
                && entry.Value.TryGetInt32(out int value)) result[key] = value;
        return result;
    }

    /// <summary>One-time import of the premerge JSON State/PlanState shape into the shared protobuf owner.</summary>
    private DBSkillData ImportLegacyJson(GameSession session, byte[] blob)
    {
        using var document = JsonDocument.Parse(blob);
        var json = document.RootElement;
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("Plans", out var savedPlans)
            || savedPlans.ValueKind != JsonValueKind.Array) throw new JsonException("Legacy plans are absent");
        var fresh = BuildFresh(session);
        var imported = fresh.Clone();
        imported.SkillAssignPlans.Clear();
        int index = 0;
        foreach (var source in savedPlans.EnumerateArray())
        {
            if (index == PlanCount) break;
            if (source.ValueKind != JsonValueKind.Object) throw new JsonException("Legacy plan is not an object");
            var plan = fresh.SkillAssignPlans[1].Clone();
            plan.IsUnlock = !source.TryGetProperty("Unlocked", out var unlocked)
                || unlocked.ValueKind != JsonValueKind.False;
            plan.Name = source.TryGetProperty("Name", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString() ?? "" : "";
            plan.SkillGenre = JsonInt(source, "Genre");
            foreach (var (group, level) in JsonMap(source, "Levels"))
                plan.SkillAssigns[group] = new SkillData { SkillId = group, SkillLevel = level };
            ImportLegacyLoads(source, "Manual", "ManualLevels", plan.ManualLoadSkill, plan, session.Progress!);
            ImportLegacyLoads(source, "Auto", "AutoLevels", plan.AutoLoadSkill, plan, session.Progress!);
            var queueLevels = JsonMap(source, "QueueLevels");
            foreach (var (slot, group) in JsonMap(source, "Queue"))
                if (slot >= 0 && slot < Parameter("QueueNum", 12))
                    plan.SkillQueue[slot] = new SkillData
                    {
                        SkillId = group,
                        SkillLevel = queueLevels.GetValueOrDefault(slot, PlanLevel(session.Progress!, plan, group))
                    };
            imported.SkillAssignPlans.Add(plan);
            index++;
        }
        imported.CurrentPlanIndex = JsonInt(json, "Current");
        _log.LogInformation("Imported legacy JSON skill plans for role {Role}", session.Role!.Id);
        return imported;
    }

    private int PlanLevel(RoleProgress progress, SkillPlan plan, int group) => Math.Max(Initial(progress, group),
        plan.SkillAssigns.GetValueOrDefault(group)?.SkillLevel ?? 0);

    private void ImportLegacyLoads(JsonElement source, string mapName, string levelsName,
        Google.Protobuf.Collections.RepeatedField<SkillData> destination, SkillPlan plan, RoleProgress progress)
    {
        var slots = JsonMap(source, mapName);
        var levels = JsonMap(source, levelsName);
        destination.Clear();
        int last = slots.Keys.Where(slot => slot is >= 1 and <= BarSlots).DefaultIfEmpty(0).Max();
        for (int slot = 1; slot <= Math.Max(1, last); slot++)
        {
            int group = slots.GetValueOrDefault(slot);
            destination.Add(new SkillData
            {
                SkillId = group,
                SkillLevel = group > 0 ? levels.GetValueOrDefault(slot, PlanLevel(progress, plan, group)) : 0
            });
        }
    }

    /// <summary>C/D 13c: frozen plan and its active progress publish together after the owned receipt.</summary>
    private async Task CommitAsync(GameSession session, DBSkillData next, RoleProgress? progress = null,
        string kind = "cd13c/skill-plan", Action? applied = null)
    {
        var tx = _roles.NewTransaction();
        tx.ItemDelta(session.Role!.Id, session.Items, session.Items);
        if (progress != null) tx.Progress(session.Role.Id, progress);
        tx.RoleBlob(session.Role.Id, "skill_plans", next.ToByteArray());
        tx.WithEconomy(_character.ProgressionEconomy(session, kind, [], [],
            new
            {
                Plans = Convert.ToBase64String(next.ToByteArray()), Skills = progress?.Skills,
                Bar = progress?.SkillBar
            }));
        await _roles.CommitAsync(tx, kind, _ =>
        {
            session.SkillPlans = next;
            if (progress != null) CopySkills(progress, session.Progress!);
            applied?.Invoke();
        });
    }

    public async Task LoadAsync(GameSession session)
    {
        using var held = await CharacterService.Lock(session);
        var saved = _roles.LoadSkillPlans(session.Role!.Id);
        var original = session.Progress!;
        var priorPlans = session.SkillPlans;
        var staged = CharacterService.GenreCloneProgress(original);
        DBSkillData next;
        session.Progress = staged;
        try { StageLoad(session, saved); next = session.SkillPlans!; }
        finally { session.Progress = original; session.SkillPlans = priorPlans; }
        bool changedProgress = !staged.Skills.OrderBy(x => x.Key).SequenceEqual(original.Skills.OrderBy(x => x.Key)) ||
            !staged.SkillBar.OrderBy(x => x.Key).SequenceEqual(original.SkillBar.OrderBy(x => x.Key));
        if (saved == null || !saved.AsSpan().SequenceEqual(next.ToByteArray()) || changedProgress)
            await CommitAsync(session, next, changedProgress ? staged : null, "cd13c/skill-login-reconcile");
        else session.SkillPlans = next;
    }

    /// <summary>
    /// 5025 as the client gets it: <see cref="Build"/> (which projects the pet guardian slot) plus the mount slot.
    /// </summary>
    internal DBSkillData ClientState(GameSession session)
    {
        var state = Build(session);
        // A flying mount's take-off (on the ground) or landing (in the air): LC_System_Skill reads the slot map from
        // whellSkills and JoyStickSkillNormalMixin._updateFlyMountSkillShow shows the mount button for SkillWheelMount1.
        session.MountSkillShown = MountSkill(session);
        if (session.MountSkillShown > 0)
        {
            var slot = new SkillWheelSlot { SkillConfigId = session.MountSkillShown, Enable = true };
            (state.SkillWheel ??= new SkillWheel()).WhellSkills[(int)SkillWheelSlotType.SkillWheelMount1] = slot;
            (state.OriginSkillWheel ??= new SkillWheel()).WhellSkills[(int)SkillWheelSlotType.SkillWheelMount1] = slot.Clone();
        }
        return state;
    }

    private int MountSkill(GameSession s) =>
        MountService.FlySkills(s, _data) is { } fly && _data.Row("SkillConfig", s.WorldState.Flying ? fly.Down : fly.Up) != null
            ? (s.WorldState.Flying ? fly.Down : fly.Up) : 0;

    /// <summary>Resend 5025 when the mount slot's skill changed (riding a flying mount, take-off, landing).</summary>
    public async Task RefreshMountSkillAsync(GameSession s)
    {
        if (s.SkillPlans == null || s.Progress == null || MountSkill(s) == s.MountSkillShown) return;
        await SyncAsync(s);
    }

    public DBSkillData Build(GameSession session)
    {
        if (session.SkillPlans == null) throw new InvalidOperationException("Skill plans must be loaded before use.");
        var state = session.SkillPlans!.Clone();
        var current = state.SkillAssignPlans[state.CurrentPlanIndex];
        current.SkillAssigns.Clear();
        var generated = BuildFresh(session);
        foreach (var (group, level) in generated.SkillAssignPlans[0].SkillAssigns) current.SkillAssigns[group] = level.Clone();
        current.SkillPoints.Clear();
        foreach (var (rank, points) in generated.SkillAssignPlans[0].SkillPoints) current.SkillPoints[rank] = points;
        state.SkillList.Clear();
        state.SkillList.Add(generated.SkillList);
        foreach (var plan in state.SkillAssignPlans)
        {
            foreach (var (old, replacement) in Replacements(session.Progress!.Career))
            {
                if (plan.SkillAssigns.TryGetValue(old, out var assignment))
                {
                    plan.SkillAssigns.Remove(old);
                    int level = Math.Max(plan.SkillAssigns.GetValueOrDefault(replacement)?.SkillLevel ?? 0, assignment.SkillLevel);
                    plan.SkillAssigns[replacement] = new SkillData { SkillId = replacement, SkillLevel = level };
                }
                foreach (var load in plan.ManualLoadSkill.Concat(plan.AutoLoadSkill).Concat(plan.SkillQueue.Values))
                    if (load.SkillId == old) load.SkillId = replacement;
            }
            foreach (int group in generated.SkillList)
                if (!plan.SkillAssigns.ContainsKey(group))
                    plan.SkillAssigns[group] = new SkillData { SkillId = group, SkillLevel = Initial(session.Progress!, group) };
            var preview = CharacterService.GenreCloneProgress(session.Progress!);
            preview.Skills.Clear();
            foreach (int group in generated.SkillList)
                if (plan.SkillAssigns.TryGetValue(group, out var assigned)
                    && assigned.SkillLevel > Initial(preview, group)) preview.Skills[group] = assigned.SkillLevel;
            plan.SkillPoints.Clear();
            foreach (var (rank, points) in SkillPoints(preview)) plan.SkillPoints[rank] = Math.Max(0, points);
            if (plan.SkillQueue.Count == 0) plan.SkillQueue[0] = new SkillData();
            if (plan.ManualLoadSkill.Count == 0) plan.ManualLoadSkill.Add(new SkillData());
            if (plan.AutoLoadSkill.Count == 0) plan.AutoLoadSkill.Add(new SkillData());
        }
        state.CurrentAtkSkillId = generated.CurrentAtkSkillId;
        state.SkillWheel = generated.SkillWheel;
        state.OriginSkillWheel = generated.OriginSkillWheel;
        var selected = state.SkillAssignPlans[state.CurrentPlanIndex];
        if (selected.ManualLoadSkill.Any(skill => skill.SkillId > 0))
        {
            state.SkillWheel.WhellSkills.Clear();
            int atkGroup = GroupOf(generated.CurrentAtkSkillId);
            for (int slot = 1; slot <= Math.Min(BarSlots, selected.ManualLoadSkill.Count); slot++)
                if (selected.ManualLoadSkill[slot - 1] is { SkillId: > 0 } skill
                    && WheelGroup(session.Progress!, skill.SkillId, atkGroup) is var group
                    && RealId(group, group == skill.SkillId ? skill.SkillLevel : Level(session.Progress!, group)) is var id and > 0)
                    state.SkillWheel.WhellSkills[slot] = new SkillWheelSlot { SkillConfigId = id, Enable = true };
        }
        foreach (var (_, skill) in selected.SkillQueue.OrderBy(kv => kv.Key))
            if (skill.SkillId > 0 && RealId(skill.SkillId, skill.SkillLevel) is var id and > 0)
            { state.SkillWheel.SkillQueue = new SkillWheelSlot { SkillConfigId = id, Enable = true }; break; }
        if (PetWheel?.Invoke(session) is { } pet)
            state.SkillWheel.WhellSkills[(int)SkillWheelSlotType.SkillWheelPet] = pet;
        state.OriginSkillWheel = state.SkillWheel.Clone();
        return state;
    }

    /// <summary>The skill bar: saved slots, or the learned active skills in tree order when nothing was set.</summary>
    private IEnumerable<(int Slot, int Group)> Bar(RoleProgress p)
    {
        if (p.SkillBar.Count > 0)
        {
            foreach (var (slot, saved) in p.SkillBar.OrderBy(s => s.Key))
                if (BarGroup(p, saved) is var group and > 0 && Level(p, group) > 0) yield return (slot, group);
            yield break;
        }
        int next = 1;
        // The PC bar renders whellSkills, not atkSkillId, and the client attacks a selected target by
        // itself only with eSelectTargetNormalAttack set, so expose the career's learned normal attack
        // in a clickable slot (inferred, as for a Novice, who has no other active skill at level 1).
        if (CareerNormalGroup(p.Career) is var attack and > 0 && Level(p, attack) > 0 && IsActive(attack)) yield return (next++, attack);
        foreach (var (group, _) in Tree(p.Career))
        {
            if (next > BarSlots) break;
            if (Level(p, group) > 0 && IsActive(group) && !IsChainNormalGroup(p, group)) yield return (next++, group);
        }
    }

    /// <summary>
    /// The group a bar slot shows. A normal attack on the bar is the career's own: the tree and a saved bar still hold
    /// the earlier careers' (the Novice's 10000), and shown, the client cast the Novice's 1000001 instead of the career's
    /// (1300001 for an Archer). Without its own (a Mage's 1200001 has no SkillConfig row) the slot is empty (0).
    /// </summary>
    private int BarGroup(RoleProgress p, int group) => IsChainNormalGroup(p, group) ? CareerNormalGroup(p.Career) : group;

    /// <summary>Active skills can go on the bar: SkillConfig._iBasicType 1 (passives such as Sword Mastery have 0).</summary>
    private bool IsActive(int group) =>
        RealId(group, 1) is var id and > 0 && _data.Row("SkillConfig", id) is { } row && GameData.Int(row, "_iBasicType") == 1;

    private bool IsNormalGroup(RoleProgress p, int group) => NormalGroups(p.Career).Contains(group);

    /// <summary>The skill group of a career's normal attack (JobConfig._kSkillNormalID).</summary>
    private int CareerNormalGroup(int career) =>
        _data.Row("SkillConfig", NormalAttack(career)) is { } row ? (int)new SkillConfig(row).SkillId : 0;

    /// <summary>The normal attack of the career or of one before it (the Novice's 10000 for every career).</summary>
    private bool IsChainNormalGroup(RoleProgress p, int group) =>
        group > 0 && CareerChain(p.Career).Any(career => CareerNormalGroup(career) == group);


    public async Task SyncAsync(GameSession session)
    {
        if (session.SkillPlans == null) await LoadAsync(session);
        var state = ClientState(session);
        // A granted skill (a socketed card's Skill(3)) is listed with its level in the current plan, where the skill panel's
        // card skills read it (UI_Sys_SkillPanelModel.GetCardSkillList: ServerAllSkills = skillList, then the plan's
        // skillAssigns with level > 0). It is sent, never saved in a plan: a plan switch checks its groups against the tree.
        if (session.Progress is { GrantedSkills.Count: > 0 } progress)
        {
            var current = state.SkillAssignPlans[state.CurrentPlanIndex];
            foreach (var (group, level) in progress.GrantedSkills)
            {
                if (!state.SkillList.Contains(group)) state.SkillList.Add(group);
                current.SkillAssigns[group] = new SkillData { SkillId = group, SkillLevel = level };
            }
        }
        await CharacterService.SyncAsync(session, SkillDataSync, state);
    }

    /// <summary>Build a role-progress candidate from an owned skill plan before a Build-window switch.</summary>
    internal int StageSkillPlan(GameSession session, DBSkillData state, RoleProgress progress, int index)
    {
        if (index is < 0 or >= PlanCount || index >= state.SkillAssignPlans.Count || !state.SkillAssignPlans[index].IsUnlock)
            return ErrorCodes.Parameter;
        var source = state.SkillAssignPlans[index];
        var tree = Tree(progress.Career).ToDictionary(t => t.Group, t => t.Career);
        // BuildFresh stores normal attacks in plans, but Level grants the current attack implicitly.
        // Ignore those from the current career chain when an older plan is selected after a job change.
        var normalAttacks = CareerChain(progress.Career).SelectMany(NormalGroups).ToHashSet();
        progress.Skills.Clear();
        foreach (var (group, assignment) in source.SkillAssigns)
        {
            if (assignment.SkillLevel == 1 && normalAttacks.Contains(group)) continue;
            if (assignment.SkillLevel < 0 || assignment.SkillLevel > MaxLevel(group)
                || (!tree.ContainsKey(group) && assignment.SkillLevel > 0)) return ErrorCodes.SkillNotInTree;
            if (assignment.SkillId != 0 && assignment.SkillId != group) return ErrorCodes.Parameter;
            if (assignment.SkillLevel > Initial(progress, group)) progress.Skills[group] = assignment.SkillLevel;
        }
        // Plans saved before Sight Sprout's free level followed Sight hold it at 1 without Sight: drop that free level.
        foreach (var (group, level) in progress.Skills.ToList())
            if (level <= Initial(progress, group, requirements: false) && Initial(progress, group) == 0)
            {
                progress.Skills.Remove(group);
                source.SkillAssigns[group] = new SkillData { SkillId = group, SkillLevel = 0 };
            }
        var remaining = SkillPoints(progress);
        if (remaining.Values.Any(v => v < 0)) return ErrorCodes.SkillPointsShort;
        if (progress.Skills.Any(kv => RealId(kv.Key, kv.Value) == 0)) return ErrorCodes.SkillMaxLevel;
        if (!PrerequisitesMet(progress)) return ErrorCodes.SkillPrerequisite;
        source.SkillPoints.Clear();
        foreach (var (rank, points) in remaining) source.SkillPoints[rank] = points;
        progress.SkillBar.Clear();
        for (int slot = 1; slot <= Math.Min(BarSlots, source.ManualLoadSkill.Count); slot++)
            if (source.ManualLoadSkill[slot - 1] is { SkillId: > 0 } skill && Level(progress, skill.SkillId) > 0)
                progress.SkillBar[slot] = skill.SkillId;
        state.CurrentPlanIndex = index;
        return 0;
    }

    public async Task SwitchPlanAsync(GameSession session, SwitchPlanReq req)
    {
        bool changed = false;
        {
            using var held = await CharacterService.Lock(session);
            var next = Build(session);
            var progress = CharacterService.GenreCloneProgress(session.Progress!);
            int result = req.Plan is < 0 or >= PlanCount || !next.SkillAssignPlans[req.Plan].IsUnlock
                ? ErrorCodes.Parameter : PlanSwitchCooldownError(session);
            if (result == 0) result = StageSkillPlan(session, next, progress, req.Plan);
            if (result == 0 && req.Plan != session.SkillPlans!.CurrentPlanIndex)
            {
                await CommitAsync(session, next, progress, "cd13c/skill-switch",
                    () => session.SkillPlanSwitchUtc = DateTime.UtcNow);
                changed = true;
            }
            await session.SendGameAsync(SwitchPlan, new SwitchPlanResp { Result = result, Plan = req.Plan, Flag = 0 });
            if (result == 0) await SyncAsync(session);
        }
        if (changed) await OnActivePlanChangedAsync(session);
    }

    public async Task RenamePlanAsync(GameSession session, SkillPlanChangeNameReq req)
    {
        using var held = await CharacterService.Lock(session);
        int result = req.Plan is < 0 or >= PlanCount || !session.SkillPlans!.SkillAssignPlans[req.Plan].IsUnlock
            || string.IsNullOrWhiteSpace(req.Name) || req.Name.Any(char.IsControl)
            || req.Name.EnumerateRunes().Count() > Parameter("SchemeNameNumMax", 8) ? ErrorCodes.Parameter : 0;
        if (result == 0)
        {
            var next = session.SkillPlans!.Clone();
            next.SkillAssignPlans[req.Plan].Name = req.Name;
            await CommitAsync(session, next, kind: "cd13c/skill-rename");
        }
        await session.SendGameAsync(RenamePlan, new SkillPlanChangeNameResp { Result = result, Plan = req.Plan, Name = result == 0 ? req.Name : "" });
        if (result == 0) await SyncAsync(session);
    }

    public Task ChangeNameAsync(GameSession session, SkillPlanChangeNameReq req) => RenamePlanAsync(session, req);

    private static bool InCombat(GameSession session) => (session.StatusBits & FightBit) != 0;

    /// <summary>Shared gate for direct skill switches and Build-window child switches.</summary>
    internal int PlanSwitchCooldownError(GameSession session) => InCombat(session) ? ErrorCodes.Parameter
        : DateTime.UtcNow - session.SkillPlanSwitchUtc < TimeSpan.FromSeconds(Parameter("SchemeCD", 3))
            ? ErrorCodes.Cooldown : 0;

    /// <summary>Run skill-change observers after a combined Build transaction releases the character lock.</summary>
    internal async Task OnActivePlanChangedAsync(GameSession session)
    {
        foreach (var handler in Learned) await handler(session);
    }

    internal static void CopySkills(RoleProgress from, RoleProgress to)
    {
        to.SourceRevision = from.SourceRevision;
        to.Skills.Clear();
        foreach (var kv in from.Skills) to.Skills[kv.Key] = kv.Value;
        to.SkillBar.Clear();
        foreach (var kv in from.SkillBar) to.SkillBar[kv.Key] = kv.Value;
    }

    private Dictionary<int, int> UpgradeGroups() => _upgradedGroupByBase ??= _data.Table("SkillConfig").Values
        .Where(r => GameData.Int(r, "_iMode") == 0 && GameData.Int(r, "_iLevel") == 1
                    && GameData.Int(r, "_iSkillAdvanced") > 0)
        .GroupBy(r => (int)GameData.Int(r, "_iSkillAdvanced"))
        .ToDictionary(g => g.Key, g => (int)GameData.Int(g.First(), "_iSkillID"));

    /// <summary>31151 accepts desired levels, including dependent refunds, as one final graph.</summary>
    public async Task PlanUpdateAsync(GameSession session, SkillGrowthSkillPlanUpdateReq req)
    {
        bool learned = false;
        using (await CharacterService.Lock(session))
        {
            var p = session.Progress!;
            var plans = Build(session);
            int code = req.Plan is < 0 or >= PlanCount || !plans.SkillAssignPlans[req.Plan].IsUnlock
                ? ErrorCodes.Parameter : 0;
            var tree = Tree(p.Career).Select(t => t.Group).ToHashSet();
            if (code == 0)
            {
                var selected = plans.SkillAssignPlans[req.Plan];
                var before = selected.Clone();
                foreach (var (group, target) in req.LevelUpList)
                {
                    code = !tree.Contains(group) ? ErrorCodes.SkillNotInTree
                        : target.SkillId != 0 && target.SkillId != group ? ErrorCodes.Parameter
                        : target.SkillLevel > MaxLevel(group) ? ErrorCodes.SkillMaxLevel
                        : target.SkillLevel < 0 ? ErrorCodes.Parameter : 0;
                    if (code != 0) break;
                    selected.SkillAssigns[group] = new SkillData { SkillId = group, SkillLevel = target.SkillLevel };
                }
                if (code == 0)
                    foreach (var (group, target) in req.LevelUpList)
                        if (target.SkillLevel > (session.SkillPlans!.SkillAssignPlans[req.Plan].SkillAssigns
                                .GetValueOrDefault(group)?.SkillLevel ?? 0)
                            && UpgradeGroups().TryGetValue(group, out int upgraded)
                            && selected.SkillAssigns.GetValueOrDefault(upgraded)?.SkillLevel > 0)
                        { code = ErrorCodes.SkillPrerequisite; break; }
                if (code == 0)
                {
                    var candidate = CharacterService.GenreCloneProgress(p);
                    int oldIndex = plans.CurrentPlanIndex;
                    code = StageSkillPlan(session, plans, candidate, req.Plan);
                    plans.CurrentPlanIndex = oldIndex;
                    // Free levels can't be unlearned, judged on the final graph: Sight Sprout drops to 0 with Sight
                    // and comes back free with it, though the client still shows the 0 it set when Sight went.
                    if (code == 0)
                        foreach (var (group, target) in req.LevelUpList)
                            if (Initial(candidate, group) is var free && target.SkillLevel < free)
                                selected.SkillAssigns[group] = new SkillData { SkillId = group, SkillLevel = free };
                    // Staging drops a free level whose prerequisites are missing; a request can't ask for it.
                    if (code == 0 && req.LevelUpList.Any(kv => kv.Value.SkillLevel > Level(candidate, kv.Key)))
                        code = ErrorCodes.SkillPrerequisite;
                    if (code == 0)
                    {
                        PruneLoads(selected, candidate, group => PlanLevel(p, before, group));
                        await CommitAsync(session, plans, req.Plan == oldIndex ? candidate : null,
                            "cd13c/skill-update");
                        learned = true;
                        await SyncAsync(session);
                    }
                }
            }
            _log.LogInformation("{Name} learns {Skills}: {Result}", session.Role!.Name,
                string.Join(",", req.LevelUpList.Select(kv => $"{kv.Key}:{kv.Value.SkillLevel}")),
                code == 0 ? "ok" : "refused");
            await session.SendGameAsync(PlanUpdate, new SkillGrowthSkillPlanUpdateResp { Result = code });
        }
        // Learned observers may take the same StateLock (cart capacity); run them after the commit and reply.
        if (learned) foreach (var handler in Learned) await handler(session);
    }

    /// <summary>
    /// GM panel GmCurAllSkillLevUpMax "learn all": raise every skill of the current career chain, in tree order, one
    /// level at a time while the plan stays valid (skill points, prerequisites, maximum levels), repeating until
    /// nothing more fits. Spends only the points the role has; returns how many levels were added.
    /// </summary>
    public async Task<int> GmLearnAllAsync(GameSession session)
    {
        int raised = 0;
        using (await CharacterService.Lock(session))
        {
            var p = session.Progress!;
            var plans = Build(session);
            int index = plans.CurrentPlanIndex;
            var selected = plans.SkillAssignPlans[index];
            var before = selected.Clone();
            var tree = Tree(p.Career);
            for (bool more = true; more;)
            {
                more = false;
                foreach (var (group, _) in tree)
                {
                    if (UpgradeGroups().TryGetValue(group, out int upgraded) && selected.SkillAssigns.GetValueOrDefault(upgraded)?.SkillLevel > 0)
                        continue;
                    int level = Math.Max(selected.SkillAssigns.GetValueOrDefault(group)?.SkillLevel ?? 0, Level(p, group));
                    if (level >= MaxLevel(group)) continue;
                    var trial = plans.Clone();
                    trial.SkillAssignPlans[index].SkillAssigns[group] = new SkillData { SkillId = group, SkillLevel = level + 1 };
                    if (StageSkillPlan(session, trial, CharacterService.GenreCloneProgress(p), index) != 0) continue;
                    selected.SkillAssigns[group] = new SkillData { SkillId = group, SkillLevel = level + 1 };
                    raised++;
                    more = true;
                }
            }
            if (raised > 0)
            {
                var candidate = CharacterService.GenreCloneProgress(p);
                if (StageSkillPlan(session, plans, candidate, index) != 0) return 0;
                PruneLoads(selected, candidate, group => PlanLevel(p, before, group));
                await CommitAsync(session, plans, candidate, "cd13c/skill-update");
                await SyncAsync(session);
            }
        }
        if (raised > 0) foreach (var handler in Learned) await handler(session);
        return raised;
    }

    /// <summary>Skills were learned (quests: 104012 CheckCostJobPoint).</summary>
    public List<Func<GameSession, Task>> Learned { get; } = new();

    /// <summary>Skill points spent on the current tree (learned levels above the free initial ones).</summary>
    public int SpentPoints(RoleProgress p) => p.Skills.Sum(kv => Math.Max(0, kv.Value - Initial(p, kv.Key)));

    /// <summary>311001 filters by the teaching career's rank, just as the skill-point dataset does.</summary>
    public int SpentPoints(RoleProgress p, int rank) => Tree(p.Career)
        .Where(t => Rank(t.Career) == rank).Select(t => t.Group).Distinct()
        .Sum(g => Math.Max(0, p.Skills.GetValueOrDefault(g) - Initial(p, g)));

    /// <summary>Evaluate desired-level row prerequisites, including the negative required-level sentinel.</summary>
    private bool PrerequisitesMet(RoleProgress p)
    {
        var tree = Tree(p.Career).Select(t => t.Group).ToHashSet();
        foreach (int group in tree)
        {
            int level = Level(p, group);
            if (level <= 0) continue;
            if (_data.Row("SkillConfig", RealId(group, level)) is not { } row
                || !RequirementsMet(p, row, (_, g) => tree.Contains(g))) return false;
        }
        return true;
    }

    /// <summary>
    /// Fit the plan's bar, auto and queue loads to its new levels. A load is capped at the learned level; when
    /// <paramref name="before"/> gives the levels the plan had, a load that sat at the old top level follows a raise
    /// (Angelus 1 on the bar becomes Angelus 2 when level 2 is learned), while one deliberately set lower stays.
    /// </summary>
    private void PruneLoads(SkillPlan plan, RoleProgress progress, Func<int, int>? before = null)
    {
        int Fit(SkillData load, int level) =>
            before != null && before(load.SkillId) is var old and > 0 && load.SkillLevel >= old && level > old
                ? level : Math.Min(load.SkillLevel, level);
        foreach (var list in new[] { plan.ManualLoadSkill, plan.AutoLoadSkill })
            foreach (var load in list)
                if (load.SkillId > 0)
                {
                    int level = Level(progress, load.SkillId);
                    if (level <= 0) { load.SkillId = 0; load.SkillLevel = 0; }
                    else load.SkillLevel = Fit(load, level);
                }
        foreach (var key in plan.SkillQueue.Keys.ToArray())
        {
            var load = plan.SkillQueue[key];
            int level = Level(progress, load.SkillId);
            if (load.SkillId > 0 && level <= 0) plan.SkillQueue[key] = new SkillData();
            else if (load.SkillId > 0) load.SkillLevel = Fit(load, level);
        }
        if (plan.SkillQueue.Count == 0) plan.SkillQueue[0] = new SkillData();
    }

    /// <summary>31153: put a skill on a bar slot (socketNum = slot - 1), remove it, or replace one.</summary>
    public async Task LoadAsync(GameSession session, SkillLoadReq req)
    {
        using var held = await CharacterService.Lock(session);
        var p = session.Progress!;
        var plans = Build(session);
        var selected = plans.SkillAssignPlans[plans.CurrentPlanIndex];
        var loadList = req.IsManual ? selected.ManualLoadSkill : selected.AutoLoadSkill;
        if (req.IsManual && loadList.All(skill => skill.SkillId == 0))
            foreach (var (slot, barGroup) in Bar(p))
            {
                while (loadList.Count < slot) loadList.Add(new SkillData());
                loadList[slot - 1] = new SkillData { SkillId = barGroup, SkillLevel = Level(p, barGroup) };
            }
        int target = req.SocketNum;
        int op = (int)req.OpType;
        int group = req.Info?.SkillId ?? 0;
        int code = InCombat(session) || target is < 0 or >= BarSlots || op is < 1 or > 3
            ? ErrorCodes.Parameter : 0;
        if (code == 0 && op == 2)
        {
            if (target >= loadList.Count || loadList[target].SkillId != group
                || req.IsManual && loadList.Count(skill => skill.SkillId > 0) <= 1)
                code = ErrorCodes.Parameter;
        }
        else if (code == 0)
        {
            // Any career may load its normal attack: the skill window draws the Novice tree's Normal Attack cell as
            // the current one (Arrow Attack for an Archer with a bow), and UI_Sys_Skill_SetItemWidget_PCCtrl only
            // stops passives, unlearned skills and PublicParamConfig BanSkill/BanSkillCanPoints from being dragged.
            int learned = Level(p, group);
            if (group <= 0 || learned <= 0 || !IsActive(group)
                || req.Info!.SkillLevel < 1 || req.Info.SkillLevel > learned
                || op == 1 && target < loadList.Count && loadList[target].SkillId > 0
                    && loadList[target].SkillId != group
                || op == 3 && (target >= loadList.Count || loadList[target].SkillId != req.ReplaceInfo?.SkillId))
                code = ErrorCodes.SkillNotLearned;
        }
        if (code == 0)
        {
            while (loadList.Count <= target) loadList.Add(new SkillData());
            if (op == 2) loadList[target] = new SkillData();
            else
            {
                // A slot holding the Novice attack or another normal attack casts the current one, so it counts as
                // the same skill and moves too.
                int atkGroup = GroupOf(plans.CurrentAtkSkillId);
                int cast = WheelGroup(p, group, atkGroup);
                foreach (var skill in loadList.Where(skill => skill.SkillId > 0 && WheelGroup(p, skill.SkillId, atkGroup) == cast))
                { skill.SkillId = 0; skill.SkillLevel = 0; }
                loadList[target] = req.Info!.Clone();
            }
            if (req.IsManual)
            {
                var progress = CharacterService.GenreCloneProgress(p);
                int result = StageSkillPlan(session, plans, progress, plans.CurrentPlanIndex);
                if (result != 0) code = result;
                else
                {
                    await CommitAsync(session, plans, progress, "cd13c/skill-load");
                }
            }
            else await CommitAsync(session, plans, kind: "cd13c/skill-load");
            if (code == 0) { session.SkillPlans = plans; await SyncAsync(session); }
        }
        await session.SendGameAsync(SkillLoad, new SkillLoadResp { Result = code, IsManual = req.IsManual });
    }

    /// <summary>31156 sender was not recovered. (inferred) Clear this plan's paid allocations without a fee.</summary>
    public async Task ResetAsync(GameSession session, SkillResetReq req)
    {
        bool changed = false;
        {
            using var held = await CharacterService.Lock(session);
            var plans = Build(session);
            int code = req.Plan is < 0 or >= PlanCount || !plans.SkillAssignPlans[req.Plan].IsUnlock
                || InCombat(session) ? ErrorCodes.Parameter : 0;
            if (code == 0)
            {
                var selected = plans.SkillAssignPlans[req.Plan];
                foreach (var (group, assigned) in selected.SkillAssigns)
                    assigned.SkillLevel = IsNormalGroup(session.Progress!, group) ? 1 : Initial(session.Progress!, group);
                var progress = CharacterService.GenreCloneProgress(session.Progress!);
                int oldIndex = plans.CurrentPlanIndex;
                code = StageSkillPlan(session, plans, progress, req.Plan);
                plans.CurrentPlanIndex = oldIndex;
                if (code == 0)
                {
                    PruneLoads(selected, progress);
                    if (req.Plan == oldIndex)
                    {
                        await CommitAsync(session, plans, progress, "cd13c/skill-reset");
                        changed = true;
                    }
                    else await CommitAsync(session, plans, kind: "cd13c/skill-reset");
                    session.SkillPlans = plans;
                    await SyncAsync(session);
                }
            }
            await session.SendGameAsync(SkillReset, new SkillResetResp { Result = code, Plan = req.Plan });
        }
        if (changed) await OnActivePlanChangedAsync(session);
    }

    public async Task SwitchGenreAsync(GameSession session, SkillSwitchGenreReq req)
    {
        using var held = await CharacterService.Lock(session);
        int code = req.Genre < 0 ? ErrorCodes.Parameter : 0; // (inferred) exact genre enumeration is not exported.
        if (code == 0)
        {
            var plans = Build(session);
            plans.SkillAssignPlans[plans.CurrentPlanIndex].SkillGenre = req.Genre;
            await CommitAsync(session, plans, kind: "cd13c/skill-genre");
            await SyncAsync(session);
        }
        await session.SendGameAsync(SwitchGenre, new SkillSwitchGenreResp { Result = code });
    }

    public async Task QueueUpdateAsync(GameSession session, SkillQueueUpdateReq req)
    {
        using var held = await CharacterService.Lock(session);
        var plans = Build(session);
        int op = (int)req.OpType;
        int group = req.Info?.SkillId ?? 0;
        int code = req.Plan is < 0 or >= PlanCount || !plans.SkillAssignPlans[req.Plan].IsUnlock
            || req.Plan != plans.CurrentPlanIndex || InCombat(session)
            || req.QueueIndex < 0 || req.QueueIndex >= Parameter("QueueNum", 12) || op is < 1 or > 3
                ? ErrorCodes.Parameter : 0;
        if (code == 0)
        {
            var selected = plans.SkillAssignPlans[req.Plan];
            var old = selected.SkillQueue.GetValueOrDefault(req.QueueIndex);
            if (op != 2)
            {
                var row = _data.Row("SkillConfig", RealId(group, 1));
                int learned = Level(session.Progress!, group);
                if (group <= 0 || learned <= 0 || row is not { }
                    || !(QueueEligibility?.Invoke(session, group) ?? GameData.Int(row.Value, "_iQueue") == 1)
                    || req.Info!.SkillLevel < 1 || req.Info.SkillLevel > learned)
                    code = ErrorCodes.SkillNotLearned;
            }
            if (code == 0 && op == 2 && (group <= 0 || old is not { SkillId: > 0 } || old.SkillId != group))
                code = ErrorCodes.Parameter;
            if (code == 0 && op == 1 && old is { SkillId: > 0 } && old.SkillId != group)
                code = ErrorCodes.Parameter;
            if (code == 0 && op == 3 && old is not { SkillId: > 0 }) code = ErrorCodes.Parameter;
            if (code == 0)
            {
                selected.SkillQueue[req.QueueIndex] = op == 2 ? new SkillData() : req.Info!.Clone();
                if (!selected.SkillQueue.ContainsKey(0)) selected.SkillQueue[0] = new SkillData();
                await CommitAsync(session, plans, kind: "cd13c/skill-queue");
                await SyncAsync(session);
            }
        }
        await session.SendGameAsync(QueueUpdate, new SkillQueueUpdateResp { Result = code, Plan = req.Plan });
    }
}
