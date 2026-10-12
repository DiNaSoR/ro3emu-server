using Romsg;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Game;

/// <summary>Detached 5067 plan validation for the composed Build window.</summary>
public sealed partial class CharacterService
{
    internal Task GenreAppearanceChangedAsync(GameSession session) => EquipmentAppearanceChangedAsync(session);

    /// <summary>Validate a selected allocation before touching live progress (inferred bounds/affordability).</summary>
    internal int StageStatPlan(BaseAttrInfo state, RoleProgress progress, int index)
    {
        if (!state.AllPlanBaseProperty.TryGetValue(index, out var plan) || index is < 0 or >= StatPlanCount
            || plan.BaseProperty.Count != 6 || plan.BaseProperty.Keys.Any(k => k is < 1 or > 6)) return ErrorCodes.Parameter;
        var values = Enumerable.Range(1, 6).Select(k => (int)plan.BaseProperty[k]).ToArray();
        if (values.Any(v => v is < 1 or > MaxStat)) return ErrorCodes.Parameter;
        if (values.Sum(StatCost) > TotalPoints(progress)) return ErrorCodes.StatPointsShort;
        if (index != state.CurPlanIndex)
        {
            var outgoing = CurrentPlan(progress, state.AllPlanBaseProperty[state.CurPlanIndex]);
            state.AllPlanBaseProperty[state.CurPlanIndex] = outgoing;
        }
        state.CurPlanIndex = index;
        values.CopyTo(progress.Stats, 0);
        return 0;
    }

    internal Task SyncStatPlansAsync(GameSession session) => SendStatPlanAsync(session);

    internal static RoleProgress GenreCloneProgress(RoleProgress p) => CloneProgress(p);
}
