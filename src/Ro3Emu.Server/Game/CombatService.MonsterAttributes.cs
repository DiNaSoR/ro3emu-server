using Romsg;
using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    private static readonly int[] PublishedMonsterAttributes = [103, 104, 105, 106, 109, 110, 111, 116];

    /// <summary>BuffConfig._kChangeAttri uses the same AttributeConfig groups for enemy PATK/MATK/DEF/MDEF.
    /// (inferred) Overlay live changes on the resolved monster template, using the existing player/pet calculator.
    /// Never mutate the shared template: expiration and two monsters of the same kind stay independent.</summary>
    internal StatSheet MonsterStats(MonsterUnit unit)
    {
        var t = unit.Template;
        var raw = new Dictionary<int, double>
        {
            [101] = t.MaxHp, [103] = t.Atk, [104] = t.Matk, [105] = t.Def, [106] = t.Mdef,
            [109] = t.Hit, [110] = t.Flee, [111] = t.Cri, [113] = t.SpeedCm, [116] = t.CastSpeed,
        };
        foreach (var (id, value) in _buffs.GuardianUnitAttributes(unit)) raw[(int)id] = raw.GetValueOrDefault((int)id) + value;
        return StatCalculator.Compute(raw, _data);
    }

    private Fighter MonsterFighter(MonsterUnit unit)
    {
        var stats = MonsterStats(unit);
        return new(stats[103], stats[104], stats[109], stats[111], unit.Template.Level, unit.Template.Element)
        { Def = stats[105], Mdef = stats[106], Flee = stats[110], ResCri = stats[112] };
    }

    private long MonsterBuffDamage(MonsterUnit monster, JsonElement? skill, long amount)
    {
        int element = skill is { } row && GameData.Int(row, "_iEleType") > 1
            ? (int)GameData.Int(row, "_iEleType") : monster.Template.Element;
        const int DamageBonus = 1215, FirstElementBonus = 301;
        double bonus = _buffs.GuardianUnitAttributes(monster).Where(c => c.Id == DamageBonus
            || element is >= 1 and <= 10 && c.Id == FirstElementBonus + element - 1).Sum(c => c.Value);
        return (long)Math.Clamp(Math.Floor(amount * Math.Max(0, 1 + bonus / 10000)), 0, long.MaxValue);
    }

    private async Task PublishMonsterAttributesAsync(WorldUnit unit)
    {
        if (unit is not MonsterUnit monster) return;
        bool guardian = _buffs.HasGuardianUnitBuffs(monster);
        // Ordinary unit buffs leave the template's stats and speed as they were; publish only a guardian's changes
        // and, once the last guardian buff is gone, the return to the template.
        if (!guardian && monster.BuffSpeedCm == null) return;
        var stats = MonsterStats(monster);
        await _monsters.SetBuffSpeedAsync(monster, guardian ? (int)Math.Clamp(stats[113], 0, int.MaxValue) : null);
        var changed = new AOIAttrChange
        {
            EntityUuid = (ulong)unit.Uid,
            StateInfo = new AoiObjStateInfo { Data = new CommonBigMask { Values = { (ulong)monster.StatusBits | _buffs.UnitStatus(unit) } } },
            ForbidInfo = new AoiObjForbidInfo { Data = new CommonBigMask { Values = { _buffs.UnitForbiddenMask(unit) } } },
        };
        foreach (int id in PublishedMonsterAttributes)
            changed.Property.Add(new AoIProperty { Idx = id, Data = stats[id], BaseData = stats.Base.GetValueOrDefault(id) });
        await _world.ToUnitViewersAsync(unit, AoiAttrChange, changed);
    }
}
