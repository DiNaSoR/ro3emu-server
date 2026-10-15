#pragma warning disable CS1998 // arms that only send or return keep the uniform async shape
using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Adventure Group 34000-34023 (AdventureGroupService). They keep the dispatcher's default Realm reservation
/// (MessageRoutes.Admit): an operation changes several roles' groups, quests and instance at once.
/// </summary>
public sealed class AdventureGroupMessages(AdventureGroupService adventureGroups) : Messaging.IMessageModule
{
    public void Map(Messaging.MessageRoutes routes)
    {
        const string owner = "advguild";
        routes.Map(AdventureGroupService.Create, owner, async (session, msgId, body) =>
            await adventureGroups.CreateAsync(session, CreateAdventureGroupReq.Parser.ParseFrom(body.Span)));
        routes.Map(AdventureGroupService.Delete, owner, async (session, msgId, body) =>
            await adventureGroups.DeleteAsync(session));
        routes.Map(AdventureGroupService.HelperList, owner, async (session, msgId, body) =>
            await adventureGroups.HelperListAsync(session, PullHelperAdventureGroupListReq.Parser.ParseFrom(body.Span)));
        routes.Map(AdventureGroupService.HelperApplicants, owner, async (session, msgId, body) =>
            await adventureGroups.HelperApplicantsAsync(session, PullHelperAdventureGroupApplyerReq.Parser.ParseFrom(body.Span)));
        routes.Map(AdventureGroupService.HelperOpe, owner, async (session, msgId, body) =>
            await adventureGroups.HelperOpeAsync(session, HelperAdventureGroupApplyerOpeReq.Parser.ParseFrom(body.Span)));
        routes.Map(AdventureGroupService.StartBrothers, owner, async (session, msgId, body) =>
            await adventureGroups.StartBrothersAsync(session));
        routes.Map(AdventureGroupService.Edit, owner, async (session, msgId, body) =>
            await adventureGroups.EditAsync(session, AdventureGroupEditReq.Parser.ParseFrom(body.Span)));
        routes.Map(AdventureGroupService.NormalList, owner, async (session, msgId, body) =>
            await adventureGroups.NormalListAsync(session, PullAdventureGroupReq.Parser.ParseFrom(body.Span)));
        routes.Map(AdventureGroupService.NormalOpe, owner, async (session, msgId, body) =>
            await adventureGroups.NormalOpeAsync(session, NormalAdventureGroupOpeReq.Parser.ParseFrom(body.Span)));
        routes.Map(AdventureGroupService.MemberData, owner, async (session, msgId, body) =>
            await adventureGroups.MemberDataAsync(session));
        routes.Map(AdventureGroupService.CheckBrothers, owner, async (session, msgId, body) =>
            await adventureGroups.CheckBrothersAsync(session));
        routes.Map(AdventureGroupService.EnterRitual, owner, async (session, msgId, body) =>
            await adventureGroups.EnterRitualAsync(session));
        routes.Map(AdventureGroupService.SelectSapling, owner, async (session, msgId, body) =>
            await adventureGroups.SelectSaplingAsync(session, SelectSaplingReq.Parser.ParseFrom(body.Span)));
        routes.Map(AdventureGroupService.Declare, owner, async (session, msgId, body) =>
            await adventureGroups.DeclareAsync(session, AdventureGroupDeclarationReq.Parser.ParseFrom(body.Span)));
        routes.Map(AdventureGroupService.Convene, owner, async (session, msgId, body) =>
            await adventureGroups.ConveneAsync(session));
        routes.Map(AdventureGroupService.ConfirmConvene, owner, async (session, msgId, body) =>
            await adventureGroups.ConfirmConveneAsync(session, AdventureGroupConfirmConveneReq.Parser.ParseFrom(body.Span)));
        routes.Map(AdventureGroupService.Commemorate, owner, async (session, msgId, body) =>
            await adventureGroups.CommemorateAsync(session));
        routes.Map(AdventureGroupService.Water, owner, async (session, msgId, body) =>
            await adventureGroups.WaterAsync(session, WateringSaplingReq.Parser.ParseFrom(body.Span)));
        routes.Map(AdventureGroupService.SaplingData, owner, async (session, msgId, body) =>
            await adventureGroups.SaplingDataAsync(session, GetSaplingDataReq.Parser.ParseFrom(body.Span)));
        routes.Map(AdventureGroupService.Detail, owner, async (session, msgId, body) =>
            await adventureGroups.DetailAsync(session, GetAdventureGroupDetailDataReq.Parser.ParseFrom(body.Span)));
    }
}
