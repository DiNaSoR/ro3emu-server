using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// C/D 15: logins and scene loads no longer run the realm stall maintenance inside their scopes. A first-visited
/// scene's system stalls are populated by their own operation, and the one-second maintenance does the rest. A test
/// that cuts receipts or AOI right after entering the world settles it first, so those realm receipts land before its
/// opening cut. A maintenance with nothing to change commits nothing.
/// </summary>
internal static class WorldSettle
{
    public static Task StallsAsync(IServiceProvider services) => services.GetRequiredService<GameLoop>()
        .InvokeAsync(() => services.GetRequiredService<StallsService>().RefreshWorldMaintenanceAsync());
}
