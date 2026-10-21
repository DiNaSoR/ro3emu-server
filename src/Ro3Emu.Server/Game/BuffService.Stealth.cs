using System.Text.Json;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Stealth: Hiding, Disguise 15112's 【Cloaking】 and Enhance Cloaking 15304 are BuffConfig _kBuffCategory 31 rows
/// ("While Cloaking is active, you cannot be seen or targeted. You are revealed after taking damage N times or dealing
/// damage"). Their _kLogicData is [id, reveal on dealing damage (1) or not (0), hits taken before the reveal]:
/// Disguise 1500610 [15007, 1, 3], Enhance Cloaking 1530410 [15305, 1, 5], Hiding 5000901 [50031, 0, 1]. A row without
/// it (Stealth 2302201) breaks on the first hit or damage (its description; inferred numbers).
/// The client hides a unit whose attribute 3010 (enum_unitAttrMap.Stealth) is set, unless the viewer's 3011/3012
/// (ShowStealth1/2) reach it (LC_UnitStealthManager.UpdateShowStealthUnit); the server sets 3010 = 1 while a stealth
/// buff lives (inferred value: the ShowStealth1 bit). Monsters stop targeting a hidden player (MonsterService).
/// </summary>
public sealed partial class BuffService
{
    public const int StealthCategory = 31, StealthAttribute = 3010;

    private IEnumerable<(int Id, long Value)> StealthProperties(GameSession player)
    {
        yield return (StealthAttribute, player.Stealthed ? 1 : 0);
    }

    /// <summary>A stealth row: category 31 with no logic or a [id, 0/1, hits] reveal rule (other category-31 rows with
    /// [buff, ms, ms] logic are chance tables, not stealth).</summary>
    internal static (bool BreakOnDeal, int Hits)? StealthRule(JsonElement row)
    {
        if (!Categories(row).Contains(StealthCategory)) return null;
        var logic = row.TryGetProperty("_kLogicData", out var l) && l.ValueKind == JsonValueKind.Array
            ? l.EnumerateArray().Select(v => GameData.Number(v) ?? 0).ToArray() : [];
        if (logic.Length == 0) return (true, 1);
        if (logic.Length >= 3 && logic[1] is 0 or 1 && logic[2] is >= 1 and <= 20) return (logic[1] == 1, (int)logic[2]);
        return null;
    }

    /// <summary>The player's live stealth buff and its rule, if any.</summary>
    private (ActiveBuff Buff, bool BreakOnDeal, int Hits)? LiveStealth(GameSession p)
    {
        long now = MonsterService.NowMs;
        foreach (var buff in p.Buffs)
            if (buff.ExpireMs > now && !buff.IsPassive && _data.Row("BuffConfig", buff.ConfigId) is { } row && StealthRule(row) is { } rule)
                return (buff, rule.BreakOnDeal, rule.Hits);
        return null;
    }

    /// <summary>After any buff change: publish attribute 3010 when the player starts or stops being hidden.</summary>
    private async Task SyncStealthAsync(GameSession p)
    {
        bool hidden = LiveStealth(p) != null;
        if (hidden == p.Stealthed) return;
        p.Stealthed = hidden;
        p.StealthHits = default;
        if (p.Role != null) await _character.RefreshStatsAsync(p);
    }

    /// <summary>A monster's hit on a hidden player counts toward the reveal.</summary>
    public async Task StealthHitAsync(GameSession p)
    {
        if (!p.Stealthed || LiveStealth(p) is not { } stealth) return;
        int hits = p.StealthHits.Guid == stealth.Buff.Guid ? p.StealthHits.Hits + 1 : 1;
        p.StealthHits = (stealth.Buff.Guid, hits);
        if (hits >= stealth.Hits) await RevealAsync(p, stealth.Buff);
    }

    /// <summary>Sight, Sight Sprout or Ruwach found the hidden player: the stealth ends (CombatService.Guard).</summary>
    public async Task UncoverAsync(GameSession p)
    {
        if (p.Stealthed && LiveStealth(p) is { } stealth) await RevealAsync(p, stealth.Buff);
    }

    /// <summary>Damage dealt from hiding reveals the player when the buff says so.</summary>
    public async Task StealthDealtAsync(GameSession p)
    {
        if (p.Stealthed && LiveStealth(p) is { BreakOnDeal: true } stealth) await RevealAsync(p, stealth.Buff);
    }

    /// <summary>Ends the stealth buff and the buffs cast with it that share its _kLogicData id (Disguise's 【Hiding】
    /// speed buff 1500702 [15007, 1, 1] beside 【Camouflage】 1500602 [15007, 1, 1]) (inferred: they end together).</summary>
    private async Task RevealAsync(GameSession p, ActiveBuff stealth)
    {
        double? tag = _data.Row("BuffConfig", stealth.ConfigId) is { } row && row.TryGetProperty("_kLogicData", out var l)
            && l.ValueKind == JsonValueKind.Array && l.GetArrayLength() > 0 ? GameData.Number(l[0]) : null;
        var ending = p.Buffs.Where(b => b == stealth || tag is > 0 && !b.IsPassive && _data.Row("BuffConfig", b.ConfigId) is { } r
            && r.TryGetProperty("_kLogicData", out var rl) && rl.ValueKind == JsonValueKind.Array && rl.GetArrayLength() > 0
            && GameData.Number(rl[0]) == tag).ToArray();
        foreach (var buff in ending) await RemoveAsync(p, buff);
    }
}
