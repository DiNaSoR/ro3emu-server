using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// More pet Main Skill buffs that act on the master's hits, on hits the master takes, and on the guardian's own hits
/// (CombatService.PetEffects has the first ones). Numbers come from the buff rows and the Main Skill's description:
/// <list type="bullet">
/// <item>Cat o' Nine-Tail (buff group 23014, 1215 +10%): "the master's next ${4} instances of damage deal ${5}% more
/// damage"; the buff ends after that many hits.</item>
/// <item>Lola's iron ball (23261, [2326601]): "When dealing damage, deals ${1}% of the higher of Physical ATK and Magic
/// ATK as Extra Neutral melee damage, with a ${2}% Chance to inflict Stun ... (internal cooldown: ${4} sec.)".</item>
/// <item>Sarah Irene 【Flame Fist】 (23341, [2334601]): "when Deals DMG, deals @{2} hits of Fire Ranged Adaptive Damage
/// ... Internal Cooldown: ${7} sec."</item>
/// <item>Retribution 【Execution】 (23361, [Slow buff, chance bp, ms]): "On HIT, has a ${8}% Chance to inflict ${6}%
/// 【Slow】 for ${9} seconds".</item>
/// <item>Scout Basilisk 【Vigilant Recon】 (23411, [2341201]): "Each time the master takes damage, Physical DEF increases
/// by ${2}% and Magic DEF by ${3}%, stacking up to ${4} times and lasting ${5} sec."</item>
/// <item>Piamette's Bunny Doll (23321, [2332401, 500]): "if the master is attacked while their Life is below ${3}% of Max
/// Life, restores Life equal to Max Life * ${4}%. Can only trigger once per duration." (500 bp of Max Life).</item>
/// <item>Wildcat Knight: "Each enemy HIT grants the Master an Extra 【Vanguard Shield】 ... stacking up to ${7} times"
/// (one more pile of the guardian's shield buff per enemy hit).</item>
/// <item>Fruit Tree Turtle: "restoring ${1} Life to its master every second for ${2} seconds ... For every ${6} enemies
/// hit, it restores ${7} Life to its master" (buff 2343101.. "Restores ${1} Life per second").</item>
/// </list>
/// The pet's own casts feed none of the hit triggers (inferred).
/// </summary>
public sealed partial class CombatService
{
    private const int NineTailGroup = 23014, IronBallGroup = 23261, FlameFistGroup = 23341, ExecutionGroup = 23361,
        ReconGroup = 23411, BunnyDollGroup = 23321, TurtleGroup = 23431, WildcatGuardianGroup = 23071;

    /// <summary>Guardian skill groups whose enemy hits are counted for an after-effect.</summary>
    private static readonly HashSet<int> GuardianHitGroups = [WildcatGuardianGroup, TurtleGroup];

    private sealed class PetTriggerState
    {
        public long IronBallReadyMs, FlameFistReadyMs, NineTailGuid, BunnyGuid;
        public int NineTailLeft;
    }

    private readonly Dictionary<long, PetTriggerState> _petTriggers = new();
    private readonly HashSet<long> _petHealLoops = new();

    private PetTriggerState TriggerState(GameSession s) =>
        _petTriggers.TryGetValue(s.Role!.Id, out var state) ? state : _petTriggers[s.Role!.Id] = new PetTriggerState();

    /// <summary>A master's hit (not one of the pet's own casts) on a monster.</summary>
    private async Task PetTriggerHitAsync(GameSession s, MonsterUnit target, double[] desc)
    {
        long now = MonsterService.NowMs;
        _petTriggers.TryGetValue(s.Role!.Id, out var state);
        if (LivePetBuff(s, NineTailGroup) is { } nine)
        {
            state = TriggerState(s);
            if (state.NineTailGuid != nine.Buff.Guid)
                (state.NineTailGuid, state.NineTailLeft) = (nine.Buff.Guid, Math.Max(1, (int)desc.ElementAtOrDefault(3)));
            if (--state.NineTailLeft <= 0) await _buffs.RemoveAsync(s, nine.Buff);
        }
        if (!target.Dead && target.Hp > 0 && LivePetBuff(s, ExecutionGroup) is { Row: var execution }
            && Numbers(execution, "_kLogicData") is [var slow, var chance, var ms, ..])
        {
            using var tuple = JsonDocument.Parse($"[{(long)slow},{chance},{ms}]");
            if (RollHostileBuff(tuple.RootElement, s, null, null, target, out int id, out int duration))
                await _buffs.ShowOnUnitAsync(target, id, duration, s.Role!.Id);
        }
        if (now >= (state?.IronBallReadyMs ?? 0) && LivePetBuff(s, IronBallGroup) is { Row: var ball }
            && Numbers(ball, "_kLogicData") is [var ballId, ..] && _data.Row("SkillConfig", (long)ballId) is { } ballRow)
        {
            state = TriggerState(s);
            state.IronBallReadyMs = now + (long)(Numbers(ball, "_kDescData").ElementAtOrDefault(3) * 1000);
            AfterHit(s, () => target.Dead || target.Hp <= 0 ? Task.CompletedTask : ProcCastAsync(s, ballRow, target));
        }
        if (now >= (state?.FlameFistReadyMs ?? 0) && LivePetBuff(s, FlameFistGroup) is { Row: var fist }
            && Numbers(fist, "_kLogicData") is [var fistId, ..] && _data.Row("SkillConfig", (long)fistId) is { } fistRow)
        {
            state = TriggerState(s);
            state.FlameFistReadyMs = now + (long)(desc.ElementAtOrDefault(6) * 1000);
            AfterHit(s, () => target.Dead || target.Hp <= 0 ? Task.CompletedTask : ProcCastAsync(s, fistRow, target));
        }
    }

    /// <summary>A monster's hit on the master.</summary>
    private async Task PetBuffTakenAsync(MonsterUnit attacker, GameSession p, long damage)
    {
        if (damage <= 0 || p.Role == null || p.Dead) return;
        if (LivePetBuff(p, ReconGroup) is { Row: var recon } && Numbers(recon, "_kLogicData") is [var stackId, ..])
        {
            double seconds = Numbers(recon, "_kDescData").ElementAtOrDefault(3);
            await _buffs.AddAsync(p, (int)stackId, (int)((seconds > 0 ? seconds : 5) * 1000), p.Role.Id);
        }
        if (LivePetBuff(p, BunnyDollGroup) is { } doll && Numbers(doll.Row, "_kLogicData") is [_, var bp, ..]
            && TriggerState(p) is var state && state.BunnyGuid != doll.Buff.Guid)
        {
            double below = MainSkillDesc(p).ElementAtOrDefault(2);
            var stats = _character.Stats(p);
            if (stats[2000] > 0 && stats[2000] * 100 < stats[101] * (below > 0 ? below : 50))
            {
                state.BunnyGuid = doll.Buff.Guid;
                await FlatHealAsync(p, (long)(stats[101] * bp / 10000), 0);
            }
        }
    }

    /// <summary>After the guardian row landed, with the enemies it hit.</summary>
    private async Task GuardianAfterAsync(GameSession s, JsonElement row, int hits)
    {
        int group = (int)GameData.Int(row, "_iSkillID");
        if (group == WildcatGuardianGroup && hits > 0 && Tuples(row, "_kBuff1").FirstOrDefault() is { ValueKind: JsonValueKind.Array } shield
            && shield.GetArrayLength() >= 3 && GameData.Number(shield[0]) is { } shieldId)
        {
            int ms = (int)(GameData.Number(shield[2]) ?? 0);
            for (int i = 0; i < hits && !s.Dead; i++) await GrantSkillBuffAsync(s, s, (int)shieldId, ms);
        }
        if (group == TurtleGroup)
        {
            var desc = MainSkillDesc(s);
            double per = desc.ElementAtOrDefault(5), restore = desc.ElementAtOrDefault(6);
            if (per > 0 && hits >= per) await FlatHealAsync(s, (long)(Math.Floor(hits / per) * restore), (int)GameData.Int(row, "_iID"));
            if (LivePetBuff(s, TurtleGroup) is { } vines && _petHealLoops.Add(vines.Buff.Guid))
                ScheduleVineHeal(s, s.Role!, vines.Buff.Guid, MonsterService.NowMs + 1000);
        }
    }

    /// <summary>The turtle's vines: the buff's _kDescData[0] Life every second while it lasts (the last at expiry).</summary>
    private void ScheduleVineHeal(GameSession s, object role, long guid, long due)
    {
        var delay = TimeSpan.FromMilliseconds(Math.Max(0, due - MonsterService.NowMs - GameLoop.TickInterval.TotalMilliseconds));
        ScheduleRuntime(s.WorldScene, delay, async () =>
        {
            var buff = s.Buffs.FirstOrDefault(b => b.Guid == guid);
            if (!ReferenceEquals(s.Role, role) || s.Dead || s.SceneLoading || buff == null
                || buff.ExpireMs + GameLoop.TickInterval.TotalMilliseconds < due || _data.Row("BuffConfig", buff.ConfigId) is not { } row)
            {
                _petHealLoops.Remove(guid);
                return;
            }
            await FlatHealAsync(s, (long)Numbers(row, "_kDescData").ElementAtOrDefault(0), 0);
            if (due + 1000 <= buff.ExpireMs + GameLoop.TickInterval.TotalMilliseconds) ScheduleVineHeal(s, role, guid, due + 1000);
            else _petHealLoops.Remove(guid);
        });
    }

    /// <summary>Restore a fixed amount of the master's Life and show it.</summary>
    private async Task FlatHealAsync(GameSession p, long amount, int skillId)
    {
        if (amount <= 0 || p.Role == null || p.Dead) return;
        var before = _character.Stats(p);
        long actual = Math.Min(amount, Math.Max(0, before[101] - before[2000]));
        await _character.SetHpSpAsync(p, before[2000] + actual, null);
        await ToPlayerAndViewersAsync(p, SkillHit, new SkillHitInfo
        {
            SrcID = p.Role.Id, SkillId = skillId, SessionId = ++_sessionIds,
            Dsts = { new DamageInfo { DstID = p.Role.Id, Damage = actual, DamageType = DamageType.Heal, DstElement = 1 } },
        });
        foreach (var handler in Healed) handler(p, actual);
        foreach (var handler in HealedAsync) await handler(p, actual);
    }
}
