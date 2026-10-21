using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The Astral Level (client "Patron", protocol "Paragon", function 71; LC_DAL_PatronLevel, UI_Sys_Personal_*): a second
/// level that Base EXP fills once the role is at the level cap, Astral Points (one per level, PatronLevelConfig
/// _iStatusPoint) spent one at a time on the stars of three constellations, and Star Core items socketed in the star-core
/// nodes. Everything the client shows comes from the 5015 DBParagonLevel autosync; the five requests (83101-83105) answer
/// only {result}. Attributes: a counted star gives PatronTalentAttrConfig[_kTalentAttrIds[points]] (rows are totals), a
/// socketed core PatronRuneConfig[item]._kAttrs, counted only in active constellations.
/// </summary>
public sealed class ParagonService
{
    public const uint Sync = 5015, Allocate = 83101, Install = 83102, Remove = 83103, ResetAll = 83104, ResetSingle = 83105;
    public const long Function = 71;
    public const int AttrType = 1;

    // ErrorCodeConfig 5900-5909 (made for this system; the client shows them through the generic error path).
    public const int ErrConfig = 5900, ErrPoints = 5901, ErrSlot = 5902, ErrSlotInactive = 5903, ErrNoRune = 5904,
        ErrRuneMissing = 5905, ErrRuneType = 5906, ErrGroupInactive = 5907, ErrPrerequisite = 5908, ErrMaxPoints = 5909;

    /// <summary>Role blob "paragon". Points: star id -> allocated points; Cores: star-core node id -> Star Core item id.</summary>
    public sealed class State
    {
        public int Lv { get; set; } = 1;
        public long Exp { get; set; }
        public Dictionary<int, int> Points { get; set; } = new();
        public Dictionary<int, int> Cores { get; set; } = new();
    }

    private sealed record Talent(int Id, int Group, int Type, int Max, int[] Children, int[] Attrs, int[] Power);

    private readonly GameData _data;
    private readonly RoleStore _roles;
    private readonly CharacterService _character;
    private readonly FunctionService _functions;
    private Dictionary<int, Talent>? _talents;
    private List<int[]>? _groups;

    public ParagonService(GameData data, RoleStore roles, CharacterService character, FunctionService functions)
    {
        this._data = data;
        this._roles = roles;
        this._character = character;
        this._functions = functions;
        character.ExtraAttributes.Add(Attributes);
        character.CapExpGained = CreditAsync;
        character.ProgressHandlers.Add(OnProgressAsync);
    }

    private static int[] Ints(JsonElement row, string field) =>
        row.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number).Select(e => e.GetInt32()).ToArray() : [];

    private List<int[]> Groups => _groups ??= _data.Table("PatronTalentGroupConfig").Values
        .OrderBy(r => GameData.Int(r, "_iID")).Select(r => Ints(r, "_kTanlentPoints")).ToList();

    private Dictionary<int, Talent> Talents => _talents ??= BuildTalents();

    private Dictionary<int, Talent> BuildTalents()
    {
        var result = new Dictionary<int, Talent>();
        foreach (var row in _data.Table("PatronTalentConfig").Values)
        {
            int id = (int)GameData.Int(row, "_iID");
            int group = Groups.FindIndex(g => g.Contains(id)) + 1;
            result[id] = new Talent(id, group, (int)GameData.Int(row, "_iType"), (int)GameData.Int(row, "_iMaxLevel"),
                Ints(row, "_iPostTalentID"), Ints(row, "_kTalentAttrIds"), Ints(row, "_iSeasonPower"));
        }
        return result;
    }

    private IEnumerable<Talent> Parents(int id) => Talents.Values.Where(t => t.Children.Contains(id));

    private int MaxLv => _data.Table("PatronLevelConfig").Values.Select(r => (int)GameData.Int(r, "_iPatronLv")).DefaultIfEmpty(1).Max();

    private long Need(int lv) => _data.Row("PatronLevelConfig", lv) is { } row ? GameData.Int(row, "_iPatronExp") : 0;

    /// <summary>Astral Points earned: every reached row's _iStatusPoint (inferred: a level's point comes on reaching it).</summary>
    private long Earned(int lv) => _data.Table("PatronLevelConfig").Values
        .Where(r => GameData.Int(r, "_iPatronLv") <= lv).Sum(r => GameData.Int(r, "_iStatusPoint"));

    private static long Spent(State st) => st.Points.Values.Sum(v => (long)v);

    private bool Completed(State st, int group) =>
        group >= 1 && group <= Groups.Count && Groups[group - 1].All(id => Talents.TryGetValue(id, out var t) && st.Points.GetValueOrDefault(id) >= t.Max);

    /// <summary>Constellation 1 is always active; the next one while the previous is active and complete (inferred).</summary>
    private int ActiveGroups(State st)
    {
        int n = Groups.Count > 0 ? 1 : 0;
        while (n < Groups.Count && Completed(st, n)) n++;
        return n;
    }

    private bool Counted(State st, Talent t) => t.Group >= 1 && t.Group <= ActiveGroups(st);

    public static State Parse(byte[]? blob) => blob is { Length: > 0 } ? JsonSerializer.Deserialize<State>(blob) ?? new State() : new State();

    public void Load(GameSession s) => s.Paragon = Parse(_roles.LoadParagon(s.Role!.Id));

    public bool Open(GameSession s) => s.Progress != null && _functions.IsOpen(s, Function);

    public Task LoginAsync(GameSession s) => Open(s) ? SyncAsync(s) : Task.CompletedTask;

    /// <summary>The function opens at the level cap: send the first 5015 then (FunctionService progress hook).</summary>
    public Task OnProgressAsync(GameSession s) => Open(s) && !s.Synced.ContainsKey(Sync) ? SyncAsync(s) : Task.CompletedTask;

    private Task SyncAsync(GameSession s) => CharacterService.SyncAsync(s, Sync, Snapshot(s));

    /// <summary>PatronExpAddConfig against the world Paragon level (PatronWordLvConfig by open day): the client shows
    /// extraAdd / 10000 as a percent, so 100000 is +10% (inferred unit 1/1,000,000 of the EXP).</summary>
    private double ExtraAdd(int lv)
    {
        int day = _character.ServerDay?.Invoke() ?? 1;
        var world = _data.Table("PatronWordLvConfig").Values.OrderBy(r => GameData.Int(r, "_iDay"))
            .Select(r => (Day: GameData.Int(r, "_iDay"), Lv: GameData.Int(r, "_iServerPatronLv"))).ToList();
        if (world.Count == 0) return 0;
        long worldLv = world.LastOrDefault(w => w.Day <= day) is { Lv: > 0 } hit ? hit.Lv : world[^1].Lv;
        long gap = lv - worldLv;
        return _data.Table("PatronExpAddConfig").Values.Where(r => gap <= GameData.Int(r, "_iBaseLvGap"))
            .Select(r => (double)GameData.Int(r, "_iPursueExpAdditionPercent")).DefaultIfEmpty(0).Max();
    }

    public DBParagonLevel Snapshot(GameSession s)
    {
        var st = s.Paragon ??= new State();
        var info = new DBParagonLevel
        {
            Lv = st.Lv, Exp = st.Exp, LeftPoints = Math.Max(0, Earned(st.Lv) - Spent(st)),
            LvPower = (int)Power(s), ExtraAdd = ExtraAdd(st.Lv),
        };
        // Every star needs an entry (the level page indexes runegroup[id].points unguarded), and `rune` is always set
        // so that a removal reaches the client as itemEntry 0 (inferred: an increment cannot delete a message field).
        foreach (var t in Talents.Values.OrderBy(t => t.Id))
            info.Runegroup[t.Id] = new ParagonLevelRune
            {
                Points = st.Points.GetValueOrDefault(t.Id),
                Rune = st.Cores.TryGetValue(t.Id, out int core) ? _character.ItemInfoOf(new InvItem { Guid = t.Id, Entry = core, Stack = 1 }) : new ItemBaseInfo(),
            };
        // OverviewWidget walks ipairs(activeGroup): the contiguous run 1..n.
        for (int g = 1; g <= ActiveGroups(st); g++) info.ActiveGroup[g] = g;
        return info;
    }

    /// <summary>lvPower: the counted stars' _iSeasonPower[points] plus the socketed cores' _iSeasonPower (inferred).</summary>
    public long Power(GameSession s)
    {
        if (s.Paragon is not { } st) return 0;
        long total = 0;
        foreach (var (id, points) in st.Points)
            if (points > 0 && Talents.TryGetValue(id, out var t) && Counted(st, t) && t.Power.Length > 0)
                total += t.Power[Math.Min(points, t.Power.Length) - 1];
        foreach (var (id, core) in st.Cores)
            if (Talents.TryGetValue(id, out var t) && Counted(st, t) && _data.Row("PatronRuneConfig", core) is { } row)
                total += GameData.Int(row, "_iSeasonPower");
        return total;
    }

    public IEnumerable<(long Id, double Value)> Attributes(GameSession s)
    {
        if (s.Paragon is not { } st || !Open(s)) yield break;
        foreach (var (id, points) in st.Points)
        {
            if (points <= 0 || !Talents.TryGetValue(id, out var t) || !Counted(st, t) || t.Type != AttrType || t.Attrs.Length == 0) continue;
            if (_data.Row("PatronTalentAttrConfig", t.Attrs[Math.Min(points, t.Attrs.Length) - 1]) is { } row)
                foreach (var pair in GameData.Pairs(row, "_kAttrs")) yield return pair;
        }
        foreach (var (id, core) in st.Cores)
            if (Talents.TryGetValue(id, out var t) && Counted(st, t) && _data.Row("PatronRuneConfig", core) is { } row)
                foreach (var pair in GameData.Pairs(row, "_kAttrs")) yield return pair;
    }

    private State Copy(State st) => new()
    {
        Lv = st.Lv, Exp = st.Exp, Points = new Dictionary<int, int>(st.Points), Cores = new Dictionary<int, int>(st.Cores),
    };

    private void Persist(RoleItemPlanBuilder tx, GameSession s, State st) =>
        tx.RoleBlob(s.Role!.Id, "paragon", JsonSerializer.SerializeToUtf8Bytes(st));

    private async Task SaveAsync(GameSession s, State next, string label)
    {
        var tx = _roles.NewTransaction();
        Persist(tx, s, next);
        await _roles.CommitAsync(tx, label, _ => s.Paragon = next);
    }

    /// <summary>
    /// Base EXP past the level cap fills the Paragon level, with the world-level catch-up bonus. Each reached level adds its
    /// Astral Point; the last level keeps no EXP. The client's tips name quests and kills as the source (inferred: every
    /// Base EXP source). Saved in its own commit after the EXP commit (inferred).
    /// </summary>
    private async Task CreditAsync(GameSession s)
    {
        if (!Open(s) || s.Paragon is not { } st || s.Progress is not { CapExp: > 0 } p) return;
        var next = Copy(st);
        long gained = (long)Math.Min(long.MaxValue / 2, p.CapExp * (1 + ExtraAdd(st.Lv) / 1_000_000));
        int max = MaxLv;
        next.Exp = st.Lv >= max ? 0 : checked(next.Exp + gained);
        for (long need; next.Lv < max && (need = Need(next.Lv)) > 0 && next.Exp >= need;)
        {
            next.Exp -= need;
            next.Lv++;
        }
        if (next.Lv >= max) next.Exp = 0;
        if (next.Lv == st.Lv && next.Exp == st.Exp) return;
        await SaveAsync(s, next, "paragon/exp");
        await SyncAsync(s);
    }

    /// <summary>GM: set the Paragon level (EXP 0); allocations stay.</summary>
    public async Task SetLevelAsync(GameSession s, int lv)
    {
        using var _ = await CharacterService.Lock(s);
        var next = Copy(s.Paragon ?? new State());
        next.Lv = Math.Clamp(lv, 1, MaxLv);
        next.Exp = 0;
        await SaveAsync(s, next, "paragon/gm-level");
        await SyncAsync(s);
    }

    private int Check(GameSession s, int id, out Talent talent, out State st)
    {
        st = s.Paragon ??= new State();
        talent = null!;
        if (!Open(s)) return ErrorCodes.FeatureUnavailable;
        if (!Talents.TryGetValue(id, out var t)) return ErrSlot;
        talent = t;
        return t.Group >= 1 && t.Group <= ActiveGroups(st) ? 0 : ErrGroupInactive;
    }

    /// <summary>83101: points into a star of an active constellation whose parent stars are all maxed.</summary>
    public async Task AllocateAsync(GameSession s, ParagonLevelAllocatePointReq req)
    {
        int result;
        using (await CharacterService.Lock(s))
        {
            result = Check(s, req.Slot, out var t, out var st);
            int have = result == 0 ? st.Points.GetValueOrDefault(t.Id) : 0;
            if (result != 0) { }
            else if (Parents(t.Id).Any(parent => st.Points.GetValueOrDefault(parent.Id) < parent.Max)) result = ErrPrerequisite;
            else if (req.Points < 1 || have + req.Points > t.Max) result = ErrMaxPoints;
            else if (Earned(st.Lv) - Spent(st) < req.Points) result = ErrPoints;
            else
            {
                var next = Copy(st);
                next.Points[t.Id] = have + req.Points;
                await SaveAsync(s, next, "paragon/allocate");
                await ChangedAsync(s);
            }
        }
        await s.SendGameAsync(Allocate, new ParagonLevelAllocatePointResp { Result = result });
    }

    /// <summary>83102: socket a bag Star Core whose _iSubType is the node's _iType; a core already there goes back.</summary>
    public async Task InstallAsync(GameSession s, ParagonLevelRuneInstallReq req)
    {
        int result;
        using (await CharacterService.Lock(s))
        {
            result = Check(s, req.Slot, out var t, out var st);
            var item = s.Items.FirstOrDefault(i => i.Guid == req.RuneGuid && i.Grid == 0 && i.Stack > 0
                && !EmblemService.IsReserved(s, i) && !TotemService.IsReserved(s, i));
            if (result != 0) { }
            else if (t.Type == AttrType) result = ErrSlot;
            else if (st.Points.GetValueOrDefault(t.Id) <= 0) result = ErrSlotInactive;
            else if (item == null || _data.Row("PatronRuneConfig", item.Entry) == null) result = ErrRuneMissing;
            else if (_data.Row("ItemConfig", item.Entry) is not { } config || GameData.Int(config, "_iSubType") != t.Type) result = ErrRuneType;
            else
            {
                var next = Copy(st);
                next.Cores[t.Id] = item.Entry;
                var back = st.Cores.TryGetValue(t.Id, out int old) ? new List<(int, int)> { (old, 1) } : [];
                var paid = await _character.ExchangeLockedAsync(s, [], back, new Dictionary<long, int> { [item.Guid] = 1 },
                    popup: false, persist: tx => Persist(tx, s, next), committed: () => s.Paragon = next);
                if (paid == null) result = ErrRuneMissing;
                else await ChangedAsync(s);
            }
        }
        await s.SendGameAsync(Install, new ParagonLevelRuneInstallResp { Result = result });
    }

    /// <summary>83103: the socketed core goes back to the bag.</summary>
    public async Task RemoveAsync(GameSession s, ParagonLevelRuneRemoveReq req)
    {
        int result;
        using (await CharacterService.Lock(s))
        {
            result = Check(s, req.SlotId, out var t, out var st);
            if (result == ErrGroupInactive) result = 0; // a core in a constellation that dropped out can still come out
            if (result != 0) { }
            else if (!st.Cores.ContainsKey(t.Id)) result = ErrNoRune;
            else result = await ResetAsync(s, st, [], [t.Id], "paragon/remove") ? 0 : ErrConfig;
        }
        await s.SendGameAsync(Remove, new ParagonLevelRuneRemoveResp { Result = result });
    }

    /// <summary>83105: this star and every star after it (through _iPostTalentID) back to 0, their points refunded and
    /// their cores returned. Free, as the client's refund popup shows.</summary>
    public async Task ResetSingleAsync(GameSession s, ParagonLevelResetSingleReq req)
    {
        int result;
        using (await CharacterService.Lock(s))
        {
            result = Check(s, req.SlotId, out var t, out var st);
            if (result == ErrGroupInactive) result = 0;
            if (result == 0)
            {
                var stars = new HashSet<int>();
                var queue = new Queue<int>([t.Id]);
                while (queue.TryDequeue(out int id))
                    if (stars.Add(id) && Talents.TryGetValue(id, out var star)) foreach (int child in star.Children) queue.Enqueue(child);
                if (!stars.Any(id => st.Points.GetValueOrDefault(id) > 0 || st.Cores.ContainsKey(id))) result = ErrSlotInactive;
                else result = await ResetAsync(s, st, stars, stars, "paragon/reset-single") ? 0 : ErrConfig;
            }
        }
        await s.SendGameAsync(ResetSingle, new ParagonLevelResetSingleResp { Result = result });
    }

    /// <summary>83104: every star back to 0 and every core returned.</summary>
    public async Task ResetAllAsync(GameSession s)
    {
        int result;
        using (await CharacterService.Lock(s))
        {
            var st = s.Paragon ??= new State();
            if (!Open(s)) result = ErrorCodes.FeatureUnavailable;
            else if (st.Points.Values.All(v => v <= 0) && st.Cores.Count == 0) result = ErrPoints;
            else result = await ResetAsync(s, st, st.Points.Keys.ToList(), st.Cores.Keys.ToList(), "paragon/reset-all") ? 0 : ErrConfig;
        }
        await s.SendGameAsync(ResetAll, new ParagonLevelResetAllResp { Result = result });
    }

    private async Task<bool> ResetAsync(GameSession s, State st, IEnumerable<int> stars, IEnumerable<int> cores, string label)
    {
        var next = Copy(st);
        foreach (int id in stars) next.Points.Remove(id);
        var back = new List<(int, int)>();
        foreach (int id in cores)
            if (next.Cores.Remove(id, out int core)) back.Add((core, 1));
        if (back.Count == 0) await SaveAsync(s, next, label);
        else if (await _character.ExchangeLockedAsync(s, [], back, popup: false,
            persist: tx => Persist(tx, s, next), committed: () => s.Paragon = next) == null) return false;
        await ChangedAsync(s);
        return true;
    }

    private async Task ChangedAsync(GameSession s)
    {
        await SyncAsync(s);
        await _character.RefreshStatsAsync(s);
    }
}
