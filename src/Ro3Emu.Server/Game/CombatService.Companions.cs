using System.Text.Json;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Class companions: the Hunter's falcon, the Ranger's Demonic Wolf and the Mechanic's Madogear (Special Class Items,
/// CareerSpecialService). A leased falcon or wolf follows as a pendant (PendantConfig _iType 1 falcon, 2 cart, 6 wolf);
/// the wolf and the Madogear are also battle mounts (MountService.RidingBattle).
/// <list type="bullet">
/// <item>Skill conditions: SkillConfig._iSkillCondition names an LC_ConditionList row the client checks before a cast
/// (LC_InteractCondition): [2001, subtype] an equipped item of that ItemConfig _iSubType (2002 none), [2003, id] riding
/// that battle mount (2004 not), [2006, type] a pendant of that PendantConfig _iType (2007 none). Blitz Beat needs the
/// falcon and no wolf ride, Warg Bite the wolf ride, Dragon Breath the dragon, Arm Cannon the Madogear, Pierce a spear.
/// m_eType 1 is all of its clauses, 2 any; m_bInverse negates a clause.</item>
/// <item>Companion buffs: a leased pendant's CartRemouldTable _iPendantBuffID (Falconry 1310101, Warg Accessory 1399901)
/// lasts while it is leased, and a ridden battle mount's _kBindBuff (the wolf's 【Wolf Taming】 1331001: Movement Speed
/// +10% and Warg Strike's mounted form) while it is ridden. Wolf's Fang 13311's BuffConfig row names the buff it gives
/// "while mounted on the Demonic Wolf" (_kLogicData [1331210, 13312]: Physical ATK +10% and +10% on the wolf skills).
/// They are re-applied after a scene change clears them (_iSceneClear) (inferred lifetimes).</item>
/// <item>Wolf Taming: "When the Demonic Wolf is not mounted and follows the Ranger, it howls every 30 sec, increasing the
/// Ranger's Physical ATK by 5% for 10 sec" (_kDescData [10, 30, 5, 10]); the howl is Warg Howl 13321's buff 1332501
/// (inferred: on the first damage dealt once the 30 sec have passed, so it howls while fighting).</item>
/// <item>Warg Bite 13325 "resets the Cooldown of 【Warg Hunt】"; Swift Warg 13326: "Casting 【Warg Charge】 or 【Warg Hunt】
/// resets this skill's Cooldown" (Warg Charge is Warg Strike's mounted form, 13312 mode 1).</item>
/// <item>King of the Wargs 13322's 【King of the Pack】 (buff group 13326, _kLogicData [charge companion, bite companion]):
/// "casting 【Warg Charge】 causes 4 wolf followers to charge alongside you, dealing 4 Extra hits ... totaling Physical
/// ATK*320%" (the companion row 1332705 deals 80% per follower) and "Casting 【Warg Bite】 causes 3 wolf followers to bite
/// the target ... totaling Physical ATK*240%" (row 1332805 deals the 240% at once) (inferred split).</item>
/// </list>
/// </summary>
public sealed partial class CombatService
{
    private const int WolfTamingGroup = 13310, WolfsFangGroup = 13311, WargStrikeSkillGroup = 13312, WargHowlGroup = 13321,
        KingOfTheWargsGroup = 13322, WargHuntGroup = 13323, WargBiteGroup = 13325, SwiftWargGroup = 13326,
        KingOfThePackBuffGroup = 13326, WolfPendantType = 6, WolfMountType = 4;

    // ------------------------------------------------------------------ skill conditions

    /// <summary>The row's _iSkillCondition holds (no condition, or an unknown clause type, holds).</summary>
    internal bool SkillConditionMet(GameSession s, JsonElement row) =>
        GameData.Int(row, "_iSkillCondition") is not (var id and > 0)
        || _data.Row("LC_ConditionList", id) is not { } condition || ConditionMet(s, condition);

    private bool ConditionMet(GameSession s, JsonElement node)
    {
        int type = (int)GameData.Int(node, "m_eType");
        bool met;
        if (type is 1 or 2)
        {
            var clauses = node.TryGetProperty("m_kSubConditionList", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(clause => ConditionMet(s, clause)).ToArray() : [];
            met = clauses.Length == 0 || (type == 1 ? clauses.All(c => c) : clauses.Any(c => c));
        }
        else met = ClauseMet(s, Numbers(node, "m_kData"));
        return node.TryGetProperty("m_bInverse", out var inverse) && inverse.ValueKind == JsonValueKind.True ? !met : met;
    }

    private bool ClauseMet(GameSession s, double[] clause) => clause switch
    {
        [2001, var subType, ..] => EquipsSubType(s, (int)subType),
        [2002, var subType, ..] => !EquipsSubType(s, (int)subType),
        [2003, var mount, ..] => MountService.RidingBattle(s) == (long)mount,
        [2004, var mount, ..] => MountService.RidingBattle(s) != (long)mount,
        [2006, var pendant, ..] => HasPendantType(s, (int)pendant),
        [2007, var pendant, ..] => !HasPendantType(s, (int)pendant),
        // CheckSceneFlyable (LC_ConditionDefine): the map allows flight (MapConfig _iFly), e.g. Take Flight's 86000558.
        [2009, var flag, ..] => (_data.Row("MapConfig", s.SceneEntry) is { } map && GameData.Int(map, "_iFly") == 1) == (flag != 0),
        _ => true,
    };

    /// <summary>CheckEquipStatue: any worn ItemConfig _iType 5 (equipment) item of that subtype.</summary>
    private bool EquipsSubType(GameSession s, int subType) => s.Items.Any(i => i.Grid > 0
        && _data.Row("ItemConfig", i.Entry) is { } item && GameData.Int(item, "_iType") == 5 && GameData.Int(item, "_iSubType") == subType);

    /// <summary>LC_Pendant_Manager.HasPendantByConfigId: a shown pendant whose PendantConfig _iType matches.</summary>
    private bool HasPendantType(GameSession s, int type) =>
        s.CareerPendants.Any(p => _data.Row("PendantConfig", p) is { } row && GameData.Int(row, "_iType") == type);

    private bool RidesWolf(GameSession s) => RiddenMountType(s) == WolfMountType;

    // ------------------------------------------------------------------ companion buffs

    /// <summary>After a lease, a ride, a learned level, a job change or a scene load: grant or end the companion buffs, and
    /// resend the normal attack when the ridden battle mount changed.</summary>
    /// <summary>WorldCoreService.FlightChanged: a flying mount's ground/air buff and its wheel slot follow the flight.</summary>
    public async Task FlightChangedAsync(GameSession s)
    {
        await SyncCompanionsAsync(s);
        await _skills.RefreshMountSkillAsync(s);
    }

    public async Task SyncCompanionsAsync(GameSession s)
    {
        if (s.Role == null || s.Progress == null) return;
        var want = new HashSet<int>();
        var info = s.CareerSpecial;
        foreach (long lease in new[] { info?.PendantSlot?.CartRemouldCfg ?? 0, info?.MountsSlot?.CartRemouldCfg ?? 0 })
            if (lease != 0 && _data.Row("CartRemouldTable", lease) is { } cart && GameData.Int(cart, "_iPendantBuffID") is var buff and > 0
                && s.CareerPendants.Contains((int)GameData.Int(cart, "_iPendantId")))
                want.Add((int)buff);
        long ride = MountService.RidingBattle(s);
        if (ride != 0 && _data.Row("CartRemouldTable", ride) is { } mount)
            foreach (var bound in Numbers(mount, "_kBindBuff")) if (bound > 0) want.Add((int)bound);
        // A flying System mount: on the ground its MountsConfig _kBindBuff (Starshatter Dragon 3112101), in the air the
        // buff that shares the landing skill's id (3112201, FlyBuffTag 3100000, which WorldCoreService.CanFly needs).
        // The two rows name each other's group in _kLogicData (31121 <-> 31122), so one replaces the other (inferred).
        if (MountService.RidingSystem(s) is var system and > 0 && _data.Row("MountsConfig", system) is { } ridden)
        {
            if (s.WorldState.Flying && MountService.FlySkills(s, _data) is { } fly) want.Add(fly.Down);
            else foreach (var bound in Numbers(ridden, "_kBindBuff")) if (bound > 0) want.Add((int)bound);
        }
        if (RidesWolf(s) && _skills.Level(s.Progress, WolfsFangGroup) is var fang and > 0
            && _data.Row("BuffConfig", _skills.RealId(WolfsFangGroup, fang)) is { } fangRow && Numbers(fangRow, "_kLogicData") is [var fangBuff, ..])
            want.Add((int)fangBuff);
        want.RemoveWhere(id => _data.Row("BuffConfig", id) == null);

        foreach (var ending in s.Buffs.Where(b => s.CompanionBuffs.Contains(b.ConfigId) && !want.Contains(b.ConfigId)
                     && b.ExpireMs == long.MaxValue).ToArray())
            await _buffs.RemoveAsync(s, ending);
        foreach (int id in want)
            if (!s.Buffs.Any(b => b.ConfigId == id && b.ExpireMs == long.MaxValue)) await _buffs.AddLastingAsync(s, id, s.Role.Id);
        s.CompanionBuffs = want;

        if (s.CompanionRide != ride)
        {
            s.CompanionRide = ride;
            await _skills.WeaponChangedAsync(s);
        }
    }

    /// <summary>The following (not ridden) wolf's howl, once per Wolf Taming's interval, when the Ranger deals damage.</summary>
    private async Task WargHowlAsync(GameSession s)
    {
        if (s.Role == null || s.Progress == null || !HasPendantType(s, WolfPendantType) || RidesWolf(s)) return;
        long now = MonsterService.NowMs;
        if (now < s.WargHowlMs || _skills.Level(s.Progress, WolfTamingGroup) is not (var level and > 0)
            || _data.Row("SkillConfig", _skills.RealId(WolfTamingGroup, level)) is not { } taming
            || _data.Row("SkillConfig", _skills.RealId(WargHowlGroup, 1)) is not { } howl
            || Tuples(howl, "_kBuff1").FirstOrDefault() is not { ValueKind: JsonValueKind.Array } grant || grant.GetArrayLength() < 3) return;
        var desc = Numbers(taming, "_kDescData");
        long every = desc.Length > 1 && desc[1] > 0 ? (long)(desc[1] * 1000) : 30000;
        int duration = desc.Length > 3 && desc[3] > 0 ? (int)(desc[3] * 1000) : (int)(GameData.Number(grant[2]) ?? 10000);
        s.WargHowlMs = now + every;
        await _buffs.AddAsync(s, (int)(GameData.Number(grant[0]) ?? 0), duration, s.Role.Id);
    }

    // ------------------------------------------------------------------ wolf skill links

    /// <summary>After a landed wolf skill: the cooldown resets and King of the Pack's extra companions.</summary>
    private async Task WolfAfterCastAsync(GameSession s, ActiveSkillCast action)
    {
        if (action.Proc) return;
        var row = action.Row;
        int group = (int)GameData.Int(row, "_iSkillID");
        bool charge = group == WargStrikeSkillGroup && GameData.Int(row, "_iMode") == 1;
        if (group == WargBiteGroup) await ResetCooldownAsync(s, WargHuntGroup);
        if (charge || group == WargHuntGroup)
        {
            await ResetCooldownAsync(s, SwiftWargGroup);
            ArmSwiftWarg(s);
        }
        if (!charge && group != WargBiteGroup) return;

        long now = MonsterService.NowMs;
        var pack = s.Buffs.Where(b => b.ExpireMs > now && !b.IsPassive).Select(b => _data.Row("BuffConfig", b.ConfigId))
            .FirstOrDefault(r => r is { } c && GameData.Int(c, "_iBuffID") == KingOfThePackBuffGroup);
        if (pack is not { } packRow || Numbers(packRow, "_kLogicData") is not [var chargeId, var biteId, ..]
            || _data.Row("SkillConfig", (long)(charge ? chargeId : biteId)) is not { } companion
            || FindMonster(s.WorldScene, action.Request.TargetId) is not { } target) return;
        int followers = 1;
        if (charge && _data.Row("SkillConfig", _skills.RealId(KingOfTheWargsGroup, (int)Math.Max(1, GameData.Int(packRow, "_iLevel")))) is { } king
            && Numbers(king, "_kDescData") is [_, var count, ..] && count > 0)
            followers = (int)count;
        for (int i = 0; i < followers && !target.Dead && target.Hp > 0; i++) await ProcCastAsync(s, companion, target);
    }
}
