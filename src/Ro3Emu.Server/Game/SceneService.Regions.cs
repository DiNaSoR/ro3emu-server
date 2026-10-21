using System.Globalization;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class SceneService
{
    /// <summary>
    /// 31209 names a region of the player's current map. Returns it when its first action is server-hosted
    /// (ActionConfig _iEXECHost 1 or 2): the client only reports such regions instead of running them
    /// (re/clientB/lua-src/Logic/Asset/Systems/LC_System_Task.lua:2257-2321). The player must stand near it
    /// (<see cref="NearRegion"/>).
    /// </summary>
    public MapRegion? ServerHostedRegion(GameSession s, long areaId)
    {
        if (s.SceneLoading || worldData.Map(s.SceneEntry)?.Regions.TryGetValue(areaId, out var region) != true) return null;
        string op = region!.Actions.Split('|')[0];
        if (!int.TryParse(op, NumberStyles.Integer, CultureInfo.InvariantCulture, out int action)
            || data.Row("ActionConfig", action) is not { } row || GameData.Int(row, "_iEXECHost") is not (1 or 2)) return null;
        return NearRegion(s, region) ? region : null;
    }

    /// <summary>
    /// The longest walk report gap a region check allows for: twice the client's 0.9 s WALK keep-alive
    /// (GameAoiSink.WalkDurationMs), so one late report still passes (inferred).
    /// </summary>
    private const double RegionWalkLagSeconds = 2;

    /// <summary>
    /// The player reported entering <paramref name="region"/> (31209): within the region's larger side plus 5 m of its
    /// centre (the region is a width x length box around the centre in the scene editor; inferred tolerance), plus the
    /// distance a walking player can have covered since its last walk report. The client reports entry the moment it
    /// crosses the edge (LC_AreaHelper.CheckInArea), while the server's position is the last WALK, up to a keep-alive
    /// behind: at the default speed (700, 7 m/s) that is about 6 m, more than the fixed 5 m, so entries into a circle
    /// region (whose edge is its radius from the centre) were refused whenever the last report was far enough behind.
    /// </summary>
    public static bool NearRegion(GameSession s, MapRegion region)
    {
        float dx = s.Position.X - region.X, dz = s.Position.Z - region.Z;
        float reach = Math.Max(region.Width, region.Length) + 5 + s.WalkReportLag(RegionWalkLagSeconds);
        return dx * dx + dz * dz <= reach * reach;
    }
}
