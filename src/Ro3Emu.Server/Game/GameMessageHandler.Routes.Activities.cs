#pragma warning disable CS1998 // arms that only send or return keep the uniform async shape
using Google.Protobuf;
using Romsg;

namespace Ro3Emu.Server.Game;

public sealed partial class GameMessageHandler
{
    private void MapActivitiesRoutes(Messaging.MessageRoutes routes)
    {
        routes.Map(82800, "genre", async (session, msgId, body) =>
        {
            await genre.RunOwnedAsync(session, () => genre.SeekAsync(session), 0, codes: true);
        });
        routes.Map(82801, "genre", async (session, msgId, body) =>
        {
            var request = GenreResponserReq.Parser.ParseFrom(body.Span);
            await genre.RunOwnedAsync(session, () => genre.RespondAsync(session, request), request.SeekUserId, codes: false);
        });
        routes.Map(82802, "genre", async (session, msgId, body) =>
        {
            var request = GenreResponserSendPlanReq.Parser.ParseFrom(body.Span);
            await genre.RunOwnedAsync(session, () => genre.SendRecommendationAsync(session, request), request.SeekUserId, codes: false);
        });
        routes.Map(82803, "genre", async (session, msgId, body) =>
        {
            var request = GenreResponserSaveReq.Parser.ParseFrom(body.Span);
            await genre.RunOwnedAsync(session, () => genre.SaveRecommendationAsync(session, request), request.SeekUserId, codes: false);
        });
        routes.Map(82804, "genre", async (session, msgId, body) =>
        {
            var request = GenreResponserDeleteReq.Parser.ParseFrom(body.Span);
            await genre.RunOwnedAsync(session, () => genre.DeleteRecommendationAsync(session, request), request.SeekerUserId, codes: false);
        });
        routes.Map(82805, "genre", async (session, msgId, body) =>
        {
            var request = GenreSeekerDeleteReq.Parser.ParseFrom(body.Span);
            await genre.RunOwnedAsync(session, () => genre.DeleteSeekerAsync(session, request), request.ResponseUserId, codes: false);
        });
        routes.Map(82806, "genre", async (session, msgId, body) =>
        {
            var request = SwitchGenrePlanReq.Parser.ParseFrom(body.Span);
            await genre.RunOwnedAsync(session, () => genre.SwitchAsync(session, request), 0, codes: false);
        });
        routes.Map(82807, "genre", async (session, msgId, body) =>
        {
            await genre.RunOwnedAsync(session, () => genre.CodeAsync(session), 0, codes: true);
        });
        routes.Map(82808, "genre", async (session, msgId, body) =>
        {
            await genre.RunOwnedAsync(session, () => genre.MyRecommendationsAsync(session), 0, codes: false);
        });
        routes.Map(82809, "genre", async (session, msgId, body) =>
        {
            var request = GenreGetAllOtherRecommendReq.Parser.ParseFrom(body.Span);
            await genre.RunOwnedAsync(session, () => genre.OthersAsync(session, request), request.ResponseUserID, codes: false);
        });
        routes.Map(82810, "genre", async (session, msgId, body) =>
        {
            var request = GenreGetMyRecConcretePlanReq.Parser.ParseFrom(body.Span);
            await genre.RunOwnedAsync(session, () => genre.ConcreteAsync(session, request), request.SeekerUserId, codes: false);
        });
        routes.Map(82813, "genre", async (session, msgId, body) =>
        {
            var request = GenreSeekerApplyPlanReq.Parser.ParseFrom(body.Span);
            await genre.RunOwnedAsync(session, () => genre.ApplyRecommendationAsync(session, request), request.ResponseUserId, codes: false);
        });
        routes.Map(82816, "genre", async (session, msgId, body) =>
        {
            var request = GenreChangePlanNameReq.Parser.ParseFrom(body.Span);
            await genre.RunOwnedAsync(session, () => genre.RenameAsync(session, request), 0, codes: false);
        });
        routes.Map(82823, "genre", async (session, msgId, body) =>
        {
            var request = GenreGetPlanPropertyReq.Parser.ParseFrom(body.Span);
            await genre.RunOwnedAsync(session, () => genre.PropertyAsync(session, request), 0, codes: false);
        });
        routes.Map(82824, "genre", async (session, msgId, body) =>
        {
            var request = GenreResponserNameSaveReq.Parser.ParseFrom(body.Span);
            await genre.RunOwnedAsync(session, () => genre.RenameRecommendationAsync(session, request), request.SeekUserId, codes: false);
        });
        routes.Map(82825, "genre", async (session, msgId, body) =>
        {
            var request = GenrePlanPowerReq.Parser.ParseFrom(body.Span);
            await genre.RunOwnedAsync(session, () => genre.PowerAsync(session, request), 0, codes: false);
        });
        routes.Map(82826, "genre", async (session, msgId, body) =>
        {
            await genre.RunOwnedAsync(session, () => genre.SeasonAsync(session), 0, codes: false);
        });
        routes.Map(82827, "genre", async (session, msgId, body) =>
        {
            var request = GenreAttrSettingSchoolReq.Parser.ParseFrom(body.Span);
            await genre.RunOwnedAsync(session, () => genre.SchoolAsync(session, request), 0, codes: false);
        });
        routes.Map(82828, "genre", async (session, msgId, body) =>
        {
            await genre.RunOwnedAsync(session, () => genre.QueryRecommendationsAsync(session), 0, codes: false);
        });
        routes.Map(82812, "genre", async (session, msgId, body) =>
            await genre.RespondCodeRequestAsync(session, GenreResponseCodeReq.Parser.ParseFrom(body.Span)));
        routes.Map(NewActivityService.GetList, "activities", async (session, msgId, body) =>
        {
            await activities.ListAsync(session);
        });
        routes.Map(NewActivityService.GetReward, "activities", async (session, msgId, body) =>
        {
            await activities.ClaimAsync(session, NewActivityGetRewardCommonReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(NewActivityService.Common, "activities", async (session, msgId, body) =>
        {
            await activities.CommonAsync(session, CommonActivityReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(NewActivityService.LevelAward, "activities", async (session, msgId, body) =>
        {
            await activities.LevelAwardAsync(session, GetThreeDayLevelAwardReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(NewActivityService.FunctionPreviewAward, "activities", async (session, msgId, body) =>
        {
            await activities.FunctionPreviewAsync(session, GetThreeDayFunctionPreviewAwardReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(82960, "activities", async (session, msgId, body) =>
        {
            await activities.PreSubmitAsync(session, NewActivityPreSubmitItemReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(82961, "activities", async (session, msgId, body) =>
        {
            await activities.PreGetDataAsync(session, NewActivityPreGetDataReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(82962, "activities", async (session, msgId, body) =>
        {
            await activities.PreReceiveAsync(session, NewActivityPreReceiveRewardReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(82963, "activities", async (session, msgId, body) =>
        {
            await activities.FragmentAskAsync(session, NewActivityFragmentGatherAskReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(82964, "activities", async (session, msgId, body) =>
        {
            await activities.FragmentSendAsync(session, NewActivityFragmentGatherSendReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(82965, "activities", async (session, msgId, body) =>
        {
            await activities.FragmentCraftAsync(session, NewActivityFragmentGatherCraftReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(82966, "activities", async (session, msgId, body) =>
        {
            await activities.FragmentProgressAsync(session, NewActivityFragmentGatherProgressReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(82967, "activities", async (session, msgId, body) =>
        {
            await activities.FragmentAskFinishedAsync(session, NewActivityFragmentGatherAskFinishedReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(83450, "activities", async (session, msgId, body) =>
        {
            await activities.WorldFillAsync(session, WorldExplorationFillPhaseNodeReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(82681, "dungeonMatches", async (session, msgId, body) =>
        {
            var startMatch = StartMatchReq.Parser.ParseFrom(body.Span);
            if (dungeonMatches.WantsEntry(startMatch.MatchEntryId) || startMatch.MatchData?.MultiDungeon != null)
                await dungeonMatches.StartMatchAsync(session, startMatch);
            else await timedBattles.StartMatchAsync(session, startMatch);
        });
        routes.Map(82682, "dungeonMatches", async (session, msgId, body) =>
        {
            var cancelMatch = CancelMatchReq.Parser.ParseFrom(body.Span);
            if (dungeonMatches.Owns(session) || dungeonMatches.WantsEntry(cancelMatch.MatchEntryId) || cancelMatch.MatchData?.MultiDungeon != null)
                await dungeonMatches.CancelMatchAsync(session, cancelMatch);
            else if (await timedBattles.CancelMatchAsync(session, cancelMatch)
                && (dungeonMatches.WantsEntry(cancelMatch.NextMatchEntry) || cancelMatch.NextMatchData?.MultiDungeon != null))
                await dungeonMatches.StartMatchAsync(session, new StartMatchReq
                { MatchEntryId = cancelMatch.NextMatchEntry, MatchData = cancelMatch.NextMatchData?.Clone() });
        });
        routes.Map(82637, "dungeonMatches", async (session, msgId, body) =>
        {
            var matchReady = MatchReadyReq.Parser.ParseFrom(body.Span);
            if (dungeonMatches.Owns(session) || dungeonMatches.WantsEntry(matchReady.MatchEntryId))
                await dungeonMatches.ReadyAsync(session, matchReady);
            else await timedBattles.ReadyAsync(session, matchReady);
        });
        routes.Map(82634, "dungeonMatches", async (session, msgId, body) =>
        {
            await dungeonMatches.RemindAsync(session, MatchReadyReminderReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(84071, "timedBattles", async (session, msgId, body) =>
        {
            await timedBattles.ClaimDragonAsync(session, DragonIslandClaimRankRewardReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(83351, "timedBattles", async (session, msgId, body) =>
        {
            await timedBattles.OnePieceInfoAsync(session, HiddenTreasureRoundInfoReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(83410, "lottery", async (session, msgId, body) =>
        {
            await lottery.DrawAsync(session, LotteryReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(84061, "training", async (session, msgId, body) =>
        {
            await training.ModeAsync(session, true);
        });
        routes.Map(84062, "training", async (session, msgId, body) =>
        {
            await training.ClaimAsync(session, TrainingAimAwardReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(84063, "training", async (session, msgId, body) =>
        {
            await training.ModeAsync(session, false);
        });
        routes.Map(84064, "training", async (session, msgId, body) =>
        {
            await training.UpdateAsync(session, TrainingUpdateWoodNpcAttrReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(84065, "training", async (session, msgId, body) =>
        {
            await training.ReconnectAsync(session, false);
        });
        routes.Map(84066, "training", async (session, msgId, body) =>
        {
            await training.ReconnectAsync(session, true);
        });
        routes.Map(84067, "training", async (session, msgId, body) =>
        {
            await training.ResetAsync(session);
        });
        routes.Map(TimedEventService.Common, "timedEvents", async (session, msgId, body) =>
        {
            await timedEvents.CommonAsync(session, ActivityReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(TimedEventService.Upload, "timedEvents", async (session, msgId, body) =>
        {
            await timedEvents.UploadAsync(session, ActivityDataUploadReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(42047, "dungeons", async (session, msgId, body) =>
        {
            await dungeons.RemindAsync(session, TeamConfirmRemindersReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(MsgId.Heartbeat, "daily", async (session, msgId, body) =>
        {
            // C/D 16: every player's heartbeat used to be one Realm scope (S3: 10k per 5 min, the top Realm waiter,
            // other replies queued behind it up to 2.5 s). The role's own reconciliation runs under its reservation;
            // realm guild maintenance (at most once a second) admits its own guild keys.
            async Task OwnAsync() { await daily.ReconcileAsync(session); await dungeons.ReconcileAsync(session); }
            if (session.Owners is { } owners && session.Role is { } role)
                await owners.RunSessionAsync("heartbeat",
                    [Ownership.ReservationKey.Role(role.Id), Ownership.ReservationKey.Lifecycle(role.Id)], session, OwnAsync);
            else await OwnAsync();
            await guilds.ReconcileOnHeartbeatAsync();
            await session.SendGameAsync(MsgId.Heartbeat, new Pong { ServerTimeMs = NowMs() });
        });
        routes.Map(DailyActivityService.RingAccept, "festival", async (session, msgId, body) =>
        {
            var dailyRequest = CommonDailyActivityReq.Parser.ParseFrom(body.Span);
            if (dailyRequest.RainbowKoiBet != null) await festival.BetAsync(session, dailyRequest);
            else await daily.AcceptRingAsync(session, dailyRequest);
        });
        routes.Map(DailyActivityService.StageGain, "daily", async (session, msgId, body) =>
        {
            await daily.ClaimAsync(session, StageRewardGainReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(DungeonService.Confirm, "dungeons", async (session, msgId, body) =>
        {
            await dungeons.ConfirmAsync(session, ConfirmTeamDungeonReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(DungeonService.ClientEvent, "training", async (session, msgId, body) =>
        {
            var dungeonEvent = ClientDungeonEvent.Parser.ParseFrom(body.Span);
            if (!await training.TryEventAsync(session, dungeonEvent)) await dungeons.ClientEventAsync(session, dungeonEvent);
        });
        routes.Map(DungeonService.Statistics, "dungeons", async (session, msgId, body) =>
        {
            await dungeons.StatisticsAsync(session);
        });
        routes.Map(DungeonService.StatisticsHistory, "dungeons", async (session, msgId, body) =>
        {
            await dungeons.StatisticsHistoryAsync(session, DungeonStatisticsInfoWithDungeonReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(DungeonService.StatisticsCurrent, "dungeons", async (session, msgId, body) =>
        {
            await dungeons.StatisticsCurrentAsync(session);
        });
        routes.Map(DungeonService.StatisticsModule, "dungeons", async (session, msgId, body) =>
        {
            await dungeons.StatisticsModuleAsync(session);
        });
        routes.Map(DungeonService.QueryResult, "dungeons", async (session, msgId, body) =>
        {
            await dungeons.QueryResultAsync(session, QueryDungeonResultReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(DungeonService.SetJobPosition, "dungeons", async (session, msgId, body) =>
        {
            await dungeons.SetPositionAsync(session, MultiDungeonSetJobPositionReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(DungeonService.SetGuide, "dungeons", async (session, msgId, body) =>
        {
            await dungeons.SetGuideAsync(session, MultiDungeonSetJobPositionGuideFlagReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(DungeonService.ClaimFirstPass, "dungeons", async (session, msgId, body) =>
        {
            await dungeons.ClaimFirstPassAsync(session, ClaimFirstPassAwardReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(TowerService.Start, "tower", async (session, msgId, body) =>
        {
            await tower.StartAsync(session, ClimTowerStartReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(TowerService.Info, "tower", async (session, msgId, body) =>
        {
            await tower.InfoAsync(session);
        });
        routes.Map(TowerService.SetPets, "tower", async (session, msgId, body) =>
        {
            await tower.SetPetsAsync(session, SetClimTowerPetsReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(TowerService.FirstPassRange, "tower", async (session, msgId, body) =>
        {
            await tower.FirstPassRangeAsync(session, QueryClimbTowerFirstPassRangeReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(TowerService.ClaimFirstPass, "tower", async (session, msgId, body) =>
        {
            await tower.ClaimFirstPassAsync(session);
        });
        routes.Map(SetAutoBattle, "afk", async (session, msgId, body) =>
        {
            await afk.SetAutoBattleAsync(session, SetAutoBattleReq.Parser.ParseFrom(body.Span).IsAutoBattle);
        });
        routes.Map(WildAfkService.Info, "afk", async (session, msgId, body) =>
        {
            await afk.InfoAsync(session);
        });
        routes.Map(WildAfkService.Collection, "afk", async (session, msgId, body) =>
        {
            await afk.CollectAsync(session, SetMonsterCollectionReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(WildAfkService.Start, "afk", async (session, msgId, body) =>
        {
            await afk.ToggleAsync(session, true);
        });
        routes.Map(WildAfkService.Pause, "afk", async (session, msgId, body) =>
        {
            await afk.ToggleAsync(session, false);
        });
        routes.Map(WildAfkService.Magnification, "afk", async (session, msgId, body) =>
        {
            await afk.ToggleAsync(session, SetHighMagnificationReq.Parser.ParseFrom(body.Span).Status, legacy: true);
        });
        routes.Map(WildAfkService.Snapshot, "afk", async (session, msgId, body) =>
        {
            await afk.SnapshotAsync(session);
        });
    }
}
