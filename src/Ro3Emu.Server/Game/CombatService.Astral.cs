using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Psychokinetic Astral Body 12343: "Auto-generating ${2} 【Psychokinetic Astral Bodies】 every ${1} seconds, up to ${3}. If
/// you already have ${4} 【Psychokinetic Astral Bodies】, hitting with Soul Expansion Launches all ... Each Deals Ghost
/// Ranged Magic DMG equal to MATK * ${5}%". The orbs are the piles of 1236101-1236110 (group 12361, the learned level);
/// the launch is that buff's _kLogicData row, Singularity 1234501-1234510, whose three _kDamageParam1 entries are the
/// three orbs (MATK x 55-100% each). Intense Telekinesis's 【Intense Telekinesis】 (group 12362, "acquisition efficiency
/// increases by 100%") gathers twice as many per beat (inferred reading of "efficiency").
/// </summary>
public sealed partial class CombatService
{
    private const int AstralBodyGroup = 12343, AstralOrbGroup = 12361, SoulExpansionGroup = 12302, IntenseTelekinesisGroup = 12362;

    private void WireAstralBodies() => DealtAsync.Add(LaunchAstralBodiesAsync);

    private (JsonElement Skill, double[] Desc, int OrbBuff)? AstralBody(GameSession s)
    {
        if (s.Progress is not { } progress || _skills.Level(progress, AstralBodyGroup) is not (var level and > 0)
            || _data.Row("SkillConfig", _skills.RealId(AstralBodyGroup, level)) is not { } skill
            || Numbers(skill, "_kDescData") is not { Length: > 4 } desc) return null;
        return (skill, desc, AstralOrbGroup * 100 + level);
    }

    private async Task GatherAstralBodiesAsync(GameSession s)
    {
        if (AstralBody(s) is not var (_, desc, orbBuff) || _data.Row("BuffConfig", orbBuff) is null) return;
        long now = Now;
        if (s.AstralNextMs == 0) s.AstralNextMs = now + (long)(desc[0] * 1000);
        if (now < s.AstralNextMs) return;
        s.AstralNextMs = now + (long)(desc[0] * 1000);
        var orbs = s.Buffs.FirstOrDefault(b => b.ConfigId == orbBuff && b.ExpireMs > now);
        int have = orbs?.PileCount ?? 0, cap = (int)desc[2];
        if (have >= cap) return;
        int gain = (int)Math.Max(1, desc[1]) * (LiveGroup(s, IntenseTelekinesisGroup) != null ? 2 : 1);
        await _buffs.AddLastingAsync(s, orbBuff, s.Role!.Id, Math.Min(cap, have + gain));
    }

    private async Task LaunchAstralBodiesAsync(GameSession s, MonsterUnit target, JsonElement row, long amount)
    {
        if (amount <= 0 || GameData.Int(row, "_iSkillID") != SoulExpansionGroup || target.Dead || target.Hp <= 0
            || AstralBody(s) is not var (_, desc, orbBuff)) return;
        var orbs = s.Buffs.FirstOrDefault(b => b.ConfigId == orbBuff && b.ExpireMs > Now);
        if (orbs == null || orbs.PileCount < desc[3] || _data.Row("BuffConfig", orbBuff) is not { } orbRow
            || Numbers(orbRow, "_kLogicData") is not [var launchId, ..] || _data.Row("SkillConfig", (long)launchId) is not { } launch) return;
        await _buffs.RemoveAsync(s, orbs);
        s.AstralNextMs = Now + (long)(desc[0] * 1000);
        await ProcCastAsync(s, launch, target);
    }
}
