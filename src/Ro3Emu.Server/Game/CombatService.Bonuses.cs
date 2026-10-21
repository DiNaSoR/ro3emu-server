using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The secondary stats in the damage and heal formulas. The original formula lists its terms in PBEnumDefine
/// DubugDamageFormulaEnum (Formula_RaceAttr, Formula_EleDmgAttr, Formula_EleMonsterDmgAttr, Formula_BodyAttr,
/// Formula_DmgAttr, Formula_AtkDmgAttr, Formula_MeleeOrRangeAttr, Formula_MagicDmgAttr, Formula_SkillDmgAttr,
/// Formula_CriDmg, Formula_CureAttr ...). Each pair is the attacker's "+X% damage" stat against the defender's matching
/// "-X% damage taken" stat. (inferred) Every pair is one factor <c>1 + deal - taken</c> (basis points, floored at 0)
/// and the factors multiply; the client names the terms but not how they combine.
/// </summary>
public sealed partial class CombatService
{
    /// <summary>Players' race (NPCConfig race 2, Demi-Human: "Damage to Demi-Human Race %" is the PvP race stat) and
    /// size (Medium, inferred: classic RO players are Medium).</summary>
    internal const int PlayerRace = 2, PlayerSize = 2;

    /// <summary>Set by TotemEffects: the part of a stat its own multipliers already apply, so it is not counted twice.</summary>
    public Func<GameSession, int, double>? CoveredBonus { get; set; }

    private static double S(Fighter f, int id) => f.Stats is { } s ? s[id] - (f.Covered?.Invoke(id) ?? 0) : 0;

    private static double Pair(double deal, double taken) => Math.Max(0, 1 + (deal - taken) / 10000);

    /// <summary>
    /// The product of the secondary-stat factors for one hit (attribute ids from AttributeConfig):
    /// race 401-410 against 411-420; element 301-310 against 311-320 by the attack element; element vs monsters
    /// 321-330 by the monster's element and 331-340 against a monster attacker's element; size 501/503/505 and
    /// 502/504/506; boss 601/602 and non-boss 603/604; Damage% 215/216; Physical 211/212 or Magic 213/214; melee or
    /// ranged (skill _iDistanceType 2) Physical 227/228 and 229/230, Magic 1250/1251 and 1252/1253; Skill Damage%
    /// 225/226 for everything but the normal attack (_iIsNormalAtk). The 12xx copies (1215/1216 Damage Increase/Reduction,
    /// 1211-1214 Physical/Magic, 1227-1230 melee/ranged Physical: True Sight, Divine Blessing, Lex Aeterna's 1216 -2000,
    /// Prison's 1212/1214 -500) are their own factors beside the 2xx ones (inferred: the client lists them as separate
    /// attributes; "from the target" is not limited to the buff's source).
    /// </summary>
    internal static double BonusFactor(Fighter a, Fighter b, JsonElement skill, bool magic, int element)
    {
        if (a.Stats == null && b.Stats == null) return 1;
        double f = 1;
        f *= Pair(b.Race is >= 1 and <= 10 ? S(a, 400 + b.Race) : 0, a.Race is >= 1 and <= 10 ? S(b, 410 + a.Race) : 0);
        if (element is >= 1 and <= 10) f *= Pair(S(a, 300 + element), S(b, 310 + element));
        f *= Pair(b.IsMonster && b.Element is >= 1 and <= 10 ? S(a, 320 + b.Element) : 0,
            a.IsMonster && a.Element is >= 1 and <= 10 ? S(b, 330 + a.Element) : 0);
        f *= Pair(b.IsMonster && b.Size is >= 1 and <= 3 ? S(a, 499 + 2 * b.Size) : 0,
            a.IsMonster && a.Size is >= 1 and <= 3 ? S(b, 500 + 2 * a.Size) : 0);
        f *= Pair(b.IsMonster ? S(a, b.Boss ? 601 : 603) : 0, a.IsMonster ? S(b, a.Boss ? 602 : 604) : 0);
        f *= Pair(S(a, 215), S(b, 216)) * Pair(S(a, 1215), S(b, 1216));
        f *= magic ? Pair(S(a, 213), S(b, 214)) * Pair(S(a, 1213), S(b, 1214))
            : Pair(S(a, 211), S(b, 212)) * Pair(S(a, 1211), S(b, 1212));
        bool ranged = GameData.Int(skill, "_iDistanceType") == 2;
        f *= magic
            ? ranged ? Pair(S(a, 1252), S(b, 1253)) : Pair(S(a, 1250), S(b, 1251))
            : ranged ? Pair(S(a, 229), S(b, 230)) * Pair(S(a, 1229), S(b, 1230))
                : Pair(S(a, 227), S(b, 228)) * Pair(S(a, 1227), S(b, 1228));
        if (GameData.Int(skill, "_iIsNormalAtk") == 0) f *= Pair(S(a, 225), S(b, 226));
        if (a.IsMonster && b.MonsterDamageCutBp > 0) f *= Math.Max(0, 1 - b.MonsterDamageCutBp / 10000.0);
        return f;
    }

    /// <summary>A critical hit: FormulaParamCriDmg 15000 plus Crit Damage% 209, minus Crit Damage Reduction% 210.</summary>
    internal static double CritFactor(Fighter a, Fighter b) => Math.Max(0, 15000 + S(a, 209) - S(b, 210)) / 10000;

    /// <summary>A heal: Healing Done% 207 and Global Healing Effect% 241 of the healer, Healing Taken% 208 and Global
    /// Healing Received% 242 of the recipient (inferred: two factors, like the damage pairs). 1208 ("per_cure1", the
    /// same "Healing Taken" name; Angeling's 2328301 "Increases this unit's healing received") adds to 208, and 1207
    /// ("per_treat1", Prayer of Gratitude's "Healing Efficiency") to 207 (inferred).</summary>
    internal static double HealFactor(Fighter healer, Fighter recipient) =>
        Math.Max(0, 1 + (S(healer, 207) + S(healer, 241) + S(healer, 1207)) / 10000) * Math.Max(0, 1 + (S(recipient, 208) + S(recipient, 242) + S(recipient, 1208)) / 10000);

    /// <summary>
    /// A skill's _kBuff1 tuples for this caster. Divine Insight ("Inspiration") lists both of its buffs: 1162305
    /// (Max Life +10%, PATK +5%) and 1162405 (Max Life +5%, PATK +10%), the second "when wielding a two-handed spear";
    /// only the one matching the weapon is granted (before, both landed and the second overwrote the first).
    /// </summary>
    /// Adrenaline Rush's second buff (16108xx, "an Extra 10% Attack Speed") goes only to a recipient of the Blacksmith
    /// class (<see cref="BlacksmithLine"/>).
    internal IEnumerable<JsonElement> Buff1(GameSession s, JsonElement row, string script, GameSession? recipient = null)
    {
        var tuples = Tuples(row, "_kBuff1").ToArray();
        if (script == "Inspiration" && tuples.Length == 2)
            return [tuples[_skills.Weapon(s).SubType == TwoHandedSpear ? 1 : 0]];
        // King's Grace's two auras are the Crusader's own; Devotion's "Self-Link" marks the Crusader, not the ally.
        if (script == "KingsGrace" && recipient != null && recipient != s) return [];
        if (script == "Sacrifice" && tuples.Length == 2) return [tuples[recipient == null || recipient == s ? 1 : 0]];
        // Firepower Loaded and Energy Release's countdown are granted by CombatService.Delayed (load counts, shield).
        if (script is "VulcanArm" or "FlameLauncher")
            return tuples.Where(t => (int)(GameData.Number(t[0]) ?? 0) != FirepowerLoadedBuff).ToArray();
        if (script.StartsWith("AxeLeapAttack2", StringComparison.Ordinal))
            return tuples.Where(t => (int)(GameData.Number(t[0]) ?? 0) != HighEnergyShieldBuff).ToArray();
        if (script == "BombSuicide1")
            return tuples.Where(t => (int)(GameData.Number(t[0]) ?? 0) != EnergyReleaseCountdownBuff).ToArray();
        if (script.StartsWith("AdrenalineRush", StringComparison.Ordinal) && tuples.Length == 2
            && !BlacksmithLine(recipient ?? s))
            return [tuples[0]];
        return tuples;
    }

    /// <summary>A player attacking, with its secondary stats.</summary>
    internal Fighter PlayerAttacker(GameSession s, StatSheet stats) =>
        new(stats[103], stats[104], stats[109], stats[111], s.Progress!.Level, 1)
        {
            Stats = stats, Race = PlayerRace, Size = PlayerSize, Covered = Covered(s),
            NormalAttackElement = HasBuffGroup(s, AspersioGroup) ? HolyElement : PoisonsNormalAttack(s) ? PoisonElement : 0,
        };

    /// <summary>A player taking a hit, with its defenses and secondary stats.</summary>
    private Fighter PlayerDefender(GameSession p, StatSheet stats) =>
        new(0, 0, 0, 0, p.Progress!.Level, 1)
        {
            Def = stats[105], Mdef = stats[106], Flee = stats[110], ResCri = stats[112],
            Stats = stats, Race = PlayerRace, Size = PlayerSize, Covered = DefenderCovered(p),
            MonsterDamageCutBp = MonsterResistanceBp(p),
        };

    /// <summary>A defender's covered parts: the totems', plus the tower pets' support reductions (1216, 1230, 315), which
    /// ApplyPetSupportResistance already applies to the committed hit.</summary>
    private Func<int, double>? DefenderCovered(GameSession p)
    {
        var totem = Covered(p);
        var pet = p.Buffs.Where(b => b.ExpireMs > MonsterService.NowMs && b.SourceId is >= (1L << 51) and < (1L << 52))
            .SelectMany(b => b.Changes).Where(c => c.Attr is 1216 or 1230 or 315)
            .GroupBy(c => (int)c.Attr).ToDictionary(g => g.Key, g => g.Sum(c => (double)c.Value));
        if (pet.Count == 0) return totem;
        return id => (totem?.Invoke(id) ?? 0) + pet.GetValueOrDefault(id);
    }

    /// <summary>Aspersio 14113: "Changes your Basic Attack element to Holy" (EleConfig row 7). Its buff 1411301 carries
    /// _kLogicData [4, 4701], which no other buff uses and the server does not decode; the element is from the
    /// description (inferred).</summary>
    private const int AspersioGroup = 14113, HolyElement = 7;

    private bool HasBuffGroup(GameSession s, int group) => s.Buffs.Any(b => b.ExpireMs > MonsterService.NowMs
        && _data.Row("BuffConfig", b.ConfigId) is { } row && GameData.Int(row, "_iBuffID") == group);

    private Func<int, double>? Covered(GameSession s) => CoveredBonus is { } covered ? id => covered(s, id) : null;

    /// <summary>The monster side of the formula: race, size, element and boss from its template.</summary>
    private Fighter AsMonster(Fighter f, MonsterTemplate t, StatSheet? stats) => f with
    {
        Stats = stats,
        Race = t.Race,
        Size = t.Size,
        IsMonster = true,
        Boss = IsBoss(t.NpcId),
    };

    private readonly Dictionary<int, bool> _bosses = new();

    /// <summary>(inferred) A boss is a monster with the large boss HP bar (NPCConfig _iMonsterHPBarDisplay 1 or 2:
    /// Baphomet, Moonlight Flower, Golden Thief Bug, the dungeon bosses; ordinary field monsters are 0).</summary>
    internal bool IsBoss(int npcId)
    {
        if (_bosses.TryGetValue(npcId, out bool boss)) return boss;
        return _bosses[npcId] = _data.Row("NPCConfig", npcId) is { } row && GameData.Int(row, "_iMonsterHPBarDisplay") != 0;
    }

    /// <summary>A monster's live buff attributes as a stat sheet (Lex Aeterna-style taken% debuffs and the like).</summary>
    private StatSheet? UnitSheet(WorldUnit unit)
    {
        StatSheet? sheet = null;
        foreach (var (id, value) in _buffs.UnitAttributes(unit))
        {
            sheet ??= new StatSheet();
            sheet[(int)id] += (long)Math.Round(value);
        }
        return sheet;
    }
}
