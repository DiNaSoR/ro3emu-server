// Generated once from the former `switch (msgId)` of GameMessageHandler.HandleCoreAsync.
// Ordinary source since: a new message is mapped once, in the domain file GameMessageHandler.Routes.<Domain>.cs.
#pragma warning disable CS1998 // arms that only send or return keep the uniform async shape
using Google.Protobuf;
using Romsg;

namespace Ro3Emu.Server.Game;

public sealed partial class GameMessageHandler
{
    // Routes are registered by domain (GameMessageHandler.Routes.<Domain>.cs); a message id is mapped once.
    private void MapRoutes(Messaging.MessageRoutes routes)
    {
        MapCoreRoutes(routes);
        MapSocialRoutes(routes);
        MapCharacterRoutes(routes);
        MapWorldRoutes(routes);
        MapActivitiesRoutes(routes);
        MapEconomyRoutes(routes);
        MapAdventureGroupRoutes(routes);
    }

    /// <summary>The former default arm: a message no route claims.</summary>
    private async Task UnroutedAsync(GameSession session, uint msgId, ReadOnlyMemory<byte> body)
    {
        Ro3Emu.Server.Diagnostics.MessageCoverage.Unanswered(msgId);
        // Unknown to the emulator: an empty response of the right type keeps the client's 10 s request
        // timeouts (enmu_ForcedReqWhite) and waiting UIs from tripping.
        var reply = catalog.EmptyResponse(msgId);
        log.LogInformation("Game msg {MsgId} {Name} from {Account} not handled{Reply}: {Body}",
            msgId, catalog.Name(msgId), session.Account, reply != null ? " (empty reply)" : "", catalog.Describe(msgId, body));
        if (reply != null) await session.SendGameAsync(msgId, reply);
    }
}
