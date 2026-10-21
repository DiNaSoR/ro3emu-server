using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Skills that cast other skills: chance procs on a hit, follow-up hits, Lex Aeterna and the parry counters.
/// The data names the follow-up row in BuffConfig or SkillConfig _kLogicData; which hit starts it comes from each
/// skill's description (inferred where noted). A follow-up runs through the same effect code as a cast (damage, kill,
/// _kBuff2, 31101), with an extra-skill timeline (31121) so the client can show it beside the current action, and rolls no
/// further procs.
/// </summary>
public sealed partial class CombatService
{
    private const int EnvenomBuffGroup = 15120, DeadlyPoisonGroup = 15211, FearBreezeBuffGroup = 13301, FearBreezeHitGroup = 13304,
        JudexGroup = 14309, DoubleHolyLightGroup = 14311, HolySpiritSurgeGroup = 14312,
        WargStrikeGroup = 13312, FocusedArrowStrikeGroup = 13305,
        ParryGroup = 11210, DeathBoundGroup = 11311, AutoCounterGroup = 11105,
        ShieldReflectGroup = 11404, ShieldReflectSkill = 11410, ReflectDamageGroup = 11610, ReflectDamageSkill = 11609,
        CastingShieldGroup = 11611,
        BowSubType = 11, PoisonElement = 6;

    /// <summary>The Auto Counter parry's "took a melee Physical hit" mark in <see cref="GameSession.ProcState"/>.</summary>
    private const int AutoCounterHitKey = -AutoCounterGroup;

    /// <summary>Skills whose own _kLogicData [row] is a follow-up hit on the target: Envenom and Virus Spread "inflict
    /// 【Poisoned】 damage on the target" (1511601-1511610, PATK x 20% Poison).</summary>
    private static readonly HashSet<string> FollowUpScripts = ["Envenom", "VenomDust"];

    /// <summary>
    /// A server-started follow-up of <paramref name="row"/> from the player at <paramref name="target"/> (or, for an
    /// area follow-up, around the player facing <paramref name="face"/>; a ground follow-up centred at <paramref name="at"/>).
    /// No cost, cooldown or cast lock. A row with a multi-part hit (<see cref="HitPlan"/>) lands its packets as a cast does.
    /// </summary>
    internal async Task ProcCastAsync(GameSession s, JsonElement row, MonsterUnit? target, float? face = null, bool area = false,
        double scale = 1, long fixedDamage = 0, Vec3? at = null, int petId = 0)
    {
        if (s.Role == null || s.Dead || s.SceneLoading || target is { Dead: true } || target?.Hp <= 0) return;
        long id = ++_sessionIds;
        var origin = s.Position.Clone();
        float facing = target != null ? MonsterService.Angle(origin.X, origin.Z, target.X, target.Z) : face ?? s.Face;
        var request = new CastSkillReq
        {
            SkillId = (int)GameData.Int(row, "_iID"), TargetId = target?.Uid ?? 0,
            TargetPos = (at ?? target?.Pos ?? origin).Clone(), Face = facing,
        };
        var action = new ActiveSkillCast
        {
            Id = id, OwnerId = s.Role.Id, WorldScene = s.WorldScene, SceneGeneration = s.SceneGeneration, Attachment = s.Attachment,
            Request = request, Row = row, Origin = origin, StartMs = Now, ChantMs = 0, ActionMs = (int)GameData.Int(row, "_iActionTime"),
            Launched = true, Resolved = true, Proc = true, AreaProc = area, ProcScale = scale, ProcFixedDamage = fixedDamage,
        };
        await SendCastTimelineAsync(s, new UseInSkillInfo
        {
            SrcID = s.Role.Id, SkillID = request.SkillId, TargetId = request.TargetId, TargetPos = request.TargetPos, Face = facing,
            SessionId = id, SkillMs = action.ActionMs, StartTime = action.StartMs, PetConfigId = petId,
        }, highLevel: GameData.Int(row, "_iSkillEffectLevel") > 0, extra: true);
        if (HitPlan(row) is { } plan) await ApplyStagedEffectsAsync(s, action, request.TargetPos, plan);
        else await ApplyEffectsAsync(s, action, request.TargetPos);
    }

    /// <summary>
    /// After a cast's hits: its follow-up hit, then the chance procs, on the main target (the requested one, else the
    /// first hit; inferred: one roll per cast, not per target of an area skill).
    /// </summary>
    private async Task RunOutgoingProcsAsync(GameSession s, ActiveSkillCast action, List<(MonsterUnit Target, long Amount)> landed,
        Func<bool> current)
    {
        var main = landed.FirstOrDefault(l => l.Target.Uid == action.Request.TargetId).Target ?? landed[0].Target;
        var row = action.Row;
        if (FollowUpScripts.Contains(Script(row)) && Numbers(row, "_kLogicData") is [var followId]
            && _data.Row("SkillConfig", (long)followId) is { } follow && current())
            await ProcCastAsync(s, follow, main);
        foreach (var proc in OutgoingProcs(s, row).ToArray())
        {
            if (!current() || main.Dead || main.Hp <= 0) break;
            if (Rolls.Proc(10000) < proc.Chance) await ProcCastAsync(s, proc.Row, main);
        }
        await BurnAsync(s, row, landed, current);
    }

    /// <summary>
    /// The chance procs a hit of <paramref name="row"/> rolls:
    /// <list type="bullet">
    /// <item>Envenom 15111's buff (group 15120, [chance, row]): "When your Basic Attack deals damage, there is a 30%
    /// chance to inflict 【Poisoned】" (normal attacks, _iIsNormalAtk).</item>
    /// <item>Deadly Poison 15211's buff [chance, row]: "After attacking, there is a 50% chance to inflict 【Venom】"
    /// (any damaging hit, inferred).</item>
    /// <item>Fear Breeze 13303's buff (group 13301, [chance]): "when a targeted bow skill hits", 30% to cast Fear Breeze
    /// 13304 of the buff's level (a single-target skill other than the normal attack, with a bow).</item>
    /// <item>Double Holy Light 14311 (learned passive, same-id BuffConfig [chance, row, chance, row]): after Judex 14309,
    /// two independent rolls; Holy Spirit Surge 14312's buff sets both to its [10000] ("becomes 100%").</item>
    /// <item>Warg Strike 13312's auto-learned trigger 13317 ([10000, row]): "every 2.5 seconds, using Focused Arrow Strike
    /// casts this skill on the target" (the internal cooldown is the Warg Strike row's _kDescData[6] seconds).</item>
    /// </list>
    /// </summary>
    private IEnumerable<(int Chance, JsonElement Row)> OutgoingProcs(GameSession s, JsonElement row)
    {
        bool normal = GameData.Int(row, "_iIsNormalAtk") == 1;
        int group = (int)GameData.Int(row, "_iSkillID");
        long now = MonsterService.NowMs;
        foreach (var buff in s.Buffs.Where(b => b.ExpireMs > now).ToArray())
        {
            if (_data.Row("BuffConfig", buff.ConfigId) is not { } config) continue;
            switch ((int)GameData.Int(config, "_iBuffID"))
            {
                case EnvenomBuffGroup when normal:
                case DeadlyPoisonGroup:
                    foreach (var pair in LogicPairs(config)) yield return pair;
                    break;
                case FearBreezeBuffGroup when !normal && GameData.Int(row, "_iRangeType") != 2 && _skills.Weapon(s).SubType == BowSubType:
                    if (Numbers(config, "_kLogicData") is [var chance, ..]
                        && _data.Row("SkillConfig", _skills.RealId(FearBreezeHitGroup, (int)Math.Max(1, GameData.Int(config, "_iLevel")))) is { } hit)
                        yield return ((int)chance, hit);
                    break;
            }
        }
        foreach (var learned in LearnedProcs(s, row, normal)) yield return learned;
        if (s.Progress is not { } progress) yield break;
        if (group == JudexGroup && _skills.Level(progress, DoubleHolyLightGroup) is var holy and > 0
            && _data.Row("BuffConfig", _skills.RealId(DoubleHolyLightGroup, holy)) is { } light)
        {
            int? surge = s.Buffs.Where(b => b.ExpireMs > now).Select(b => _data.Row("BuffConfig", b.ConfigId))
                .Where(r => r is { } c && GameData.Int(c, "_iBuffID") == HolySpiritSurgeGroup)
                .Select(r => Numbers(r!.Value, "_kLogicData").FirstOrDefault() is var v and > 0 ? (int?)v : null).FirstOrDefault(v => v != null);
            foreach (var (chance, hit) in LogicPairs(light)) yield return (surge ?? chance, hit);
        }
        if (group == FocusedArrowStrikeGroup && _skills.Level(progress, WargStrikeGroup) is var warg and > 0
            && _data.Row("SkillConfig", _skills.RealId(WargStrikeGroup, warg)) is { } strike
            && Numbers(strike, "_kAutoLearnSkill") is [var trigger, ..] && _data.Row("BuffConfig", (long)trigger) is { } wargTrigger
            && now >= s.ProcState.GetValueOrDefault(WargStrikeGroup))
        {
            var desc = Numbers(strike, "_kDescData");
            s.ProcState[WargStrikeGroup] = now + (long)(desc.Length > 6 && desc[6] > 0 ? desc[6] * 1000 : 2500);
            foreach (var pair in LogicPairs(wargTrigger)) yield return pair;
        }
    }

    /// <summary>A buff's _kLogicData read as [chance bp, SkillConfig row, chance, row, ...].</summary>
    private IEnumerable<(int Chance, JsonElement Row)> LogicPairs(JsonElement buff)
    {
        var logic = Numbers(buff, "_kLogicData");
        for (int i = 0; i + 1 < logic.Length; i += 2)
            if (logic[i] > 0 && _data.Row("SkillConfig", (long)logic[i + 1]) is { } row) yield return ((int)logic[i], row);
    }

    /// <summary>Envenom 15111: "While active, Basic Attack becomes Poison-property".</summary>
    private bool PoisonsNormalAttack(GameSession s) => HasBuffGroup(s, EnvenomBuffGroup);

    /// <summary>
    /// Lex Aeterna 1410201 (_kBuffScript Buff_Lex_Aeterna, 1216 -2000): "The [Damage Taken Increase] effect ... disappears
    /// after the target takes damage". The hit that removes it already counted it.
    /// </summary>
    private async Task ConsumeOnHitDebuffsAsync(MonsterUnit target)
    {
        foreach (var buff in _buffs.UnitBuffs(target).Where(b => _data.Row("BuffConfig", b.ConfigId) is { } row
                     && row.TryGetProperty("_kBuffScript", out var script) && script.ValueKind == JsonValueKind.String
                     && script.GetString() == "Buff_Lex_Aeterna").Select(b => b.ConfigId).Distinct().ToArray())
            await _buffs.RemoveUnitConfigAsync(target, buff);
    }

    /// <summary>
    /// Auto Counter 11105: "enter a Parry state for 1 sec. ... When Parry ends, perform an Auto Counter against up to 5
    /// enemies within a 3m x 5m area ahead ... If you take melee Physical damage while parrying, Auto Counter damage
    /// increases by 50%." The parry buff 1110510's _kLogicData names the counter row (1110601-1110610).
    /// </summary>
    private void ScheduleAutoCounter(GameSession s, ActiveSkillCast action)
    {
        var parry = Tuples(action.Row, "_kBuff1").FirstOrDefault();
        if (parry.ValueKind != JsonValueKind.Array || parry.GetArrayLength() < 3
            || _data.Row("BuffConfig", (long)(GameData.Number(parry[0]) ?? 0)) is not { } buff
            || Numbers(buff, "_kLogicData") is not [var counterId] || _data.Row("SkillConfig", (long)counterId) is not { } counter) return;
        long duration = (long)(GameData.Number(parry[2]) ?? 1000);
        var desc = Numbers(action.Row, "_kDescData");
        double bonus = desc.Length > 3 ? desc[3] / 100 : 0;
        var role = s.Role;
        var lease = s.Attachment;
        long scene = s.WorldScene, generation = s.SceneGeneration;
        float face = action.Request.Face;
        s.ProcState[AutoCounterHitKey] = 0;
        ScheduleRuntime(scene, TimeSpan.FromMilliseconds(duration), async () =>
        {
            if (!ReferenceEquals(s.Role, role) || s.Dead || s.WorldScene != scene || s.SceneGeneration != generation
                || !ReferenceEquals(s.Attachment, lease) || lease?.Cancelled == true) return;
            bool hit = s.ProcState.Remove(AutoCounterHitKey, out long mark) && mark != 0;
            await ProcCastAsync(s, counter, null, face, area: true, scale: hit ? 1 + bonus : 1);
        });
    }

    /// <summary>
    /// A player's answers to a monster's melee hit (row null = the monster's plain swing, melee Physical):
    /// <list type="bullet">
    /// <item>Auto Counter's parry marks the hit for the +50% counter.</item>
    /// <item>Parry 11210: "When you take melee Physical damage, casts 【Auto Counter】 in front of you. Internal cooldown:
    /// 1 seconds" (the buff's _kLogicData row 1121101-1121110 at the attacker; cooldown the skill's _kDescData[2]).</item>
    /// <item>Death Bound 11311: "When you take melee damage, reflects the damage to nearby enemies, dealing ... 100% of the
    /// damage taken to up to 5 enemies within a 5-meter radius. Built-in Cooldown: 0.2 seconds" (buff _kDescData
    /// [radius, targets, percent, cooldown]; the hit is the buff's _kLogicData row 1138101-1138105 around the player).</item>
    /// <item>Shield Reflect 11410: "When you take Melee Physical DMG from an attacker within ${5} meters, deals ... PATK *
    /// ${2}% + ${6} to the attacker. This effect can trigger up to ${4} times per second" (buff 1140401, _kLogicData
    /// [11419]: the SReflect row of the learned level).</item>
    /// <item>Reflect Damage 11609: "When you are hit, deals ... PATK * ${2}% to up to ${3} enemies within a ${7}-meter
    /// radius. This effect can trigger up to ${5} times every ${4} seconds" (buff group 11610, _kLogicData [11618]: the
    /// Thorn row of its level around the player). Any hit counts, ranged and magic too.</item>
    /// <item>Shield Cast's 【Casting Shield】 (group 11611, _kLogicData [11618]): "When you are hit, triggers an Extra Auto
    /// Counter from 【Reflect Damage】": the mode-1 Thorn row (1161811-1161815) at the Reflect Damage level, once per
    /// Reflect Damage interval of its own (inferred; needs Reflect Damage learned).</item>
    /// </list>
    /// </summary>
    internal async Task ReactToMonsterHitAsync(MonsterUnit attacker, GameSession p, JsonElement? row, long damage)
    {
        if (damage <= 0 || p.Dead || p.Progress == null) return;
        bool melee = row is not { } r || GameData.Int(r, "_iDistanceType") != 2;
        bool physical = row is not { } dmg || GameData.Int(dmg, "_iDmgType") != 2;
        long now = MonsterService.NowMs;
        await ReactWithTriggersAsync(attacker, p, now);
        foreach (var buff in p.Buffs.Where(b => b.ExpireMs > now).ToArray())
        {
            if (p.Dead || _data.Row("BuffConfig", buff.ConfigId) is not { } config) continue;
            int group = (int)GameData.Int(config, "_iBuffID");
            if (group is ShieldReflectGroup or ReflectDamageGroup or CastingShieldGroup)
            {
                await ReflectAsync(attacker, p, group, melee && physical, now);
                continue;
            }
            if (!melee) continue;
            if (group == AutoCounterGroup && physical) p.ProcState[AutoCounterHitKey] = 1;
            if (group is not (ParryGroup or DeathBoundGroup) || group == ParryGroup && !physical
                || now < p.ProcState.GetValueOrDefault(group)
                || Numbers(config, "_kLogicData") is not [var answerId] || _data.Row("SkillConfig", (long)answerId) is not { } answer) continue;
            int level = (int)Math.Max(1, GameData.Int(config, "_iLevel"));
            if (group == ParryGroup)
            {
                var desc = _data.Row("SkillConfig", _skills.RealId(ParryGroup, level)) is { } skill ? Numbers(skill, "_kDescData") : [];
                p.ProcState[group] = now + (long)((desc.Length > 2 ? desc[2] : 1) * 1000);
                if (!attacker.Dead && attacker.Hp > 0) await ProcCastAsync(p, answer, attacker);
            }
            else
            {
                var desc = Numbers(config, "_kDescData");
                p.ProcState[group] = now + (long)((desc.Length > 3 ? desc[3] : 0.2) * 1000);
                long reflected = (long)Math.Round(damage * (desc.Length > 2 ? desc[2] : 100) / 100);
                if (reflected > 0) await ProcCastAsync(p, answer, null, p.Face, area: true, fixedDamage: reflected);
            }
        }
    }
    private async Task ReflectAsync(MonsterUnit attacker, GameSession p, int group, bool meleePhysical, long now)
    {
        if (now < p.ProcState.GetValueOrDefault(group)) return;
        if (group == ShieldReflectGroup)
        {
            int level = _skills.Level(p.Progress!, ShieldReflectSkill);
            if (!meleePhysical || attacker.Dead || attacker.Hp <= 0 || level <= 0
                || _data.Row("SkillConfig", _skills.RealId(ShieldReflectSkill, level)) is not { } skill
                || _data.Row("SkillConfig", _skills.RealId(11419, level)) is not { } answer) return;
            var desc = Numbers(skill, "_kDescData");
            if (desc.Length > 4 && MonsterService.Distance(p.Position.X, p.Position.Z, attacker.X, attacker.Z) > desc[4]) return;
            p.ProcState[group] = now + (long)(1000 / Math.Max(1, desc.Length > 3 ? desc[3] : 1));
            await ProcCastAsync(p, answer, attacker);
            return;
        }
        int reflectLevel = _skills.Level(p.Progress!, ReflectDamageSkill);
        if (reflectLevel <= 0 || _data.Row("SkillConfig", _skills.RealId(ReflectDamageSkill, reflectLevel)) is not { } reflect) return;
        var rdesc = Numbers(reflect, "_kDescData");
        double every = rdesc.Length > 3 ? rdesc[3] : 0.5, times = Math.Max(1, rdesc.Length > 4 ? rdesc[4] : 1);
        // Thorn 1161801-1161805 (mode 0) for Reflect Damage, its mode-1 copy 1161811-1161815 for Casting Shield.
        long thornId = 1161800 + (group == CastingShieldGroup ? 10 : 0) + reflectLevel;
        if (_data.Row("SkillConfig", thornId) is not { } thorn) return;
        p.ProcState[group] = now + (long)(every * 1000 / times);
        await ProcCastAsync(p, thorn, null, p.Face, area: true);
    }
}
