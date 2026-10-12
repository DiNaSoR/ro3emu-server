#pragma warning disable CS1998 // arms that only send or return keep the uniform async shape
using Google.Protobuf;
using Romsg;

namespace Ro3Emu.Server.Game;

public sealed partial class GameMessageHandler
{
    private void MapCoreRoutes(Messaging.MessageRoutes routes)
    {
        routes.Map(GuildChallengeService.Notify, "handler", async (session, msgId, body) =>
        {
        });
        routes.Map(2001, "handler", async (session, msgId, body) =>
        {
            var order = CreateOrderReq.Parser.ParseFrom(body.Span);
            await session.SendGameAsync(2001, new CreateOrderResp { Result = ErrorCodes.FeatureUnavailable,
                ChargeId = order.ChargeId, ChargeType = order.ChargeType });
        });
        routes.Map(2002, "handler", async (session, msgId, body) =>
        {
            await session.SendGameAsync(2002, new PayResultResp { Result = ErrorCodes.FeatureUnavailable,
                OrderId = PayResultReq.Parser.ParseFrom(body.Span).OrderId });
        });
        routes.Map(2003, "handler", async (session, msgId, body) =>
        {
            await session.SendGameAsync(2003, new GetRechargeFirstAwardResp { Result = ErrorCodes.FeatureUnavailable });
        });
        routes.Map(2004, "handler", async (session, msgId, body) =>
        {
        });
        routes.Map(2005, "handler", async (session, msgId, body) =>
        {
            await session.SendGameAsync(2005, new ItemExchangeResp { Result = ErrorCodes.FeatureUnavailable });
        });
        routes.Map(WorldCoreService.TeleportObject, "handler", async (session, msgId, body) =>
        {
            await WorldCoreService.ErrorAsync(session, msgId, ErrorCodes.Parameter);
        });
        routes.Map(31204, "tasks", async (session, msgId, body) =>
        {
            await tasks.ResetAsync(session, TaskResetReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(31212, "tasks", async (session, msgId, body) =>
        {
            await tasks.NpcChatAsync(session, TaskNpcChatReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(31213, "tasks", async (session, msgId, body) =>
        {
            await tasks.FinishPydAsync(session, FinishPYDTaskReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(31215, "tasks", async (session, msgId, body) =>
        {
            await tasks.SelectBranchAsync(session, SelectTaskQuestBranchReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(31217, "tasks", async (session, msgId, body) =>
        {
            await tasks.LuckyTurnAsync(session, LuckyTurnReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(30016, "tasks", async (session, msgId, body) =>
        {
            await tasks.TrackTeleportAsync(session, TaskTrackTeleportReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(LifeJobService.CheckCollect, "handler", async (session, msgId, body) =>
        {
            // msgtable supplies romsg.null, not a collection-id request; no mutation or cast is inferred.
            await session.SendGameAsync(LifeJobService.CheckCollect, new CheckCanCollectNtf { Result = ErrorCodes.Parameter });
        });
        routes.Map(MsgId.LoginRoleCreate, "handler", async (session, msgId, body) =>
        {
            await CreateRoleAsync(session, AccountLoginCreateRoleReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(MsgId.LoginRoleSelect, "handler", async (session, msgId, body) =>
        {
            await SelectRoleAsync(session, AccountLoginSelectRoleReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(DungeonService.Result, "handler", async (session, msgId, body) =>
        {
        });
        routes.Map(DungeonService.ResultNotify, "handler", async (session, msgId, body) =>
        {
        });
        routes.Map(DungeonService.Event, "handler", async (session, msgId, body) =>
        {
        });
        routes.Map(DungeonService.AwardNotify, "handler", async (session, msgId, body) =>
        {
        });
        routes.Map(TowerService.Sweep, "handler", async (session, msgId, body) =>
        {
            await session.SendGameAsync(TowerService.Sweep, new ClimTowerSweepRes { Result = ErrorCodes.FeatureUnavailable });
        });
        routes.Map(TowerService.End, "handler", async (session, msgId, body) =>
        {
        });
        routes.Map(WildAfkService.SaveCharge, "handler", async (session, msgId, body) =>
        {
            if (session.WildAfk is { } state) state.PowerSaving = WildAfkSaveChargeReq.Parser.ParseFrom(body.Span).IsSaveCharge;
        });
        routes.Map(WildAfkService.OfflineReward, "handler", async (session, msgId, body) =>
        {
            await session.SendGameAsync(WildAfkService.OfflineReward, new ReceiveOfflineRewardRsp { Result = ErrorCodes.AfkNoSummary });
        });
        routes.Map(WildAfkService.DungeonStart, "handler", async (session, msgId, body) =>
        {
            await session.SendGameAsync(WildAfkService.DungeonStart, new AfkDungeonStartRsp { Result = ErrorCodes.FeatureUnavailable });
        });
        routes.Map(EquipGetRank, "handler", async (session, msgId, body) =>
        {
            // Equipment ranking in tooltips: both ids echoed (an empty reply caches item 0), unranked.
            var rank = QueryEquipRankReq.Parser.ParseFrom(body.Span);
            await session.SendGameAsync(EquipGetRank, new QueryEquipRankRes { Rankid = rank.Rankid, Itemuid = rank.Itemuid, Rank = 0 });
        });
        routes.Map(AuctionService.Sync, "handler", async (session, msgId, body) =>
        {
        });
        routes.Map(AuctionService.Changed, "handler", async (session, msgId, body) =>
        {
        });
        routes.Map(AuctionService.DungeonDone, "handler", async (session, msgId, body) =>
        {
        });
        routes.Map(AuctionService.DungeonChanged, "handler", async (session, msgId, body) =>
        {
        });
        routes.Map(AuctionService.HasAuto, "handler", async (session, msgId, body) =>
        {
        });
        routes.Map(AuctionService.Reddot, "handler", async (session, msgId, body) =>
        {
        });
        routes.Map(MsgId.Walk, "handler", async (session, msgId, body) =>
        {
            await WalkAsync(session, WalkReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(MsgId.SaveSetting, "handler", async (session, msgId, body) =>
        {
            await SaveSettingAsync(session, SaveSettingReq.Parser.ParseFrom(body.Span), body);
        });
        routes.Map(MsgId.Logout, "handover", async (session, msgId, body) =>
        {
            await handover.Begin(session);
            await session.SendGameAsync(MsgId.Logout, new LogoutResp());
        });
        routes.Map(MsgId.PreSwitchRole, "handler", async (session, msgId, body) =>
        {
            // The client then calls the avatar RPC SwitchToAccount (see SwitchToAccountAsync).
            await session.SendGameAsync(MsgId.PreSwitchRole, new PreSwitchRoleResp { Result = 0 });
        });
        routes.Map(MsgId.ClientEnterMainUi, "handler", async (session, msgId, body) =>
        {
            // newLv 0: no queued level-up popup.
            await session.SendGameAsync(MsgId.ClientEnterMainUi, new ClientEnterRes { NewLv = 0 });
        });
        routes.Map(MsgId.UploadClientSdkInfo, "handler", async (session, msgId, body) =>
        {
        });
        routes.Map(BuffService.ClientRemoveBuff, "buffs", async (session, msgId, body) =>
        {
            await buffs.RemoveByRequestAsync(session, ClientRemoveBuffReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(BuffService.ImmuneDeathConfirm, "buffs", async (session, msgId, body) =>
        {
            await buffs.ConfirmImmuneDeathAsync(session, PlayerConfirmImmuneDeadReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(SceneService.ClientNpcInteract, "tasks", async (session, msgId, body) =>
        {
            await tasks.OnDialogAsync(session, ClientNpcInteractReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(SceneService.ClientEnterArea, "tasks", async (session, msgId, body) =>
        {
            var area = ClientEnterAreaReq.Parser.ParseFrom(body.Span);
            await tasks.OnAreaAsync(session, area);
            // A region whose action is server-hosted (613 EnterDungeon_Sever) is the server's to run.
            if (scene.ServerHostedRegion(session, area.AreaId) is { } region) await dungeons.EnterFromRegionAsync(session, region);
        });
        routes.Map(TaskService.TriggerTaskEvent, "tasks", async (session, msgId, body) =>
        {
            await tasks.OnClientInteractAsync(session, TriggerTaskEventReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(TaskService.PlotActingEnd, "tasks", async (session, msgId, body) =>
        {
            var plotEnd = PlotActingEndReq.Parser.ParseFrom(body.Span);
            await tasks.OnPlotEndAsync(session, plotEnd);
            await dungeons.PlotEndedAsync(session, plotEnd.PlotId); // a scripted scene's story
        });
        // A story's end runs both client actions of its plot: 601 VideoPerformEndClient (84011 above) and 150
        // PlotActingTrigger (LC_TaskHelper.PlotActingTriggerReq, 84012). Both are fire-and-forget: no client code
        // waits on their NullArg response type, so neither is answered.
        routes.Map(DungeonService.PlotSceneEvent, "tasks", async (session, msgId, body) =>
            await dungeons.PlotEndedAsync(session, PlotActingTriggerReq.Parser.ParseFrom(body.Span).PlotId));
        routes.Map(SceneService.DialogueActionTrigger, "handler", async (session, msgId, body) =>
        {
        });
        routes.Map(TaskService.ExitCutSceneState, "tasks", async (session, msgId, body) =>
        {
            await tasks.ExitCutSceneAsync(session);
        });
        routes.Map(TaskService.TaskTrack, "tasks", async (session, msgId, body) =>
        {
            await tasks.TrackAsync(session, TaskTrackReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(TaskService.GoodsStack, "tasks", async (session, msgId, body) =>
        {
            await tasks.GoodsStackAsync(session);
        });
        routes.Map(TaskService.TaskFollow, "tasks", async (session, msgId, body) =>
        {
            await tasks.FollowAsync(session, TaskFollowReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(TaskService.TaskAccept, "tasks", async (session, msgId, body) =>
        {
            await tasks.AcceptAsync(session, TaskAcceptReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(TaskService.TaskDelivery, "tasks", async (session, msgId, body) =>
        {
            await tasks.DeliverAsync(session, TaskDeliveryReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(TaskService.TaskGiveUp, "tasks", async (session, msgId, body) =>
        {
            await tasks.GiveUpAsync(session, TaskGiveUpReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GmCommands.GeneralProcGm, "gm", async (session, msgId, body) =>
        {
            await gm.PanelAsync(session, GeneralGMProcReq.Parser.ParseFrom(body.Span));
        });
        // The menu's Feedback button (Ro3Emu.ClientHooks.FeedbackButton): the report text as UTF-8.
        routes.Map(FeedbackService.SubmitMsg, "feedback", async (session, msgId, body) =>
        {
            if (session.Role == null) return;
            var text = System.Text.Encoding.UTF8.GetString(body.Span[..Math.Min(body.Length, 16 * 1024)]);
            await chat.Feedback.SubmitAsync(session, text, "button");
        });
    }
}
