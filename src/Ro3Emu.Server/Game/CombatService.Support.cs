using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Passives whose numbers live only in their descriptions (_kDescData), with no buff or attribute row to apply:
/// <list type="bullet">
/// <item>Wood Spirit's Blessing 14307: "when you take lethal damage, you gain one chance to 【Respawn】. When triggered,
/// you recover ${2}% Life and enter the 【Withered】 state for ${3} minutes" ([1.5, 30, 4]); the Respawn skill 14308
/// (SpiritGuard) grants 【Tree Spirit's Blessing Trigger】 1430801 "Invincible." for its 1.5 s (${1}).</item>
/// <item>Emergency Treatment 16303: "When you take damage while your HP is below ${1}%, grants a shield equal to ${2}%
/// of your HP and restores ${3}% of your Life ... every ${4} sec." ([50, 16..20, 6..10, 180]); the shield and heal show
/// as 【Emergency Treatment Shield】 16308 and 【Emergency Treatment Healing】 16309, the rows whose percentage matches.
/// Once per period and a 10 s shield (inferred); monster hits only.</item>
/// <item>Dragon Training 11307: "increases the damage of 【Dragon Breath (Fire)】 and 【Dragon Breath (Water)】 by ${2}%".</item>
/// <item>Arcane Mastery 14207: "increases the damage of [Turn Undead] by ${2}" (55..100, read as a flat amount: the
/// text has no %, inferred).</item>
/// </list>
/// </summary>
public sealed partial class CombatService
{
    private const int WoodSpiritGroup = 14307, SpiritGuardGroup = 14308, EmergencyTreatmentGroup = 16303,
        EmergencyShieldGroup = 16308, EmergencyHealGroup = 16309, DragonTrainingGroup = 11307,
        DragonBreathFireGroup = 11304, DragonBreathWaterGroup = 11305, ArcaneMasteryGroup = 14207, TurnUndeadGroup = 14118;

    private void WireSupportSkills()
    {
        _buffs.LethalSavers.Add(WoodSpiritAsync);
        TakenAsync.Add(EmergencyTreatmentAsync);
        DamageAdjusters.Add(PassiveSkillDamage);
    }

    private double[] LearnedDesc(GameSession s, int group) =>
        s.Progress != null && _skills.Level(s.Progress, group) is var level and > 0
        && _data.Row("SkillConfig", _skills.RealId(group, level)) is { } row ? Numbers(row, "_kDescData") : [];

    // ------------------------------------------------------------------ Wood Spirit's Blessing

    /// <summary>A lethal hit with Wood Spirit's Blessing learned and not 【Withered】: keep ${2}% Life, turn invincible for
    /// ${1} s and lock it for ${3} minutes. Runs under the character lock of the hit (BuffService.TryPreventLethalAsync).</summary>
    private async Task<bool> WoodSpiritAsync(GameSession p)
    {
        if (p.Role == null || LearnedDesc(p, WoodSpiritGroup) is not { Length: > 0 } desc) return false;
        long now = Now;
        if (now < p.WoodSpiritReadyMs) return false;
        int invincible = desc[0] > 0 ? (int)(desc[0] * 1000) : 1500;
        double percent = desc.Length > 1 && desc[1] > 0 ? desc[1] : 30;
        long lockout = desc.Length > 2 && desc[2] > 0 ? (long)(desc[2] * 60000) : 240000;
        p.WoodSpiritReadyMs = now + lockout;
        p.InvincibleUntilMs = now + invincible;
        var stats = _character.Stats(p);
        await _character.SetHpSpAsync(p, Math.Max(1, (long)(stats[101] * percent / 100)), null);
        if (_data.Row("SkillConfig", _skills.RealId(SpiritGuardGroup, 1)) is { } respawn
            && Tuples(respawn, "_kBuff1").FirstOrDefault() is { ValueKind: JsonValueKind.Array } grant && grant.GetArrayLength() > 0)
            await _buffs.AddAsync(p, (int)(GameData.Number(grant[0]) ?? 0), invincible, p.Role.Id);
        // The passive's own buff shows the lockout as its effect cooldown, like the revive choices.
        if (p.Buffs.FirstOrDefault(b => _data.Row("BuffConfig", b.ConfigId) is { } row && GameData.Int(row, "_iBuffID") == WoodSpiritGroup) is { } passive)
        {
            p.BuffEffectCds[passive.Guid] = now + lockout;
            await p.SendGameAsync(BuffService.BuffEffectCd, new BuffEffectCdNotify
            {
                BuffGuid = passive.Guid, StartTimestampMs = now, EndTimestampMs = now + lockout,
            });
        }
        return true;
    }

    // ------------------------------------------------------------------ Emergency Treatment

    private async Task EmergencyTreatmentAsync(MonsterUnit _, GameSession p, long damage)
    {
        if (damage <= 0 || p.Dead || p.Role == null || LearnedDesc(p, EmergencyTreatmentGroup) is not { Length: > 3 } desc) return;
        var role = p.Role;
        using var held = await CharacterService.Lock(p);
        long now = Now;
        if (!ReferenceEquals(p.Role, role) || p.Dead || p.SceneLoading || now < p.EmergencyTreatmentReadyMs) return;
        var stats = _character.Stats(p);
        long max = stats[101], hp = stats[2000];
        if (max <= 0 || hp <= 0 || hp * 100 >= max * desc[0]) return;
        p.EmergencyTreatmentReadyMs = now + (long)(desc[3] * 1000);
        long shield = (long)(max * desc[1] / 100), heal = Math.Min((long)(max * desc[2] / 100), Math.Max(0, max - hp));
        if (EmergencyRow(EmergencyShieldGroup, desc[1]) is { } shieldId && shield > 0)
            await _buffs.AddSupportAsync(p, shieldId, 10000, role.Id, shield);
        if (EmergencyRow(EmergencyHealGroup, desc[2]) is { } healId) await _buffs.AddAsync(p, healId, 1000, role.Id);
        if (heal <= 0) return;
        await _character.SetHpSpAsync(p, hp + heal, null);
        await ToPlayerAndViewersAsync(p, SkillHit, new SkillHitInfo
        {
            SrcID = role.Id, SessionId = ++_sessionIds,
            Dsts = { new DamageInfo { DstID = role.Id, Damage = heal, DamageType = DamageType.Heal, DstElement = 1 } },
        });
    }

    /// <summary>The 16308/16309 row whose T_AXadd0_etc percentage (_kDamageParamExtra [0, bp, 101, 3]) is this one.</summary>
    private int? EmergencyRow(int group, double percent)
    {
        for (int id = group * 100 + 1; id <= group * 100 + 20; id++)
            if (_data.Row("BuffConfig", id) is { } row && row.TryGetProperty("_kDamageParamExtra", out var extra)
                && extra.ValueKind == JsonValueKind.Array && extra.GetArrayLength() > 0 && extra[0].ValueKind == JsonValueKind.Array
                && extra[0].GetArrayLength() > 1 && GameData.Number(extra[0][1]) is { } bp && Math.Abs(bp - percent * 100) < 0.5)
                return id;
        return null;
    }

    // ------------------------------------------------------------------ damage passives

    private long PassiveSkillDamage(GameSession s, MonsterUnit target, JsonElement row, DamageHitResult result, long amount)
    {
        int group = (int)GameData.Int(row, "_iSkillID");
        if (group is DragonBreathFireGroup or DragonBreathWaterGroup && LearnedDesc(s, DragonTrainingGroup) is { Length: > 1 } dragon)
            return (long)Math.Round(amount * (1 + dragon[1] / 100));
        if (group == TurnUndeadGroup && LearnedDesc(s, ArcaneMasteryGroup) is { Length: > 1 } arcane)
            return amount + (long)arcane[1];
        return amount;
    }
}
