using Romsg;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// GM panel combat aids (GmCommands, 30300): a GM hit on a monster that settles like any other player hit (kill
/// rewards and quest kills go to the GM), and the server-wide fixed damage roll (GmDamageFix).
/// </summary>
public sealed partial class CombatService
{
    /// <summary>GmDamageFix: every damage roll takes the middle of DamageFloat [9700, 10300] (test aid, in memory).</summary>
    public bool FixedDamageRoll { get; set; }

    private double DamageFloatRoll() => FixedDamageRoll ? 0.5 : Rolls.DamageFloat();

    /// <summary>
    /// GmKillEntity / GmChangeHp on a monster: <paramref name="amount"/> damage from the GM through the ordinary hit
    /// settlement, the same path as a damage-over-time tick (PeriodicMonsterDamageAsync). Returns the HP left, or null
    /// when the monster cannot be hit by the GM (another scene, dead, protected).
    /// </summary>
    public async Task<long?> GmDamageMonsterAsync(GameSession s, MonsterUnit m, long amount)
    {
        var role = s.Role;
        if (role == null || s.SceneLoading || s.Dead || s.WorldScene != m.WorldScene || m.Dead || m.Hp <= 0
            || !CanHitMonster(s, m) || amount <= 0) return null;
        var attachment = s.Attachment;
        long generation = s.SceneGeneration, scene = s.WorldScene;
        bool Current() => ReferenceEquals(s.Role, role) && ReferenceEquals(s.Attachment, attachment) && !s.SceneLoading
            && s.SceneGeneration == generation && s.WorldScene == scene && _sessions.Contains(s);
        long before = m.Hp;
        m.Hp = m.HpAfter(before, amount);
        long dealt = before - m.Hp;
        bool lethal = m.Hp == 0;
        try
        {
            try { await SettleMonsterHitAsync(s, m, before, dealt); }
            catch (Exception e) when (e is not EventSettlementOutcomeUnknownException)
            {
                _log.LogDebug(e, "GM hit on {Uid} not settled", m.Uid);
                return null;
            }
            var hit = new SkillHitInfo
            {
                SrcID = role.Id, SessionId = ++_sessionIds,
                Dsts = { new DamageInfo { DstID = m.Uid, Damage = dealt, DstElement = m.Template.Element, IsDead = m.Hp == 0 } },
            };
            foreach (var viewer in m.Viewers.Append(s).Distinct().ToArray()) await viewer.SendGameAsync(SkillHit, hit);
            if (m.Hp == 0 && !m.DeathDurable) return 0; // another hit is still committing that death
            await _world.ToUnitViewersAsync(m, AoiAttrChange, MonsterService.Hp(m));
            await FightingAsync(m);
            if (m.Hp > 0)
            {
                if (Current()) _monsters.Engage(m, s);
                return m.Hp;
            }
            if (!lethal) return 0;
            _log.LogInformation("GM {Name} killed {Monster} ({Npc})", role.Name, m.Uid, m.Template.NpcId);
            await _monsters.KillAsync(m);
            foreach (var other in _sessions.All().Where(o => o.Attack?.Target == m))
            {
                other.Attack = null;
                await other.SendGameAsync(SkillEnd, new SkillEndNotify
                {
                    SrcID = other.Role!.Id, Type = SkillEndNotify.Types.SkillEndType.Interrupted,
                    InterruptedInfo = new SkillInterruptedInfo { Type = SkillInterruptedInfo.Types.SkillInterruptType.LockerInvalid },
                });
            }
            await PublishDurableKillAsync(s, m, Current);
            return 0;
        }
        finally
        {
            if (lethal && m.Hp == 0 && m.DeathDurable)
            {
                if (!m.Dead)
                    try { await _monsters.KillAsync(m); }
                    catch (Exception e) { _log.LogError(e, "Could not finalize GM kill of {Uid}", m.Uid); }
                await PublishDurableKillAsync(s, m, Current);
                await FinalizeLethalPublicationAsync(m);
            }
        }
    }

    /// <summary>GmChangeHp heal on a monster: current HP up, capped at its maximum, shown to its viewers.</summary>
    public async Task<long?> GmHealMonsterAsync(MonsterUnit m, long amount)
    {
        if (m.Dead || m.Hp <= 0 || amount <= 0) return null;
        m.Hp = Math.Min(m.Template.MaxHp, m.Hp + amount);
        await _world.ToUnitViewersAsync(m, AoiAttrChange, MonsterService.Hp(m));
        return m.Hp;
    }

    /// <summary>GmKillEntity / GmChangeHp on a player: unattributed damage, as the Survivor Battle zone deals it.</summary>
    public Task<long> GmDamagePlayerAsync(GameSession p, long amount) => EnvironmentDamageAsync(p, amount);
}
