using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The last pet Main Skills whose effect outlasts the guardian's one hit (CombatService.PetEffects and PetTriggers
/// have the others):
/// <list type="bullet">
/// <item>Owl Baron 【Lightning Strike】 (guardian 2311401, _kDescData [12, 0.5, 1, 8, 5, 40]): "locks onto a ST enemy
/// within ${1} meters, Summoning ${3} Lightning Strikes every ${2} sec to attack random ST enemies within a ${4}-meter
/// radius of the target. Summons ${5} Lightning Strikes in total." The guardian's own hit on the locked enemy is the
/// first strike; the rest are the same row cast again at random enemies around the locked one (or where it fell).
/// The interval is the guardian row's 0.5 s (the Main Skill row reads 0.15): five strikes then fit the 2.5 s of
/// 2311301 the guardian grants the master (inferred).</item>
/// <item>Angeling 【Radiant Ring】 (buff 2328101, _kLogicData [2328101, 2328301]): "granting the master a ${4}-meter-radius
/// 【Radiant Ring】. Allies within the area receive ${5}% more healing and periodically recover Adaptive ATK*${6}% Life
/// for ${7} seconds." Every second while the ring lasts (inferred, like the other periodic heals), allies within it
/// get 2328301 (healing received +10%, _kData1 1500 = the 15%) and that heal.</item>
/// <item>Baphomet Jr. 【Raging Gale】 (guardian 2340101, _kBuff2 [[Slow 2340001], [Immobilize 902401, 5000, 1000]]):
/// "If a target is already 【Slowed】, there is a ${7}% Chance to apply the Extra 【Immobilize】": the Immobilize tuple
/// rolls only on a target slowed before the hit (CombatService.Effects).</item>
/// <item>Fire Ninja 【Ninja Art: Vanishing Trail】 (2302201, script "petmove"): "moves its master ${2} meters in the
/// designated direction" (the show's 2300311 movement, 300 ms; CombatService.Effects.Displacements).</item>
/// </list>
/// </summary>
public sealed partial class CombatService
{
    private const int OwlStrikeGroup = 23114, RadiantRingGroup = 23281, RagingGaleGroup = 23401;

    /// <summary>After the Owl Baron's first strike: the remaining strikes, one wave every interval.</summary>
    private void StartOwlStrikes(GameSession s, JsonElement row, MonsterUnit? target, int petId)
    {
        if (GameData.Int(row, "_iSkillID") != OwlStrikeGroup) return;
        var desc = Numbers(row, "_kDescData");
        double lockMeters = desc.ElementAtOrDefault(0), seconds = desc.ElementAtOrDefault(1);
        int perWave = Math.Max(1, (int)desc.ElementAtOrDefault(2)), total = (int)desc.ElementAtOrDefault(4);
        float radius = (float)desc.ElementAtOrDefault(3);
        if (seconds <= 0 || total <= perWave) return;
        // (inferred) A cast with no enemy selected locks the nearest one in range.
        target ??= _world.Map(s.WorldScene).Units.OfType<MonsterUnit>()
            .Where(m => m.Hp > 0 && !m.Dead && CanHitMonster(s, m) && MonsterService.Distance(m, s) <= lockMeters + 0.5f)
            .OrderBy(m => MonsterService.Distance(m, s)).ThenBy(m => m.Uid).FirstOrDefault();
        if (target == null) return;
        ScheduleOwlStrike(s, s.Role!, row, target, target.Pos.Clone(), radius, perWave, total - perWave,
            (int)Math.Round(seconds * 1000), petId);
    }

    private void ScheduleOwlStrike(GameSession s, object role, JsonElement row, MonsterUnit locked, Vec3 center, float radius,
        int perWave, int left, int intervalMs, int petId)
    {
        long scene = s.WorldScene, generation = s.SceneGeneration;
        ScheduleRuntime(scene, TimeSpan.FromMilliseconds(Math.Max(0, intervalMs - GameLoop.TickInterval.TotalMilliseconds)), async () =>
        {
            if (!ReferenceEquals(s.Role, role) || s.Dead || s.SceneLoading || s.WorldScene != scene
                || s.SceneGeneration != generation || !_sessions.Contains(s)) return;
            if (!locked.Dead && locked.Hp > 0) center = locked.Pos.Clone();
            var pool = _world.Map(scene).Units.OfType<MonsterUnit>()
                .Where(m => m.Hp > 0 && !m.Dead && CanHitMonster(s, m) && Distance(center, m.Pos) <= radius + 0.1f).ToList();
            int wave = Math.Min(perWave, left);
            for (int i = 0; i < wave && pool.Count > 0; i++)
            {
                var pick = pool[Rolls.Proc(pool.Count)];
                pool.Remove(pick);
                await ProcCastAsync(s, row, pick, petId: petId);
            }
            if (left - wave > 0) ScheduleOwlStrike(s, role, row, locked, center, radius, perWave, left - wave, intervalMs, petId);
        });
    }

    /// <summary>Angeling's ring: start its tick for the ring buff the guardian granted.</summary>
    private void StartRadiantRing(GameSession s, JsonElement row)
    {
        if (GameData.Int(row, "_iSkillID") != RadiantRingGroup || LivePetBuff(s, RadiantRingGroup) is not { } ring
            || !_petHealLoops.Add(ring.Buff.Guid)) return;
        ScheduleRadiantRing(s, s.Role!, ring.Buff.Guid, MonsterService.NowMs + 1000);
    }

    private void ScheduleRadiantRing(GameSession s, object role, long guid, long due)
    {
        var delay = TimeSpan.FromMilliseconds(Math.Max(0, due - MonsterService.NowMs - GameLoop.TickInterval.TotalMilliseconds));
        ScheduleRuntime(s.WorldScene, delay, async () =>
        {
            var buff = s.Buffs.FirstOrDefault(b => b.Guid == guid);
            if (!ReferenceEquals(s.Role, role) || s.Dead || s.SceneLoading || buff == null
                || buff.ExpireMs + GameLoop.TickInterval.TotalMilliseconds < due || _data.Row("BuffConfig", buff.ConfigId) is not { } ring)
            {
                _petHealLoops.Remove(guid);
                return;
            }
            await RadiantRingTickAsync(s, ring);
            if (due + 1000 <= buff.ExpireMs + GameLoop.TickInterval.TotalMilliseconds) ScheduleRadiantRing(s, role, guid, due + 1000);
            else _petHealLoops.Remove(guid);
        });
    }

    private async Task RadiantRingTickAsync(GameSession s, JsonElement ring)
    {
        if (Numbers(ring, "_kLogicData") is not [_, var healId, ..] || _data.Row("BuffConfig", (long)healId) is not { } heal) return;
        float radius = (float)MainSkillDesc(s).ElementAtOrDefault(3);
        if (radius <= 0) radius = 7;
        double ratio = Numbers(heal, "_kData1").FirstOrDefault();
        var recipients = new List<GameSession> { s };
        recipients.AddRange(_world.PlayersIn(s.WorldScene).Where(p => p != s && !p.Dead && p.Role != null && p.Progress != null
            && Distance(p.Position, s.Position) <= radius));
        var stats = _character.Stats(s);
        var healer = PlayerAttacker(s, stats);
        var hit = new SkillHitInfo { SrcID = s.Role!.Id, SkillId = (int)GameData.Int(heal, "_iID"), SessionId = ++_sessionIds };
        foreach (var recipient in recipients)
        {
            // The healing-received buff lands first, so this tick already counts it; it outlasts the next tick.
            await GrantSkillBuffAsync(s, recipient, (int)healId, 1500);
            if (ratio <= 0 || recipient.Dead || recipient.Role == null) continue;
            var before = _character.Stats(recipient);
            long amount = (long)Math.Max(0, Math.Round(Math.Max(stats[103], stats[104]) * ratio / 10000
                * HealFactor(healer, PlayerDefender(recipient, before))));
            long actual = Math.Min(amount, Math.Max(0, before[101] - before[2000]));
            if (amount <= 0) continue;
            await _character.SetHpSpAsync(recipient, before[2000] + actual, null);
            hit.Dsts.Add(new DamageInfo { DstID = recipient.Role.Id, Damage = actual, DamageType = DamageType.Heal, DstElement = 1 });
            foreach (var handler in Healed) handler(s, actual);
            foreach (var handler in HealedAsync) await handler(s, actual);
        }
        if (hit.Dsts.Count > 0) await ToPlayerAndViewersAsync(s, SkillHit, hit);
    }

    /// <summary>Raging Gale's Immobilize (its _kBuff2 after the Slow) needs a target already slowed.</summary>
    private bool GaleSkipsBuff(JsonElement row, int index, bool slowedBefore) =>
        index > 0 && !slowedBefore && GameData.Int(row, "_iSkillID") == RagingGaleGroup;

    /// <summary>A unit under a movement speed cut (a buff lowering 113/1131, or a direct slow).</summary>
    private bool Slowed(MonsterUnit target)
    {
        long now = MonsterService.NowMs;
        return target.SlowMovementExpireMs > now && target.SlowMovementBp > 0
            || _buffs.UnitBuffs(target).Any(b => b.ExpireMs > now && b.Changes.Any(c => c.Attr is 113 or 1131 && c.Value < 0));
    }
}
