using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>The Tavern rooms 82481-82514 (<see cref="TavernService"/>). Rooms are shared by their members, so every
/// request keeps the default Realm reservation.</summary>
public sealed class TavernMessages(TavernService tavern) : Messaging.IMessageModule
{
    public void Map(Messaging.MessageRoutes routes)
    {
        routes.Map(TavernService.Create, "tavern", (s, _, body) => tavern.CreateAsync(s, CreateBarRoomReq.Parser.ParseFrom(body.Span)));
        routes.Map(TavernService.Enter, "tavern", (s, _, body) => tavern.EnterAsync(s, EnterBarRoomReq.Parser.ParseFrom(body.Span)));
        routes.Map(TavernService.Exit, "tavern", (s, _, body) => tavern.ExitAsync(s, ExitBarRoomReq.Parser.ParseFrom(body.Span)));
        routes.Map(TavernService.Get, "tavern", (s, _, _) => tavern.GetAsync(s));
        routes.Map(TavernService.Invite, "tavern", (s, _, body) => tavern.InviteAsync(s, InviteEnterBarRoomReq.Parser.ParseFrom(body.Span)));
        routes.Map(TavernService.InviteResponse, "tavern", (s, _, body) => tavern.RespondInviteAsync(s, ResponseInviteEnterBarRoomReq.Parser.ParseFrom(body.Span)));
        routes.Map(TavernService.SetInfo, "tavern", (s, _, body) => tavern.SetInfoAsync(s, SetBarRoomInfoReq.Parser.ParseFrom(body.Span)));
        routes.Map(TavernService.Transfer, "tavern", (s, _, body) => tavern.TransferAsync(s, TransferOwnerReq.Parser.ParseFrom(body.Span)));
        routes.Map(TavernService.GmSetInfo, "tavern", (s, _, body) => tavern.GmSetInfoAsync(s, SetGmBarRoomInfoReq.Parser.ParseFrom(body.Span)));
        routes.Map(TavernService.ApplyMic, "tavern", (s, _, body) => tavern.ApplyMicAsync(s, ApplyOpenMicReq.Parser.ParseFrom(body.Span)));
        routes.Map(TavernService.HandleMic, "tavern", (s, _, body) => tavern.HandleMicAsync(s, OwnerHandleOpenMicReq.Parser.ParseFrom(body.Span)));
        routes.Map(TavernService.DownMic, "tavern", (s, _, body) => tavern.DownMicAsync(s, DownMicReq.Parser.ParseFrom(body.Span)));
        routes.Map(TavernService.MicMode, "tavern", (s, _, body) => tavern.MicModeAsync(s, ModifyControlMicTypeReq.Parser.ParseFrom(body.Span)));
        routes.Map(TavernService.MicOrder, "tavern", (s, _, body) => tavern.MicOrderAsync(s, SetMicOrderReq.Parser.ParseFrom(body.Span)));
        routes.Map(TavernService.Forbid, "tavern", (s, _, body) => tavern.ForbidAsync(s, ForbiddenUserReq.Parser.ParseFrom(body.Span)));
        routes.Map(TavernService.CancelForbid, "tavern", (s, _, body) => tavern.CancelForbidAsync(s, CanecelForbiddenUserReq.Parser.ParseFrom(body.Span)));
        routes.Map(TavernService.Kick, "tavern", (s, _, body) => tavern.KickAsync(s, KickUserReq.Parser.ParseFrom(body.Span)));
        routes.Map(TavernService.CancelKick, "tavern", (s, _, body) => tavern.CancelKickAsync(s, CancelKickUserReq.Parser.ParseFrom(body.Span)));
        routes.Map(TavernService.ShutUpAll, "tavern", (s, _, body) => tavern.ShutUpAllAsync(s, ShutUpAllReq.Parser.ParseFrom(body.Span).BarRoomGuid, closed: true));
        routes.Map(TavernService.CancelShutUpAll, "tavern", (s, _, body) => tavern.ShutUpAllAsync(s, CancelShutUpAllReq.Parser.ParseFrom(body.Span).BarRoomGuid, closed: false));
        routes.Map(TavernService.MutePlayer, "tavern", (s, _, body) => tavern.MutePlayerAsync(s, MuteBarRoomPlayerReq.Parser.ParseFrom(body.Span)));
        routes.Map(TavernService.Gift, "tavern", (s, _, body) => tavern.GiftAsync(s, PresentGiftReq.Parser.ParseFrom(body.Span)));
    }
}
