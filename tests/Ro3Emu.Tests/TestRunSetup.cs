using System.Runtime.CompilerServices;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Tests;

/// <summary>
/// Test-run settings that apply before any test starts. Every test starts its own server, so work a server does once
/// is paid ~2,000 times per run: sharing the parsed client tables and one gate key saves each server 2-3 s and ~250 MB
/// of parsing and 0.2-0.6 s of key generation (see <see cref="ProcessCache"/>), and fewer password hash rounds save
/// ~10% of the run's CPU (every bot logs in; each test's database is new, so no stored hash uses the full count).
/// </summary>
internal static class TestRunSetup
{
    [ModuleInitializer]
    internal static void Enable()
    {
        ProcessCache.Enabled = true;
        AccountHash.Iterations = 1_000;
    }
}
