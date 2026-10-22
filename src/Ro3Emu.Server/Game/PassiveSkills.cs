using System.Globalization;
using System.Runtime.CompilerServices;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The learned passives that act on events rather than stand as attributes (those are CombatService.Passives). Each reads
/// its own BuffConfig row (CombatService.OwnBuffs) and its description values, quoted per passive. The trigger is written
/// only in the description, as for the talents' procs (inferred). Double Attack, Deadly Poison's Venom and Advanced Katar
/// Mastery are in TalentProcs.Passives, next to the talents that change them. Not executed: Union (its stacks count
/// Crusaders on the team), the mount passives (Riding, Dragon Training, Wolf Taming, Wolf's Fang), Sight Sprout's
/// detection, Psychokinetic Astral Body, Supersonic Blow, Element Control's Boss resistance hit and Arcane Mastery's
/// Turn Undead bonus.
/// </summary>
public sealed class PassiveSkills
{
    private const int DoubleHolyLight = 14311, Judex = 14309, EnchantBlade = 11313, EnergyCoat = 12003,
        EmergencyTreatment = 16303, SoulDrain = 12209, PreyOnTheWeak = 15316, PrayerOfGratitude = 14306,
        WoodSpiritsBlessing = 14307;
    private const int AttrMaxSp = 102; // CharacterService stats: 101 Max HP, 102 Max SP, 2000 HP, 2001 SP
    private const int AttrMaxHp = 101, AttrHp = 2000, AttrSp = 2001;
    // "Permanent" for the Prayer of Gratitude shield: a day, renewed on every scene load (inferred).
    private const int PermanentMs = 86_400_000;

    private readonly GameData _data;
    private readonly CombatService _combat;
    private readonly BuffService _buffs;
    private readonly CharacterService _character;
    private readonly GameLoop _loop;
    private readonly ConditionalWeakTable<GameSession, State> _states = [];

    private sealed class State
    {
        public readonly Dictionary<int, long> ReadyAt = [];
        public long LastHitMs;
    }

    // A test can fix each ten-thousandth roll without changing the production probability.
    internal Func<int, int> Roll { get; set; } = bound => Random.Shared.Next(bound);

    public PassiveSkills(GameData data, CombatService combat, BuffService buffs, CharacterService character, SceneService scenes,
        SkillService skills, GameLoop loop)
    {
        _data = data;
        _combat = combat;
        _buffs = buffs;
        _character = character;
        _loop = loop;
        combat.CastResolved.Add(OnCastResolvedAsync);
        combat.TakenAsync.Add(OnTakenAsync);
        combat.Killed.Add(OnKilledAsync);
        combat.DamageAdjusters.Add(PreyOnTheWeakDamage);
        buffs.PassiveLethalSave = WoodSpiritsBlessingAsync;
        // It touches only the loaded session's own buffs, inside the scene load's scope: no hook scope of its own.
        scenes.AddOwnedAfterLoaded(PrayerShieldAsync);
        skills.Learned.Add(PrayerShieldAsync);
        character.JobChanged.Add(PrayerShieldAsync);
    }

    private (SkillConfig Skill, BuffConfig? Own)? Learned(GameSession s, int group) =>
        _combat.LearnedPassive(s, group) is { } skill ? (skill, _combat.OwnBuff(skill)) : null;

    private static double Value(string[] values, int index) =>
        index < values.Length && double.TryParse(values[index], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

    private bool Ready(GameSession s, int group, long cooldownMs)
    {
        var ready = _states.GetOrCreateValue(s).ReadyAt;
        long now = MonsterService.NowMs;
        if (ready.GetValueOrDefault(group) > now) return false;
        ready[group] = now + cooldownMs;
        return true;
    }

    private async Task OnCastResolvedAsync(GameSession s, ActiveSkillCast action, IReadOnlyList<MonsterUnit> processed, SkillHitInfo hit)
    {
        if (s.Role == null || processed.Count == 0) return;
        var cast = new SkillConfig(action.Row);
        var target = processed.FirstOrDefault(m => m.Uid == action.Request.TargetId) ?? processed[0];
        // Double Holy Light: "[Judex] cast by you has a ${1}% Chance to deal one Extra instance of Neutral ranged magic damage
        // ... and a ${1}% Chance to deal one Extra instance of Neutral ranged Physical damage ... (The two Chances are
        // calculated independently.)" _kLogicData [chance, row, chance, row] in basis points.
        if (cast.SkillId == Judex && Learned(s, DoubleHolyLight) is { Own: { LogicData: [var magicChance, var magic, var physicalChance, var physical, ..] } })
            foreach (var (chance, id) in new[] { (magicChance, magic), (physicalChance, physical) })
                if (target.Hp > 0 && Roll(10000) < chance && SkillConfig.Get(_data, id) is { } row)
                    await _combat.CastExtraAsync(s, row.Row, [target]);
        // Enchant Blade: "After dealing damage, increases your DEF by ${2}% for @{1} seconds." _kLogicData [the DEF buff,
        // whose _iTime is those seconds].
        if (hit.Dsts.Any(d => d.Damage > 0) && Learned(s, EnchantBlade) is { Own: { LogicData: [var defense, ..] } })
            await _buffs.AddAsync(s, (int)defense, 0, s.Role.Id);
    }

    private async Task OnTakenAsync(MonsterUnit attacker, GameSession s, long damage)
    {
        if (damage <= 0 || s.Role == null || s.Dead) return;
        _states.GetOrCreateValue(s).LastHitMs = MonsterService.NowMs;
        // Energy Coat: "When you take damage, gain [Energy Coat] for ${1} sec., increasing your damage reduction by ${2}%.
        // Cooldown: ${3} sec." _kLogicData [the reduction buff, ms, piles].
        if (Learned(s, EnergyCoat) is { Skill: var coat, Own: { LogicData: [var buff, var ms, ..] } }
            && Ready(s, EnergyCoat, (long)(Value(coat.DescData, 2) * 1000)))
            await _buffs.AddAsync(s, (int)buff, (int)ms, s.Role.Id);
        // Emergency Treatment: "When you take damage while your HP is below ${1}%, grants a shield equal to ${2}% of your HP
        // and restores ${3}% of your Life. This effect can trigger up to @{1} times every ${4} sec." _kLogicData [the shield
        // buff, the healing buff]; the runtime count is read as one.
        if (Learned(s, EmergencyTreatment) is { Skill: var treatment, Own: { LogicData: [var shield, ..] } })
        {
            var stats = _character.Stats(s);
            var desc = treatment.DescData;
            if (stats[AttrHp] * 100 < stats[AttrMaxHp] * Value(desc, 0) && Ready(s, EmergencyTreatment, (long)(Value(desc, 3) * 1000)))
            {
                await _buffs.AddSupportAsync(s, (int)shield, 0, s.Role.Id, (long)(stats[AttrMaxHp] * Value(desc, 1) / 100));
                await _character.SetHpSpAsync(s, Math.Min(stats[AttrMaxHp], stats[AttrHp] + (long)(stats[AttrMaxHp] * Value(desc, 2) / 100)), null);
            }
        }
        // Prayer of Gratitude: "(Refreshes after taking no damage for ${5} sec.)"
        if (Learned(s, PrayerOfGratitude) is { Skill: var prayer })
        {
            var role = s.Role;
            long quiet = (long)(Value(prayer.DescData, 4) * 1000);
            _loop.ScheduleRuntime(TimeSpan.FromMilliseconds(quiet + 50), async () =>
            {
                if (ReferenceEquals(s.Role, role) && !s.Dead && MonsterService.NowMs - _states.GetOrCreateValue(s).LastHitMs >= quiet)
                    await PrayerShieldAsync(s);
            });
        }
    }

    /// <summary>Prayer of Gratitude: "... plus a shield equal to Max Life * ${4}%." Its own row is "Gain a permanent
    /// damage-absorption shield."; it is renewed on a scene load and after the quiet time, and leaves with the passive
    /// (a career change).</summary>
    private async Task PrayerShieldAsync(GameSession s)
    {
        if (s.Role == null || s.Dead) return;
        if (Learned(s, PrayerOfGratitude) is not { Skill: var prayer, Own: { } own })
        {
            foreach (var stale in s.Buffs.Where(b => BuffConfig.Get(_data, b.ConfigId)?.BuffId == PrayerOfGratitude).ToArray())
                await _buffs.RemoveAsync(s, stale);
            return;
        }
        long amount = (long)(_character.Stats(s)[AttrMaxHp] * Value(prayer.DescData, 3) / 100);
        if (s.Buffs.Any(b => b.ConfigId == own.Id && b.ShieldRemaining >= amount && b.ExpireMs > MonsterService.NowMs)) return;
        await _buffs.AddSupportAsync(s, (int)own.Id, PermanentMs, s.Role.Id, amount);
    }

    // Soul Drain: "Each Kill restores ${2}% of Max MP as MP."
    private async Task OnKilledAsync(GameSession s, MonsterUnit monster)
    {
        if (s.Role == null || Learned(s, SoulDrain) is not { Skill: var drain }) return;
        var stats = _character.Stats(s);
        long sp = Math.Min(stats[AttrMaxSp], stats[AttrSp] + (long)Math.Round(stats[AttrMaxSp] * Value(drain.DescData, 1) / 100));
        if (sp > stats[AttrSp]) await _character.SetHpSpAsync(s, null, sp);
    }

    // Prey on the Weak: "Damage dealt to enemies afflicted with status effects is increased by ${1}%." A status effect is
    // any negative buff on the monster (BuffConfig._iType 2) (inferred).
    private long PreyOnTheWeakDamage(GameSession s, MonsterUnit target, System.Text.Json.JsonElement row, DamageHitResult result, long damage) =>
        _buffs.UnitHasDebuff(target) && Learned(s, PreyOnTheWeak) is { Skill: var prey }
            ? (long)Math.Round(damage * (1 + Value(prey.DescData, 0) / 100)) : damage;

    /// <summary>
    /// Wood Spirit's Blessing: "when you take lethal damage, you gain one chance to [Respawn]. When triggered, you recover
    /// ${2}% Life and enter the [Withered] state for ${3} minutes. During this time, [Respawn] cannot trigger again." The
    /// own row's _kLogicData[0] (240000 ms) is the Withered time.
    /// </summary>
    private async Task<bool> WoodSpiritsBlessingAsync(GameSession s)
    {
        if (Learned(s, WoodSpiritsBlessing) is not { Skill: var blessing, Own: { LogicData: [var withered, ..] } }
            || !Ready(s, WoodSpiritsBlessing, withered)) return false;
        var stats = _character.Stats(s);
        await _character.SetHpSpAsync(s, Math.Max(1, (long)(stats[AttrMaxHp] * Value(blessing.DescData, 1) / 100)), null);
        return true;
    }
}
