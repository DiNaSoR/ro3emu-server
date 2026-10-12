using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Every hit roll lands; damage float, crit and procs stay random. For tests that prepare a lethal hit and
/// are not about the hit chance (a FLEE miss there is noise, not a finding).</summary>
internal sealed class SureHitRolls : CombatRolls
{
    public override double Hit() => 0;

    public static void Install(WebApplication app) => app.Services.GetRequiredService<CombatService>().Rolls = new SureHitRolls();
}
