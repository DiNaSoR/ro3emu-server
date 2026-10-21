using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    private const int DoubleAttackGroup = 15002, DaggerSubType = 1;

    /// <summary>Extra hits the last DoubleAttackDamage call added (game loop only), read right after by the hit builder.</summary>
    private int _doubleAttackHits;

    /// <summary>
    /// Thief Double Attack (SkillConfig 15002, learned level L): "When a Dagger is equipped, each Basic Attack has a
    /// desc[1]% chance to deal desc[2] extra hits of Neutral melee Physical damage equal to PATK x desc[3]% (this damage
    /// is not considered a Normal Attack)", desc = the level row's _kDescData (1%..10% chance, 1 hit, 50%). Any
    /// normal attack (_iIsNormalAtk 1) with a Dagger in the main hand can proc; the extra hits go into the same hit's
    /// number, which SkillHitInfo.split shows as that many more numbers (inferred: the client has no separate packet). The Katar CRIT half of the skill is not applied here.
    /// </summary>
    private long DoubleAttackDamage(GameSession s, MonsterUnit target, JsonElement row, long amount)
    {
        if (GameData.Int(row, "_iIsNormalAtk") != 1 || s.Progress is not { } p) return 0;
        int level = _skills.Level(p, DoubleAttackGroup);
        if (level <= 0 || _skills.Weapon(s).SubType != DaggerSubType
            || _data.Row("SkillConfig", _skills.RealId(DoubleAttackGroup, level)) is not { } skill) return 0;
        var desc = skill.TryGetProperty("_kDescData", out var d) && d.ValueKind == JsonValueKind.Array
            ? d.EnumerateArray().Select(v => GameData.Number(v) ?? 0).ToArray() : [];
        if (desc.Length < 3 || desc[0] <= 0 || desc[1] <= 0 || Rolls.Proc(100) >= desc[0]) return 0;
        var stats = _character.Stats(s);
        var defender = AsDefender(target);
        double one = Mitigated(defender, stats[103] * desc[2] / 100.0, magic: false, element: 1);
        _doubleAttackHits = (int)desc[1];
        return (long)desc[1] * Math.Max(1, (long)Math.Round(one));
    }
}
