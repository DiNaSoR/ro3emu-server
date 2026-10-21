using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    private readonly PetService _pets;

    private Task SyncGuardianWheelAsync(GameSession session) => session.SkillPlans == null
        ? Task.CompletedTask : _skills.SyncAsync(session);

    private async Task CastGuardianAsync(GameSession s, CastSkillReq req)
    {
        var skill = _pets.Guardians(s).FirstOrDefault(g => g.IsPrimary && g.Id == req.SkillId && g.Kind != GuardianPetRules.Kind.Unsupported);
        int error = skill == null ? ErrorCodes.SkillNotLearned : GuardianError(s, skill, req);
        if (error != 0)
        {
            await RefuseCastAsync(s, req, error, 0);
            return;
        }
        if (skill != null) await BeginGuardianAsync(s, skill, req);
    }

    private int GuardianError(GameSession s, GuardianPetRules.Skill skill, CastSkillReq req)
    {
        if (s.Role == null || s.Progress == null || s.Dead || s.SceneLoading || s.SceneRecoveryRequired
            || !_sessions.All().Contains(s) || s.Attachment?.Cancelled == true) return ErrorCodes.SkillBadState;
        if (!_pets.GuardianEquipped(s, skill)) return ErrorCodes.SkillNotLearned;
        if (req.OpUnitId != 0 && req.OpUnitId != s.Role.Id) return ErrorCodes.SkillRequirements;
        if (!float.IsFinite(req.Face) || req.TargetPos is { } pos && !Finite(pos)) return ErrorCodes.Parameter;
        if ((_buffs.HasForbidden(s, ForbiddenState.CastSkill) || _buffs.HasForbidden(s, ForbiddenState.NormalAttack)) && !GuardianPetRules.DispelsControl(skill)
            && !GuardianShinobiDispel(s, skill).Any()
            || MatchSkillRule?.Invoke(s, skill.Id) == false) return ErrorCodes.SkillBadState;
        if (GameData.Int(skill.Active, "_iBasicType") != 1 || GameData.Int(skill.Effect, "_iBasicType") != 1)
            return ErrorCodes.SkillPassive;
        // These families have no configured cost. Never let an extra cast bypass an unfamiliar cost shape.
        if (GuardianPetRules.HasCost(skill.Active) || GuardianPetRules.HasCost(skill.Effect)) return ErrorCodes.SkillRequirements;
        int group = (int)GameData.Int(skill.Active, "_iSkillID"), mode = (int)GameData.Int(skill.Active, "_iMode");
        if (Math.Max(s.SkillCds.GetValueOrDefault(group), s.SkillModeCds.GetValueOrDefault((group, mode))) > Now)
            return ErrorCodes.SkillCooldown;
        return skill.Kind is GuardianPetRules.Kind.Damage or GuardianPetRules.Kind.Debuff
            ? ValidateTarget(s, GuardianPetRules.TargetRow(skill), req, s.Position, 0) : 0;
    }

    private async Task BeginGuardianAsync(GameSession s, GuardianPetRules.Skill skill, CastSkillReq req)
    {
        skill = GuardianPetRules.Prepare(_data, skill, _pets.Guardians(s));
        int duration = (int)Math.Max(1, GameData.Int(skill.Effect, "_iActionTime"));
        var action = new ActiveSkillCast
        {
            Id = ++_sessionIds, OwnerId = s.Role!.Id, WorldScene = s.WorldScene, SceneGeneration = s.SceneGeneration,
            Attachment = s.Attachment, Row = GuardianPetRules.DamageRow(skill, _character.Stats(s)),
            Request = new CastSkillReq
            {
                SkillId = skill.EffectId, TargetId = req.TargetId,
                TargetPos = req.TargetPos?.Clone() ?? s.Position.Clone(), Face = req.Face, IsExtraSkill = true
            },
            Origin = s.Position.Clone(), StartMs = Now, ChantMs = 0, ActionMs = duration,
            SourceEligible = () => _pets.GuardianEquipped(s, skill),
            GuardianDamage = [],
            GuardianGate = new(1, 1),
            GuardianOnce = [],
        };
        await StartCooldownAsync(s, skill.Active); // Reserve before publication can yield to another request.
        bool deathWitch = GuardianPetRules.Script(skill.Effect) == "Siwanliandao";
        if (GuardianPetRules.DispelsControl(skill)) await _buffs.DispelControlsAsync(s, allDebuffs: deathWitch);
        if (deathWitch)
            foreach (var tuple in Tuples(skill.Active, "_kBuff1"))
                if (CurrentCastMap(s, action) && RollBuff(tuple, out int id, out int ms))
                    await _buffs.AddGuardianAsync(s, id, ms, action.OwnerId);
        foreach (var shinobi in GuardianShinobiDispel(s, skill).ToArray())
        {
            if (!CurrentCastMap(s, action)) return;
            await _buffs.DispelControlsAsync(s);
            if (CurrentCastMap(s, action) && _pets.GuardianEquipped(s, shinobi))
                await _buffs.AddGuardianAsync(s, GuardianPetRules.ControlImmunityBuff,
                    (int)(GuardianPetRules.Description(shinobi, 0) * 1000), action.OwnerId);
        }
        if (!CurrentCastMap(s, action)) return;
        await s.SendGameAsync(UseSkill, new CastSkillRsp { SessionId = action.Id, IsAutoCast = req.IsAutoCast });
        if (!CurrentCastMap(s, action)) return;
        await SendCastTimelineAsync(s, new UseInSkillInfo
        {
            SrcID = action.OwnerId, SkillID = skill.EffectId, PetConfigId = skill.PetId,
            TargetId = req.TargetId, TargetPos = action.Request.TargetPos, Face = req.Face,
            SessionId = action.Id, SkillMs = duration, StartTime = action.StartMs,
        }, extra: true); // LC_Scene_Sync._useExtraSkill requires child type 41 + petConfigId.
        var template = _data.SkillTemplate(GameData.Int(skill.Effect, "_iScriptShow"));
        var pulses = GuardianPetRules.Pulses(_data, skill);
        for (int i = 0; i < pulses.Length; i++)
        {
            var pulse = pulses[i];
            bool first = i == 0;
            bool last = i == pulses.Length - 1;
            int delay = ImpactOffset(template, pulse.Point, 0, duration) + pulse.DelayMs.GetValueOrDefault();
            ScheduleRuntime(action.WorldScene, TimeSpan.FromMilliseconds(delay), () => ResolveGuardianAsync(s, skill, action, pulse, first, last));
        }
        if (CurrentCastMap(s, action)) await SyncGuardianWheelAsync(s);
    }

    private IEnumerable<GuardianPetRules.Skill> GuardianShinobiDispel(GameSession source, GuardianPetRules.Skill primary) =>
        _pets.Guardians(source).Where(p => !p.IsPrimary && p.Reactive == GuardianPetRules.Reaction.ShinobiDispel
            && GuardianPetRules.ReactionTriggered(p.Reactive, primary.Effect));

    private async Task ResolveGuardianAsync(GameSession s, GuardianPetRules.Skill skill, ActiveSkillCast action, GuardianPetRules.Pulse pulse, bool first, bool last)
    {
        if (action.GuardianGate is not { } gate) return;
        await gate.WaitAsync();
        try { await ResolveGuardianPulseAsync(s, skill, action, pulse, first, last); }
        finally { gate.Release(); }
    }

    private async Task ResolveGuardianPulseAsync(GameSession s, GuardianPetRules.Skill skill, ActiveSkillCast action, GuardianPetRules.Pulse pulse, bool first, bool last)
    {
        if (!CurrentCastMap(s, action)) return;
        var pulseRow = GuardianPetRules.PulseRow(action.Row, pulse, skill.Active);
        var request = action.Request.Clone();
        if (GuardianPetRules.Script(skill.Effect) == "PetSkill_2_2_EXiaoNanJue")
            request.TargetId = GuardianOwlTarget(s, skill, action);
        var hit = new ActiveSkillCast
        {
            Id = action.Id, OwnerId = action.OwnerId, WorldScene = action.WorldScene, SceneGeneration = action.SceneGeneration,
            Attachment = action.Attachment, Request = request, Row = pulseRow,
            Origin = action.Origin, StartMs = action.StartMs, ChantMs = 0, ActionMs = action.ActionMs,
            SourceEligible = () => CurrentCastMap(s, action),
            GuardianDamage = [],
        };
        if (skill.Kind is GuardianPetRules.Kind.Damage or GuardianPetRules.Kind.Debuff)
            await ApplyEffectsAsync(s, hit, action.Request.TargetPos ?? action.Origin);
        foreach (var (targetId, amount) in hit.GuardianDamage) RecordGuardianDamage(action, targetId, amount);
        if (CurrentCastMap(s, action)) await GuardianHitReactionsAsync(s, skill, action, hit);
        if (CurrentCastMap(s, action) && hit.GuardianDamage.Count > 0 && GuardianPetRules.Script(skill.Effect) == "JingZhunShouShu")
        {
            var desc = Numbers(skill.Active, "_kDescData");
            if (desc.Length >= 5) await GuardianAreaHealAsync(s, skill, action, (float)desc[2], (int)desc[3], desc[4]);
        }
        if (CurrentCastMap(s, action) && pulse.HealCoefficient is { } heal && hit.GuardianDamage.Count > 0)
        {
            var stats = _character.Stats(s);
            long amount = (long)Math.Floor(Math.Max(stats[103], stats[104]) * heal / 100 * hit.GuardianDamage.Count);
            await GuardianHealAsync(s, s, skill.EffectId, action.Id, GuardianPetRules.DirectHealing(skill.Effect, amount),
                () => CurrentCastMap(s, action));
        }
        if (first && CurrentCastMap(s, action)) await GuardianShinobiMoveAsync(s, skill, action);
        if (first && CurrentCastMap(s, action)) await GuardianSupportAsync(s, skill, action);
        if (first && CurrentCastMap(s, action)) await GuardianSpecialSupportAsync(s, skill, action);
        if (first && CurrentCastMap(s, action)) await GuardianReactionsAsync(s, skill, action);
        if (last && CurrentCastMap(s, action)) await GuardianCoreDamageAsync(s, action);
    }

    private GameSession[] GuardianAllies(GameSession s, GuardianPetRules.Skill skill)
    {
        if (GuardianPetRules.Script(skill.Effect) is "moqihuanrao" or "shengguanglingyu" or "BaXiLiSiKe_HuZhu" or "renfamizong") return [s];
        bool orc = GuardianPetRules.Script(skill.Effect) == "PetSkill_2_2_ShouRenZhanShiZhang";
        if (skill.Kind is not (GuardianPetRules.Kind.Heal or GuardianPetRules.Kind.Buff) && !orc) return [s];
        float radius = (float)(Numbers(skill.Effect, "_kRange").FirstOrDefault() / 100);
        int maximum = (int)Math.Max(1, GameData.Int(skill.Effect, "_iTargetMax"));
        if (orc)
        {
            radius = (float)Numbers(skill.Active, "_kDescData").ElementAtOrDefault(3);
            maximum = (int)Numbers(skill.Active, "_kDescData").ElementAtOrDefault(4);
        }
        // (inferred) Ally means non-hostile player; owner first, then distance/ID. Original team scope is unverified.
        return [.. _world.PlayersIn(s.WorldScene).Where(p => p.Role != null && !p.Dead && !p.SceneLoading && !p.SceneRecoveryRequired
                && PlayerHostile?.Invoke(s, p) != true && PlayerHostile?.Invoke(p, s) != true && !CanPlayerHit(s, p) && !CanPlayerHit(p, s)
                && Distance(s.Position, p.Position) <= radius)
            .OrderBy(p => p == s ? 0 : 1).ThenBy(p => Distance(s.Position, p.Position))
            .ThenBy(p => p.Role!.Id).Take(maximum)];
    }

    private async Task GuardianSupportAsync(GameSession s, GuardianPetRules.Skill skill, ActiveSkillCast action)
    {
        var ownerStats = _character.Stats(s);
        if (GuardianPetRules.Script(skill.Effect) == "PetSkill_2_2_KuangBaoYueYeMao") _guardianDamageCharges.Remove(s);
        foreach (var recipient in GuardianAllies(s, skill))
        {
            var role = recipient.Role;
            var attachment = recipient.Attachment;
            long generation = recipient.SceneGeneration;
            bool Current() => CurrentCastMap(s, action) && role != null && ReferenceEquals(recipient.Role, role)
                && !recipient.Dead && !recipient.SceneLoading && recipient.SceneGeneration == generation
                && recipient.WorldScene == action.WorldScene && ReferenceEquals(recipient.Attachment, attachment)
                && attachment?.Cancelled != true;
            foreach (var tuple in Tuples(skill.Effect, "_kBuff1"))
            {
                if (recipient != s && GuardianPetRules.Script(skill.Effect) == "ShengGuangJiangLin") continue;
                if (!Current()) break;
                if (!RollBuff(tuple, out int buffId, out int ms)) continue;
                long shield = GuardianShield(skill, buffId, ownerStats, _character.Stats(recipient));
                if (GuardianPetRules.Script(skill.Effect) == "xianzhenchongfeng")
                    shield *= 1 + Math.Min((int)Numbers(skill.Active, "_kDescData").ElementAtOrDefault(6), action.GuardianDamage?.Count ?? 0);
                await _buffs.AddGuardianAsync(recipient, buffId, ms, action.OwnerId, shield);
            }
            if (!Current() || skill.Kind != GuardianPetRules.Kind.Heal) continue;
            long amount = (long)Math.Max(0, Math.Floor(Math.Max(ownerStats[GuardianPetRules.PhysicalAttack], ownerStats[GuardianPetRules.MagicAttack])
                * Numbers(skill.Effect, "_kDamageParam1").FirstOrDefault() / 10000));
            await GuardianHealAsync(s, recipient, skill.EffectId, action.Id, GuardianPetRules.DirectHealing(skill.Effect, amount), Current);
        }
    }

    private long GuardianShield(GuardianPetRules.Skill skill, int buffId, StatSheet owner, StatSheet recipient)
    {
        // Goat BuffConfig._kDamageFormulaExtra/_kDamageParamExtra explicitly uses MaxHP.
        // Bobo's ordinary description uses adaptive ATK; its tower HP formula must not leak into this path.
        // (inferred) Floor and replace-on-refresh use the shared finite support-buff lifecycle.
        if ((skill.Kind == GuardianPetRules.Kind.Shield || GuardianPetRules.Script(skill.Effect) == "xianzhenchongfeng")
            && GuardianPetRules.Script(skill.Effect) != "BoLiPaoPao"
            && _data.Row("BuffConfig", buffId) is { } buff)
            return PetCombatRules.AttributeAmount(buff, owner, recipient);
        return GuardianPetRules.Script(skill.Effect) == "BoLiPaoPao"
            ? (long)Math.Max(0, Math.Floor(Math.Max(owner[GuardianPetRules.PhysicalAttack], owner[GuardianPetRules.MagicAttack])
                * Numbers(skill.Active, "_kDescData").FirstOrDefault() / 100)) : 0;
    }

    private async Task GuardianHealAsync(GameSession source, GameSession recipient, int skillId, long castId, long amount, Func<bool> current)
    {
        if (!current()) return;
        foreach (var modifier in ExtraHeal) amount = modifier(source, amount);
        var stats = _character.Stats(recipient);
        amount = ModifiedHealing(_buffs.GuardianAttributes(source), _buffs.GuardianAttributes(recipient), amount);
        long actual = Math.Min(Math.Max(0, amount), Math.Max(0, stats[GuardianPetRules.Hp] - stats[GuardianPetRules.CurrentHp]));
        long owner = source.Role!.Id, target = recipient.Role!.Id;
        await _character.SetHpSpAsync(recipient, stats[GuardianPetRules.CurrentHp] + actual, null);
        if (!current()) return;
        await ToPlayerAndViewersAsync(source, SkillHit, new SkillHitInfo
        {
            SrcID = owner, SkillId = skillId, SessionId = castId, RootCtxGuid = castId,
            ShowPoint = HitEffect(_data.Row("SkillConfig", skillId) is { } row ? GameData.Int(row, "_iScriptShow") : 0),
            Dsts = { new DamageInfo { DstID = target, Damage = actual, DamageType = DamageType.Heal, DstElement = 1 } },
        });
        if (!current()) return;
        foreach (var observer in Healed) observer(source, actual);
        foreach (var observer in HealedAsync)
        {
            if (!current()) break;
            await observer(source, actual);
        }
    }
}
