#pragma warning disable CS1998 // arms that only send or return keep the uniform async shape
using Google.Protobuf;
using Romsg;

namespace Ro3Emu.Server.Game;

public sealed partial class GameMessageHandler
{
    private void MapWorldRoutes(Messaging.MessageRoutes routes)
    {
        routes.Map(1031, "common", async (session, msgId, body) =>
        {
            await common.TransmitAsync(session, GMCommandTransmitReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(1034, "common", async (session, msgId, body) =>
        {
            await common.InspectAsync(session, InspectCommandReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(1027, "common", async (session, msgId, body) =>
        {
            await common.OnlineHeartAsync(session);
        });
        routes.Map(30310, "common", async (session, msgId, body) =>
        {
            await common.LegacyProcAsync(session, GMProcReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(32703, "interaction", async (session, msgId, body) =>
        {
            await interaction.UseActionAsync(session, UseInteractActionReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(32708, "interaction", async (session, msgId, body) =>
        {
            await interaction.ChangeSeatAsync(session, ChangeVehicleSeatReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(31074, "interaction", async (session, msgId, body) =>
        {
            await interaction.SyncVehicleAsync(session, SyncVehicleReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(WorldCoreService.PlayerDetail, "worldCore", async (session, msgId, body) =>
        {
            await worldCore.DetailAsync(session, GetPlayerInSceneDetailReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(WorldCoreService.SceneQueue, "worldCore", async (session, msgId, body) =>
        {
            await worldCore.QueueAsync(session, SceneQueuingReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(WorldCoreService.QueueCancel, "worldCore", async (session, msgId, body) =>
        {
            await worldCore.CancelQueueAsync(session, SceneQueuingCancel.Parser.ParseFrom(body.Span));
        });
        routes.Map(WorldCoreService.QueryNpc, "worldCore", async (session, msgId, body) =>
        {
            await worldCore.QueryNpcAsync(session, QueryCurrentNpcReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(WorldCoreService.AoiUnview, "worldCore", async (session, msgId, body) =>
        {
            await worldCore.UnviewAsync(session, SyncAoIUnviewReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(WorldCoreService.MonitorCustom, "worldCore", async (session, msgId, body) =>
        {
            await worldCore.MonitorAsync(session, MonitorCustomDataReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(WorldCoreService.PerformanceWatch, "worldCore", async (session, msgId, body) =>
        {
            await worldCore.WatchAsync(session, ScenePerformaceWatchReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(WorldCoreService.StuckEscape, "worldCore", async (session, msgId, body) =>
        {
            await worldCore.EscapeAsync(session, StuckEscapeReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(WorldCoreService.RecoverLeave, "worldCore", async (session, msgId, body) =>
        {
            await worldCore.RecoverAsync(session);
        });
        routes.Map(WorldCoreService.ReliveHelp, "worldCore", async (session, msgId, body) =>
        {
            await worldCore.HelpAsync(session, EntityReliveHelpReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(WorldCoreService.ReliveAccept, "worldCore", async (session, msgId, body) =>
        {
            await worldCore.AcceptReviveAsync(session, EntityReliveInviteAcceptReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(WorldCoreService.ReliveItem, "worldCore", async (session, msgId, body) =>
        {
            await worldCore.ItemReviveAsync(session);
        });
        routes.Map(WorldCoreService.FlySwitch, "worldCore", async (session, msgId, body) =>
        {
            await worldCore.FlySwitchAsync(session, FlySwitchReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(WorldCoreService.FlyMove, "worldCore", async (session, msgId, body) =>
        {
            await worldCore.FlyMoveAsync(session, FlyMoveReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(WorldCoreService.FreeFall, "worldCore", async (session, msgId, body) =>
        {
            await worldCore.FreeFallAsync(session, FreeFallJumpReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(MsgId.PlayerAsset, "worldCore", async (session, msgId, body) =>
        {
            await worldCore.AssetsAsync(session, PlayerAssetReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(MsgId.SceneLoaded, "scene", async (session, msgId, body) =>
        {
            var loadedRole = session.Role;
            var loadedAttachment = session.Attachment;
            long sceneGeneration = session.SceneGeneration;
            session.WorldState.NameBoardLimit = Math.Min(SceneLoadedReq.Parser.ParseFrom(body.Span).NameBoardMaxCount, 1000);
            await scene.SceneLoadedAsync(session, SceneLoadedReq.Parser.ParseFrom(body.Span));
            using var scope = session.Owners is { } loadedOwners ? await EnterScenePublicationAsync(loadedOwners, session) : null;
            if (!ReferenceEquals(session.Role, loadedRole) || !ReferenceEquals(session.Attachment, loadedAttachment)
                || session.SceneGeneration != sceneGeneration) return;
            await teams.OnEnteredAsync(session);
            session.GuildOnlinePublished = true; // undo even if the publication throws after saving
            await guilds.OnlineChangedAsync(session, true);
            await guilds.SceneGuildIdAsync(session);
        });
        routes.Map(DungeonService.Enter, "territory", async (session, msgId, body) =>
        {
            var dungeonRequest = EnterDungeonReq.Parser.ParseFrom(body.Span);
            if (await territory.EnterAsync(session, dungeonRequest) || await territory.SiegeEnterAsync(session, dungeonRequest)) return;
            if (!await training.TryEnterAsync(session, dungeonRequest) && !await festival.TryEnterAsync(session, dungeonRequest) && !await timedBattles.TryEnterOnePieceAsync(session, dungeonRequest) && !await guildHome.TryEnterViaDungeonAsync(session, dungeonRequest))
                await dungeons.EnterAsync(session, dungeonRequest);
        });
        routes.Map(TowerService.LeaveDungeon, "territory", async (session, msgId, body) =>
        {
            if (await territory.LeaveAsync(session)) return;
            if (session.TowerAttempt != null) await tower.LeaveAsync(session);
            else await dungeons.LeaveAsync(session);
        });
        routes.Map(ChangeViewCfg, "world", async (session, msgId, body) =>
        {
            // Graphics quality / visible-count settings: accepted as asked (the AOI keeps its own limits).
            var view = ChangeViewCfgReq.Parser.ParseFrom(body.Span);
            await session.SendGameAsync(ChangeViewCfg, new ChangeViewCfgResp
            {
                Result = 0, ViewMaxCount = view.ViewMaxCount, NameBoardMaxCount = view.NameBoardMaxCount, LodHighCount = view.LodHighCount,
            });
            await world.SetViewLimitAsync(session, view.ViewMaxCount);
            await worldCore.ViewAsync(session, view.NameBoardMaxCount);
        });
        routes.Map(SyncMiniMapStatus, "worldCore", async (session, msgId, body) =>
        {
            await worldCore.MiniMapAsync(session, MiniMapStatus.Parser.ParseFrom(body.Span));
        });
        routes.Map(SceneGetLineInfo, "scene", async (session, msgId, body) =>
        {
            await scene.LineInfoAsync(session, GetSceneLineInfoReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(MsgId.SwitchSceneLine, "scene", async (session, msgId, body) =>
        {
            await scene.SwitchLineAsync(session, SwitchSceneLineReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(MvpService.BriefInfo, "mvps", async (session, msgId, body) =>
        {
            await mvps.BriefAsync(session);
        });
        routes.Map(MvpService.AwardNum, "mvps", async (session, msgId, body) =>
        {
            await mvps.AwardNumAsync(session);
        });
        routes.Map(MvpService.ReportRank, "mvps", async (session, msgId, body) =>
        {
            await mvps.ReportAsync(session, MVPReportRankReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(MsgId.ChangeFace, "world", async (session, msgId, body) =>
        {
            if (session.Role != null)
            {
                session.Face = ChangeFaceReq.Parser.ParseFrom(body.Span).Angle;
                await world.TurnAsync(session, session.Face);
            }
        });
        routes.Map(CombatService.LockEnemy, "combat", async (session, msgId, body) =>
        {
            combat.Lock(session, LockEnemyReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(CombatService.SkillInterrupt, "combat", async (session, msgId, body) =>
        {
            await combat.InterruptAsync(session, SkillInterruptReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(CombatService.BossAllSkillCds, "combat", async (session, msgId, body) =>
        {
            await combat.BossCooldownSnapshotAsync(session, BossAllSkillShowCdReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(CombatService.TrainingOperation, "combat", async (session, msgId, body) =>
        {
            await combat.TrainingOperationAsync(session, BattleTrainingGroundOpReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(CombatService.Relive, "territory", async (session, msgId, body) =>
        {
            if (await territory.ReviveAsync(session, EntityReliveReq.Parser.ParseFrom(body.Span), combat)) return;
            if (session.DungeonAttempt != null) await dungeons.ReviveAsync(session, EntityReliveReq.Parser.ParseFrom(body.Span));
            else await combat.ReviveAsync(session, EntityReliveReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(SceneService.SceneTeleport, "scene", async (session, msgId, body) =>
        {
            await scene.SceneTeleportAsync(session, SceneTeleportReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(SceneService.CustomJumpMap, "scene", async (session, msgId, body) =>
        {
            await scene.CustomJumpAsync(session, ClientCustomJumpMapReq.Parser.ParseFrom(body.Span));
        });
    }
}
