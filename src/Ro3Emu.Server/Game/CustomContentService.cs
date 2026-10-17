using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Hands the client the custom content (content/*.json, <see cref="CustomContent"/>): at role login, before any quest or
/// NPC data, game message <see cref="ContentMsg"/> carries the resolved rows and texts as UTF-8 JSON. The client's own
/// message table has no such id; Ro3Emu.ClientHooks.CustomContent reads it, adds the rows to the client's config tables
/// and keeps a copy so the next start has them before login. Nothing is sent when there is no custom content.
/// </summary>
public sealed class CustomContentService(GameData data, ILogger<CustomContentService> log)
{
    /// <summary>Server -> client hook. Must equal Ro3Emu.ClientHooks.CustomContent.MsgId.</summary>
    public const uint ContentMsg = 77020;

    private byte[]? _payload;

    public byte[] Payload()
    {
        if (_payload is { } built) return built;
        var payload = data.Custom.IsEmpty ? [] : data.Custom.ClientPayload(data.Table);
        if (payload.Length > 0) log.LogInformation("Custom content payload: {Bytes} bytes", payload.Length);
        return _payload = payload;
    }

    public Task SendAsync(GameSession session)
    {
        var payload = Payload();
        return payload.Length == 0 ? Task.CompletedTask : session.SendGameRawAsync(ContentMsg, payload);
    }
}
