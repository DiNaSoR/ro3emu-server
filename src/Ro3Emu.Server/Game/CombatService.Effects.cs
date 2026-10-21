using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>An owned skill NPC/trigger. NPCConfig identity and MG_Entity.owner survive AOI re-entry.</summary>
public sealed class SkillObject : WorldUnit
{
    public required GameSession OwnerSession { get; init; }
    public required long OwnerId { get; init; }
    public required int NpcId { get; init; }
    public required int EntityType { get; init; }
    public required ActiveSkillCast Cast { get; init; }
    public required long ExpiresMs { get; init; }
    public long NextTickMs { get; set; }
    public int IntervalMs { get; init; }
    public float Radius { get; init; }
    public bool Trap { get; init; }
    public bool Busy { get; set; }
    public bool Removing { get; set; }
    public int Charges { get; set; }
    /// <summary>Strikes left for a field with a set number of waves (Meteor Storm); 0 = strike until it expires, -1 = done.</summary>
    public int WavesLeft { get; set; }
    public int ProtectionDistanceType { get; init; }
    /// <summary>Enemy touches before the field ends early (Fire Wall); 0 = no limit. <see cref="Touched"/> counts them.</summary>
    public int TouchLimit { get; init; }
    public int Touched { get; set; }
    /// <summary>Meters a touched enemy is knocked back from the field (Fire Wall); 0 = none.</summary>
    public float PushMeters { get; init; }
    public HashSet<int> Skills { get; } = new();

    public override EntityBase Entity() => new()
    {
        Uuid = (ulong)Uid, EntityTemplateID = (ulong)NpcId, EntityType = EntityType, Owner = OwnerId,
        AiMasterEntityId = (ulong)OwnerId, EntityCamp = 1, State = EntityState.Alive, BornTimeMs = Cast.StartMs,
        Face = Face, MovementInfo = new MovementInfo { Pos = Pos, Face = Face, ServerTime = MonsterService.NowMs },
        NotifyType = EntityBase.Types.NotifyType.All,
    };
}

public sealed partial class CombatService
{
    private long _skillObjectIds = 0x600000000000L;

    private async Task ResolveCastAsync(GameSession s, ActiveSkillCast action)
    {
        var row = action.Row;
        if (!CurrentCastMap(s, action)) return;
        foreach (var handler in CastLanded)
        {
            await handler(s, row);
            if (!CurrentCastMap(s, action)) return;
        }
        if (GameData.Int(row, "_iSystemType") == ActiveGuardSkill)
        {
            await PetGuardianAsync(s, action);
            return;
        }
        if (Script(row) == "Resurrection")
        {
            var target = FindPlayer(s, action.Request.TargetId, dead: true);
            if (target == null || target.SceneLoading || _buffs.HasForbidden(target, 16)
                || s.WorldState.ResurrectionCastId != action.Id
                || Distance(s.Position, target.Position) > Range(row) + 1.5f) return;
            s.WorldState.ResurrectionCastId = 0;
            if (action.Request.SkillId == PublicSkill("WorldTreeSkillId"))
            {
                if (ReviveItemSkill != null && await ReviveItemSkill(s, target, row))
                    await _buffs.NotifyWorldTreeReviveAsync(s, target.Role!.Name);
            }
            else if (ReviveOffer != null) await ReviveOffer(s, target, row);
            return;
        }
        if (Script(row) == "GaBanTaYin") await ClearGroundAsync(s, action);
        if (!CurrentCastMap(s, action)) return;
        if (await CreateSkillObjectAsync(s, action)) return;
        if (!CurrentCastMap(s, action)) return;
        // SkillConfig._bMovement is enum_SkillLockMoveType (may the caster walk while casting), not a displacement.
        await MoveCasterAsync(s, action);
        if (!CurrentCastMap(s, action)) return;
        await SpendCastResourcesAsync(s, action);
        if (!CurrentCastMap(s, action)) return;
        if (await WargHuntAsync(s, action)) { }
        else if (!await StartFlightAsync(s, action.Row)) await ResolveHitsAsync(s, action);
        if (CurrentCastMap(s, action)) await AfterCastResourcesAsync(s, action);
    }

    /// <summary>The landed cast's hits: its server bolt, its staged packets, or one application (plus bounces).</summary>
    private async Task ResolveHitsAsync(GameSession s, ActiveSkillCast action)
    {
        var row = action.Row;
        if (action.Operator == null && ServerBullet(GameData.Int(row, "_iScriptShow")) is { Effect: > 0 } bullet)
        {
            await LaunchBulletAsync(s, action, bullet);
            return;
        }
        if (Script(row) == "Counter_Attack") ScheduleAutoCounter(s, action);
        var center = action.Request.TargetPos ?? action.Origin;
        if (HitPlan(row) is { } plan)
        {
            await ApplyStagedEffectsAsync(s, action, center, plan);
            return;
        }
        var bounceFrom = Script(row) == "PoisonBlade" ? FindMonster(s.WorldScene, action.Request.TargetId) : null;
        await ApplyEffectsAsync(s, action, center);
        if (bounceFrom != null && CurrentCastMap(s, action)) ScheduleBounces(s, action, bounceFrom);
    }

    /// <summary>
    /// The server script's bolt (31120) flies first; its damage, 31101 and the damage numbers follow when the first
    /// bolt lands, m_fBulletLifeTime later (Fire Bolt 400 ms), so the client shows them on impact (inferred timing).
    /// Moving or casting again after launch no longer cancels it; caster death, a scene change or a new
    /// attachment still does (ApplyEffectsAsync's own checks).
    /// </summary>
    private async Task LaunchBulletAsync(GameSession s, ActiveSkillCast action, (int Effect, int FlightMs, float Speed, int IntervalMs) bullet)
    {
        var row = action.Row;
        var center = action.Request.TargetPos ?? action.Origin;
        var targets = SelectEnemies(s, action, center);
        action.Launched = true;
        int split = ShowHits(row);
        bool high = GameData.Int(row, "_iSkillEffectLevel") > 0;
        // Speed bullets (Soul Strike 1200803, 10/s) fly distance / speed like LC_Bullet_Helper; the hit follows the first target's flight.
        int Flight(MonsterUnit target) => bullet.Speed > 0
            ? Math.Max(1, (int)Math.Round(Distance(s.Position, target.Pos) / bullet.Speed * 1000)) : bullet.FlightMs;
        int landMs = targets.Count > 0 ? Flight(targets[0]) : bullet.FlightMs;
        foreach (var target in targets)
        {
            if (!CurrentCastMap(s, action)) return;
            await SendSkillBulletAsync(s, new SkillFixTimeBulletNotify
            {
                SourceId = s.Role!.Id, MasterId = s.Role.Id, TargetId = target.Uid, EffectId = bullet.Effect,
                FixedTime = Flight(target), Split = split, SkillConfigId = action.Request.SkillId,
                SourcePosition = s.Position.Clone(), TargetPosition = target.Pos.Clone(), StartTimestampMs = Now,
            }, high);
        }
        // Loop timers fire on the next 50 ms tick, so a timer set for the full flight lands up to a tick after the
        // bolt; one tick early keeps the hit inside the bolt's last tick.
        // The client emits the split bolts m_fBulletHatchInterval apart (LC_BulletEmit_Mgr: Fire Bolt 100 ms, Jupitel
        // Thunder 250 ms). Fire Bolt's hit effect 1200404 spreads one split hit's numbers over the bolts itself
        // (m_isPlayShow_Multistage, m_fDisplayInterval 0.1 s); Jupitel Thunder's 1210203 does not (interval 0), so its
        // numbers all showed with the first bolt. Such a skill lands each bolt as its own hit with its share of the
        // damage (inferred).
        int bolts = bullet.IntervalMs > 0 && split > 1 && !SpreadsSplitNumbers(HitEffect(GameData.Int(row, "_iScriptShow"))) ? split : 1;
        for (int i = 0; i < bolts; i++)
        {
            int bolt = i;
            var land = TimeSpan.FromMilliseconds(Math.Max(0, landMs + bolt * bullet.IntervalMs - GameLoop.TickInterval.TotalMilliseconds));
            ScheduleRuntime(action.WorldScene, land, async () =>
            {
                if (CurrentCastMap(s, action)) await ApplyEffectsAsync(s, action, center, bolt: bolt, bolts: bolts);
            });
        }
    }

    /// <summary>The hit effect shows a split hit's numbers one by one (m_kActionDamge m_isPlayShow_Multistage with a
    /// positive m_fDisplayInterval).</summary>
    private bool SpreadsSplitNumbers(int hitEffect) =>
        _data.SkillTemplateTable("Lua_Config_EffectData").TryGetValue(hitEffect.ToString(), out var e)
        && e.TryGetProperty("m_kDamage", out var damage) && damage.ValueKind == JsonValueKind.Object
        && damage.TryGetProperty("m_kActionDamge", out var action) && action.ValueKind == JsonValueKind.Object
        && action.TryGetProperty("m_isPlayShow_Multistage", out var multi) && multi.ValueKind == JsonValueKind.True
        && action.TryGetProperty("m_fDisplayInterval", out var interval) && interval.TryGetDouble(out double ms) && ms > 0;

    /// <summary>A damaging follow-up always hits enemies: some name their trigger's self-cast visual (Death Bound's 11381
    /// selects the caster), so the visual's selector does not decide its targets.</summary>
    private static bool HostileProc(ActiveSkillCast action) => action.Proc && GameData.Int(action.Row, "_iDmgType") is 1 or 2;

    private bool Hostile(ActiveSkillCast action, MonsterUnit m) =>
        (HostileProc(action) || Relation(action.Row, false, race: m.Template.Race)) && GroundFieldHits(action.Row, m);

    private List<MonsterUnit> SelectEnemies(GameSession s, ActiveSkillCast action, Vec3 center, float? radiusOverride = null, bool area = false)
    {
        var row = action.Row;
        int max = (int)Math.Max(1, GameData.Int(row, "_iTargetMax"));
        if (GameData.Int(row, "_iRangeType") != 2 && radiusOverride == null && !action.AreaProc && !area)
        {
            if (SelectAroundTarget(s, action) is { } around) return around;
            var target = FindMonster(s.WorldScene, action.Request.TargetId);
            return target != null && target.Hp > 0 && CanHitMonster(s, target) && Hostile(action, target) ? [target] : [];
        }
        int release = (int)GameData.Int(row, "_iRelease");
        if (release is not (1 or 7 or 8) && radiusOverride == null) center = action.Origin;
        var range = Numbers(row, "_kRange");
        float radius = radiusOverride ?? (float)(range.FirstOrDefault(300) / 100);
        float height = GameData.Int(row, "_iAreaHeightRange") / 100f;
        bool InShape(MonsterUnit m)
        {
            if (height > 0 && MathF.Abs(m.Y - center.Y) > height) return false;
            float dx = m.X - center.X, dz = m.Z - center.Z;
            double radians = action.Request.Face * Math.PI / 180;
            double forward = dx * Math.Sin(radians) + dz * Math.Cos(radians), side = dx * Math.Cos(radians) - dz * Math.Sin(radians);
            if (radiusOverride == null && release is 3 or 5 or 6 or 7)
                return Math.Abs(side) <= radius / 2 && Math.Abs(forward) <= (range.Length > 1 ? range[1] / 200 : radius);
            if (Distance(center, m.Pos) > radius + 0.1f) return false;
            return radiusOverride != null || release != 4 || Math.Atan2(Math.Abs(side), forward) <= (range.Length > 1 ? range[1] : 90) * Math.PI / 360;
        }
        return _world.Map(s.WorldScene).Units.OfType<MonsterUnit>().Where(m => m.Hp > 0 && CanHitMonster(s, m) && Hostile(action, m) && InShape(m))
            .OrderBy(m => Distance(center, m.Pos)).ThenBy(m => m.Uid).Take(max).ToList();
    }

    private async Task ApplyEffectsAsync(GameSession s, ActiveSkillCast action, Vec3 center, SkillObject? field = null, int bolt = 0, int bolts = 1,
        HitStage? stage = null, MonsterUnit? only = null)
    {
        if (s.Dead || s.Role?.Id != action.OwnerId || s.WorldScene != action.WorldScene || s.SceneLoading
            || s.SceneGeneration != action.SceneGeneration || field?.Removing == true
            || !ReferenceEquals(s.Attachment, action.Attachment) || action.Attachment?.Cancelled == true) return;
        var row = action.Row;
        var castingRole = s.Role;
        bool CurrentCaster() => ReferenceEquals(s.Role, castingRole) && !s.SceneLoading
            && s.SceneGeneration == action.SceneGeneration && (field != null ? !field.Removing : !action.Cancelled || action.Launched)
            && s.WorldScene == action.WorldScene && _sessions.Contains(s);
        bool CanApplyMoreEffects() => CurrentCaster() && ReferenceEquals(s.Attachment, action.Attachment)
            && action.Attachment?.Cancelled != true;
        // A published death still owes its callbacks after the cast/field is cancelled.
        // A cancelled but unchanged lease can finish committed consequences under its admitted owner.
        bool CurrentKillRecipient() => ReferenceEquals(s.Role, castingRole) && !s.SceneLoading
            && s.SceneGeneration == action.SceneGeneration && s.WorldScene == action.WorldScene
            && ReferenceEquals(s.Attachment, action.Attachment)
            && _sessions.Contains(s);
        string script = Script(row);
        var targets = only != null
            ? only.Hp > 0 && !only.Dead && CanHitMonster(s, only) && Hostile(action, only) ? [only] : new List<MonsterUnit>()
            : SelectEnemies(s, action, center, field?.Trap == true ? field.Radius : null);
        var playerTargets = only != null || stage is { First: false } ? []
            : GameData.Int(row, "_iRangeType") == 2 ? EventAreaTargets(s, row)
            : EventTarget(s, action.Request.TargetId) is { } eventTarget
                && PlayerDistance(s, eventTarget) <= Range(row) + 1.5f ? new[] { eventTarget } : [];
        if (playerTargets.Length > 0)
        {
            int maxTargets = checked((int)Math.Max(1, GameData.Int(row, "_iTargetMax")));
            var selected = targets.Select(m => (Id: m.Uid, Distance: MonsterService.Distance(m, s)))
                .Concat(playerTargets.Select(p => (Id: p.Role!.Id, Distance: PlayerDistance(s, p))))
                .OrderBy(p => p.Distance).ThenBy(p => p.Id).Take(maxTargets).Select(p => p.Id).ToHashSet();
            targets.RemoveAll(m => !selected.Contains(m.Uid));
            await HitEventPlayersAsync(s, playerTargets.Where(p => selected.Contains(p.Role!.Id)).ToArray(),
                row, action.Request.SkillId, HitEffect(GameData.Int(row, "_iScriptShow")), action.Id, CanApplyMoreEffects);
        }
        if (!CanApplyMoreEffects()) return; // player publication can yield before any monster hit is committed
        bool friendly = !HostileProc(action) && (Relation(row, true, true) || Relation(row, true));
        var allies = new List<GameSession>();
        if (friendly)
        {
            if (GameData.Int(row, "_iRelease") == 1)
            {
                var ally = action.Request.TargetId == s.Role.Id ? s : FindPlayer(s, action.Request.TargetId);
                if (ally != null && Relation(row, true, ally == s)) allies.Add(ally);
            }
            else
            {
                float radius = field?.Radius ?? (float)(Numbers(row, "_kRange").FirstOrDefault(0) / 100);
                allies.AddRange(_world.PlayersIn(s.WorldScene).Where(p => !p.Dead && Relation(row, true, p == s)
                    && (p == s || radius > 0 && Distance(p.Position, center) <= radius))
                    .OrderBy(p => Distance(p.Position, center)).Take((int)Math.Max(1, GameData.Int(row, "_iTargetMax"))));
                if (allies.Count == 0 && Relation(row, true, true)) allies.Add(s);
            }
        }
        var stats = _character.Stats(s);
        var attacker = PlayerAttacker(s, stats);
        var hit = new SkillHitInfo
        {
            SrcID = field?.Uid ?? action.SourceId, SkillId = action.Request.SkillId, SessionId = action.Id,
            RootCtxGuid = action.Id, ComboIndex = action.Request.ComboIndex, ShowPoint = HitEffect(GameData.Int(row, "_iScriptShow")),
            SrcElement = stage is { Element: > 0 } ? stage.Element : (int)Math.Max(1, GameData.Int(row, "_iEleType")),
        };
        // One bolt (or one packet) of several is one number; its buffs, procs and pushes come with the first one only.
        int shownHits = stage is { Split: > 0 } ? stage.Split : bolts > 1 ? 1 : ShowHits(row);
        bool firstBolt = bolt == 0 && stage?.First != false;
        if (shownHits > 1) hit.Split = shownHits;
        var killed = new List<MonsterUnit>();
        var shownOnly = new List<DamageInfo>();
        var processed = new List<MonsterUnit>();
        var landed = new List<(MonsterUnit Target, long Amount)>();
        bool damage = Numbers(row, "_kDamageParam1").Any(v => v > 0) || Numbers(row, "_kDamageParam2").Any(v => v > 0)
            || action.ProcFixedDamage > 0;
        if (DelayedHitScripts.Contains(Script(row))) damage = false;
        try
        {
            foreach (var target in targets)
            {
                if (!CanApplyMoreEffects()) break; // a previous target's awaited callback may have invalidated the caster
                if (target.Dead || target.Hp <= 0) continue; // another awaited cast may already own this death source
                processed.Add(target);
                if (field != null) field.Touched++;
                if (damage)
                {
                    var defender = AsDefender(target);
                    var (amount, result) = Damage(attacker, defender, row, stage?.Branch ?? DamageBranch(s, row, target), stage?.Element ?? 0);
                    if (action.ProcFixedDamage > 0 && result != DamageHitResult.Miss) amount = action.ProcFixedDamage;
                    else if (amount > 0 && action.ProcScale != 1) amount = Math.Max(1, (long)Math.Round(amount * action.ProcScale));
                    if (amount > 0 && bolts > 1) amount = amount / bolts + (bolt < amount % bolts ? 1 : 0);
                    if (amount > 0)
                    {
                        foreach (var modifier in DamageAdjusters) amount = Math.Max(0, modifier(s, target, row, result, amount));
                        // (inferred) A follow-up is itself the extra hit: it adds no Double Attack or card extra hit.
                        if (!action.Proc) foreach (var extra in ExtraHitDamage) amount += Math.Max(0, extra(s, target, row, amount));
                        // A Double Attack proc shows as its own number(s) on this swing, as _kShowDmg splits multi-hit skills.
                        if (_doubleAttackHits > 0) hit.Split = Math.Max(hit.Split, shownHits + _doubleAttackHits);
                        _doubleAttackHits = 0;
                    }
                    long beforeHp = target.Hp;
                    target.Hp = target.HpAfter(beforeHp, amount);
                    long effective = beforeHp - target.Hp;
                    if (target.Hp == 0) killed.Add(target); // reserve before awaited damage callbacks
                    if (amount > 0)
                    {
                        landed.Add((target, amount));
                        await ConsumeOnHitDebuffsAsync(target);
                    }
                    await SettleMonsterHitAsync(s, target, beforeHp, amount);
                    if (CanApplyMoreEffects())
                        foreach (var handler in DealtAsync)
                        {
                            if (!CanApplyMoreEffects()) break;
                            await handler(s, target, row, amount);
                        }
                    // The hit is already committed; an unchanged (even cancelled) lease
                    // still owns its damage callback, while a replacement lease does not.
                    if (effective > 0 && CurrentKillRecipient())
                        foreach (var handler in MonsterDamageApplied)
                        {
                            if (!CurrentKillRecipient()) break;
                            try { await handler(s, target, effective); }
                            catch (Exception ex) { _log.LogError(ex, "Monster damage callback failed for {Uid}", target.Uid); }
                        }
                    hit.Dsts.Add(new DamageInfo { DstID = target.Uid, Damage = amount, HitResult = result, DstElement = target.Template.Element, IsDead = target.Hp == 0 });
                }
                if (CanApplyMoreEffects() && (firstBolt || stage is { Buff2: >= 0 } || only != null))
                {
                    var tuples = Tuples(row, "_kBuff2").ToArray();
                    var rolled = stage is { Buff2: >= 0 } ? tuples.Skip(stage.Buff2).Take(1) : stage?.Fallback >= 0 ? tuples.Take(stage.Fallback) : tuples;
                    bool slowedBefore = Slowed(target);
                    foreach (var (tuple, index) in rolled.Select((t, i) => (t, i)).ToArray())
                        if (CanApplyMoreEffects() && !GaleSkipsBuff(row, index, slowedBefore)
                            && RollHostileBuff(tuple, s, null, null, target, out int id, out int duration))
                        {
                            await _buffs.ShowOnUnitAsync(target, id, duration, castingRole.Id);
                            action.StatusLanded.Add(target.Uid);
                        }
                    // "guaranteed to inflict at least 1 of these Status Effects": the last packet's fallback tuple.
                    if (stage is { Last: true, Fallback: >= 0 } && stage.Fallback < tuples.Length && !action.StatusLanded.Contains(target.Uid)
                        && CanApplyMoreEffects() && RollHostileBuff(tuples[stage.Fallback], s, null, null, target, out int fid, out int fduration))
                        await _buffs.ShowOnUnitAsync(target, fid, fduration, castingRole.Id);
                }
                if (CanApplyMoreEffects() && firstBolt && KnockUps.Contains(script) && !target.Dead && target.Hp > 0)
                    await _buffs.ShowOnUnitAsync(target, KnockUpBuff, KnockUpMs, castingRole.Id);
                if (CanApplyMoreEffects() && script is ("Provoke" or "Taunt")) _monsters.Engage(target, s);
                if (CanApplyMoreEffects() && firstBolt && !target.Dead && target.Hp > 0)
                {
                    if (field is { PushMeters: > 0 }) await PushFromFieldAsync(s, action, field, target);
                    else await DisplaceTargetAsync(s, action, target);
                }
            }
            // Buff1 is self for hostile selectors, but actual friend recipients for ally selectors (Detoxify, Blessing).
            foreach (var recipient in friendly ? allies : new List<GameSession> { s })
            {
                if (!CanApplyMoreEffects()) break;
                if (field == null && firstBolt)
                {
                    foreach (var tuple in Buff1(s, row, script, recipient))
                        if (CanApplyMoreEffects() && RollBuff(tuple, out int id, out int duration)) await GrantSkillBuffAsync(s, recipient, id, duration);
                    if (script == "Sacrifice" && recipient != s)
                        foreach (var tuple in Buff1(s, row, script))
                            if (CanApplyMoreEffects() && RollBuff(tuple, out int id, out int duration)) await GrantSkillBuffAsync(s, s, id, duration);
                }
                // A support cast with no number to show (Blessing, Increase AGI, Angelus, cleanses) still needs its recipient in
                // 31101: the client plays the hit effect's gfx (fx_buff_angelblessing on Blessing's 1400102) only per dst
                // (LC_DamageHelper._ActionJumpDamage -> ShowHitDisplay). DamageShare with a Normal result maps to no HUD
                // word (LC_DamageHelper damage-word type -> None), so the entry shows the effect and no "0" (inferred).
                if (friendly && !damage && field == null && firstBolt)
                    shownOnly.Add(new DamageInfo { DstID = recipient.Role!.Id, DamageType = DamageType.DamageShare, DstElement = 1 });
                if (friendly && damage && !PetAuraGuardian(row))
                {
                    double multiplier = Numbers(row, "_kDamageParam1").FirstOrDefault(), flat = Numbers(row, "_kDamageParam2").FirstOrDefault();
                    // Heal's friendly branch is P1[0]/P2[0]; its enemy branch is [1] (description + select race mask).
                    var before = _character.Stats(recipient);
                    long amount = (long)Math.Max(0, Math.Round((HealPower(row, stats) * multiplier / 10000 + flat)
                        * FormulaFactor(attacker, row, 0, heal: true) * HealFactor(attacker, PlayerDefender(recipient, before))));
                    foreach (var modifier in ExtraHeal) amount = modifier(s, amount);
                    long actual = Math.Min(amount, Math.Max(0, before[101] - before[2000]));
                    await _character.SetHpSpAsync(recipient, before[2000] + amount, null);
                    hit.Dsts.Add(new DamageInfo { DstID = recipient.Role!.Id, Damage = actual, DamageType = DamageType.Heal, DstElement = 1 });
                    foreach (var handler in Healed) handler(s, actual);
                    foreach (var handler in HealedAsync) await handler(s, actual);
                }
                if (CleanseTargets(row) is { Length: > 0 } cleansed)
                    foreach (var buff in recipient.Buffs.Where(b => _data.Row("BuffConfig", b.ConfigId) is { } br && Cleanses(cleansed, br)).ToArray())
                        await _buffs.RemoveAsync(recipient, buff);
                if (CleansedCategories(row, recipient == s) is { Length: > 0 } categories)
                    foreach (var buff in recipient.Buffs.Where(b => _data.Row("BuffConfig", b.ConfigId) is { } br && HasCategory(br, categories)).ToArray())
                        await _buffs.RemoveAsync(recipient, buff);
            }
            if (hit.Dsts.Count > 0 || shownOnly.Count > 0)
            {
                foreach (var result in hit.Dsts)
                {
                    if (!CurrentCaster()) break;
                    await RecordDamageStatisticAsync(s, new DamageStatistics
                    {
                        DamageSourceId = hit.SrcID, DamageTargetId = result.DstID, DamageValue = result.Damage,
                        DamageBeShield = result.SheildDamage, DamageType = result.DamageType, HitResult = result.HitResult, SkillId = hit.SkillId,
                    });
                }
                if (CurrentCaster() && firstBolt && hit.Dsts.Any(d => d.HitResult != DamageHitResult.Miss))
                {
                    await _buffs.RecoverEnergyOnHitAsync(s, row);
                    if (CurrentCaster()) await ConvertChargesAsync(s, row);
                }
                hit.Dsts.AddRange(shownOnly); // display only: no statistics, energy or charges
                var sourceViewers = CurrentCaster() ? _world.Viewers(s).Append(s) : Enumerable.Empty<GameSession>();
                var recipients = shownOnly.Count > 0 ? allies.Where(a => shownOnly.Any(d => d.DstID == a.Role?.Id)) : Enumerable.Empty<GameSession>();
                foreach (var viewer in processed.SelectMany(t => t.Viewers).Concat(sourceViewers).Concat(recipients).Distinct()) await viewer.SendGameAsync(SkillHit, hit);
                if (CurrentCaster() && field?.Trap == true)
                    await ToPlayerAndViewersAsync(s, TrapDamage, new TrapDamageNtf { SkillConfigId = action.Request.SkillId, DamageValue = hit.Dsts.Sum(d => d.Damage), Pos = center.Clone() });
            }
            foreach (var target in processed)
            {
                if (!damage) continue;
                if (target.Hp == 0 && !target.DeathDurable) continue; // another cast is still committing that death
                await _world.ToUnitViewersAsync(target, AoiAttrChange, MonsterService.Hp(target));
                await FightingAsync(target);
                if (target.Hp > 0 && CurrentCaster()) _monsters.Engage(target, s);
            }
            if (processed.Count > 0 && CurrentCaster()) await FightingAsync(s);
            foreach (var target in killed.Where(target => target.Hp == 0 && target.DeathDurable))
            {
                _log.LogDebug("{Name} killed {Monster} ({Npc})", castingRole.Name, target.Uid, target.Template.NpcId);
                await _monsters.KillAsync(target);
                foreach (var other in _sessions.All().Where(o => o.Attack?.Target == target))
                {
                    other.Attack = null;
                    await other.SendGameAsync(SkillEnd, new SkillEndNotify
                    {
                        SrcID = other.Role!.Id, SessionId = action.Id, Type = SkillEndNotify.Types.SkillEndType.Interrupted,
                        InterruptedInfo = new SkillInterruptedInfo { Type = SkillInterruptedInfo.Types.SkillInterruptType.LockerInvalid },
                    });
                }
                await PublishDurableKillAsync(s, target, CurrentKillRecipient);
                await FinalizeLethalPublicationAsync(target);
            }
        }
        finally
        {
            foreach (var target in killed.Where(target => target.Hp == 0 && target.DeathDurable))
            {
                if (!target.Dead)
                    try { await _monsters.KillAsync(target); }
                    catch (Exception ex) { _log.LogError(ex, "Could not finalize reserved monster death for {Uid}", target.Uid); }
                // A later target/effect can fail after this source committed. Its room observers must still run.
                await PublishDurableKillAsync(s, target, CurrentKillRecipient);
                await FinalizeLethalPublicationAsync(target);
            }
        }
        if (!action.Proc && firstBolt && landed.Count > 0 && CanApplyMoreEffects()) await RunOutgoingProcsAsync(s, action, landed, CanApplyMoreEffects);
    }

    /// <summary>
    /// A skill's _kBuff1 on its caster or an ally. A shield buff (_kBuffCategory 30: Kyrie Eleison, Radiant Holy Light,
    /// Rift Impact, Royal Guard's stacks; every category-30 row with a formula is described as a shield) gets its
    /// absorb amount: _kData1 x the caster's PATK (_iDmgType 1) or MATK (2) / 10000 ("absorbs damage equal to Magic ATK x
    /// 80%"), else the T_AXadd0_etc attribute formula of _kDamageParamExtra ("a shield equal to 10% of Max Life",
    /// PetCombatRules.AttributeAmount). Other buffs are added as before.
    /// </summary>
    private async Task GrantSkillBuffAsync(GameSession caster, GameSession recipient, int id, int durationMs)
    {
        long shield = 0;
        if (_data.Row("BuffConfig", id) is { } row)
        {
            var casterStats = _character.Stats(caster);
            shield = ShieldAmount(row, casterStats, ReferenceEquals(caster, recipient) ? casterStats : _character.Stats(recipient));
        }
        if (durationMs < 0 && LastingSkillBuffs.Contains(id))
        {
            await _buffs.AddLastingAsync(recipient, id, caster.Role!.Id);
            await LastingBuffGrantedAsync(recipient, id);
        }
        else if (shield > 0) await _buffs.AddSupportAsync(recipient, id, durationMs, caster.Role!.Id, shield);
        else await _buffs.AddAsync(recipient, id, durationMs, caster.Role!.Id);
    }

    internal static long ShieldAmount(JsonElement buff, StatSheet caster, StatSheet recipient)
    {
        if (!(buff.TryGetProperty("_kBuffCategory", out var c) && c.ValueKind == JsonValueKind.Array
              && c.EnumerateArray().Any(v => GameData.Number(v) == 30))) return 0;
        int dmgType = (int)GameData.Int(buff, "_iDmgType");
        double ratio = Numbers(buff, "_kData1").FirstOrDefault();
        long amount = ratio > 0 && dmgType is 1 or 2 ? (long)Math.Floor((dmgType == 2 ? caster[104] : caster[103]) * ratio / 10000)
            : PetCombatRules.AttributeAmount(buff, caster, recipient);
        return ShieldEfficiency(amount, caster, recipient);
    }

    /// <summary>Shield Efficiency: the caster's 1205 ("per_shieldbonus", Prayer of Gratitude) and the recipient's 1206
    /// ("per_beshieldbonus") raise a granted shield (inferred: two factors, like <see cref="HealFactor"/>).</summary>
    internal static long ShieldEfficiency(long amount, StatSheet caster, StatSheet recipient) => amount <= 0 ? amount
        : (long)Math.Floor(amount * Math.Max(0, 1 + caster[1205] / 10000.0) * Math.Max(0, 1 + recipient[1206] / 10000.0));

    /// <summary>
    /// What a cleansing skill removes. Healing Skills/Recovery (Cleanse, Cleanse2) list status kinds in _kLogicData,
    /// the first _kBuffCategory entry of the status (6 Silence, 5 Blind, 13 Stun, 9 Frozen, 10 Petrify, matching their
    /// descriptions); Thief Detoxify (PoisonBoom) lists the Poisoned group 9030; Guillotine Cross Antidote (jieduji)
    /// has no data and cures Poisoned by its description (inferred).
    /// </summary>
    internal static int[] CleanseTargets(JsonElement row) => Script(row) switch
    {
        "Cleanse" or "Cleanse2" or "PoisonBoom" or "Detoxify" or "HealingSkills" => Numbers(row, "_kLogicData").Select(v => (int)v).ToArray(),
        "jieduji" => [9030],
        _ => [],
    };

    /// <summary>A buff matches a cleanse id when it is that BuffConfig group (ids of 1000 and up) or a debuff whose kind
    /// (first category, also in its monster and PvP copies at +50/+79) is that id. Poisoned 9030 also covers its copies
    /// 9072/9091, whose kind is 1 + 50/79.</summary>
    internal static bool Cleanses(int[] ids, JsonElement buff)
    {
        if (GameData.Int(buff, "_iType") != 2) return false;
        int group = (int)GameData.Int(buff, "_iBuffID");
        int kind = buff.TryGetProperty("_kBuffCategory", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 0
            ? (int)(GameData.Number(c[0]) ?? 0) : 0;
        foreach (int id in ids)
        {
            int wantKind = id >= 1000 ? (id == 9030 ? 1 : -1) : id;
            if (id >= 1000 && group == id) return true;
            if (wantKind > 0 && (kind == wantKind || kind == wantKind + 50 || kind == wantKind + 79)) return true;
        }
        return false;
    }

    private bool RollBuff(JsonElement tuple, out int id, out int duration)
    {
        id = duration = 0;
        if (tuple.GetArrayLength() < 3) return false;
        id = (int)(GameData.Number(tuple[0]) ?? 0);
        duration = (int)(GameData.Number(tuple[2]) ?? 0);
        return id > 0 && Rolls.Proc(10000) < (GameData.Number(tuple[1]) ?? 0);
    }

    /// <summary>
    /// Knock-Up 9018 (901801, first category 16: SpecialStatusConfig 3 击飞, the FlyHit animation, the same forbidden
    /// actions as Stun). Its rows carry no time, so the toss lasts <see cref="KnockUpMs"/> (inferred). Skills whose
    /// description names it: Sonic Burst ("inflicting Knock-Up and 【Immobilize】"), Fearless Charge's second stage ("then
    /// inflict Knock-Up") and Energy Release's blast (BombSuicide2, a knock-up show with no text). Category 25 makes
    /// control immunity (Boss CC Immunity, Immunity) shrug it off.
    /// </summary>
    internal const int KnockUpBuff = 901801, KnockUpMs = 1000;

    internal static readonly HashSet<string> KnockUps = ["Sonicblast", "PinpointAttack2", "BombSuicide2"];

    internal enum DisplaceKind { CasterForward, CasterBack, TargetPush, TargetPull, CasterToTarget, CasterToPoint, PullToCenter }

    /// <summary>
    /// Skills whose show has a displacement EffectData (m_kPoint.m_bMovement) and whose description names the
    /// distance: _kDescData index, or -1 for a pull "in front of you". Who moves and which way comes from the
    /// description and the effect's curve name (击退 knockback, 拉人 pull); the server choice of skills is (inferred).
    /// ShieldsProtection's 1161341 knockback belongs to its periodic aura, which is not simulated.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, (DisplaceKind Kind, int DescIndex)> Displacements =
        new Dictionary<string, (DisplaceKind, int)>
        {
            ["BackSlide"] = (DisplaceKind.CasterBack, 0),
            ["RetreatingShadows"] = (DisplaceKind.CasterBack, 0),
            ["RetreatingShadowsS1"] = (DisplaceKind.CasterBack, 0),
            ["WolvesDash"] = (DisplaceKind.CasterForward, 0),
            ["HighspeedSlide"] = (DisplaceKind.CasterForward, 0),
            ["HighspeedSlideMecha"] = (DisplaceKind.CasterForward, 0),
            ["PinpointAttack"] = (DisplaceKind.CasterForward, 3),
            ["ArrowRepel"] = (DisplaceKind.TargetPush, 3),
            ["PileBunker"] = (DisplaceKind.TargetPush, 5),
            ["PhantomThrust"] = (DisplaceKind.TargetPull, -1),
            // "knocking them back ${4} m" (Jupitel Thunder 1210210 is its 击退 effect).
            ["Jupitel_Thunder"] = (DisplaceKind.TargetPush, 3),
            // "Charge at a designated enemy" (1111203): the caster ends next to the target.
            ["Charge_Attack"] = (DisplaceKind.CasterToTarget, -1),
            // "Dives toward a designated location" (Rift Impact 1130109), "Leap to a selected area" (Twilight 1160707).
            ["IgnitionBreak"] = (DisplaceKind.CasterToPoint, -1),
            ["HesperusLit"] = (DisplaceKind.CasterToPoint, -1),
            // Fire Ninja's Main Skill 2302201: "moves its master ${2} meters in the designated direction" (show 2300311).
            ["petmove"] = (DisplaceKind.CasterForward, 1),
            // "applying 【Knockback】 for ${4} meters" (Spear Stab), "knocking them back ${5} meters" (Sightrasher).
            ["Spear_Stab"] = (DisplaceKind.TargetPush, 3),
            ["Sightrasher"] = (DisplaceKind.TargetPush, 4),
            // "Pulls the targets closer" (Spear Formation - Crescent Moon).
            ["MoonSlasher2"] = (DisplaceKind.TargetPull, -1),
            // "applying 【Pull】" (Tetra Vortex), "continuously pulling them in" (Gravitational Impact): toward the chosen
            // target, around which the area is centred (inferred: they end 1 m from it).
            ["TetraVortex"] = (DisplaceKind.PullToCenter, -1),
            ["VectorShock"] = (DisplaceKind.PullToCenter, -1),
            // "Swing your Axe to Move rapidly in front of the target" (Heavy Axe Smash 1630505 冲锋攻击).
            ["PowerSwing"] = (DisplaceKind.CasterToTarget, -1),
            // "Charges to a point ${5} meters in front of the target" (Dark Illusion 1540104).
            ["hesehuanying"] = (DisplaceKind.CasterToTarget, 4),
            // "Choose a direction to dash ${1} meters as a phantom" (Flowing Shadow 1542101).
            ["yingliu"] = (DisplaceKind.CasterForward, 0),
            // "Move ${12} meters in that direction" (Fearless Charge 1162411 黄昏之光水平移动). Its _kDescData has 11 values,
            // so ${12} is missing; the 12 m of ${1}, the area's length, stands in (inferred).
            ["PinpointAttack2"] = (DisplaceKind.CasterForward, 0),
            // Warg Hunt's ranged form: "If you are more than ${5}m from the target, you also dash in front of it" (show
            // 13330's 1333014 and 133301's 13330113 are its move).
            ["WolvesShadowFar"] = (DisplaceKind.CasterToTarget, -1),
        };

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, (int Id, int Ms)> _movementEffects = new();

    /// <summary>
    /// The show's displacement EffectData: the lowest id in [scriptShow*100, scriptShow*100+99] with m_kPoint.m_bMovement
    /// (BackSlide 15104 -> 1510403, PileBunker 16314 -> 1631405 knockback). The client's SkillStartMove ignores a 31112
    /// without it (LC_PerformEffect_Agent.lua SkillStartMove returns on effectId nil/0).
    /// </summary>
    internal (int Id, int Ms) MovementEffect(long scriptShow) => _movementEffects.GetOrAdd(scriptShow, show =>
    {
        var (id, ms, _) = FindMovementEffect(_data, show);
        return (id, ms);
    });

    /// <summary>The show's displacement EffectData (see <see cref="MovementEffect"/>), with its move curve's name.</summary>
    internal static (int Id, int Ms, string Curve) FindMovementEffect(GameData data, long show)
    {
        var effects = data.SkillTemplateTable("Lua_Config_EffectData");
        for (long id = show * 100; id < show * 100 + 100; id++)
            if (effects.TryGetValue(id.ToString(), out var e) && e.TryGetProperty("m_kPoint", out var point)
                && point.TryGetProperty("m_bMovement", out var moves) && moves.ValueKind == JsonValueKind.True)
                return ((int)id, point.TryGetProperty("m_iMovementTime", out var ms) && ms.TryGetInt32(out int t) ? t : 0,
                    point.TryGetProperty("m_kMoveCurveName", out var curve) && curve.ValueKind == JsonValueKind.String ? curve.GetString() ?? "" : "");
        return (0, 0, "");
    }

    private (DisplaceKind Kind, float Distance, int EffectId, int Ms)? Displacement(JsonElement row)
    {
        if (!Displacements.TryGetValue(Script(row), out var rule)) return null;
        var (effect, ms) = MovementEffect(GameData.Int(row, "_iScriptShow"));
        // The caster's own move needs the show's displacement effect (the client ignores a 31112 without one); a monster
        // is moved by its position update alone when the show has none (Spear Stab, Sightrasher, Crescent Moon).
        if (effect == 0 && rule.Kind is not (DisplaceKind.TargetPush or DisplaceKind.TargetPull or DisplaceKind.PullToCenter)) return null;
        // (inferred) "pulling them in front of you": 1 m ahead of the caster; a charge also stops 1 m short.
        float distance = rule.DescIndex < 0 ? 1 : (float)Numbers(row, "_kDescData").ElementAtOrDefault(rule.DescIndex);
        return distance > 0 ? (rule.Kind, distance, effect, ms) : null;
    }

    private Task MoveCasterAsync(GameSession s, ActiveSkillCast action) => MoveCasterAsync(s, action.Row, action.Request, action.ActionMs);

    private async Task MoveCasterAsync(GameSession s, JsonElement row, CastSkillReq request, int actionMs)
    {
        if (Displacement(row) is not
            {
                Kind: DisplaceKind.CasterForward or DisplaceKind.CasterBack
                or DisplaceKind.CasterToTarget or DisplaceKind.CasterToPoint
            } move) return;
        float dx, dz;
        if (move.Kind == DisplaceKind.CasterToTarget)
        {
            // Stop the given distance short of the locked target, along the line to it.
            if (FindMonster(s.WorldScene, request.TargetId) is not { } target) return;
            float tx = target.X - s.Position.X, tz = target.Z - s.Position.Z, length = MathF.Sqrt(tx * tx + tz * tz);
            if (length <= move.Distance) return;
            dx = tx / length * (length - move.Distance);
            dz = tz / length * (length - move.Distance);
        }
        else if (move.Kind == DisplaceKind.CasterToPoint)
        {
            if (request.TargetPos is not { } point) return;
            dx = point.X - s.Position.X;
            dz = point.Z - s.Position.Z;
        }
        else
        {
            float face = request.Face + (move.Kind == DisplaceKind.CasterBack ? 180 : 0);
            double angle = face * Math.PI / 180;
            dx = (float)Math.Sin(angle) * move.Distance;
            dz = (float)Math.Cos(angle) * move.Distance;
        }
        var end = WalkableEnd(s.SceneEntry, s.Position, dx, dz);
        var start = s.Position.Clone();
        await ToPlayerAndViewersAsync(s, SkillMove, new SkillStartMoveNotify
        {
            EntityID = (ulong)s.Role!.Id, StartPos = start, EndPos = end, SkillConfigId = request.SkillId,
            EffectId = move.EffectId, StartTimestampMs = Now, MoveType = 1, Face = request.Face,
            MoveTimeMs = move.Ms > 0 ? move.Ms : actionMs,
        });
        // MoveAsync updates persistent position and AOI membership from the same authoritative destination.
        s.PreviousPosition = start;
        s.Position = end;
        await _world.MoveAsync(s, new WalkReq { Pos = end, Dir = s.Face, Face = s.Face, BStop = true }, forceTeleport: true);
    }

    private async Task DisplaceTargetAsync(GameSession s, ActiveSkillCast action, MonsterUnit target)
    {
        if (Displacement(action.Row) is not { Kind: DisplaceKind.TargetPush or DisplaceKind.TargetPull or DisplaceKind.PullToCenter } move) return;
        var from = s.Position;
        if (move.Kind == DisplaceKind.PullToCenter)
        {
            // The centre is the chosen target (or the chosen point); the chosen target itself stays.
            if (FindMonster(s.WorldScene, action.Request.TargetId) is { } centre) { if (centre == target) return; from = centre.Pos; }
            else if (action.Request.TargetPos is { } point) from = point;
            else return;
        }
        float dx = target.X - from.X, dz = target.Z - from.Z;
        float length = MathF.Sqrt(dx * dx + dz * dz);
        if (length < 0.001f) return;
        // A push goes away from the caster; a pull stops the given distance in front of it.
        float distance = move.Kind == DisplaceKind.TargetPush ? move.Distance : -(length - move.Distance);
        if (move.Kind != DisplaceKind.TargetPush && length <= move.Distance) return;
        var start = target.Pos;
        var end = WalkableEnd(s.SceneEntry, start, dx / length * distance, dz / length * distance);
        target.Moving = false;
        target.X = end.X; target.Y = end.Y; target.Z = end.Z;
        if (move.EffectId > 0) await _world.ToUnitViewersAsync(target, SkillMove, new SkillStartMoveNotify
        {
            EntityID = (ulong)target.Uid, StartPos = start, EndPos = end, SkillConfigId = action.Request.SkillId,
            EffectId = move.EffectId, MoveType = 2, StartTimestampMs = Now, Face = target.Face,
            MoveTimeMs = move.Ms > 0 ? move.Ms : 300,
        });
        await _world.MoveUnitAsync(target, new MovementChangeNtf { Uid = target.Uid, MovementInfo = target.CurrentMovement(Now) });
    }

    private Vec3 WalkableEnd(long sceneId, Vec3 start, float dx, float dz)
    {
        var nav = _worldData.Map(sceneId)?.Nav;
        var end = start.Clone();
        int steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dx * dx + dz * dz) / 0.25));
        for (int i = 1; i <= steps; i++)
        {
            float x = start.X + dx * i / steps, z = start.Z + dz * i / steps;
            if (nav != null && !nav.CanWalk(end.X, end.Y, end.Z, x, z)) break;
            end = new Vec3 { X = x, Y = nav?.WalkHeight(x, z, end.Y) ?? end.Y, Z = z };
        }
        return end;
    }

    /// <summary>
    /// Owned ground triggers use NPCConfig identities, not WidgetConfig. Fire_Wall/Safety_Wall/Pneuma tuples explicitly
    /// reference NPCs; Hunter trap NPCs match their localized skill names. Timing/limits below are inferred from the
    /// corresponding descriptions and _kLogicData, documented per family in docs/systems/skills-rest.md.
    /// </summary>
    private async Task<bool> CreateSkillObjectAsync(GameSession s, ActiveSkillCast action)
    {
        if (!CurrentCastMap(s, action)) return true;
        var row = action.Row;
        string script = Script(row);
        var logic = Numbers(row, "_kLogicData");
        int npc = 0, duration = 0, interval = 1000, protection = 0, charges = 0, waves = 0, touches = 0;
        float push = 0;
        bool trap = false;
        float radius = (float)(Numbers(row, "_kRange").FirstOrDefault(150) / 100);
        switch (script)
        {
            case "Fire_Wall" or "Quagmire" or "Fire_Pillar" or "Meteor_Storm" or "Lord_of_Vermilion" or "Storm_Gust" when logic.Length >= 3:
                npc = (int)logic[0]; interval = (int)logic[1]; duration = (int)logic[2];
                // Meteor Storm [npc, ms between meteors, ms, ?, ?, meteors]: "strikes in ${6} waves" (Lv1 2, Lv10 7), its
                // NPC named for that count; 1.6 s / 300 ms would otherwise strike 6 times at Lv1.
                if (script == "Meteor_Storm" && logic.Length >= 6) waves = Math.Max(1, (int)logic[5]);
                if (script == "Fire_Wall") (touches, push) = FireWallLimits(row);
                break;
            case "Magnus_Exorcismus" or "Sanctuary" or "ZhongLiYuanYe":
                (npc, duration, interval, radius, waves) = HolyAndGravityField(script, row, logic, radius);
                break;
            case "Safety_Wall" or "Pneuma" when logic.Length >= 8:
                npc = (int)logic[5]; duration = (int)logic[2]; radius = (float)(logic[7] / 100);
                protection = script == "Safety_Wall" ? 1 : 2;
                // [?, ?, ms, hits, buff, npc, ?, radius cm]: index 3 is the hits it blocks, rising with the level
                // like the description's count (Safety Wall Lv1-5: 1-5, Pneuma: 2). Index 1 is 1 at every level.
                charges = Math.Max(1, (int)logic[3]); break;
            case "LandMine": npc = 22000016; trap = true; break;
            case "ClaymoreTrap": npc = 22000017; trap = true; break;
            case "FreezingTrap": npc = 22000018; trap = true; break;
            case "BlastMine": npc = 22000019; trap = true; break;
            default:
                if (script.Contains("Summon", StringComparison.OrdinalIgnoreCase))
                {
                    npc = logic.Select(v => (int)v).FirstOrDefault(id => _data.Row("NPCConfig", id) != null);
                    duration = 30000; // (inferred) bounded lifecycle for a named summon without an explicit lifetime tuple.
                }
                break;
        }
        if (npc == 0 || _data.Row("NPCConfig", npc) is not { } npcRow) return false;
        if (trap)
        {
            var desc = row.TryGetProperty("_kDescData", out var d) && d.ValueKind == JsonValueKind.Array ? d.EnumerateArray().ToArray() : [];
            duration = desc.Length > 0 && double.TryParse(desc[^1].ToString(), System.Globalization.CultureInfo.InvariantCulture, out double seconds) ? (int)(seconds * 1000) : 20000;
            radius = 3; interval = 100;
        }
        var point = action.Request.TargetPos ?? action.Origin;
        var unit = new SkillObject
        {
            Uid = ++_skillObjectIds, SceneEntry = s.SceneEntry, InstanceId = s.InstanceId, RuntimeMap = s.RuntimeMap,
            OwnerSession = s, OwnerId = s.Role!.Id,
            NpcId = npc, EntityType = (int)GameData.Int(npcRow, "_iNPCType"), Cast = action,
            X = point.X, Y = point.Y, Z = point.Z, Face = action.Request.Face, ExpiresMs = Now + Math.Max(1, duration),
            IntervalMs = Math.Max(50, interval), NextTickMs = Now, Radius = radius, Trap = trap,
            ProtectionDistanceType = protection, Charges = charges, WavesLeft = waves, TouchLimit = touches, PushMeters = push,
        };
        foreach (int skill in Numbers(npcRow, "_kSkills").Select(v => (int)v)) unit.Skills.Add(skill);
        if (script == "Fire_Wall") await LimitFieldsAsync(s, row);
        s.SkillObjects.Add(unit);
        await _world.AddUnitAsync(unit);
        if (!CurrentCastMap(s, action)) { await RemoveSkillObjectAsync(unit); return true; }
        foreach (var tuple in Tuples(row, "_kBuff1"))
            if (RollBuff(tuple, out int buffId, out int buffDuration)) await GrantSkillBuffAsync(s, s, buffId, buffDuration);
        return true;
    }

    private async Task TickSkillObjectAsync(SkillObject unit)
    {
        if (unit.Busy || unit.Removing) return;
        unit.Busy = true;
        try
        {
            var owner = unit.OwnerSession;
            if (Now >= unit.ExpiresMs || owner.Role?.Id != unit.OwnerId || owner.SceneLoading || owner.Dead
                || owner.WorldScene != unit.WorldScene || owner.SceneGeneration != unit.Cast.SceneGeneration
                || !ReferenceEquals(owner.Attachment, unit.Cast.Attachment) || unit.Cast.Attachment?.Cancelled == true)
            {
                await RemoveSkillObjectAsync(unit);
                return;
            }
            if (Now < unit.NextTickMs) return;
            unit.NextTickMs = Now + unit.IntervalMs;
            if (unit.ProtectionDistanceType != 0 || unit.WavesLeft < 0) return;
            if (unit.Trap && !_world.Map(unit.WorldScene).Near(unit.X, unit.Z, 1.5f).OfType<MonsterUnit>().Any(m => !m.Dead && Distance(m.Pos, unit.Pos) <= 1.5f)) return;
            await ApplyEffectsAsync(owner, unit.Cast, unit.Pos, unit);
            if (unit.Trap || unit.TouchLimit > 0 && unit.Touched >= unit.TouchLimit) await RemoveSkillObjectAsync(unit);
            else if (unit.WavesLeft > 0 && --unit.WavesLeft == 0) unit.WavesLeft = -1; // the shower stays until it expires
        }
        finally { unit.Busy = false; }
    }

    private async Task RemoveSkillObjectAsync(SkillObject unit)
    {
        if (unit.Removing) return;
        unit.Removing = true;
        unit.OwnerSession.SkillObjects.Remove(unit);
        await _world.RemoveUnitAsync(unit);
    }

    /// <summary>Scene/role/disconnect teardown invalidates callbacks before despawning owned skill entities.</summary>
    public async Task ClearSkillsAsync(GameSession s)
    {
        _eventAttacks.Remove(s);
        s.WorldState.ResurrectionCastId = 0;
        s.Attack = null;
        if (s.SkillCast is { } cast) cast.Cancelled = true;
        s.SkillCast = null;
        if (s.CastMoveLockId != 0) { s.CastMoveLockId = 0; await _buffs.RefreshCastLockAsync(s); }
        foreach (var unit in s.SkillObjects.ToArray()) await RemoveSkillObjectAsync(unit);
    }

    private async Task<bool> GroundProtectsAsync(GameSession target, JsonElement? skill)
    {
        int distanceType = skill is { } row ? (int)GameData.Int(row, "_iDistanceType") : 1;
        var field = _world.Map(target.WorldScene).Units.OfType<SkillObject>().FirstOrDefault(u => !u.Removing && u.ExpiresMs > Now
            && u.ProtectionDistanceType == distanceType && u.Charges > 0 && Distance(u.Pos, target.Position) <= u.Radius);
        if (field == null) return false;
        if (--field.Charges == 0) await RemoveSkillObjectAsync(field);
        return true;
    }
}
