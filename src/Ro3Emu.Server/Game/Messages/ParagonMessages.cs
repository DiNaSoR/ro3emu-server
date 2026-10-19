using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>83101-83105 PARAGONLEVEL_* (<see cref="ParagonService"/>): the role's own stars and Star Cores.</summary>
public sealed class ParagonMessages(ParagonService paragon) : Messaging.IMessageModule
{
    public void Map(Messaging.MessageRoutes routes)
    {
        routes.Map(ParagonService.Allocate, "paragon", (s, _, body) =>
            paragon.AllocateAsync(s, ParagonLevelAllocatePointReq.Parser.ParseFrom(body.Span)));
        routes.Map(ParagonService.Install, "paragon", (s, _, body) =>
            paragon.InstallAsync(s, ParagonLevelRuneInstallReq.Parser.ParseFrom(body.Span)));
        routes.Map(ParagonService.Remove, "paragon", (s, _, body) =>
            paragon.RemoveAsync(s, ParagonLevelRuneRemoveReq.Parser.ParseFrom(body.Span)));
        routes.Map(ParagonService.ResetAll, "paragon", (s, _, _) => paragon.ResetAllAsync(s));
        routes.Map(ParagonService.ResetSingle, "paragon", (s, _, body) =>
            paragon.ResetSingleAsync(s, ParagonLevelResetSingleReq.Parser.ParseFrom(body.Span)));
    }
}
