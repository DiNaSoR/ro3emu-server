using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Skills that run on a class resource the client only displays (SkillConfig._kSkillCostParam is the UI's meter, not a
/// cost): Cross Ripper's blades, the Warlock's spellbook pages, the Rune Knight's spear charges and the Shield
/// Crusader's Shield Bash count. The meters are EnergyConfig rows (BuffService.Energy); what fills, spends and converts
/// them comes from each skill's description, inferred where noted.
/// </summary>
public sealed partial class CombatService
{
    private const int CrossBladesEnergy = 540002, PagesEnergy = 240001, PrecisionEnergy = 140001, SpearEnergy = 140002,
        EnhancedSpearEnergy = 140003, ShieldBashEnergy = 141002,
        ReadingBuff = 1231001, ReadingBuffGroup = 12310, PageBuff = 1233501, FuriousStormBuff = 1132401, FuriousStormGroup = 11324,
        EnhanceShieldBashBuff = 1163101, EnhanceShieldBashGroup = 11631, PrecisionThrustGroup = 11315,
        DarkIllusionGroup = 15301, BlackPhantomBuff = 1530101, BlackPhantomGroup = 15301;

    /// <summary>The spellbook's extra cast follows the main one when the 500 ms "Spellbook - ..." mark it grants ends
    /// (_kBuff1 [1231901, 10000, 500], whose _kLogicData names the copy's group) (inferred timing).</summary>
    private static readonly TimeSpan SpellbookCopyDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>Class-skill buffs granted with duration -1 on a row without a time, which last until their end rule:
    /// Reading Spellbook until Stop Reading, Furious Storm until its enhanced charges are spent, Dark Illusion's
    /// 【Black Phantom】 until the next Cross Ripper (all inferred).</summary>
    private static readonly HashSet<int> LastingSkillBuffs = [ReadingBuff, FuriousStormBuff, BlackPhantomBuff];

    /// <summary>
    /// Before a landed cast's hits:
    /// <list type="bullet">
    /// <item>Cross Ripper Slasher 15302 "deals blade count (minimum 1) * PATK * 40% ..." and throws every blade
    /// (540002, starts at 4, never refills by itself); "At 4 blades, resets this skill's cooldown" (_kDescData[4]).
    /// After Dark Illusion 15301 (【Black Phantom】 1530101, up to 2 piles) the throw spends a pile: "multiplies the
    /// blade count of the next Cross Ripper Slasher by 3, and increases each hit's damage by 10%" (its _kDescData[9], [10]).</item>
    /// <item>Dark Illusion "Immediately gains 4 Blade stacks" (_kDescData[8]).</item>
    /// <item>Rolling Cutter 15303 "adds 1 blade to Cross Ripper Slasher, up to 4" (_kDescData[5], [6]) on each cast
    /// (inferred: whether or not it hits, as classic RO counts spins).</item>
    /// <item>Shield Bash (Enhanced) 1163611 spends one 【Enhance Shield Bash】 pile; the last one returns Shield Bash to
    /// its normal form (the buff's _kSkillAttributeList mode switch) (inferred: one pile per cast).</item>
    /// <item>Stop Reading 1231941 ends 【Reading】.</item>
    /// <item>Hundred Spears in its enhanced form ends Furious Storm once the enhanced charges run out (inferred).</item>
    /// </list>
    /// </summary>
    private async Task SpendCastResourcesAsync(GameSession s, ActiveSkillCast action)
    {
        var row = action.Row;
        var desc = Numbers(row, "_kDescData");
        switch (Script(row))
        {
            case "huixuanshizizhan" when s.Energy.TryGetValue(CrossBladesEnergy, out var blades):
                long count = blades.Value;
                double scale = Math.Max(1, count);
                if (HasBuffGroup(s, BlackPhantomGroup) && DarkIllusionRow(s) is { } illusion)
                {
                    var phantom = Numbers(illusion, "_kDescData");
                    if (phantom.Length > 9 && phantom[9] > 0) scale *= phantom[9];
                    if (phantom.Length > 10) scale *= 1 + phantom[10] / 100;
                    await _buffs.ConsumeBuffPilesAsync(s, BlackPhantomGroup, 1);
                }
                action.ProcScale = scale;
                long resetAt = desc.Length > 4 && desc[4] > 0 ? (long)desc[4] : _buffs.EnergyLimit(CrossBladesEnergy);
                action.ResetsCooldown = count > 0 && count >= resetAt;
                await _buffs.SetEnergyAsync(s, CrossBladesEnergy, 0);
                break;
            case "huixuandaoren" when s.Energy.TryGetValue(CrossBladesEnergy, out var cutter):
                long add = desc.Length > 5 && desc[5] > 0 ? (long)desc[5] : 1;
                long cap = desc.Length > 6 && desc[6] > 0 ? (long)desc[6] : _buffs.EnergyLimit(CrossBladesEnergy);
                if (cutter.Value < cap) await _buffs.SetEnergyAsync(s, CrossBladesEnergy, Math.Min(cap, cutter.Value + add));
                break;
            case "hesehuanying" when s.Energy.TryGetValue(CrossBladesEnergy, out var illusionBlades):
                long gain = desc.Length > 8 && desc[8] > 0 ? (long)desc[8] : _buffs.EnergyLimit(CrossBladesEnergy);
                await _buffs.SetEnergyAsync(s, CrossBladesEnergy, illusionBlades.Value + gain);
                break;
            case "Smite2":
                await _buffs.ConsumeBuffPilesAsync(s, EnhanceShieldBashGroup, 1);
                break;
            case "book":
                await RemoveBuffGroupAsync(s, ReadingBuffGroup);
                break;
            default:
                if (CostsEnergy(row, EnhancedSpearEnergy) && s.Energy.GetValueOrDefault(EnhancedSpearEnergy)?.Value is null or 0)
                    await RemoveBuffGroupAsync(s, FuriousStormGroup);
                break;
        }
    }

    /// <summary>After a landed cast's hits: Cross Ripper's cooldown reset, then a spellbook skill's extra cast.</summary>
    private async Task AfterCastResourcesAsync(GameSession s, ActiveSkillCast action)
    {
        var row = action.Row;
        if (action.ResetsCooldown) await ResetCooldownAsync(s, (int)GameData.Int(row, "_iSkillID"));
        await CastFromSpellbookAsync(s, action);
        await WolfAfterCastAsync(s, action);
    }

    /// <summary>
    /// Reading Spellbook 12319: "While active, casting Comet, Tetra Vortex, Frost Blast, Earth Strain, Crimson Rock,
    /// Thunderbolt, Soul Burst, or Gravity Impact triggers the corresponding skill effect one Extra time and consumes the
    /// corresponding Floors of Book Pages". Each such row's _kSkillHighParam names the condition, ["0:1231001:1&2:240001:3",
    /// "1"] (the 【Reading】 buff, 3 pages of meter 240001) or the older ["0", "1231001:1&1233501:5", "1"] (5 piles of the
    /// "Page" buff 1233501, read as pages of the same meter, inferred). The extra cast is the row's
    /// _kAutoLearnSkill[0] copy at the same level ("Jack Frost (Copy)"), at the same target or point.
    /// </summary>
    private async Task CastFromSpellbookAsync(GameSession s, ActiveSkillCast action)
    {
        var row = action.Row;
        if (action.Proc || SpellbookPages(row) is not { } pages || !HasBuffGroup(s, ReadingBuffGroup)
            || !s.Energy.TryGetValue(PagesEnergy, out var meter) || meter.Value < pages
            || Numbers(row, "_kAutoLearnSkill") is not [var copyId, ..] || _data.Row("SkillConfig", (long)copyId) is not { } copy) return;
        await _buffs.SetEnergyAsync(s, PagesEnergy, meter.Value - pages);
        var target = FindMonster(s.WorldScene, action.Request.TargetId);
        var at = (action.Request.TargetPos ?? action.Origin).Clone();
        float face = action.Request.Face;
        ScheduleRuntime(action.WorldScene, SpellbookCopyDelay, async () =>
        {
            if (!CurrentCastMap(s, action)) return;
            var live = target is { Dead: false, Hp: > 0 } ? target : null;
            await ProcCastAsync(s, copy, live, face, at: live?.Pos ?? at);
        });
    }

    /// <summary>The pages a spellbook skill row spends while 【Reading】, or null for a row with no such condition.</summary>
    internal static int? SpellbookPages(JsonElement row)
    {
        if (!row.TryGetProperty("_kSkillHighParam", out var high) || high.ValueKind != JsonValueKind.Array) return null;
        foreach (var entry in high.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() == 0) continue;
            var parts = entry.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ToString()).ToArray();
            // "type:id:n&type:id:n" in the first element, or type first then "id:n&id:n" (all of that type).
            var terms = parts[0].Contains(':')
                ? parts[0].Split('&').Select(t => t.Split(':')).Where(t => t.Length == 3).Select(t => (Type: t[0], Id: t[1], N: t[2]))
                : parts.Length > 1
                    ? parts[1].Split('&').Select(t => t.Split(':')).Where(t => t.Length == 2).Select(t => (Type: parts[0], Id: t[0], N: t[1]))
                    : [];
            var list = terms.ToList();
            if (!list.Any(t => t.Type == "0" && t.Id == ReadingBuff.ToString())) continue;
            foreach (var (type, id, n) in list)
                if ((type == "2" && id == PagesEnergy.ToString() || type == "0" && id == PageBuff.ToString())
                    && int.TryParse(n, out int amount) && amount > 0)
                    return amount;
        }
        return null;
    }

    /// <summary>
    /// After a hit that recovered a meter (_kHitRecoverEnergy), a full meter converts:
    /// <list type="bullet">
    /// <item>Precision Thrust 11315 "Gain 1 stack of 【Hundred Spear Pierce】 charge for every 6 HITs": a full 140001
    /// (limit 6) empties into one Hundred Spears charge (140002, limit 5) (inferred 6 and 1 from the meter limits).</item>
    /// <item>Shield Bash 1163601 counts to 20 (141002); a full count empties into 3 piles of 【Enhance Shield Bash】
    /// 1163101 (_iMaxStack 3), which turns Shield Bash into its enhanced form (inferred).</item>
    /// </list>
    /// </summary>
    private async Task ConvertChargesAsync(GameSession s, JsonElement row)
    {
        int group = (int)GameData.Int(row, "_iSkillID");
        if (group == PrecisionThrustGroup && Full(PrecisionEnergy))
        {
            await _buffs.SetEnergyAsync(s, PrecisionEnergy, 0);
            if (s.Energy.TryGetValue(SpearEnergy, out var spears)) await _buffs.SetEnergyAsync(s, SpearEnergy, spears.Value + 1);
        }
        else if (Script(row) == "Smite3" && Full(ShieldBashEnergy))
        {
            await _buffs.SetEnergyAsync(s, ShieldBashEnergy, 0);
            int piles = _data.Row("BuffConfig", EnhanceShieldBashBuff) is { } buff ? (int)Math.Max(1, GameData.Int(buff, "_iMaxStack")) : 3;
            await _buffs.AddLastingAsync(s, EnhanceShieldBashBuff, s.Role!.Id, piles);
        }

        bool Full(int id) => s.Energy.TryGetValue(id, out var meter) && _buffs.EnergyLimit(id) is var limit and > 0 && meter.Value >= limit;
    }

    /// <summary>Furious Storm 1132401 (Sonic Burst): "immediately gains 5 floors of Enhance Hundred Spear charge"
    /// (_kDescData[0]) in 140003.</summary>
    private async Task LastingBuffGrantedAsync(GameSession p, int id)
    {
        if (id != FuriousStormBuff || _data.Row("BuffConfig", id) is not { } row) return;
        var desc = Numbers(row, "_kDescData");
        long charges = desc.Length > 0 && desc[0] > 0 ? (long)desc[0] : _buffs.EnergyLimit(EnhancedSpearEnergy);
        await _buffs.SetEnergyAsync(p, EnhancedSpearEnergy, charges);
    }

    private JsonElement? DarkIllusionRow(GameSession s) =>
        s.Progress is { } progress && _skills.Level(progress, DarkIllusionGroup) is var level and > 0
            ? _data.Row("SkillConfig", _skills.RealId(DarkIllusionGroup, level)) : null;

    private static bool CostsEnergy(JsonElement row, int energy) =>
        row.TryGetProperty("_kCost", out var costs) && costs.ValueKind == JsonValueKind.Array
        && costs.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 1
            && GameData.Number(c[0]) == 8 && GameData.Number(c[1]) == energy);

    private async Task RemoveBuffGroupAsync(GameSession s, int group)
    {
        foreach (var buff in s.Buffs.Where(b => !b.IsPassive && _data.Row("BuffConfig", b.ConfigId) is { } r
            && GameData.Int(r, "_iBuffID") == group).ToArray())
            await _buffs.RemoveAsync(s, buff);
    }

    /// <summary>Ends every cooldown of a skill group (all its modes) and tells the client.</summary>
    private async Task ResetCooldownAsync(GameSession s, int group)
    {
        if (s.Role == null) return;
        long now = Now;
        var notice = new SkillCdNotify { Uuid = s.Role.Id };
        foreach (var key in s.SkillModeCds.Keys.Where(k => k.Item1 == group).ToArray())
        {
            if (s.SkillModeCds[key] > now)
                notice.Infos.Add(new SkillCdInfo { SkillId = group, SkillMode = key.Item2, CdStartTimestampMs = now, CdEndTimestampMs = now });
            s.SkillModeCds.Remove(key);
        }
        if (s.SkillCds.Remove(group, out long shared) && shared > now && notice.Infos.All(i => i.SkillId != group))
            notice.Infos.Add(new SkillCdInfo { SkillId = group, CdStartTimestampMs = now, CdEndTimestampMs = now });
        if (notice.Infos.Count > 0) await s.SendGameAsync(SkillCdUpdate, notice);
    }
}
