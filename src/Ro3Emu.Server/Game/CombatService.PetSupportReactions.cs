using System.Runtime.CompilerServices;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    private readonly ConditionalWeakTable<GameSession, Dictionary<int, long>> _guardianReactionCds = [];

    private async Task GuardianSupportReactionAsync(GameSession s, GuardianPetRules.Skill passive, ActiveSkillCast action, Func<bool> current)
    {
        if (!current() || passive.Passive is not { } row) return;
        int skillId = (int)GameData.Int(row, "_iID");
        double Desc(int index) => GuardianPetRules.Description(passive, index);
        switch (passive.Reactive)
        {
            case GuardianPetRules.Reaction.AliotHealingWindow or GuardianPetRules.Reaction.DullahanWindow or GuardianPetRules.Reaction.LolaWindow:
                GuardianReactiveWindow(s, passive, action);
                break;
            case GuardianPetRules.Reaction.ElenaBurst:
                if (GuardianAuxiliary(passive, "Pet_Bolkoba_23296") is { } burst)
                    await ApplyEffectsAsync(s, GuardianProcAction(s, passive, action, burst), s.Position);
                break;
            case GuardianPetRules.Reaction.SuccubusSilence or GuardianPetRules.Reaction.ChimeraDebuff
                or GuardianPetRules.Reaction.WildcatShield or GuardianPetRules.Reaction.SoheeReduction or GuardianPetRules.Reaction.IncubusAura
                or GuardianPetRules.Reaction.ScoutDetection or GuardianPetRules.Reaction.OwlLightning or GuardianPetRules.Reaction.CatVulnerability:
                await GuardianAdditionalReactionAsync(s, passive, action);
                break;
            case GuardianPetRules.Reaction.WanderSlash:
                await GuardianWanderReactionAsync(s, passive, action);
                break;
            case GuardianPetRules.Reaction.Immunity:
                await _buffs.AddGuardianAsync(s, GuardianPetRules.ControlImmunityBuff, (int)(Desc(0) * 1000), action.OwnerId);
                break;
            case GuardianPetRules.Reaction.Endure:
                foreach (var ally in GuardianReactionAllies(s, (float)Desc(0), (int)Desc(1)))
                    if (current() && !ally.Dead && !ally.SceneLoading && ally.WorldScene == action.WorldScene)
                        await _buffs.AddGuardianAsync(ally, GuardianPetRules.EndureBuff, (int)(Desc(2) * 1000), action.OwnerId);
                break;
            case GuardianPetRules.Reaction.ControlShield or GuardianPetRules.Reaction.FairyShield:
                await GuardianReactiveShieldAsync(s, passive, action);
                break;
            case GuardianPetRules.Reaction.PeriodicHeal:
                if (!ReserveGuardianReaction(s, skillId, (int)(Desc(1) * 1000))) break;
                // Description: one flat heal every second for the specified duration. First tick after 1 s (inferred).
                for (int second = 1; second <= (int)Desc(2); second++)
                    ScheduleRuntime(action.WorldScene, TimeSpan.FromSeconds(second), () => current()
                        ? GuardianHealAsync(s, s, skillId, action.Id, (long)Desc(0), current) : Task.CompletedTask);
                break;
        }
    }

    private GameSession[] GuardianReactionAllies(GameSession source, float radius, int maximum) => [.. _world.PlayersIn(source.WorldScene)
        .Where(p => p.Role != null && !p.Dead && !p.SceneLoading && !p.SceneRecoveryRequired
            && PlayerHostile?.Invoke(source, p) != true && PlayerHostile?.Invoke(p, source) != true
            && !CanPlayerHit(source, p) && !CanPlayerHit(p, source) && Distance(source.Position, p.Position) <= radius)
        .OrderBy(p => p == source ? 0 : 1).ThenBy(p => Distance(source.Position, p.Position)).ThenBy(p => p.Role!.Id)
        .Take(Math.Max(0, maximum))];

    private bool ReserveGuardianReaction(GameSession source, int skillId, int milliseconds)
    {
        var cooldowns = _guardianReactionCds.GetOrCreateValue(source);
        if (cooldowns.GetValueOrDefault(skillId) > Now) return false;
        cooldowns[skillId] = Now + Math.Max(0, milliseconds);
        return true;
    }

    private async Task GuardianReactiveShieldAsync(GameSession s, GuardianPetRules.Skill passive, ActiveSkillCast action)
    {
        // Named candidate links (inferred), validated by T_AXadd0_etc and the exact MaxHP coefficient.
        // Fairy's active shield uses a different group: never use the passive's same-ID active shield.
        const int FairyReactiveShieldGroup = 23186;
        int buffId = passive.Id;
        if (passive.Reactive == GuardianPetRules.Reaction.FairyShield)
        {
            var candidate = _data.Table("BuffConfig").Values.FirstOrDefault(b => GameData.Int(b, "_iBuffID") == FairyReactiveShieldGroup
                && GameData.Int(b, "_iLevel") == passive.Star);
            buffId = candidate.ValueKind == System.Text.Json.JsonValueKind.Undefined ? 0 : (int)GameData.Int(candidate, "_iID");
        }
        if (_data.Row("BuffConfig", buffId) is not { } buff) return;
        var stats = _character.Stats(s);
        long expected = (long)Math.Floor(stats[GuardianPetRules.Hp] * GuardianPetRules.Description(passive, 0) / 100);
        long amount = PetCombatRules.AttributeAmount(buff, stats, stats);
        if (amount <= 0 || amount != expected) return;
        bool coreShield = passive.Reactive == GuardianPetRules.Reaction.FairyShield && passive.Core != null;
        if (coreShield) amount = (long)Math.Floor(stats[GuardianPetRules.Hp] * GuardianPetRules.CoreDescription(passive, 1) / 100);
        int durationIndex = passive.Reactive == GuardianPetRules.Reaction.FairyShield ? 2 : 1;
        await _buffs.AddGuardianAsync(s, buffId, (int)(GuardianPetRules.Description(passive, durationIndex) * 1000), action.OwnerId,
            amount, shieldDebuffImmunity: coreShield);
    }
}
