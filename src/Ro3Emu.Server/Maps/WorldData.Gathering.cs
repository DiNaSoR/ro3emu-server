using System.Text.Json;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Maps;

public sealed partial class WorldData
{
    /// <summary>
    /// Follow scene-editor group -> point -> RefreshInfo -> NPCConfig subtype 12 -> LifeCollectionConfig.
    /// A direct monster point with an empty m_kConfigIds is not a combat spawn.
    /// Point.Id as point_config_id and the selection of target-count authored points are inferred until original-server
    /// captures establish the exact population placement algorithm.
    /// </summary>
    private List<GatheringSpawnPoint> LoadGatheringPoints(JsonElement scene, long sceneEntry)
    {
        var result = new List<GatheringSpawnPoint>();
        if (!scene.TryGetProperty("m_kWorldMonsterSpawnGroups", out var groups) || groups.ValueKind != JsonValueKind.Object
            || !scene.TryGetProperty("m_kWorldMonsterSpawnPoints", out var points) || points.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var pointProperty in points.EnumerateObject())
        {
            var point = pointProperty.Value;
            int groupId = (int)GameData.Int(point, "monsterGroupId");
            if (!groups.TryGetProperty(groupId.ToString(), out var group) || group.ValueKind != JsonValueKind.Object
                || !group.TryGetProperty("useRefreshInfo", out var enabled) || enabled.ValueKind != JsonValueKind.True)
                continue;
            if (point.TryGetProperty("useRefreshInfo", out var pointEnabled) && pointEnabled.ValueKind != JsonValueKind.True)
                continue;
            int refreshId = (int)GameData.Int(point, "m_iRefreshId");
            if (refreshId == 0) refreshId = (int)GameData.Int(group, "m_iRefreshId");
            if (refreshId == 0 || data.Row("RefreshInfo", refreshId) is not { } refresh
                || !refresh.TryGetProperty("_kUnitIDs", out var units) || units.ValueKind != JsonValueKind.Array
                || !point.TryGetProperty("position", out var pos)) continue;

            var candidates = new List<(int NpcId, int Weight)>();
            foreach (var entry in units.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 2) continue;
                int npcId = (int)(GameData.Number(entry[0]) ?? 0);
                int weight = (int)(GameData.Number(entry[1]) ?? 0);
                if (weight <= 0 || data.Row("NPCConfig", npcId) is not { } npc
                    || GameData.Int(npc, "_iNPCType") != 2 || GameData.Int(npc, "_iNPCSubType") != 12
                    || !CurrentFor101(npc)) continue;
                long collectionId = GameData.Int(npc, "_iSubTypeValue");
                if (data.Row("LifeCollectionConfig", collectionId) is not { } collection
                    || !CurrentFor101(collection)
                    || !collection.TryGetProperty("_iMapIds", out var maps) || maps.ValueKind != JsonValueKind.Array
                    || !maps.EnumerateArray().Any(m => GameData.Number(m) == sceneEntry)) continue;
                candidates.Add((npcId, weight));
            }
            if (candidates.Count == 0) continue;
            int id = (int)GameData.Int(point, "Id");
            if (id <= 0 || result.Any(p => p.Id == id)) continue;
            result.Add(new GatheringSpawnPoint(id, groupId, refreshId,
                new MapPoint(WorldNpc.Float(pos, "x"), WorldNpc.Float(pos, "y"), WorldNpc.Float(pos, "z"), WorldNpc.Float(point, "direction")),
                candidates, (int)GameData.Int(refresh, "_iMinExistUnitNum"), (int)GameData.Int(refresh, "_iUnitNum"),
                (int)GameData.Int(refresh, "_iRefreshTime")));
        }
        return result;
    }

    /// <summary>Season-101 row eligibility uses the same 0/all or 101 member rule as LifeJobService.Current.</summary>
    private static bool CurrentFor101(JsonElement row) => !row.TryGetProperty("_kMultiverseArray", out var books)
        || books.ValueKind != JsonValueKind.Array || books.GetArrayLength() == 0
        || books.EnumerateArray().Any(b => GameData.Number(b) == 0 || GameData.Number(b) == 101);
}
