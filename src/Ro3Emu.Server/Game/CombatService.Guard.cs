using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Protection skills (hiding and detection are handled by CombatService.Stealth and StealthEffects):
/// <list type="bullet">
/// <item>Damage sharing: an ally under 【Devotion】 (group 11504, "The Crusader takes @{1}% of the damage you receive",
/// _kLogicData [12..20]) or 【King's Grace】 (11615, [50]) passes that share of each hit to the buff's source; Devotion
/// ends when the two are more than its _kDescData[3] (10) m apart, and "Damage Sharing does not affect allied targets
/// under 【Devotion】". The share lands on the Crusader as its own hit next tick (inferred), never shared again.</item>
/// <item>King's Grace: only the caster carries its two auras (1161401 allies, 1162001 enemies; BuffService.Halo), the
/// cast removes the caster's control effects (category 25), and "taking DMG restores HP equal to ${4}% of the DMG
/// taken" while it lasts. Enemies under its orbit (group 11626, _kLogicData [slow row]) gain a 【Slow】 pile every ${9} s;
/// at ${10} piles the slow becomes Stun 901601 for ${6} s.</item>
/// <item>Melt Down 16203: while its buff lasts, a monster hit has its ${4}% chance to put _kLogicData[2] (1620510,
/// "Reduces Physical ATK and DEF by 5%") on the monster for ${6} s: the monster hits for 5% less and, monsters having no
/// DEF in the formula, takes 5% more (inferred). A hit on an enemy player has its ${2}% chance to put 【Weapon Damage】
/// 1620401 on them for ${3} s: their main- and off-hand weapons add no base stats, affixes or Stunts meanwhile
/// (CharacterService.WeaponDamaged). Refine and enhance levels of the grids still count (inferred).</item>
/// </list>
/// </summary>
public sealed partial class CombatService
{
    private const int DevotionGroup = 11504, DevotionSkillGroup = 11413, KingsGraceSkillGroup = 11612,
        KingsGraceCarrierGroup = 11614, KingsGraceLinkGroup = 11615, KingsGraceOrbitGroup = 11626,
        MeltDownGroup = 16203, MeltDownDebuffGroup = 16205, StunBuff = 901601, ControlCategory = 25;

    private void WireGuardSkills()
    {
        DealtAsync.Add(MeltDownAsync);
        MonsterDamageAdjusters.Add(MeltDownDamage);
        PvpDealtAsync.Add(MeltDownPlayerAsync);
        _buffs.Changed.Add(SyncWeaponDamageAsync);
    }

    private ActiveBuff? LiveGroup(GameSession p, int group) =>
        p.Buffs.FirstOrDefault(b => b.ExpireMs > Now && !b.IsPassive && _data.Row("BuffConfig", b.ConfigId) is { } row
            && GameData.Int(row, "_iBuffID") == group);

    private double[] SkillDescAt(int group, int level) =>
        _data.Row("SkillConfig", _skills.RealId(group, Math.Max(1, level))) is { } row ? Numbers(row, "_kDescData") : [];

    private int BuffLevel(ActiveBuff buff) =>
        _data.Row("BuffConfig", buff.ConfigId) is { } row ? (int)Math.Max(1, GameData.Int(row, "_iLevel")) : 1;

    // ------------------------------------------------------------------ damage sharing

    /// <summary>Inside the target's damage commit: move Devotion's or King's Grace's share to the guardian.</summary>
    internal async Task<long> ShareDamageAsync(GameSession target, long damage)
    {
        if (damage <= 0 || target.Role == null) return damage;
        var link = LiveGroup(target, DevotionGroup);
        bool devotion = link != null;
        link ??= LiveGroup(target, KingsGraceLinkGroup);
        if (link == null || link.SourceId == target.Role.Id
            || _data.Row("BuffConfig", link.ConfigId) is not { } row || Numbers(row, "_kLogicData") is not [var percent, ..] || percent <= 0)
            return damage;
        var guardian = _sessions.All().FirstOrDefault(g => g.Role?.Id == link.SourceId);
        if (guardian == null || guardian.Dead || guardian.SceneLoading || guardian.WorldScene != target.WorldScene) return damage;
        if (devotion && SkillDescAt(DevotionSkillGroup, BuffLevel(link)) is { Length: > 3 } desc && desc[3] > 0
            && Distance(guardian.Position, target.Position) > desc[3])
        {
            await _buffs.RemoveAsync(target, link); // "If the Distance between you exceeds 10 meters ... the effect ends"
            return damage;
        }
        long share = (long)(damage * percent / 100);
        if (share <= 0) return damage;
        long protectedId = target.Role.Id;
        ScheduleRuntime(guardian.WorldScene, TimeSpan.Zero, () => GuardianTakesAsync(guardian, share, protectedId));
        return damage - share;
    }

    private async Task GuardianTakesAsync(GameSession guardian, long amount, long protectedId)
    {
        var role = guardian.Role;
        if (role == null || guardian.Dead) return;
        var committed = await CommitPlayerDamageAsync(guardian, amount, null, () => ReferenceEquals(guardian.Role, role), shared: true);
        if (committed == null) return;
        var (damage, hp, _, shieldDamage) = committed.Value;
        try
        {
            await ToPlayerAndViewersAsync(guardian, SkillHit, new SkillHitInfo
            {
                SrcID = protectedId, SessionId = ++_sessionIds, SrcElement = 1,
                Dsts = { new DamageInfo { DstID = role.Id, Damage = damage, SheildDamage = shieldDamage, DstElement = 1, IsDead = hp == 0 } },
            });
            await _character.SetHpSpAsync(guardian, null, null);
            if (hp == 0 && ReferenceEquals(guardian.Role, role)) await PlayerDiesAsync(guardian);
        }
        finally { if (hp == 0) ReleaseDeathPublication(guardian); }
    }

    /// <summary>King's Grace: the Crusader recovers ${4}% of the damage it takes while carrying the aura.</summary>
    internal long KingsGraceRecovery(GameSession target, long damage)
    {
        if (damage <= 0 || LiveGroup(target, KingsGraceCarrierGroup) is not { } carrier
            || SkillDescAt(KingsGraceSkillGroup, BuffLevel(carrier)) is not { Length: > 3 } desc) return 0;
        return (long)(damage * desc[3] / 100);
    }

    // ------------------------------------------------------------------ periodic checks (every 500 ms)

    private async Task GuardTickAsync(GameSession s)
    {
        if (s.Role == null || s.Progress == null || s.Dead || s.SceneLoading) return;
        await KingsGraceSlowAsync(s);
    }

    private async Task KingsGraceSlowAsync(GameSession s)
    {
        if (LiveGroup(s, KingsGraceCarrierGroup) is not { } carrier) return;
        var desc = SkillDescAt(KingsGraceSkillGroup, BuffLevel(carrier));
        long every = desc.Length > 8 && desc[8] > 0 ? (long)(desc[8] * 1000) : 500;
        if (Now < s.ProcState.GetValueOrDefault(KingsGraceSkillGroup)) return;
        s.ProcState[KingsGraceSkillGroup] = Now + every;
        int stun = desc.Length > 5 && desc[5] > 0 ? (int)(desc[5] * 1000) : 1000;
        foreach (var unit in _world.Map(s.WorldScene).Units.OfType<MonsterUnit>().Where(m => !m.Dead && m.Hp > 0).ToArray())
        {
            var orbit = _buffs.UnitBuffs(unit).FirstOrDefault(b => b.ExpireMs > Now && b.SourceId == s.Role!.Id
                && _data.Row("BuffConfig", b.ConfigId) is { } r && GameData.Int(r, "_iBuffID") == KingsGraceOrbitGroup);
            if (orbit == null || _data.Row("BuffConfig", orbit.ConfigId) is not { } orbitRow
                || Numbers(orbitRow, "_kLogicData") is not [var slowId, ..] || _data.Row("BuffConfig", (long)slowId) is not { } slowRow) continue;
            int max = (int)Math.Max(1, desc.Length > 9 && desc[9] > 0 ? desc[9] : GameData.Int(slowRow, "_iMaxStack"));
            await _buffs.ShowOnUnitAsync(unit, (int)slowId, 3000, s.Role!.Id);
            var slow = _buffs.UnitBuffs(unit).FirstOrDefault(b => b.ConfigId == (int)slowId);
            if (slow == null || slow.PileCount < max) continue;
            await _buffs.RemoveUnitConfigAsync(unit, (int)slowId);
            await _buffs.ShowOnUnitAsync(unit, StunBuff, stun, s.Role.Id);
        }
    }

    // ------------------------------------------------------------------ Melt Down

    private async Task MeltDownAsync(GameSession s, MonsterUnit target, JsonElement row, long amount)
    {
        if (amount <= 0 || target.Dead || target.Hp <= 0 || LiveGroup(s, MeltDownGroup) is not { } melt
            || _data.Row("BuffConfig", melt.ConfigId) is not { } meltRow) return;
        var desc = Numbers(meltRow, "_kDescData");
        var logic = Numbers(meltRow, "_kLogicData");
        if (desc.Length < 6 || logic.Length < 3 || Rolls.Proc(10000) >= desc[3] * 100) return;
        await _buffs.ShowOnUnitAsync(target, (int)logic[2], (int)(desc[5] * 1000), s.Role!.Id);
    }

    private const int WeaponDamageBuff = 1620401;

    private async Task MeltDownPlayerAsync(GameSession s, GameSession target, long amount, bool killed)
    {
        if (amount <= 0 || killed || LiveGroup(s, MeltDownGroup) is not { } melt
            || _data.Row("BuffConfig", melt.ConfigId) is not { } meltRow) return;
        var desc = Numbers(meltRow, "_kDescData");
        if (desc.Length < 3 || Rolls.Proc(10000) >= desc[1] * 100) return;
        await _buffs.AddAsync(target, WeaponDamageBuff, (int)(desc[2] * 1000), s.Role!.Id);
    }

    /// <summary>Weapon Damage carries no attribute changes, so its start and end resend the stat sheet here.</summary>
    private async Task SyncWeaponDamageAsync(GameSession p)
    {
        bool damaged = CharacterService.WeaponDamaged(p, _data);
        if (damaged == p.WeaponDamagedShown) return;
        p.WeaponDamagedShown = damaged;
        await _character.RefreshStatsAsync(p);
    }

    private long MeltDownDamage(MonsterUnit monster, long damage, bool monsterAttacks)
    {
        if (damage <= 0) return damage;
        var melted = _buffs.UnitBuffs(monster).FirstOrDefault(b => b.ExpireMs > Now && _data.Row("BuffConfig", b.ConfigId) is { } r
            && GameData.Int(r, "_iBuffID") == MeltDownDebuffGroup);
        if (melted == null || _data.Row("BuffConfig", melted.ConfigId) is not { } row || Numbers(row, "_kDescData") is not [var percent, ..]) return damage;
        return Math.Max(1, (long)Math.Round(damage * (monsterAttacks ? 100 - percent : 100 + percent) / 100));
    }
}
