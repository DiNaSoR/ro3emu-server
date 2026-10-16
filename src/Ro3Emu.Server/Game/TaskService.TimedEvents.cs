namespace Ro3Emu.Server.Game;

public sealed partial class TaskService
{
    /// <summary>One actual server-settled OnePiece dungeon reward, not a client-uploaded treasure count.</summary>
    public Task OnOnePieceRewardAsync(GameSession session) =>
        ProgressAsync(session, TOnePieceCount, target => target.Length >= 4 && target[1] == 200 ? 1 : 0);

    /// <summary>One Guild Hunting Challenge taken part in (target 210011).</summary>
    public Task OnGuildHuntAsync(GameSession session) =>
        ProgressAsync(session, TGuildHunt, target => target.Length == 2 ? 1 : 0);

    /// <summary>One actual PvP match settlement in Kafra Survivor or Dragon Coffin Island.</summary>
    public Task OnTimedBattleSettlementAsync(GameSession session, int matchEntry) => matchEntry is 2 or 6
        ? ProgressAsync(session, TTimedBattle, target => target.Length == 2 ? 1 : 0)
        : Task.CompletedTask;

    /// <summary>One Wedding Crashers clear by the guild this member took part in (target 210010).</summary>
    public Task OnDraculaWeddingAsync(GameSession session) =>
        ProgressAsync(session, TDraculaWedding, target => target.Length == 2 ? 1 : 0);
}
