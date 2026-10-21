using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// What a pet Main Skill's buff on the master does afterwards, and the life its hit drains. The numbers come from the
/// Main Skill row's description (_kDescData) and the buff's _kLogicData:
/// <list type="bullet">
/// <item>Silver Moon Witch 【Moonlight】 (buff group 23032, _kLogicData [2303301.., 5000]): "For the next ${5} sec, hitting
/// an enemy has a ${6}% Chance to grant the owner 【Moonlight】, increasing damage by ${7}% for ${8} sec. This DMG Bonus is
/// guaranteed if the target hit is more than ${9} meters away".</item>
/// <item>Mummy 【Evil Curse】 (23121, [2312601..]): "When the master Kills an enemy, casts 【Malicious Curse】 at that enemy's
/// location. Can trigger up to ${6} times."</item>
/// <item>Medusa (23061, [chance bp, Petrify ms]): "whenever the master's Normal Attack-type skills HIT a target, they
/// have a ${6}% Chance to inflict 【Petrify】 for ${7} seconds. This effect has a ${8}-second Cooldown."</item>
/// <item>Headless Warrior (23272, [2327501..]): "the master's Normal Attacks and skills deal Extra Neutral melee
/// Adaptive Damage equal to the master's Adaptive ATK*${6}%. This effect has a ${7}-second Cooldown."</item>
/// <item>Life drain: Death Witch "Each enemy hit restores Adaptive ATK*${5}% Life"; Elio "restoring Adaptive ATK*${5}%
/// Life to up to ${4} allies within a ${3}-meter radius"; Bloody Butterfly "Each hit restores HP to the owner equal to
/// the owner's Adaptive Attack*${5}% ... The final hit ... restores ... *${8}%" (each enemy hit counted once, the final
/// part once per cast: inferred, the storm lands as one server hit).</item>
/// </list>
/// Follow-up casts run after the hit that triggered them has finished (inferred).
/// </summary>
public sealed partial class CombatService
{
    private const int MoonlightGroup = 23032, EvilCurseGroup = 23121, MedusaGazeGroup = 23061, WarriorFuryGroup = 23272;
    private const int PetrifyBuff = 902101;

    /// <summary>Guardian skill group -> how its hit drains life.</summary>
    private enum DrainKind { PerEnemy, Allies, Storm }
    private static readonly IReadOnlyDictionary<int, DrainKind> PetDrains = new Dictionary<int, DrainKind>
    {
        [23101] = DrainKind.PerEnemy, // Death Witch: ${5}
        [23141] = DrainKind.Allies,   // Elio: ${5} to ${4} allies within ${3} m
        [23331] = DrainKind.Storm,    // Bloody Butterfly: ${5} per hit, ${8} on the final hit
    };

    private sealed class PetEffectState
    {
        public long PetrifyReadyMs, FuryReadyMs;
        public long CurseGuid;
        public int CursesLeft;
    }

    private readonly Dictionary<long, PetEffectState> _petEffects = new();
    private readonly Dictionary<GameSession, (long RowId, int Hits)> _guardianHits = new();
    private HashSet<long>? _petProcRows;

    private void RegisterPetEffects()
    {
        DealtAsync.Add(PetBuffDealtAsync);
        Killed.Add(PetBuffKilledAsync);
        TakenAsync.Add(PetBuffTakenAsync);
    }

    /// <summary>The rows a pet buff casts (Malicious Curse, the Headless Warrior's extra hit): Adaptive like the pet's.</summary>
    private bool PetProcRow(JsonElement row)
    {
        if (_petProcRows == null)
        {
            var found = new HashSet<long>();
            foreach (int group in new[] { EvilCurseGroup, WarriorFuryGroup, IronBallGroup, FlameFistGroup })
                foreach (var (_, buff) in _data.Table("BuffConfig"))
                    if (GameData.Int(buff, "_iBuffID") == group && Numbers(buff, "_kLogicData") is [var id, ..]
                        && _data.Row("SkillConfig", (long)id) != null) found.Add((long)id);
            _petProcRows = found;
        }
        return _petProcRows.Contains(GameData.Int(row, "_iID"));
    }

    /// <summary>A row the pet itself casts (Main Skill, guardian, aura or buff follow-up), which feeds no pet buff.</summary>
    private bool PetCast(JsonElement row) => GameData.Int(row, "_iSystemType") is ActiveGuardSkill or GuardianSkill
        || PetAuraRow(row) || PetProcRow(row);

    private PetEffectState PetState(GameSession s) =>
        _petEffects.TryGetValue(s.Role!.Id, out var state) ? state : _petEffects[s.Role!.Id] = new PetEffectState();

    private (ActiveBuff Buff, JsonElement Row)? LivePetBuff(GameSession s, int group)
    {
        long now = MonsterService.NowMs;
        foreach (var buff in s.Buffs)
            if (buff.ExpireMs > now && _data.Row("BuffConfig", buff.ConfigId) is { } row && GameData.Int(row, "_iBuffID") == group)
                return (buff, row);
        return null;
    }

    /// <summary>The master's current Main Skill description numbers, or none when the pet left the Main Slot.</summary>
    private double[] MainSkillDesc(GameSession s) => _skills.PetMainSkill?.Invoke(s) is { } pet
        && _data.Row("SkillConfig", pet.SkillId) is { } main ? Numbers(main, "_kDescData") : [];

    private Task PetBuffDealtAsync(GameSession s, MonsterUnit target, JsonElement row, long amount)
    {
        if (amount <= 0 || s.Role == null) return Task.CompletedTask;
        if (_guardianHits.TryGetValue(s, out var count) && count.RowId == GameData.Int(row, "_iID"))
            _guardianHits[s] = (count.RowId, count.Hits + 1);
        if (PetCast(row)) return Task.CompletedTask;
        return PetBuffHitAsync(s, target, row);
    }

    private async Task PetBuffHitAsync(GameSession s, MonsterUnit target, JsonElement row)
    {
        long now = MonsterService.NowMs;
        var desc = MainSkillDesc(s);
        if (LivePetBuff(s, MoonlightGroup) is { Row: var moon } && Numbers(moon, "_kLogicData") is [var bonus, var ms, ..]
            && _data.Row("BuffConfig", (long)bonus) != null)
        {
            double chance = desc.ElementAtOrDefault(5), far = desc.ElementAtOrDefault(8);
            bool sure = far > 0 && MonsterService.Distance(target, s) > far;
            if (sure || Rolls.Proc(10000) < chance * 100) await _buffs.AddAsync(s, (int)bonus, (int)ms, s.Role!.Id);
        }
        _petEffects.TryGetValue(s.Role!.Id, out var state);
        if (GameData.Int(row, "_iIsNormalAtk") == 1 && now >= (state?.PetrifyReadyMs ?? 0) && !target.Dead && target.Hp > 0
            && LivePetBuff(s, MedusaGazeGroup) is { Row: var gaze } && Numbers(gaze, "_kLogicData") is [var petrifyChance, var petrifyMs, ..])
        {
            state = PetState(s);
            state.PetrifyReadyMs = now + (long)(desc.ElementAtOrDefault(7) * 1000);
            using var tuple = JsonDocument.Parse($"[{PetrifyBuff},{petrifyChance},{petrifyMs}]");
            if (RollHostileBuff(tuple.RootElement, s, null, null, target, out int id, out int duration))
                await _buffs.ShowOnUnitAsync(target, id, duration, s.Role!.Id);
        }
        await PetTriggerHitAsync(s, target, desc);
        if (now >= (state?.FuryReadyMs ?? 0) && LivePetBuff(s, WarriorFuryGroup) is { Row: var fury }
            && Numbers(fury, "_kLogicData") is [var extraId, ..] && _data.Row("SkillConfig", (long)extraId) is { } extra)
        {
            state = PetState(s);
            state.FuryReadyMs = now + (long)(desc.ElementAtOrDefault(6) * 1000);
            AfterHit(s, () => target.Dead || target.Hp <= 0 ? Task.CompletedTask : ProcCastAsync(s, extra, target));
        }
    }

    private Task PetBuffKilledAsync(GameSession s, MonsterUnit monster)
    {
        if (s.Role == null || LivePetBuff(s, EvilCurseGroup) is not { } curse
            || Numbers(curse.Row, "_kLogicData") is not [var id, ..] || _data.Row("SkillConfig", (long)id) is not { } row)
            return Task.CompletedTask;
        var state = PetState(s);
        if (state.CurseGuid != curse.Buff.Guid)
        {
            int times = (int)MainSkillDesc(s).ElementAtOrDefault(5);
            (state.CurseGuid, state.CursesLeft) = (curse.Buff.Guid, times > 0 ? times : 5);
        }
        if (state.CursesLeft <= 0) return Task.CompletedTask;
        state.CursesLeft--;
        var at = monster.Pos.Clone();
        AfterHit(s, () => ProcCastAsync(s, row, null, face: s.Face, area: true, at: at));
        return Task.CompletedTask;
    }

    /// <summary>Run a follow-up once the hit or kill that caused it has been published.</summary>
    private void AfterHit(GameSession s, Func<Task> cast)
    {
        var role = s.Role;
        ScheduleRuntime(s.WorldScene, TimeSpan.Zero, async () =>
        {
            if (ReferenceEquals(s.Role, role) && !s.Dead && !s.SceneLoading) await cast();
        });
    }

    /// <summary>Count the guardian row's enemy hits while it lands (it lands in one pass), then drain.</summary>
    private async Task GuardianCastAsync(GameSession s, JsonElement row, Func<Task> cast)
    {
        int group = (int)GameData.Int(row, "_iSkillID");
        bool drains = PetDrains.TryGetValue(group, out var kind);
        if (!drains && !GuardianHitGroups.Contains(group))
        {
            await cast();
            await GuardianAfterAsync(s, row, 0);
            return;
        }
        _guardianHits[s] = (GameData.Int(row, "_iID"), 0);
        int hits;
        try { await cast(); }
        finally { hits = _guardianHits.Remove(s, out var count) ? count.Hits : 0; }
        if (s.Role == null || s.Dead) return;
        await GuardianAfterAsync(s, row, hits);
        if (!drains || hits <= 0) return;
        var desc = MainSkillDesc(s);
        double percent = desc.ElementAtOrDefault(4);
        var recipients = new List<GameSession> { s };
        switch (kind)
        {
            case DrainKind.PerEnemy: percent *= hits; break;
            case DrainKind.Storm: percent = percent * hits + desc.ElementAtOrDefault(7); break;
            case DrainKind.Allies:
                float radius = (float)desc.ElementAtOrDefault(2);
                int cap = Math.Max(1, (int)desc.ElementAtOrDefault(3));
                recipients.AddRange(_world.PlayersIn(s.WorldScene).Where(p => p != s && !p.Dead && p.Role != null && p.Progress != null
                        && Distance(p.Position, s.Position) <= radius)
                    .OrderBy(p => Distance(p.Position, s.Position)).Take(cap - 1));
                break;
        }
        if (percent <= 0) return;
        var stats = _character.Stats(s);
        var healer = PlayerAttacker(s, stats);
        var hit = new SkillHitInfo { SrcID = s.Role.Id, SkillId = (int)GameData.Int(row, "_iID"), SessionId = ++_sessionIds };
        foreach (var recipient in recipients)
        {
            var before = _character.Stats(recipient);
            long amount = (long)Math.Max(0, Math.Round(Math.Max(stats[103], stats[104]) * percent / 100
                * HealFactor(healer, PlayerDefender(recipient, before))));
            long actual = Math.Min(amount, Math.Max(0, before[101] - before[2000]));
            if (amount <= 0) continue;
            await _character.SetHpSpAsync(recipient, before[2000] + actual, null);
            hit.Dsts.Add(new DamageInfo { DstID = recipient.Role!.Id, Damage = actual, DamageType = DamageType.Heal, DstElement = 1 });
            foreach (var handler in Healed) handler(s, actual);
            foreach (var handler in HealedAsync) await handler(s, actual);
        }
        if (hit.Dsts.Count > 0) await ToPlayerAndViewersAsync(s, SkillHit, hit);
    }
}
