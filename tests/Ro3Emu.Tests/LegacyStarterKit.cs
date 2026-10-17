using System.Runtime.CompilerServices;

namespace Ro3Emu.Tests;

/// <summary>
/// Most suites were written when every new character got a Novice Dagger and a Cotton Shirt in the bag (they pick the
/// dagger from the first bag sync), so test hosts keep that kit through ServerOptions.StarterKit. StarterKitTests
/// covers the shipped default, which has no gear.
/// </summary>
internal static class LegacyStarterKit
{
    [ModuleInitializer]
    internal static void Apply()
    {
        (int Entry, int Count)[] kit = [(1000000, 20), (2010121, 1), (2550120, 1), (1, 1000)];
        for (int i = 0; i < kit.Length; i++)
        {
            Environment.SetEnvironmentVariable($"Ro3Emu__StarterKit__{i}__Entry", kit[i].Entry.ToString());
            Environment.SetEnvironmentVariable($"Ro3Emu__StarterKit__{i}__Count", kit[i].Count.ToString());
        }
    }
}
