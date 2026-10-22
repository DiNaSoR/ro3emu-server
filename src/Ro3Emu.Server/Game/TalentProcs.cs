using System.Globalization;
using System.Runtime.CompilerServices;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Profession talents whose Buff(2) row carries a proc: BuffConfig._kLogicData names the SkillConfig row (or the
/// BuffConfig row) the proc applies, and the talent's SpecialEffectConfig._kDescData holds its chance, cooldown and area
/// in the order its description uses them. Which cast triggers a proc, and which description value is which, is written
/// only in the description, so each rule below quotes it (inferred). The proc row's own _kDamageParam1/2, _kShowDmg and
/// _kScriptLogic carry its damage, and the proc is an extra cast through the ordinary effect path.
/// <see cref="WhileActive"/> lists the talents whose _kLogicData buff applies while another buff lives ("While [Aura
/// Blade] is active ..."); SeasonTalentService applies those. Rules exist for the Knight, Lord Knight, Crusader and
/// Paladin lines. Not covered yet: counters ("after every N casts"), taking-damage triggers, Cross Thrust (its target
/// count is a runtime value), Concussive Shield Bash (its _kLogicData names no SkillConfig row) and Shield Bash Echo (no
/// _kLogicData).
/// </summary>
public sealed class TalentProcs
{
    // SkillConfig._iSkillID of the triggering casts, named by SkillConfig._iName.
    private const int Pierce = 11120, BowlingBash = 11104, HolyCross = 11411, GrandCross = 11412, ShieldBoomerang = 11418,
        GloriaDomini = 11501, RapidSmiting = 11504;
    // BuffConfig._iBuffID of the skills' own buffs, named by BuffConfig._iName.
    private const long AuraBlade = 11201, Parry = 11210, AutoGuard = 11403, Sacrifice = 11501, BattleChant = 11505;
    private const long TwoHandedSword = 3; // ItemConfig._iSubType

    /// <summary>
    /// A proc rule. Indexes are into the talent's _kDescData; null means none (a sure proc, no cooldown).
    /// <paramref name="Radius"/>/<paramref name="Count"/>: the proc hits the target and up to Count - 1 more monsters
    /// within Radius meters of it; <paramref name="Others"/> instead counts the monsters besides the target, and
    /// <paramref name="OthersOnly"/> leaves the target out. <paramref name="RowArea"/> takes the area from the proc row
    /// (_kRange, _iTargetMax); <paramref name="AroundCaster"/> centres the area on the caster. <paramref name="Buff"/>:
    /// _kLogicData is [BuffConfig id, duration ms] on the caster. <paramref name="Active"/>: a buff group that must live.
    /// </summary>
    private sealed record Rule(int[] Groups, bool NormalAttack = false, long Weapon = 0, int? Chance = null, int? Cooldown = null,
        int? Radius = null, int? Count = null, int? Others = null, bool OthersOnly = false, bool RowArea = false, bool Buff = false,
        long Active = 0, bool AroundCaster = false);

    /// <summary>Talent BuffConfig id -> the buff group that must live for its _kLogicData buff to apply.</summary>
    internal static readonly IReadOnlyDictionary<long, long> WhileActive = new Dictionary<long, long>
    {
        [1123301] = AuraBlade, // Aura Blade Enhance: "While [Aura Blade] is active, ... Normal Attack-type skills ..."
        [1123501] = AuraBlade, // Aura Insight - Swiftness: "While [Aura Blade] is active, ASPD increases by ${1}% ..."
        [1123701] = AuraBlade, // Aura Insight - Technique: "While [Aura Blade] is active, [Bash]'s ..."
        [1153701] = AutoGuard, // Auto Guard Enhance: "While [Auto Guard] is active, PATK +${1} and Max HP +${2}."
        [1153901] = AutoGuard, // Strong Shield: "While [Auto Guard] is active, Smite and Rapid Smiting's ..."
        [1155001] = BattleChant, // Battle Cry: "While [Battle Chant] is active, your [Shield Boomerang]'s ..."
        [1154801] = BattleChant, // Fortifying Cry: "While [Battle Chant] is active, your Max HP increases by ${1}."
        [1153501] = Sacrifice, // Martyr's Reckoning Enhance: "While [Sacrifice] is active, [Basic Attack]'s ..."
    };

    /// <summary>Keyed by the talent's BuffConfig id (its SpecialEffectConfig op [2, buff]).</summary>
    private static readonly Dictionary<long, Rule> Rules = new()
    {
        // Rapid Pierce: "When casting [Pierce], there is a ${1}% Chance to Deals DMG to the target an Extra @{1} times,
        // dealing a total of PATK * ${2}% + ${3} ... Cooldown: ${4} seconds."
        [1112101] = new([Pierce], Chance: 0, Cooldown: 3),
        // Piercing Blast: "When casting [Pierce], there is a ${1}% Chance to trigger an Extra Shatter, which Deals DMG ...
        // to the target and ${3} Other enemies within ${2} meters. Cooldown: ${6} seconds."
        [1112201] = new([Pierce], Chance: 0, Cooldown: 5, Radius: 1, Others: 2),
        // Double Counter: "When casting [Bowling Bash], there is a ${1}% Chance to cast it one Extra time."
        [1112301] = new([BowlingBash], Chance: 0, RowArea: true),
        // Sword Attack Speed Increase: "When a Two-Handed Sword is equipped, casting [Basic Attack] has a ${1}% Chance to
        // increase ASPD by ${2}% for ${3} seconds. Cooldown: ${4} seconds."
        [1112401] = new([], NormalAttack: true, Weapon: TwoHandedSword, Chance: 0, Cooldown: 3, Buff: true),
        // Power Slash: "Casting [Basic Attack] triggers Slash, which Deals DMG ... to the target. Internal Cooldown: ${3} seconds."
        [1112601] = new([], NormalAttack: true, Cooldown: 2),
        // Heavy Blow: "Using [Basic Attack] triggers Splash damage, dealing ... to ${3} other enemies within ${2} meters of
        // the target."
        [1112701] = new([], NormalAttack: true, Radius: 1, Others: 2, OthersOnly: true),
        // Cross Blast: "When [Holy Cross] hits an enemy, it additionally casts [Cross Blast] on them, dealing ... to up to
        // ${2} enemies within ${1} meters of the target. Internal Cooldown: ${5} seconds."
        [1151201] = new([HolyCross], Cooldown: 4, Radius: 0, Count: 1),
        // Absolute Judex: "[Grand Cross] and [Holy Cross] additionally cast [Absolute Judex] on the target, dealing ... to
        // the target and a total of ${2} enemies within ${1} meters. Internal Cooldown: ${5} seconds."
        [1151401] = new([GrandCross, HolyCross], Cooldown: 4, Radius: 0, Count: 1),
        // Shield Boomerang Combo: "[Shield Boomerang] deals an Extra @{1} hits of ... to the locked-on target."
        [1151501] = new([ShieldBoomerang]),
        // Triple Slash: "While [Parry] is active, casting [Basic Attack] triggers Triple Slash, which Deals DMG to the
        // target over @{1} hits ... Cooldown: ${3} seconds."
        [1123901] = new([], NormalAttack: true, Active: Parry, Cooldown: 2),
        // Greatsword Sweep Attack: "While [Parry] is active, casting [Basic Attack] triggers Sweep, which Deals DMG ... to
        // up to ${2} enemies within ${1} meters of you. Cooldown: ${5} seconds."
        [1124001] = new([], NormalAttack: true, Active: Parry, Cooldown: 4, Radius: 0, Count: 1, AroundCaster: true),
        // Double Pressure: "When casting [Gloria Domini], Deals DMG ... to the target and up to ${2} enemies within ${1}
        // meters of the target."
        [1151601] = new([GloriaDomini], Radius: 0, Others: 1),
        // Divine Punishment: "[Gloria Domini] Deals DMG to the locked target an Extra @{1} times ..."
        [1151701] = new([GloriaDomini]),
        // Shield Pursuit: "[Rapid Smiting] has a ${1}% Chance to Deals DMG to the target an Extra @{1} times ..."
        [1155301] = new([RapidSmiting], Chance: 0),
    };

    private readonly GameData _data;
    private readonly CombatService _combat;
    private readonly BuffService _buffs;
    private readonly CharacterService _character;
    private readonly ConditionalWeakTable<GameSession, Dictionary<long, long>> _readyAt = new();
    private readonly Dictionary<long, SkillConfig?> _procRows = new();

    // A test can fix each ten-thousandth roll without changing the production probability.
    internal Func<int, int> Roll { get; set; } = bound => Random.Shared.Next(bound);

    public TalentProcs(GameData data, CombatService combat, BuffService buffs, CharacterService character)
    {
        _data = data;
        _combat = combat;
        _buffs = buffs;
        _character = character;
        combat.CastResolved.Add(OnCastResolvedAsync);
        buffs.Changed.Add(OnBuffsChangedAsync);
    }

    /// <summary>A "while a buff is active" talent's attributes follow that buff (SeasonTalentService.Attributes).</summary>
    private async Task OnBuffsChangedAsync(GameSession s)
    {
        if (s.Role == null || !s.TalentProcBuffs.Any(b => WhileActive.ContainsKey(b.Buff))) return;
        if (s.SceneLoading) _character.Refresh(s);
        else await _character.RefreshStatsAsync(s);
    }

    private static double Value(string[] description, int index) =>
        index < description.Length && double.TryParse(description[index], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

    private bool Ready(GameSession s, long buff, long cooldownMs)
    {
        var ready = _readyAt.GetOrCreateValue(s);
        long now = MonsterService.NowMs;
        if (ready.GetValueOrDefault(buff) > now) return false;
        if (cooldownMs > 0) ready[buff] = now + cooldownMs;
        return true;
    }

    private async Task OnCastResolvedAsync(GameSession s, ActiveSkillCast action, IReadOnlyList<MonsterUnit> processed)
    {
        if (s.TalentProcBuffs.Count == 0 || s.Role == null) return;
        var target = processed.FirstOrDefault(m => m.Uid == action.Request.TargetId && m.Hp > 0) ?? processed.FirstOrDefault(m => m.Hp > 0);
        if (target == null) return;
        var cast = new SkillConfig(action.Row);
        foreach (var (buff, description) in s.TalentProcBuffs)
        {
            if (!Rules.TryGetValue(buff, out var rule)) continue;
            if (rule.NormalAttack ? cast.IsNormalAtk != 1 : Array.IndexOf(rule.Groups, (int)cast.SkillId) < 0) continue;
            if (rule.Weapon != 0 && !SkillParameterRules.Wields(s, _data, rule.Weapon)) continue;
            if (rule.Active != 0 && !SkillParameterRules.HasBuffGroup(s, _data, rule.Active)) continue;
            if (rule.Chance is int chance && Roll(10000) >= Value(description, chance) * 100) continue;
            if (!Ready(s, buff, rule.Cooldown is int cooldown ? (long)(Value(description, cooldown) * 1000) : 0)) continue;
            if (BuffConfig.Get(_data, buff)?.LogicData is not [var first, ..] logic) continue;
            if (rule.Buff)
            {
                if (logic.Length >= 2 && BuffConfig.Get(_data, first) != null)
                    await _buffs.AddAsync(s, (int)first, (int)logic[1], s.Role.Id);
                continue;
            }
            if (ProcRow(first) is not { } row || target.Hp <= 0) continue;
            await _combat.CastExtraAsync(s, row.Row, Targets(s, rule, description, row, target));
        }
    }

    /// <summary>_kLogicData's first value: a SkillConfig row, or a skill group (Double Counter's 11127) at its level 1.</summary>
    private SkillConfig? ProcRow(long id)
    {
        if (_procRows.TryGetValue(id, out var cached)) return cached;
        return _procRows[id] = SkillConfig.Get(_data, id) is { } row ? row
            : SkillConfig.All(_data).Where(r => r.SkillId == id && r.Mode == 0).OrderBy(r => r.Level).Cast<SkillConfig?>().FirstOrDefault();
    }

    private List<MonsterUnit> Targets(GameSession s, Rule rule, string[] description, SkillConfig row, MonsterUnit target)
    {
        float radius;
        int others;
        if (rule.RowArea)
        {
            radius = (row.Range is [var range, ..] ? range : 0) / 100f;
            others = (int)Math.Max(0, row.TargetMax - 1);
        }
        else if (rule.Radius is int r)
        {
            radius = (float)Value(description, r);
            others = rule.Others is int o ? (int)Value(description, o) : rule.Count is int c ? (int)Value(description, c) - 1 : 0;
        }
        else return [target];
        var near = _combat.MonstersNear(s, rule.AroundCaster ? s.Position : target.Pos, radius).Where(m => m != target)
            .Take(Math.Max(0, others));
        return rule.OthersOnly ? near.ToList() : [target, .. near];
    }
}
