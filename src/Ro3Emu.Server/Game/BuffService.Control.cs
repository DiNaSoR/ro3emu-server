using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class BuffService
{
    private const int ControlBuffTag = 37, NegativeBuffType = 2;
    private const int ControlImmunityGroup = 9032, DebuffImmunityGroup = 9033;
    private const int ControlChargeGroup = 9101; // Chimera's three _kBuff1 entries; three applications, capped by BuffConfig._iMaxStack.

    private async Task<bool> RejectIncomingAsync(GameSession player, JsonElement incoming)
    {
        if (ImmuneTo(player.Buffs, incoming)) return true;
        if (GameData.Int(incoming, "_iType") != NegativeBuffType
            || !GuardianPetRules.Numbers(incoming, "_kBuffCategory").Contains(ControlBuffTag)) return false;
        var charge = player.Buffs.FirstOrDefault(b => b.IsGuardianOwned && b.ExpireMs > MonsterService.NowMs
            && _data.Row("BuffConfig", b.ConfigId) is { } row && GameData.Int(row, "_iBuffID") == ControlChargeGroup);
        if (charge == null) return false;
        // (inferred) Spend one charge per incoming control application; ordinary immunity spends none.
        if (charge.PileCount <= 1) await RemoveAsync(player, charge);
        else await AddCoreAsync(player, charge.ConfigId, (int)Math.Clamp(charge.ExpireMs - MonsterService.NowMs, 1, int.MaxValue),
            charge.SourceId, charge.PileCount - 1, guardianOwned: true);
        return true;
    }

    /// <summary>SpecialStatusConfig 23/24 explicitly says immunity is implemented through BuffConfig categories.
    /// The named standard immunity groups and tag 37 identify control effects; _iType 2 identifies debuffs.
    /// (inferred) Reject the incoming buff before decay, rather than merely hiding its icon.</summary>
    private bool ImmuneTo(IEnumerable<ActiveBuff> active, JsonElement incoming)
    {
        if (GameData.Int(incoming, "_iType") != NegativeBuffType) return false;
        if (active.Any(b => b.ExpireMs > MonsterService.NowMs && b.ShieldDebuffImmunity && b.ShieldRemaining > 0)) return true;
        bool control = GuardianPetRules.Numbers(incoming, "_kBuffCategory").Contains(ControlBuffTag);
        bool displacement = GameData.Int(incoming, "_iBuffID") is 9017 or 9018;
        return active.Any(b => b.ExpireMs > MonsterService.NowMs && _data.Row("BuffConfig", b.ConfigId) is { } row
            && (GameData.Int(row, "_iBuffID") is DebuffImmunityGroup or 9034
                || displacement && GameData.Int(row, "_iBuffID") == 9031
                || control && GameData.Int(row, "_iBuffID") == ControlImmunityGroup));
    }

    internal async Task DispelControlsAsync(GameSession player, bool allDebuffs = false)
    {
        foreach (var buff in player.Buffs.Where(b => !b.IsPassive && _data.Row("BuffConfig", b.ConfigId) is { } row
            && GameData.Int(row, "_iType") == NegativeBuffType && GameData.Int(row, "_iDisperse") != 0
            && (allDebuffs || GuardianPetRules.Numbers(row, "_kBuffCategory").Contains(ControlBuffTag))).ToArray())
            await RemoveAsync(player, buff);
    }

    internal bool UnitForbidden(WorldUnit unit, int ordinal) => _unitBuffs.TryGetValue(unit.Uid, out var buffs)
        && buffs.Any(b => b.ExpireMs > MonsterService.NowMs && b.StateOrdinal > 0
            && _data.Row("SpecialStatusConfig", b.StateOrdinal) is { } state
            && GuardianPetRules.Numbers(state, "_kForbidden").Contains(ordinal));

    internal bool UnitHasGroup(WorldUnit unit, int group) => _unitBuffs.TryGetValue(unit.Uid, out var buffs)
        && buffs.Any(b => b.ExpireMs > MonsterService.NowMs && _data.Row("BuffConfig", b.ConfigId) is { } row
            && GameData.Int(row, "_iBuffID") == group);

    internal ulong UnitStatus(WorldUnit unit) => !_unitBuffs.TryGetValue(unit.Uid, out var buffs) ? 0
        : buffs.Where(b => b.ExpireMs > MonsterService.NowMs && b.StateOrdinal is > 0 and < 33)
            .Aggregate(0UL, (mask, b) => mask | 1UL << (b.StateOrdinal - 1));

    internal ulong UnitForbiddenMask(WorldUnit unit)
    {
        ulong mask = 0;
        for (int ordinal = 1; ordinal < 32; ordinal++)
            if (UnitForbidden(unit, ordinal)) mask |= ForbiddenMask(ordinal);
        return mask;
    }
}
