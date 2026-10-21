using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Which SpecialStatusConfig state a buff puts its bearer in, for players and monsters alike, and the monster side of
/// it: forbidden actions (MonsterService reads them before walking, swinging or casting), the movement speed change
/// (attribute 1131) and status immunity.
/// </summary>
public sealed partial class BuffService
{
    /// <summary>
    /// The first _kBuffCategory entry of a control buff names its kind. The player, monster and PvP copies of each
    /// status use the same kind with an offset of 50 and 79 (Stun 9016 [13,...], 9058 [63,...], 9077 [92,...]). The
    /// state is the SpecialStatusConfig row of the same name (眩晕 Stun 18, 冰冻 Frozen 11, 石化 Petrify 12, 睡眠 Sleep 19,
    /// 沉默 Silence 20, 定身 Immobilize 21, 击退 Knockback 4, 击飞 Knock-Up 3, 僵直 Stagger 1). Fear has no state row; it
    /// holds its bearer in place like Immobilize (inferred, classic RO Fear stops movement).
    /// </summary>
    private static readonly IReadOnlyDictionary<int, int> StateByKind = new Dictionary<int, int>
    {
        [13] = 18, [9] = 11, [10] = 12, [12] = 19, [6] = 20, [8] = 21, [7] = 21, [15] = 4, [16] = 3, [14] = 1,
    };

    /// <summary>Exact groups whose name gives the state but whose category does not (kept from the first mapping).</summary>
    private static int StateForGroup(int buffGroup) => buffGroup switch
    {
        9016 => 18,
        9022 => 19,
        9023 => 20,
        12003 => 11,
        9034 => 25,
        _ => 0,
    };

    /// <summary>The control state (SpecialStatusConfig id) a BuffConfig row applies; 0 for none.</summary>
    internal static int StateFor(JsonElement row)
    {
        int byGroup = StateForGroup((int)GameData.Int(row, "_iBuffID"));
        if (byGroup != 0) return byGroup;
        // Only debuffs carry a control kind; a beneficial buff's first category is a tag such as 19 Endure.
        if (GameData.Int(row, "_iType") != 2) return 0;
        int kind = Categories(row).FirstOrDefault();
        foreach (int offset in (int[])[0, 50, 79])
            if (kind - offset > 0 && StateByKind.TryGetValue(kind - offset, out int state)) return state;
        return 0;
    }

    private static int[] Categories(JsonElement row) =>
        row.TryGetProperty("_kBuffCategory", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(v => (int)(GameData.Number(v) ?? 0)).ToArray() : [];

    /// <summary>
    /// Status immunity (inferred from SpecialStatusConfig 23/24's notes: immunity "is implemented through the buff tag"):
    /// a bearer with a category-20 buff (Immunity 9032, Boss CC Immunity 9037, the MVP born buffs 900801/900901) shrugs
    /// off control buffs (categories 25 stun group, 26 silence, 27 immobilize/stagger, 28 blind); a category-21 buff
    /// (Debuff Immunity 9033) shrugs off every debuff (category 37).
    /// </summary>
    internal static bool Blocks(IEnumerable<int> bearerCategories, JsonElement incoming)
    {
        var have = bearerCategories as ISet<int> ?? bearerCategories.ToHashSet();
        if (have.Count == 0) return false;
        var kinds = Categories(incoming);
        return have.Contains(20) && kinds.Any(k => k is >= 25 and <= 28)
            || have.Contains(21) && kinds.Contains(37);
    }

    /// <summary>The categories of a player's live buffs.</summary>
    internal HashSet<int> CategoriesOf(GameSession p) => p.Buffs
        .Where(b => b.ExpireMs > MonsterService.NowMs && _data.Row("BuffConfig", b.ConfigId) is { })
        .SelectMany(b => Categories(_data.Row("BuffConfig", b.ConfigId)!.Value)).ToHashSet();

    /// <summary>The categories of a unit's live buffs plus its NPCConfig born buffs (MVP and boss immunities).</summary>
    internal HashSet<int> CategoriesOf(WorldUnit unit)
    {
        var result = new HashSet<int>();
        if (_unitBuffs.TryGetValue(unit.Uid, out var list))
            foreach (var buff in list.Where(b => b.ExpireMs > MonsterService.NowMs))
                if (_data.Row("BuffConfig", buff.ConfigId) is { } row) result.UnionWith(Categories(row));
        if (unit is MonsterUnit m) result.UnionWith(BornCategories(m.Template.NpcId));
        return result;
    }

    private readonly Dictionary<int, int[]> _bornCategories = new();

    /// <summary>Categories of NPCConfig _kBornEventBuff [buff, ms, ...]. MvpService only sends these rows for MVPs, so
    /// immunity is read from the template for every monster (inferred: the rows name what the unit is born with).</summary>
    private int[] BornCategories(int npcId)
    {
        if (_bornCategories.TryGetValue(npcId, out var cached)) return cached;
        var result = new List<int>();
        if (_data.Row("NPCConfig", npcId) is { } npc && npc.TryGetProperty("_kBornEventBuff", out var born)
            && born.ValueKind == JsonValueKind.Array)
            foreach (var tuple in born.EnumerateArray())
                if (tuple.ValueKind == JsonValueKind.Array && tuple.GetArrayLength() > 0
                    && _data.Row("BuffConfig", (long)(GameData.Number(tuple[0]) ?? 0)) is { } row)
                    result.AddRange(Categories(row));
        return _bornCategories[npcId] = result.Distinct().ToArray();
    }

    /// <summary>
    /// Recompute a monster's control state and buff movement speed after a buff change, and publish the state masks the
    /// way players get theirs (31048 state/forbid/stagger), so the client plays the frozen or stunned pose.
    /// </summary>
    private async Task RefreshUnitControlAsync(WorldUnit unit)
    {
        if (unit is not MonsterUnit m) return;
        long now = MonsterService.NowMs;
        var live = _unitBuffs.TryGetValue(unit.Uid, out var list) ? list.Where(b => b.ExpireMs > now).ToArray() : [];
        ulong status = 0, forbiddenMask = 0, stagger = 0;
        long ordinals = 0;
        foreach (var buff in live.Where(b => b.StateOrdinal is > 0 and < 33))
        {
            status |= 1UL << (buff.StateOrdinal - 1);
            if (_data.Row("SpecialStatusConfig", buff.StateOrdinal) is not { } row) continue;
            if (row.TryGetProperty("_kForbidden", out var actions) && actions.ValueKind == JsonValueKind.Array)
                foreach (var action in actions.EnumerateArray())
                    if (GameData.Number(action) is { } ordinal && ordinal is > 0 and < 64)
                    {
                        ordinals |= 1L << (int)ordinal;
                        forbiddenMask |= ForbiddenMask((int)ordinal);
                    }
            int staggerType = (int)GameData.Int(row, "_iStaggerType");
            if (staggerType is > 0 and < 64) stagger |= 1UL << staggerType;
        }
        // Attribute 1131 "% Movement Speed Change" (Frozen's chill -50%, Sluggish -30%), in basis points.
        int speedBp = (int)Math.Clamp(live.SelectMany(b => b.Changes).Where(c => c.Attr == 1131).Sum(c => c.Value), -9000, 30000);
        bool speedChanged = m.BuffSpeedBp != speedBp;
        bool controlChanged = m.ForbiddenOrdinals != ordinals || m.BuffStatusBits != (long)status || m.StaggerBits != stagger;
        m.BuffSpeedBp = speedBp;
        if (controlChanged)
        {
            m.ForbiddenOrdinals = ordinals;
            m.StatusBits = (m.StatusBits & ~m.BuffStatusBits) | (long)status;
            m.BuffStatusBits = (long)status;
            m.ForbiddenBits = forbiddenMask;
            m.StaggerBits = stagger;
            await _world.ToUnitViewersAsync(m, AoiAttrChange, new AOIAttrChange
            {
                EntityUuid = (ulong)m.Uid,
                StateInfo = new AoiObjStateInfo { Data = Mask((ulong)m.StatusBits) },
                ForbidInfo = new AoiObjForbidInfo { Data = Mask(m.ForbiddenBits) },
                StaggerInfo = new AoiObjStaggerInfo { StaggerMask = m.StaggerBits },
            });
        }
        if (speedChanged || controlChanged)
            foreach (var handler in UnitControlChanged) await handler(m, speedChanged);
    }

    /// <summary>MonsterService stops a unit that can no longer walk and republishes its speed (true when it changed).</summary>
    public List<Func<MonsterUnit, bool, Task>> UnitControlChanged { get; } = new();

    /// <summary>
    /// A hit breaks Frozen, Petrify and Sleep on a monster (classic RO; the RO3 data agrees for the first two: Frozen
    /// 902001 and Petrify 902101 carry _kLogicData [next buff, ms] = the 3-second chill 902601 / Sluggish 902801 that
    /// follows the thaw, inferred). Sleep has no follow-up row and simply ends (inferred).
    /// </summary>
    public async Task BreakOnHitAsync(WorldUnit unit)
    {
        if (!_unitBuffs.TryGetValue(unit.Uid, out var list)) return;
        foreach (var buff in list.Where(b => b.StateOrdinal is 11 or 12 or 19 && b.ExpireMs > MonsterService.NowMs).ToArray())
        {
            await RemoveUnitBuffAsync(unit, buff);
            if (_data.Row("BuffConfig", buff.ConfigId) is not { } row) continue;
            var logic = row.TryGetProperty("_kLogicData", out var l) && l.ValueKind == JsonValueKind.Array
                ? l.EnumerateArray().Select(v => (int)(GameData.Number(v) ?? 0)).ToArray() : [];
            if (logic.Length >= 2 && logic[0] > 0 && logic[1] > 0 && _data.Row("BuffConfig", logic[0]) != null)
                await AddSupportAsync(unit, logic[0], logic[1], buff.SourceId);
        }
    }
}
