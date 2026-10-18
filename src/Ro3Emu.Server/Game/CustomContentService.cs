using System.Text.Json;
using System.Text.Json.Nodes;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Hands the client the custom content (content/*.json, <see cref="CustomContent"/>): at role login, before any quest or
/// NPC data, game message <see cref="ContentMsg"/> carries the resolved rows and texts as UTF-8 JSON. The client's own
/// message table has no such id; Ro3Emu.ClientHooks.CustomContent reads it, adds the rows to the client's config tables
/// and keeps a copy so the next start has them before login. Nothing is sent when there is no custom content.
/// </summary>
public sealed class CustomContentService(GameData data, WorldData world, ILogger<CustomContentService> log)
{
    /// <summary>Server -> client hook. Must equal Ro3Emu.ClientHooks.CustomContent.MsgId.</summary>
    public const uint ContentMsg = 77020;

    private byte[]? _payload;

    public byte[] Payload()
    {
        if (_payload is { } built) return built;
        var payload = data.Custom.IsEmpty ? [] : data.Custom.ClientPayload(data.Table, SceneEditorNpcs());
        if (payload.Length > 0) log.LogInformation("Custom content payload: {Bytes} bytes", payload.Length);
        return _payload = payload;
    }

    /// <summary>The placed custom NPCs' kNpcList entries, with the scene-editor module of their map.</summary>
    private List<(string Module, JsonObject Entry)> SceneEditorNpcs()
    {
        var result = new List<(string, JsonObject)>();
        foreach (long mapId in data.Custom.Npcs.Select(n => n.Map).Distinct())
        {
            if ((data.Row("MapConfig", mapId) ?? data.Row("DungeonConfig", mapId)) is not { } row
                || !row.TryGetProperty("_kResSceneEditorName", out var name) || name.GetString() is not { Length: > 0 } module
                || world.Map(mapId) is not { } map) continue;
            foreach (var npc in map.Npcs)
                if (npc.Entry.ValueKind == JsonValueKind.Object && npc.Entry.TryGetProperty("CustomContent", out _))
                    result.Add((module, JsonNode.Parse(npc.Entry.GetRawText())!.AsObject()));
        }
        return result;
    }

    public Task SendAsync(GameSession session)
    {
        var payload = Payload();
        return payload.Length == 0 ? Task.CompletedTask : session.SendGameRawAsync(ContentMsg, payload);
    }
}
