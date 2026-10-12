using System.Globalization;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class SceneService
{
    /// <summary>
    /// 31209 names a region of the player's current map. Returns it when its first action is server-hosted
    /// (ActionConfig _iEXECHost 1 or 2): the client only reports such regions instead of running them
    /// (re/clientB/lua-src/Logic/Asset/Systems/LC_System_Task.lua:2257-2321). The player must stand near it: within the
    /// region's larger side plus 5 m of its centre (inferred tolerance for the walk report's lag; the region is a
    /// width x length box around the centre in the scene editor).
    /// </summary>
    public MapRegion? ServerHostedRegion(GameSession s, long areaId)
    {
        if (s.SceneLoading || worldData.Map(s.SceneEntry)?.Regions.TryGetValue(areaId, out var region) != true) return null;
        string op = region!.Actions.Split('|')[0];
        if (!int.TryParse(op, NumberStyles.Integer, CultureInfo.InvariantCulture, out int action)
            || data.Row("ActionConfig", action) is not { } row || GameData.Int(row, "_iEXECHost") is not (1 or 2)) return null;
        float dx = s.Position.X - region.X, dz = s.Position.Z - region.Z, reach = Math.Max(region.Width, region.Length) + 5;
        return dx * dx + dz * dz <= reach * reach ? region : null;
    }
}
