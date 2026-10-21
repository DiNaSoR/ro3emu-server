using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Monsters fighting monsters: a story encounter's allied companions (camp 1 story templates such as 5300007-5300009)
/// and the boss they fight (DungeonService.Encounter). The original server's companion AI is not in the client except
/// one tree (MainStory_MuShi); the damage, heal and buff rules below reuse the formulas the server already applies to
/// players, monsters and tower pets (inferred).
/// </summary>
public sealed partial class CombatService
{
    /// <summary>
    /// The player a room owner credits with a kill that a non-player landed (a companion's last hit on the boss): the
    /// ordinary kill settlement, rewards and dungeon clear then run for that player. Null: no credit, the hit stops at 1 HP.
    /// </summary>
    public List<Func<MonsterUnit, GameSession?>> NonPlayerKillCredit { get; } = new();

    /// <summary>The normal swing of one monster on another (MonsterService.AttackMonster).</summary>
    private async Task MonsterAttackMonsterAsync(MonsterUnit m, MonsterUnit target)
    {
        if (m.Dead || m.Hp <= 0 || target.Dead || target.Hp <= 0 || _data.Row("SkillConfig", m.Template.NormalSkill) is not { } row) return;
        long castId = ++_sessionIds;
        await SendMonsterTimelineAsync(m, new UseInSkillInfo
        {
            SrcID = m.Uid, SkillID = m.Template.NormalSkill, TargetId = target.Uid, TargetPos = target.Pos, Face = m.Face,
            SessionId = castId, SkillMs = m.Template.SkillMs, StartTime = Now,
        });
        ScheduleRuntime(m.WorldScene, TimeSpan.FromSeconds(m.Template.HitPointSeconds), async () =>
        {
            if (m.Dead || m.Hp <= 0 || target.Dead || target.Hp <= 0 || m.WorldScene != target.WorldScene
                || MonsterService.Distance(m.X, m.Z, target.X, target.Z) > m.Template.AttackRange + 2f) return;
            await MonsterDamagesMonsterAsync(m, target, row, castId);
        });
    }

    /// <summary>
    /// One skill row's damage from <paramref name="source"/> on <paramref name="target"/>, scaled by
    /// <paramref name="scaleBp"/> (one hit of a multi-hit skill). A companion (camp 1) never drops below 1 HP: the story
    /// NPCs are not meant to die (inferred). A lethal hit on a hostile monster settles as the credited player's kill.
    /// </summary>
    internal async Task<long> MonsterDamagesMonsterAsync(MonsterUnit source, MonsterUnit target, JsonElement row, long castId,
        long scaleBp = 10000)
    {
        if (source.Dead || source.Hp <= 0 || target.Dead || target.Hp <= 0) return 0;
        var attacker = AsMonster(new Fighter(source.Template.Atk, source.Template.Matk, source.Template.Hit, source.Template.Cri,
            source.Template.Level, source.Template.Element), source.Template, UnitSheet(source));
        bool ally = IsAlly(target);
        var defender = ally
            ? new Fighter(0, 0, 0, 0, target.Template.Level, target.Template.Element)
            { Def = target.Template.Def, Mdef = target.Template.Mdef, Flee = target.Template.Flee }
            : AsDefender(target);
        var (damage, result) = Damage(attacker, defender, row);
        damage = ScaleMonsterDamage(damage, source.DamageRatio);
        if (scaleBp != 10000 && damage > 0) damage = Math.Max(1, damage * scaleBp / 10000);
        if (!ally) damage = await _buffs.AbsorbAsync(target, damage);
        var hit = new SkillHitInfo
        {
            SrcID = source.Uid, SkillId = (int)GameData.Int(row, "_iID"), ShowPoint = HitEffect(GameData.Int(row, "_iScriptShow")),
            SessionId = castId, SrcElement = source.Template.Element,
        };
        long previousHp = target.Hp;
        if (ally)
        {
            target.Hp = Math.Max(1, target.Hp - damage);
            hit.Dsts.Add(new DamageInfo { DstID = target.Uid, Damage = damage, HitResult = result, DstElement = target.Template.Element });
            await _world.ToUnitViewersAsync(target, SkillHit, hit);
            await _world.ToUnitViewersAsync(target, AoiAttrChange, MonsterService.Hp(target));
            await FightingAsync(target);
            return damage;
        }
        GameSession? credit = NonPlayerKillCredit.Select(f => f(target)).FirstOrDefault(c => c != null);
        if (credit?.Role == null)
        {
            target.Hp = Math.Max(1, target.HpAfter(target.Hp, damage)); // nobody to settle a kill for
            hit.Dsts.Add(new DamageInfo { DstID = target.Uid, Damage = damage, HitResult = result, DstElement = target.Template.Element });
            await _world.ToUnitViewersAsync(target, SkillHit, hit);
            await _world.ToUnitViewersAsync(target, AoiAttrChange, MonsterService.Hp(target));
            return damage;
        }
        var role = credit.Role;
        var attachment = credit.Attachment;
        long generation = credit.SceneGeneration;
        bool Current() => ReferenceEquals(credit.Role, role) && ReferenceEquals(credit.Attachment, attachment)
            && !credit.SceneLoading && credit.SceneGeneration == generation && credit.WorldScene == target.WorldScene;
        target.Hp = target.HpAfter(target.Hp, damage);
        bool lethal = target.Hp == 0;
        try
        {
            await SettleMonsterHitAsync(credit, target, previousHp, damage);
        }
        catch (OperationCanceledException e)
        {
            _log.LogInformation("Companion hit on {Uid} not settled: {Reason}", target.Uid, e.Message);
            return 0;
        }
        hit.Dsts.Add(new DamageInfo
        {
            DstID = target.Uid, Damage = damage, HitResult = result,
            DstElement = target.Template.Element, IsDead = target.Hp == 0
        });
        await _world.ToUnitViewersAsync(target, SkillHit, hit);
        await _world.ToUnitViewersAsync(target, AoiAttrChange, MonsterService.Hp(target));
        if (!lethal)
        {
            await FightingAsync(target);
            return damage;
        }
        if (!target.DeathDurable) return damage;
        try
        {
            await _monsters.KillAsync(target);
            foreach (var other in _sessions.All().Where(o => o.Attack?.Target == target))
            {
                other.Attack = null;
                await other.SendGameAsync(SkillEnd, new SkillEndNotify
                {
                    SrcID = other.Role!.Id, SessionId = castId, Type = SkillEndNotify.Types.SkillEndType.Interrupted,
                    InterruptedInfo = new SkillInterruptedInfo { Type = SkillInterruptedInfo.Types.SkillInterruptType.LockerInvalid },
                });
            }
            await PublishDurableKillAsync(credit, target, Current);
        }
        finally
        {
            if (target.Dead) await FinalizeLethalPublicationAsync(target);
        }
        return damage;
    }

    /// <summary>A story companion: a camp-1 monster its encounter owns.</summary>
    internal static bool IsAlly(MonsterUnit m) => m.ScriptedTargeting && (m.CampOverride ?? m.Template.Camp) == 1;

    /// <summary>
    /// A companion's heal (dmg type 2, e.g. MainStory_Heal 5330801 / Sanctuary 5330701): MATK x _kDamageParam1 /
    /// 10000 + _kDamageParam2, as the tower pets' magic heal (inferred), on each player and companion given.
    /// </summary>
    internal async Task MonsterHealsAsync(MonsterUnit source, JsonElement row, IReadOnlyList<GameSession> players,
        IReadOnlyList<MonsterUnit> units, long castId)
    {
        if (source.Dead) return;
        long amount = Math.Max(0, (long)Math.Floor(source.Template.Matk * Numbers(row, "_kDamageParam1").FirstOrDefault() / 10000
            + Numbers(row, "_kDamageParam2").FirstOrDefault()));
        var hit = new SkillHitInfo
        {
            SrcID = source.Uid, SkillId = (int)GameData.Int(row, "_iID"), SessionId = castId,
            SrcElement = source.Template.Element, ShowPoint = HitEffect(GameData.Int(row, "_iScriptShow"))
        };
        foreach (var unit in units.Where(u => !u.Dead && u.Hp > 0))
        {
            long actual = Math.Min(amount, Math.Max(0, unit.Template.MaxHp - unit.Hp));
            unit.Hp += actual;
            hit.Dsts.Add(new DamageInfo { DstID = unit.Uid, Damage = actual, DamageType = DamageType.Heal });
            await _world.ToUnitViewersAsync(unit, AoiAttrChange, MonsterService.Hp(unit));
        }
        foreach (var p in players)
        {
            var role = p.Role;
            if (role == null || p.Dead || p.SceneLoading) continue;
            long actual;
            using (await CharacterService.Lock(p))
            {
                if (p.Dead || p.SceneLoading || !ReferenceEquals(role, p.Role)) continue;
                var stats = _character.Stats(p);
                actual = Math.Min(amount, Math.Max(0, stats[101] - stats[2000]));
                await _character.SetHpSpAsync(p, stats[2000] + actual, null);
            }
            hit.Dsts.Add(new DamageInfo { DstID = role.Id, Damage = actual, DamageType = DamageType.Heal });
        }
        if (hit.Dsts.Count > 0) await _world.ToUnitViewersAsync(source, SkillHit, hit);
    }

    /// <summary>A skill row's _kBuff1 tuples [buff, chance, ms] on each player and unit given (ms -1: permanent on a unit).</summary>
    internal async Task MonsterBuffsAsync(MonsterUnit source, JsonElement row, IReadOnlyList<GameSession> players,
        IReadOnlyList<WorldUnit> units)
    {
        foreach (var tuple in Tuples(row, "_kBuff1"))
        {
            if (source.Dead || !RollBuff(tuple, out int id, out int duration)) continue;
            foreach (var p in players.Where(p => p.Role != null && !p.Dead && !p.SceneLoading))
                await _buffs.AddSupportAsync(p, id, Math.Max(0, duration), source.Uid);
            foreach (var unit in units)
            {
                if (unit is MonsterUnit { Dead: true }) continue;
                if (duration < 0) await _buffs.AddPermanentAsync(unit, id, source.Uid);
                else await _buffs.AddSupportAsync(unit, id, duration, source.Uid);
            }
        }
    }
}
