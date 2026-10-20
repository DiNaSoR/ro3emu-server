using Google.Protobuf;

namespace Ro3Emu.Server.Game;

public sealed partial class TaskService
{
    /// <summary>E05e: MVP target counters share the settled source; normal task settlement resumes durable debt.
    /// Connect Quest 106012 also counts the MVP's NPC id while the role is in a party.</summary>
    internal CharacterService.FirstCardSightPlan? StageMvpParticipant(GameSession session, string source, long npc = 0)
    {
        bool party = npc != 0 && InParty(session);
        return StageEventTarget(session, "mvp/" + source,
            target => target[0] is TMvp or TMvpOnce || party && target is [TPartyMvp, _, var mvp] && mvp == npc);
    }

    /// <summary>Kafra Survivor (2) is also the battle royale of Connect Quest 106013, counted while in a party.</summary>
    internal CharacterService.FirstCardSightPlan? StageTimedBattleSettlement(GameSession session, int entry, string source)
    {
        bool party = entry == 2 && InParty(session);
        return entry is 2 or 6 ? StageEventTarget(session, "timed-battle/" + source,
            target => TimedBattleTarget(target, entry) || party && target is [TPartyBattleRoyale, _]) : null;
    }

    internal CharacterService.FirstCardSightPlan? StageOnePieceReward(GameSession session, string source)
        => StageEventTarget(session, "one-piece/" + source,
            target => target[0] == TOnePieceCount && target.Length >= 4 && target[1] == 200);

    private CharacterService.FirstCardSightPlan? StageEventTarget(GameSession session, string source, Func<long[], bool> matches)
    {
        if (session.Tasks == null || session.Progress == null) return null;
        session.Owners?.Current?.AssertRole(session.Role!.Id);
        var next = session.Tasks.Clone();
        bool changed = false;
        foreach (var info in next.Infos.Values.Where(i => i.Status == Running))
        {
            if (!Defs().TryGetValue(info.TaskId, out var def)) continue;
            for (int index = 0; index < def.Targets.Length && index < info.CompleteNum.Count; index++)
            {
                var target = def.Targets[index];
                if (!matches(target)) continue;
                long required = Required(session, target);
                if (required <= 0) continue;
                long old = Math.Clamp(info.CompleteNum[index], 0, required);
                long value = old < required ? old + 1 : old;
                changed |= value != info.CompleteNum[index];
                info.CompleteNum[index] = value;
            }
        }
        return !changed ? null : new(builder =>
        {
            builder.RoleBlob(session.Role!.Id, "tasks", next.ToByteArray());
            builder.TaskFollowup(session.Role.Id, source);
        }, () => session.Tasks = next);
    }
}
