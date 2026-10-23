using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class DungeonService
{
    /// <summary>
    /// A step's strike: the interaction (the airship's 'Board the Cannon') casts its skill from the player at the nearest
    /// living monster of the step's fight, through the ordinary extra-cast path (damage, kills, the tracker) (inferred:
    /// the original's cannon gameplay status is a server script the client does not ship). With shots, the k-th strike on
    /// a target first brings it down to its (shots - k) / shots share of the maximum HP, so that many strikes sink it at
    /// any player level, even when the far target resets between them (inferred: the rapid fire button stood for a volley).
    /// </summary>
    private async Task StrikeAsync(Attempt a, GameSession s, SceneActorUnit actor, long interaction)
    {
        if (Step(a) is not { Strikes: { Count: > 0 } strikes } || a.Settled) return;
        foreach (var (id, skill, shots) in strikes)
        {
            if (id != interaction || _data.Row("SkillConfig", skill) is not { } row) continue;
            var target = a.Room.Monsters.Where(m => !m.Dead && m.Hp > 0)
                .OrderBy(m => MonsterService.Distance(actor.X, actor.Z, m.X, m.Z)).FirstOrDefault();
            if (target == null) continue;
            if (shots > 0)
            {
                int struck = a.Struck[target.Uid] = a.Struck.GetValueOrDefault(target.Uid) + 1;
                target.Hp = Math.Min(target.Hp, target.Template.MaxHp * Math.Max(0, shots - struck) / shots + 1);
            }
            await _combat.CastExtraAsync(s, row, [target]);
        }
    }

}
