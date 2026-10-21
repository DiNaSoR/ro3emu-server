using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class PetService
{
    /// <summary>
    /// The Main Skill a deployed pet gives its master. Localization 40253: "When a pet is deployed in the Main Slot,
    /// the Adventurer gains its Main Skill, which can be activated in battle. The Main Skill's star rank also increases
    /// with the pet's star rank." The Main Slot is setup slot PublicParamConfig.PetProtectPosition (1); the skill is
    /// that pet's current PetStarConfig._iProtectSkill (one row per star, e.g. Choco 2353301..2353306), which
    /// LC_PetHelper lists as enum_PetSkillType.Protect. It needs the pet function (33) open, like the lineup itself.
    /// </summary>
    internal PetMainSkill? MainSkill(GameSession s)
    {
        if (s.Pets is not { } pets || s.Progress == null || !_functions.IsOpen(s, 33)
            || !pets.SetupPlans.TryGetValue(pets.CurrentPlan, out var plan)
            || !plan.SetUpPets.TryGetValue(MainSlot, out int petId) || !pets.ListPet.TryGetValue(petId, out var pet)
            || StarRow(pet) is not { } row || GameData.Int(row, "_iProtectSkill") is not (> 0 and var skill and <= int.MaxValue)
            || _data.Row("SkillConfig", skill) == null) return null;
        return new PetMainSkill((int)skill, petId);
    }

    private int MainSlot => _data.Table("PublicParamConfig").TryGetValue("PetProtectPosition", out var v)
        && GameData.Number(v) is { } slot and > 0 ? (int)slot : 1;

    /// <summary>Resend 5025 when the pet slot's skill changed (deploy, swap, star up, plan switch, login).</summary>
    private async Task RefreshMainSkillAsync(GameSession s)
    {
        int now = MainSkill(s)?.SkillId ?? 0;
        if (now == s.PetMainSkillShown || s.SkillPlans == null) return;
        s.PetMainSkillShown = now;
        await _skills.SyncAsync(s);
    }
}
