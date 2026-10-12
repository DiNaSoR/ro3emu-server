using Romsg;

namespace Ro3Emu.Server.Game;

public sealed partial class GameMessageHandler
{
    // Shared match packets retain legacy admission if either end belongs to an unconverted dungeon family.
    private bool IsTimedBattleOwnerRequest(GameSession session, uint message, ReadOnlyMemory<byte> body)
    {
        if (!timedBattles.IsOwnerRequest(message, body)) return false;
        if (message == TimedBattleService.StartMatch)
            return !dungeonMatches.WantsEntry(StartMatchReq.Parser.ParseFrom(body.Span).MatchEntryId);
        if (message == TimedBattleService.CancelMatch)
        {
            var request = CancelMatchReq.Parser.ParseFrom(body.Span);
            return !dungeonMatches.Owns(session) && !dungeonMatches.WantsEntry(request.MatchEntryId)
                && !dungeonMatches.WantsEntry(request.NextMatchEntry) && request.NextMatchData?.MultiDungeon == null;
        }
        if (message == TimedBattleService.Ready)
            return !dungeonMatches.Owns(session) && !dungeonMatches.WantsEntry(MatchReadyReq.Parser.ParseFrom(body.Span).MatchEntryId);
        return true;
    }
}
