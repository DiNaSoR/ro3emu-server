using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Prayer of Gratitude 14306 (Archbishop passive): "plus a shield equal to Max Life * ${4}%. (Refreshes after taking no
/// damage for ${5} sec.)" Its same-id BuffConfig row (1430601-1430603, "Gain a permanent damage-absorption shield",
/// category 30, T_AXadd0_etc [0, 1000, 101, 3]: 10% of Max Life) is the shield. The role holds it while the skill is
/// learned, without a countdown; whenever it is spent or below full and the role has taken no damage for ${5} seconds
/// (30), it is granted again at full. The shield counts its owner's Shield Efficiency like any other.
/// Checked on the 0.5 s guard tick, so it also comes back after a scene change clears it (inferred: right away, since
/// the 30 s count only resets on damage).
/// </summary>
public sealed partial class CombatService
{
    private const int PrayerOfGratitudeGroup = 14306;

    private HashSet<int>? _prayerBuffIds;

    /// <summary>BuffConfig rows of the Prayer of Gratitude group, so the 500 ms check runs no row lookups per buff.</summary>
    private HashSet<int> PrayerBuffIds => _prayerBuffIds ??= _data.Table("BuffConfig")
        .Where(e => GameData.Int(e.Value, "_iBuffID") == PrayerOfGratitudeGroup && int.TryParse(e.Key, out _))
        .Select(e => int.Parse(e.Key)).ToHashSet();

    private async Task PrayerShieldAsync(GameSession s)
    {
        if (s.Role == null || s.Progress == null) return;
        int level = _skills.Level(s.Progress, PrayerOfGratitudeGroup);
        var ids = PrayerBuffIds;
        var held = s.Buffs.FirstOrDefault(b => !b.IsPassive && ids.Contains(b.ConfigId));
        if (level <= 0)
        {
            if (held != null) await _buffs.RemoveAsync(s, held);
            return;
        }
        long id = _skills.RealId(PrayerOfGratitudeGroup, level);
        if (_data.Row("SkillConfig", id) is not { } skill || _data.Row("BuffConfig", id) is not { } row) return;
        var stats = _character.Stats(s);
        long full = ShieldAmount(row, stats, stats);
        if (full <= 0 || held is { } h && h.ConfigId == id && h.ShieldRemaining >= full) return;
        double quiet = Numbers(skill, "_kDescData").ElementAtOrDefault(4);
        if (quiet <= 0) quiet = 30;
        if (s.LastDamagedMs != 0 && Now - s.LastDamagedMs < quiet * 1000) return;
        await _buffs.AddLastingAsync(s, (int)id, s.Role.Id, shieldAmount: full);
    }
}
