using System.Text.Json;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// A deployed pet's Main Skill, cast by its master from the wheel's pet slot (SkillService.ClientState).
/// <list type="bullet">
/// <item>The cast row is enum_SkillSystemType.ActiveGuardSkill (8): an instant trigger (_iActionTime 1, script
/// "petyinyue" and the like, template 23001/23002 with no hit) whose _kLogicData[0] names the skill group the pet
/// performs, one of its _kAutoLearnSkill rows at the same level (Choco 2353301 -> 2353401, Lv2 2353302 -> 2353402).</item>
/// <item>That row is enum_SkillSystemType.GuardianSkill (41) and carries the effect (_kBuff1, _kDamageParam1).
/// LC_Scene_Sync._extraUseInSkill shows it when a 31121 for the master names petConfigId: it builds a local pet
/// model beside the master (PetSkillDistance) that plays the template. So the pet is only seen while it acts.</item>
/// <item>The effect runs as a server follow-up from the master (ProcCastAsync): its stats, targets and buffs.
/// "Adaptive ATK" in the descriptions is taken as the higher of the master's Physical and Magic ATK (inferred).</item>
/// </list>
/// Other _kAutoLearnSkill rows (Demonic Aura's periodic hit, the Holy Light Field's shields) run from CombatService.PetAuras.
/// </summary>
public sealed partial class CombatService
{
    internal const int ActiveGuardSkill = 8, GuardianSkill = 41;

    private bool AdaptiveGuardian(JsonElement skill) => GameData.Int(skill, "_iDmgType") == 4
        && (GameData.Int(skill, "_iSystemType") == GuardianSkill || PetAuraRow(skill) || PetProcRow(skill));

    /// <summary>What a friendly heal scales from: MATK, or Adaptive ATK for a pet's rows ("Is restores Adaptive
    /// ATK*180% Life", Angeling's halo).</summary>
    private double HealPower(JsonElement skill, StatSheet stats) =>
        AdaptiveGuardian(skill) ? Math.Max(stats[103], stats[104]) : stats[104];

    /// <summary>The GuardianSkill row a Main Skill row triggers, or null when the data names none.</summary>
    internal JsonElement? GuardianRow(JsonElement main)
    {
        var group = Numbers(main, "_kLogicData") is [var first, ..] ? (long)first : 0;
        JsonElement? fallback = null;
        foreach (var id in Numbers(main, "_kAutoLearnSkill"))
        {
            if (_data.Row("SkillConfig", (long)id) is not { } row || GameData.Int(row, "_iSystemType") != GuardianSkill) continue;
            if (GameData.Int(row, "_iSkillID") == group) return row;
            fallback ??= row;
        }
        return fallback;
    }

    private async Task PetGuardianAsync(GameSession s, ActiveSkillCast action)
    {
        if (GuardianRow(action.Row) is not { } row || _skills.PetMainSkill?.Invoke(s) is not { } pet
            || pet.SkillId != action.Request.SkillId) return;
        var target = FindMonster(s.WorldScene, action.Request.TargetId);
        if (target != null && !CanHitMonster(s, target)) target = null;
        await MoveCasterAsync(s, action);
        if (s.Dead || s.Role == null) return;
        await GuardianCastAsync(s, row, () => ProcCastAsync(s, row, target, face: action.Request.Face,
            at: target == null ? action.Request.TargetPos : null, petId: pet.PetId));
        StartPetAuras(s, action.Row, row);
        StartOwlStrikes(s, row, target, pet.PetId);
        StartRadiantRing(s, row);
    }
}
