using System.Runtime.CompilerServices;

namespace Ro3Emu.Tests;

/// <summary>
/// Most suites were written against a starter kit that lands in the bag (they pick the dagger from the bag sync and
/// equip it themselves), so test hosts keep ServerOptions.WearStarterGear off unless a test turns it on;
/// StarterGearTests covers the worn default.
/// </summary>
internal static class StarterGearDefault
{
    [ModuleInitializer]
    internal static void Apply() => Environment.SetEnvironmentVariable("Ro3Emu__WearStarterGear", "false");
}
