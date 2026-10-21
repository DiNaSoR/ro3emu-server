using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Game;

/// <summary>A single accepted cast, bound to its role, scene and generation (LC_Scene_Sync._skillEndNotify).</summary>
public sealed class ActiveSkillCast
{
    public required long Id { get; init; }
    public required long OwnerId { get; init; }
    public required long WorldScene { get; init; }
    public long SceneGeneration { get; init; }
    public Ro3Emu.Server.Game.Ownership.AttachmentLease? Attachment { get; init; }
    public required CastSkillReq Request { get; init; }
    public required JsonElement Row { get; init; }
    public required Vec3 Origin { get; set; }
    /// <summary>Accepted while the caster still walked (WalkSlack): its stop right after is where it cast from.</summary>
    public bool StartedWalking { get; set; }
    /// <summary>Where the server had the caster at the request: its steps until the stop are measured from here.</summary>
    public Vec3? WalkStart { get; init; }
    /// <summary>How far that stop may land from the origin: the walk speed at the request, before a cast dismount slowed it.</summary>
    public float ArrivalSlack { get; init; }
    public required long StartMs { get; init; }
    public required int ChantMs { get; init; }
    public required int ActionMs { get; init; }
    public bool Cancelled { get; set; }
    public bool Resolved { get; set; }
    /// <summary>HP/SP and buff-pile costs still owed by a chanted cast; paid once when the chant completes.</summary>
    internal Func<Task<bool>>? ChantCost { get; set; }
    /// <summary>A server bullet is in flight (CombatService.ServerBullet): cancelling the cast no longer stops it landing.</summary>
    public bool Launched { get; set; }
    public SkillObject? Operator { get; init; }
    public long SourceId => Operator?.Uid ?? OwnerId;
    /// <summary>A server-started follow-up (CombatService.Procs): it does not roll further procs.</summary>
    internal bool Proc { get; init; }
    /// <summary>A follow-up hits the row's area even when its _iRangeType names one target (Auto Counter's front box).</summary>
    internal bool AreaProc { get; init; }
    /// <summary>A damage multiplier: a follow-up's (Auto Counter +50% after a parried hit), or Cross Ripper's blade count.</summary>
    internal double ProcScale { get; set; } = 1;
    /// <summary>A follow-up that deals a set amount instead of the formula (Death Bound's reflected damage).</summary>
    internal long ProcFixedDamage { get; init; }
    /// <summary>Monsters a status of this cast already reached (Tetra Vortex's "at least 1" guarantee).</summary>
    internal HashSet<long> StatusLanded { get; } = new();
    /// <summary>This cast's own cooldown ends once it lands (Cross Ripper thrown with every blade).</summary>
    internal bool ResetsCooldown { get; set; }
    /// <summary>The cooldowns this cast started on accept, handed back if it is cancelled before its hit.</summary>
    internal IReadOnlyList<CooldownStamp>? Cooldowns { get; set; }
}

/// <summary>One cooldown a cast set: its end, and what the group/mode held before (0 = none).</summary>
internal readonly record struct CooldownStamp(int Group, int Mode, bool Shared, long End, long PreviousMode, long PreviousShared);

public sealed partial class CombatService
{
    public const uint SkillMove = 31112, TrapDamage = 31128;

    /// <summary>Runs under the role lock once a cast is accepted, before the client hears of it (MountService dismounts).</summary>
    public List<Func<GameSession, int, Task>> BeforeCast { get; } = new();

    /// <summary>WorldCoreService.CanLand: whether a flying mount's landing skill may be cast where the rider is.</summary>
    public Func<GameSession, bool>? CanLand { get; set; }

    /// <summary>
    /// 31102: row identity, ownership, selector, mode, all costs and cooldowns are checked before the cast commits.
    /// Target/position come from CastSkillReq; m_kSelectInfo uses relation masks, never _iDmgType as a selector.
    /// Evidence: LC_Skill_Function.SendSkill, SkillConfig.GetCost*, MG_Skill.proto.
    /// </summary>
    public async Task CastAsync(GameSession s, CastSkillReq request)
    {
        s.EndCutscene(); // a player who acts is out of the story; no attacking while immune
        LegacyOwnerScope? admission = null;
        OwnerNotices? notices = null;
        if (s.Owners is { } owners && s.Role is { } role)
            admission = await owners.EnterWithGateAsync("combat/cast", s.SkillCastGate, [ReservationKey.Role(role.Id)]);
        else await s.SkillCastGate.WaitAsync();
        try
        {
            using var state = await CharacterService.Lock(s);
            // Complete the runtime admission before any socket send can suspend. Frozen packets
            // retain the original order; an obsolete attachment cannot start a committed cast.
            if (s.Owners?.Current is { Notices: null } context) notices = new OwnerNotices(context);
            await CastCoreAsync(s, request);
        }
        finally { notices?.Dispose(); s.SkillCastGate.Release(); admission?.Dispose(); }
        if (notices != null) await notices.FlushAsync();
    }

    private async Task CastCoreAsync(GameSession s, CastSkillReq request)
    {
        var req = request.Clone();
        var row = EffectiveSkillRow(s, req.SkillId);
        var owned = req.OpUnitId == 0 || req.OpUnitId == s.Role?.Id ? null
            : s.SkillObjects.FirstOrDefault(u => u.Uid == req.OpUnitId && u.WorldScene == s.WorldScene && u.ExpiresMs > Now);
        int error = s.Role == null || s.Progress == null || s.Dead || s.SceneLoading ? ErrorCodes.SkillBadState
            : row == null ? ErrorCodes.SkillUnknown
            : req.OpUnitId != 0 && req.OpUnitId != s.Role.Id && owned == null ? ErrorCodes.SkillRequirements
            : !float.IsFinite(req.Face) || req.TargetPos is { } pos && !Finite(pos) ? ErrorCodes.Parameter : 0;
        if (error == 0)
        {
            req.SkillId = (int)GameData.Int(row!.Value, "_iID");
            if (owned != null ? !owned.Skills.Contains(req.SkillId) : !Learned(s, row!.Value)) error = ErrorCodes.SkillNotLearned;
            else if (!SkillModeAllowed(s, row!.Value)) error = ErrorCodes.SkillRequirements;
            else if (owned == null && !SkillConditionMet(s, row!.Value)) error = ErrorCodes.SkillRequirements; // weapon, mount, pendant
            else if (owned == null && !LandingAllowed(s, row!.Value)) error = ErrorCodes.SkillRequirements; // over water (inferred)
            else if (GameData.Int(row!.Value, "_iBasicType") != 1) error = ErrorCodes.SkillPassive;
            else if (_buffs.HasForbidden(s, GameData.Int(row.Value, "_iIsNormalAtk") == 1 ? 5 : 4)) error = ErrorCodes.SkillBadState;
            // Hymn of the Lamb: "This skill cannot be cast while You are under any control effect" (category 25: Stun,
            // Knockback, Fear, Frozen, Petrify, Sleep). Fear leaves other skills castable; Blind or Immobilize do not stop it.
            else if (Script(row.Value) == "Praefatio" && _buffs.CategoriesOf(s).Contains(25))
                error = ErrorCodes.SkillBadState;
            else if (req.SkillId == PublicSkill("WorldTreeSkillId") && _buffs.HasForbidden(s, 12)) error = ErrorCodes.ItemUseForbidden;
            else if (req.SkillId == PublicSkill("WorldTreeSkillId") && !ItemAllowedInMap(s, (int)PublicSkill("WorldTreeItemId"))) error = ErrorCodes.ItemUseAreaForbidden;
            else if (req.IsExtraSkill && owned == null && GameData.Int(row.Value, "_iExtraType") == 0) error = ErrorCodes.SkillRequirements;
        }
        var origin = owned?.Pos ?? s.Position;
        if (error == 0)
        {
            int group = (int)GameData.Int(row!.Value, "_iSkillID"), mode = (int)GameData.Int(row.Value, "_iMode");
            if (Math.Max(s.SkillCds.GetValueOrDefault(group), s.SkillModeCds.GetValueOrDefault((group, mode))) > Now) error = ErrorCodes.SkillCooldown;
            // A live cast refuses a new one until it yields (_iInterruptedType). _bCancelCast is only the client's
            // cancel-button flag (LC_SkillInput_Handler_Default.CheckShowCancelBtn), not a licence to recast.
            else if (s.SkillCast is { Cancelled: false } active && Now < active.StartMs + active.ChantMs + active.ActionMs
                && !CastYieldsTo(active, row.Value)) error = ErrorCodes.SkillBusy;
            else if (owned == null && NormalAttackTooSoon(s, row.Value)) error = ErrorCodes.SkillBusy;
            else error = ValidateTarget(s, row.Value, req, origin, owned == null ? WalkSlack(s) : 0);
        }
        var costs = row is { } value ? ResolveCosts(s, value) : new CastCosts();
        long errorParam = 0;
        if (error == 0) (error, errorParam) = ValidateCosts(s, costs);
        if (error == 0 && !_buffs.CanSpendEnergy(s, row!.Value)) error = ErrorCodes.SkillEnergyShort;
        if (error != 0)
        {
            await RefuseCastAsync(s, req, error, errorParam);
            return;
        }

        long id = ++_sessionIds;
        var lease = s.Attachment;
        long scene = s.SceneGeneration, worldScene = s.WorldScene;
        // E05c: the receipt is the paid action's admission. Repeating a protocol request
        // is a new intent and pays again; a lost writer ACK resolves this exact source.
        if (!await AdmitCastCostAsync(s, row!.Value, req, costs, id))
        {
            await RefuseCastAsync(s, req, ErrorCodes.SkillCostShort, costs.Items[0].Entry);
            return;
        }
        if (lease?.Cancelled == true || !ReferenceEquals(s.Attachment, lease) ||
            s.SceneGeneration != scene || s.WorldScene != worldScene || s.SceneLoading) return;
        float arrival = ArrivalSlack(s); // before BeforeCast: a cast dismount lowers Speed, not the ground the ride covered
        // Before the 31102 reply: LC_State_Unit_Skill holds a skill while m_bRideSystemMount is set, then drops it once
        // its time has passed, so a rider dismounted later by the 1 s mount tick swung unseen.
        foreach (var handler in BeforeCast) await handler(s, req.SkillId);
        await CancelCastAsync(s, SkillInterruptedInfo.Types.SkillInterruptType.Skill);
        _eventAttacks.Remove(s);
        // Classic RO rule (rAthena skill_castend): a chanted skill spends HP/SP only when its chant completes,
        // so an interrupted chant costs nothing. Instant skills pay now. Items and energy stay paid on accept.
        bool chanted = GameData.Int(row!.Value, "_iChantFix") + GameData.Int(row.Value, "_iChantVar") > 0;
        if (!chanted) await PayCostsAsync(s, costs);
        await _buffs.SpendEnergyAsync(s, row!.Value);
        await s.SendGameAsync(UseSkill, new CastSkillRsp { SessionId = id, IsAutoCast = req.IsAutoCast });
        var target = FindMonster(s.WorldScene, req.TargetId);
        var playerTarget = EventTarget(s, req.TargetId);
        // _iIfAutoAtk 2 (enum_AutoNormalAttackType.AutoNormalAttack) follows the cast with the career's normal attack
        // (LC_AutoAttackAppendState: GetNormalSkill). Repeating the cast skill itself bypassed its cooldown: Blade
        // Smash and Sonic Slash fired every swing.
        int loopSkill = GameData.Int(row!.Value, "_iIsNormalAtk") == 1 ? req.SkillId : _skills.NormalAttack(s);
        if (GameData.Int(row!.Value, "_iIfAutoAtk") == 2 && owned == null && target != null && CanHitMonster(s, target))
        {
            s.Attack = new AttackLoop
            {
                Target = target, SkillId = loopSkill,
                NextSwingMs = Now + SkillMs(s, row.Value) + ChantTime(s, row.Value) + 1,
                StartX = s.Position.X, StartZ = s.Position.Z, StartedWalking = s.Walking, StartMs = Now, ArrivalSlack = arrival
            };
        }
        else s.Attack = null;
        if (GameData.Int(row.Value, "_iIfAutoAtk") == 2 && owned == null && playerTarget != null)
            _eventAttacks[s] = new EventAttack(playerTarget, loopSkill,
                Now + SkillMs(s, row.Value) + ChantTime(s, row.Value) + 1,
                s.Position.X, s.Position.Z);
        var cooldowns = await StartCooldownAsync(s, row.Value);
        await StartCastAsync(s, row.Value, req, id, owned, chanted ? costs : null, arrival, cooldowns);
    }

    /// <summary>
    /// The chant: _iChantFix plus _iChantVar cut by Cast Speed 116 ("cspd": 2 per DEX by AttributeConversionConfig 5,
    /// Suffragium +10, Mystical Amplification +1 a level). The client has no formula; this is classic RO's renewal rule
    /// (inferred), where the variable cast shrinks by sqrt((DEX x 2 + INT) / 530) and the fixed part stays. Cast Speed
    /// below 0 ("Cast Speed decreases") lengthens it the same way. Before, Cast Speed changed nothing.
    /// </summary>
    internal int ChantTime(GameSession s, JsonElement row)
    {
        long fix = Math.Max(0, GameData.Int(row, "_iChantFix")), variable = Math.Max(0, GameData.Int(row, "_iChantVar"));
        if (variable == 0) return (int)fix;
        return (int)(fix + Math.Round(variable * ChantFactor(_character.Stats(s)[116])));
    }

    internal static double ChantFactor(long castSpeed) =>
        Math.Max(0, 1 - Math.Sign(castSpeed) * Math.Sqrt(Math.Min(Math.Abs(castSpeed), 530) / 530.0));

    /// <summary>The distance the client's stop right after a cast may land from where the server had it (in-game 11.10).</summary>
    private static float ArrivalSlack(GameSession s) => s.Speed / 100f * 0.75f + 0.5f;

    /// <summary>
    /// Whether a live cast gives way to a new skill, as LC_Skill_RunTimeContext.SkillCanInterrupted and
    /// GetSkillCanInterrupted decide it: _iInterruptedType 1 (normal attacks) yields to any other skill at once but never
    /// to itself; 2 yields once the template's m_fInterruptTime has passed (never without one); any other type holds
    /// until the cast ends. An interrupt point inside the chant counts from the chant's end, as the client sets it.
    /// </summary>
    internal bool CastYieldsTo(ActiveSkillCast active, JsonElement next)
    {
        // High-Energy Impact's flight (its 5 s action) is cut short by "Tap the skill again".
        if (FlightScript(Script(active.Row)) && FlightAttackScript(Script(next))) return true;
        switch (GameData.Int(active.Row, "_iInterruptedType"))
        {
            case 1:
                // (inferred) A swing whose hit already landed on a target that is now dead or gone has nothing left to
                // deal, so a press on the next target starts at once instead of waiting out the animation tail.
                return GameData.Int(next, "_iSkillID") != GameData.Int(active.Row, "_iSkillID")
                    || active.Resolved && !TargetAlive(active.WorldScene, active.Request.TargetId);
            case 2:
                return InterruptPointPassed(active);
            default:
                return false;
        }
    }

    /// <summary>Type 2's interrupt point (the template's m_fInterruptTime, never inside the chant) has passed.</summary>
    private bool InterruptPointPassed(ActiveSkillCast active)
    {
        var template = _data.SkillTemplate(GameData.Int(active.Row, "_iScriptShow"));
        if (template is not { } t || !t.TryGetProperty("m_fInterruptTime", out var value)
            || GameData.Number(value) is not double point || point <= 0) return false;
        return Now >= active.StartMs + Math.Max(active.ChantMs, ImpactOffset(template, point, active.ChantMs, active.ActionMs));
    }

    /// <summary>
    /// Whether move input breaks a live cast. While the server forbids walking, the client turns a move key or a
    /// ground click into a bare 31124 (LC_UnitChecker.CheckClearCacheSkill: Skill state, a cached skill and
    /// IsSkillBlockMove) and stays in the skill unless the server agrees (LC_Scene_Sync._skillInterrupt). Moving
    /// cancels a cast (Steven's call, 2026-10-19), except in its first MoveCancelGraceMs: the click that places a
    /// ground skill can come back as one of these requests right after the cast starts, and granting it dropped the
    /// spell (inferred window). Normal attacks (_iInterruptedType 1) and casts past their hit break at once.
    /// </summary>
    private bool MoveBreaksCast(ActiveSkillCast active) => active.Resolved || active.Launched
        || GameData.Int(active.Row, "_iInterruptedType") == 1 || Now >= active.StartMs + MoveCancelGraceMs;

    /// <summary>How long after a cast starts move input cannot cancel it (the placing click, see MoveBreaksCast).</summary>
    internal const int MoveCancelGraceMs = 300;

    private bool TargetAlive(long sceneId, long uid) => uid != 0 && (FindMonster(sceneId, uid) != null
        || _world.PlayersIn(sceneId).Any(p => p.Role?.Id == uid && !p.Dead));

    /// <summary>Cast item cost and frozen intent share one owner receipt; HP/SP/buffs remain runtime resources.
    /// The admission uses the same scheduled cast and lethal pipeline as every ordinary skill.</summary>
    private async Task<bool> AdmitCastCostAsync(GameSession s, JsonElement row, CastSkillReq request, CastCosts costs, long id)
    {
        if (costs.Items.Count == 0) return true;
        var mutation = _character.ProgressionEconomy(s, "cd13c/cast-admit", costs.Items, [],
            new
            {
                SessionId = id, Skill = row.GetRawText(), Request = Convert.ToBase64String(Google.Protobuf.MessageExtensions.ToByteArray(request)),
                Plan = s.SkillPlans?.CurrentPlanIndex, Attachment = s.Attachment?.Generation,
                s.WorldScene, s.SceneGeneration, AttributeCosts = costs.Attrs, BuffCosts = costs.Buffs,
                EnergyCosts = costs.Energy, Outcome = "admitted"
            });
        return await _character.ExchangeLockedAsync(s, costs.Items, [], popup: false,
            persist: tx => tx.WithEconomy(mutation), asyncCommit: true) != null;
    }

    private Task RefuseCastAsync(GameSession s, CastSkillReq req, int error, long parameter) => RefuseCoreAsync(s, req, error, parameter);

    // Refusal must not interrupt a different accepted cast: the client does not compare 31103.sessionId.
    private async Task RefuseCoreAsync(GameSession s, CastSkillReq req, int error, long parameter)
    {
        var reply = new CastSkillRsp { Result = error, IsAutoCast = req.IsAutoCast };
        if (parameter != 0) reply.Params.Add(parameter);
        await s.SendGameAsync(UseSkill, reply);
        if (s.SkillCast is not { Cancelled: false })
        {
            var end = new SkillEndNotify { SrcID = s.Role?.Id ?? 0, Type = SkillEndNotify.Types.SkillEndType.Interrupted, Result = error };
            if (parameter != 0) end.Params.Add(parameter);
            await s.SendGameAsync(SkillEnd, end);
        }
    }

    /// <summary>
    /// A normal attack (_iInterruptedType 1) waits for the previous one's swing, cancelled or not: cancelling a swing once
    /// its hit landed, or alternating two normal-attack groups, would otherwise attack several times per swing. A
    /// fifth of the swing is allowed as latency slack, and a swing whose target already died gives way at once
    /// (CastYieldsTo).
    /// </summary>
    private bool NormalAttackTooSoon(GameSession s, JsonElement row)
    {
        if (GameData.Int(row, "_iInterruptedType") != 1 || s.LastNormalAttack is not { } last) return false;
        if (last.WorldScene != s.WorldScene || last.Resolved && !TargetAlive(last.WorldScene, last.Request.TargetId)) return false;
        return Now < last.StartMs + (last.ChantMs + last.ActionMs) * 4L / 5;
    }

    private bool Learned(GameSession s, JsonElement row)
    {
        if (GameData.Int(row, "_iID") == PublicSkill("WorldTreeSkillId")) return true; // paid item skill, LC_ReviveHelper.
        // A Survivor Battle career owns the match skill set (TimedBattleService.Chicken), not the job tree.
        if (MatchSkillRule?.Invoke(s, (int)GameData.Int(row, "_iID")) is { } granted) return granted;
        int normal = _skills.NormalAttack(s);
        if (GameData.Int(row, "_iID") == normal) return true;
        // Only the normal attack of the weapon and battle mount in use (SkillService.NormalAttack): the career's others
        // count as learned for the skill window, but an Archer without a bow does not shoot at bow range.
        int group = (int)GameData.Int(row, "_iSkillID");
        if (_skills.NormalGroups(s.Progress!.Career).Contains(group)
            && _data.Row("SkillConfig", normal) is { } current && GameData.Int(current, "_iSkillID") != group) return false;
        // The flying mount's take-off on the ground, its landing in the air (MountService.FlySkills, SkillWheelMount1).
        if (MountService.FlySkills(s, _data) is { } fly && GameData.Int(row, "_iID") == (s.WorldState.Flying ? fly.Down : fly.Up)) return true;
        if (GameData.Int(row, "_iSystemType") == ActiveGuardSkill) return _skills.PetMainSkill?.Invoke(s)?.SkillId == GameData.Int(row, "_iID");
        if (ChainOffers(s, row)) return true; // Thousand Shadows, Shadow Loop, Shadow Echo's return (CombatService.Chains)
        return _skills.Level(s.Progress, (int)GameData.Int(row, "_iSkillID")) >= GameData.Int(row, "_iLevel");
    }

    /// <summary>A flying mount's landing skill needs walkable ground under the rider (<see cref="CanLand"/>).</summary>
    private bool LandingAllowed(GameSession s, JsonElement row) =>
        !s.WorldState.Flying || MountService.FlySkills(s, _data) is not { } fly || GameData.Int(row, "_iID") != fly.Down
        || CanLand?.Invoke(s) != false;

    private MonsterUnit? FindMonster(long sceneId, long uid) => _world.Map(sceneId).Units.OfType<MonsterUnit>().FirstOrDefault(m => m.Uid == uid && !m.Dead);
    private GameSession? FindPlayer(GameSession s, long uid, bool dead = false) => _world.PlayersIn(s.WorldScene).FirstOrDefault(p => p.Role?.Id == uid && p.Dead == dead);
    private long PublicSkill(string key) => _data.Table("PublicParamConfig").TryGetValue(key, out var value) ? (long)(GameData.Number(value) ?? 0) : 0;
    private static bool Finite(Vec3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    private static float Distance(Vec3 a, Vec3 b) => MonsterService.Distance(a.X, a.Z, b.X, b.Z);
    private static float Range(JsonElement row) => Math.Max(0, GameData.Int(row, "_iDistanceMax") / 100f);
    private static string Script(JsonElement row) => row.TryGetProperty("_kScriptLogic", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static double[] Numbers(JsonElement row, string key) => row.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array
        ? list.EnumerateArray().Select(v => GameData.Number(v) ?? 0).ToArray() : [];
    private static IEnumerable<JsonElement> Tuples(JsonElement row, string key) => row.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array
        ? list.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Array) : [];

    private bool Relation(JsonElement row, bool friendly, bool self = false, int race = 0, bool dead = false)
    {
        var template = _data.SkillTemplate(GameData.Int(row, "_iScriptShow"));
        if (template is not { } t || !t.TryGetProperty("m_kSelectInfo", out var selectors) || selectors.ValueKind != JsonValueKind.Array)
            return friendly == (GameData.Int(row, "_iDmgType") == 3); // (inferred) typed recovery fallback for rows without a visual selector.
        foreach (var select in selectors.EnumerateArray())
        {
            int relation = (int)GameData.Int(select, "m_kSelectType"), subtype = (int)GameData.Int(select, "m_kSelectSubType");
            if ((relation & (dead ? friendly ? 32 : 16 : friendly ? 2 : 1)) == 0 && (relation & (dead ? 64 : 4)) == 0) continue;
            if (subtype == 2 && !self || subtype == 16 && self) continue;
            long raceMask = GameData.Int(select, "m_kSelectRaceType");
            if (!friendly && raceMask > 0 && (race <= 0 || (raceMask & (1L << (race - 1))) == 0)) continue;
            return true;
        }
        return false;
    }

    // The client casts on reaching range while it still walks; its stop arrives just after the cast (in-game 11.10:
    // 13 ms later, refused 3024 "invalid skill distance" on most approaches). The cast carries no caster position, so
    // a walking caster's range allows the distance it can have covered since its last walk, at most 0.75 s of it.
    private static float WalkSlack(GameSession s) => !s.Walking ? 0
        : (float)(s.Speed / 100.0 * Math.Clamp((DateTime.UtcNow - s.LastWalkUtc).TotalSeconds, 0, 0.75));

    private int ValidateTarget(GameSession s, JsonElement row, CastSkillReq req, Vec3 origin, float slack = 0)
    {
        int release = (int)GameData.Int(row, "_iRelease");
        var monster = FindMonster(s.WorldScene, req.TargetId);
        bool revive = Script(row) == "Resurrection";
        var player = req.TargetId == s.Role!.Id ? s : FindPlayer(s, req.TargetId, revive);
        var enemyPlayer = EventTarget(s, req.TargetId);
        Vec3 targetPosition;
        if (release == 1)
        {
            if (monster != null && CanHitMonster(s, monster) && Relation(row, false, race: monster.Template.Race)) targetPosition = monster.Pos;
            else if (!revive && enemyPlayer != null) targetPosition = enemyPlayer.Position;
            else if (player != null && player.Dead == revive && Relation(row, true, player == s, dead: revive)
                && (!revive || !_buffs.HasForbidden(player, 16))) targetPosition = player.Position;
            else if (req.TargetId == 0 && Relation(row, true, true)) { req.TargetId = s.Role.Id; targetPosition = s.Position; }
            else return ErrorCodes.SkillTargetInvalid;
            req.TargetPos = targetPosition.Clone();
            if (revive)
            {
                if (player == s) return ErrorCodes.SkillTargetInvalid;
                if (req.SkillId == PublicSkill("WorldTreeSkillId") ? ReviveItemSkill == null : ReviveOffer == null)
                    return ErrorCodes.SkillReviveNotAllowed;
                if (ReviveCheck?.Invoke(player!) is { } reviveError && reviveError != 0) return reviveError;
            }
        }
        else if (release is 7 or 8)
        {
            if (req.TargetPos == null) return ErrorCodes.SkillTargetInvalid;
            targetPosition = req.TargetPos;
        }
        else
        {
            // No release target: the hit picks req.TargetId (single-target rows) and summons or pulls use req.TargetPos,
            // so both must lie within the row's reach of the caster. One beyond it is dropped rather than refused, so a
            // client that sends a stale target still casts (Auto Battle does not retry a refusal).
            float reach = Math.Max(5f, Math.Max(Range(row), (float)(Numbers(row, "_kRange").FirstOrDefault(0) / 100))) + 1.5f + slack;
            if (req.TargetPos == null || Distance(origin, req.TargetPos) > reach) req.TargetPos = origin.Clone();
            if (req.TargetId != 0 && req.TargetId != s.Role.Id
                && (monster?.Pos ?? enemyPlayer?.Position ?? player?.Position) is { } at && Distance(origin, at) > reach)
                req.TargetId = 0;
            return 0;
        }
        float d = Distance(origin, targetPosition);
        if (d < GameData.Int(row, "_iDistanceMin") / 100f - slack || d > Range(row) + 0.5f + slack) return ErrorCodes.SkillDistance;
        float height = GameData.Int(row, "_iHeightRange") / 100f;
        if (height > 0 && MathF.Abs(origin.Y - targetPosition.Y) > height) return ErrorCodes.SkillHeight;
        // Same test as the client's own pre-cast check (LC_Skill_Function.CheckPosIsCanInputSkill), so a cast the client
        // lets through is never refused here: neither manual casts nor Auto Battle reposition on 3026, and Auto Battle
        // (LC_AutoAttack_AttackSubState) never runs the check at all, so a stricter server test loops it forever.
        if (_worldData.Map(s.SceneEntry)?.Nav is { } nav
            && !nav.CanPass(origin.X, origin.Z, targetPosition.X, targetPosition.Z, (int)GameData.Int(row, "_iPassBlock"))
            && (release != 1 || monster == null
                || BlockedMonsterEndpointAllowed?.Invoke(s, monster, origin, targetPosition) != true))
            return ErrorCodes.SkillBlocked;
        return 0;
    }

    /// <summary>LC_ItemHelper.ItemCanUseInCurrentMap: MapConfig type/subtype blacklist (1) or whitelist (2).</summary>
    private bool ItemAllowedInMap(GameSession s, int entry)
    {
        if (_data.Row("MapConfig", s.SceneEntry) is not { } map || _data.Row("ItemConfig", entry) is not { } item) return false;
        int mode = (int)GameData.Int(map, "_iLimitType");
        if (mode is not (1 or 2)) return true;
        bool listed = Tuples(map, "_kLimitItem").Any(t => t.GetArrayLength() >= 2 && GameData.Number(t[1]) ==
            GameData.Int(item, GameData.Number(t[0]) == 1 ? "_iType" : "_iSubType"));
        return mode == 1 ? !listed : listed;
    }

    private sealed class CastCosts
    {
        public Dictionary<int, long> Attrs { get; } = new();
        public List<(int Entry, long Count)> Items { get; } = new();
        public Dictionary<int, long> Buffs { get; } = new();
        public Dictionary<int, long> Energy { get; } = new();
        public bool Invalid { get; set; }
    }

    private CastCosts ResolveCosts(GameSession s, JsonElement row)
    {
        var result = new CastCosts();
        var stats = _character.Stats(s);
        foreach (var tuple in Tuples(row, "_kCost"))
        {
            var c = tuple.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray();
            // (inferred) retained opaque type-4 markers follow the live client checker, which skips them.
            // Lua_GameDefine.enum_SkillCostType / LC_Skill_Function.CheckSkillCost only consume 1/2/3/6/7/8.
            if (c.Length == 2 && c[0] == 4) continue;
            if (c.Length < 3) { result.Invalid = true; continue; }
            int key = (int)c[1];
            long amount = c.Length > 3 ? c[3] : c[2];
            switch (c[0])
            {
                case 1 when c.Length >= 4 && key is 2000 or 2001:
                    amount += stats[key == 2000 ? 101 : 102] * c[2] / 10000;
                    result.Attrs[key] = result.Attrs.GetValueOrDefault(key) + Math.Max(0, amount);
                    break;
                case 7 when c.Length >= 4 && key is 2000 or 2001:
                    // AttrConvert [7, resource, basis-property, ratio], LC_Skill_Function cost checker.
                    result.Attrs[key] = result.Attrs.GetValueOrDefault(key) + Math.Max(0, stats[(int)c[2]] * c[3] / 10000);
                    break;
                case 2: result.Items.Add((key, Math.Max(0, c[2]))); break;
                case 3: result.Buffs[key] = result.Buffs.GetValueOrDefault(key) + Math.Max(0, c[2]); break;
                case 8: result.Energy[key] = result.Energy.GetValueOrDefault(key) + Math.Max(0, amount); break;
                default: result.Invalid = true; break; // reject an unsupported cost rather than grant a free cast.
            }
        }
        if (result.Attrs.TryGetValue(2001, out long sp)) result.Attrs[2001] = SpAfterDeduction(stats, sp);
        return result;
    }

    private (int Error, long Parameter) ValidateCosts(GameSession s, CastCosts costs)
    {
        if (costs.Invalid) return (ErrorCodes.SkillRequirements, 0);
        var stats = _character.Stats(s);
        foreach (var (attr, cost) in costs.Attrs)
            if (cost > stats[attr] || attr == 2000 && cost >= stats[attr]) return (ErrorCodes.SkillCostShort, attr);
        foreach (var (group, count) in costs.Buffs)
            if (!_buffs.HasBuffPiles(s, group, (int)count))
                return (ErrorCodes.SkillBuffCostShort, group);
        return (0, 0);
    }

    private async Task PayCostsAsync(GameSession s, CastCosts costs)
    {
        if (costs.Attrs.Count > 0)
        {
            var stats = _character.Stats(s);
            await _character.SetHpSpAsync(s, stats[2000] - costs.Attrs.GetValueOrDefault(2000), stats[2001] - costs.Attrs.GetValueOrDefault(2001));
        }
        foreach (var (group, count) in costs.Buffs)
            await _buffs.ConsumeBuffPilesAsync(s, group, (int)count);
    }

    /// <summary>Spends a chanted cast's deferred costs once. If the caster no longer has them, the cast fails
    /// like a refusal (31104 Interrupted with the cost error) and nothing is spent.</summary>
    private async Task<bool> PayChantCostAsync(GameSession s, ActiveSkillCast action, CastCosts costs)
    {
        action.ChantCost = null;
        var (error, parameter) = ValidateCosts(s, costs);
        if (error == 0)
        {
            await PayCostsAsync(s, costs);
            return true;
        }
        await CancelCastAsync(s, SkillInterruptedInfo.Types.SkillInterruptType.Skill, error, parameter);
        return false;
    }

    private async Task<IReadOnlyList<CooldownStamp>?> StartCooldownAsync(GameSession s, JsonElement row, bool landing = false)
    {
        if (s.GmNoCooldown) return null; // GM panel GmNoCD
        // High-Energy Impact's take-off shares its cooldown with the attack it arms; the cooldown starts on landing.
        if (!landing && FlightScript(Script(row))) return null;
        int group = (int)GameData.Int(row, "_iSkillID"), mode = (int)GameData.Int(row, "_iMode");
        long now = Now, cd = GameData.Int(row, "_iCD");
        var notice = new SkillCdNotify { Uuid = s.Role!.Id };
        var stamps = new List<CooldownStamp>();
        void Set(int g, int m, long duration, bool shared)
        {
            if (duration <= 0) return;
            long end = now + duration;
            long previousMode = s.SkillModeCds.GetValueOrDefault((g, m)), previousShared = s.SkillCds.GetValueOrDefault(g);
            s.SkillModeCds[(g, m)] = Math.Max(end, previousMode);
            if (shared) s.SkillCds[g] = Math.Max(end, previousShared);
            stamps.Add(new CooldownStamp(g, m, shared, end, previousMode, previousShared));
            notice.Infos.Add(new SkillCdInfo { SkillId = g, SkillMode = m, CdStartTimestampMs = now, CdEndTimestampMs = end });
        }
        Set(group, mode, cd, GameData.Int(row, "_iCdModeGroup") < 0);
        var shared = Numbers(row, "_iPubCD");
        var groups = Numbers(row, "_iPubCDGroup");
        // Parallel arrays, e.g. BackSlide [15104,15330,15329] / [1000,15000,0] (inferred server application).
        for (int i = 0; i < Math.Min(shared.Length, groups.Length); i++)
        {
            int sibling = (int)groups[i];
            if (_data.Row("SkillConfig", sibling) is { } siblingRow) sibling = (int)GameData.Int(siblingRow, "_iSkillID");
            if (sibling > 0) Set(sibling, 0, (long)shared[i], true);
        }
        if (notice.Infos.Count > 0) await s.SendGameAsync(SkillCdUpdate, notice);
        return stamps;
    }

    /// <summary>
    /// A cast cancelled before its hit did nothing, so it gives back the cooldowns it started on accept (classic RO
    /// starts them only when the cast lands, rAthena skill_castend). A group whose cooldown something else raised since
    /// keeps that later one. Items and energy stay spent, as on accept.
    /// </summary>
    private async Task RefundCooldownsAsync(GameSession s, ActiveSkillCast action)
    {
        if (action.Cooldowns is not { Count: > 0 } stamps || s.Role == null) return;
        action.Cooldowns = null;
        long now = Now;
        var notice = new SkillCdNotify { Uuid = s.Role.Id, NotPlayCdRefreshEffect = true };
        foreach (var stamp in stamps.Reverse()) // undo in reverse: a later stamp may have raised an earlier one's group
        {
            bool changed = false;
            if (s.SkillModeCds.GetValueOrDefault((stamp.Group, stamp.Mode)) == stamp.End)
            {
                if (stamp.PreviousMode > now) s.SkillModeCds[(stamp.Group, stamp.Mode)] = stamp.PreviousMode;
                else s.SkillModeCds.Remove((stamp.Group, stamp.Mode));
                changed = true;
            }
            if (stamp.Shared && s.SkillCds.GetValueOrDefault(stamp.Group) == stamp.End)
            {
                if (stamp.PreviousShared > now) s.SkillCds[stamp.Group] = stamp.PreviousShared;
                else s.SkillCds.Remove(stamp.Group);
                changed = true;
            }
            if (!changed) continue;
            long end = Math.Max(now, Math.Max(s.SkillModeCds.GetValueOrDefault((stamp.Group, stamp.Mode)),
                stamp.Shared ? s.SkillCds.GetValueOrDefault(stamp.Group) : 0));
            notice.Infos.Add(new SkillCdInfo { SkillId = stamp.Group, SkillMode = stamp.Mode, CdStartTimestampMs = now, CdEndTimestampMs = end });
        }
        if (notice.Infos.Count > 0) await s.SendGameAsync(SkillCdUpdate, notice);
    }

    /// <summary>Legacy auto-attack entry point, using the same cancellation-safe cast owner as manual skills.</summary>
    private async Task SwingAsync(GameSession s, MonsterUnit? target, int skillId, long id)
    {
        if (s.Role == null || s.Progress == null) return;
        using var held = await CharacterService.Lock(s);
        if (target != null && !CanHitMonster(s, target)) return;
        if (EffectiveSkillRow(s, skillId) is not { } row || !Learned(s, row) || !SkillModeAllowed(s, row)
            || _buffs.HasForbidden(s, 5) || s.SkillCast is { Cancelled: false }) return;
        var request = new CastSkillReq
        {
            SkillId = (int)GameData.Int(row, "_iID"), TargetId = target?.Uid ?? 0,
            TargetPos = target?.Pos ?? s.Position.Clone(), Face = s.Face
        };
        if (!await AdmitAutomaticCostAsync(s, row, request, id)) { s.Attack = null; return; }
        await StartCastAsync(s, row, request, id);
    }

    private async Task<bool> AdmitAutomaticCostAsync(GameSession s, JsonElement row, CastSkillReq request, long id)
    {
        var costs = ResolveCosts(s, row);
        if (costs.Items.Count == 0)
        {
            // No item receipt to admit, but HP/SP still apply: Sacrifice's basic-attack modes cost 30 HP a swing.
            if (costs.Attrs.Count == 0 && costs.Buffs.Count == 0) return true;
            if (ValidateCosts(s, costs).Error != 0) return false;
            await PayCostsAsync(s, costs);
            return true;
        }
        if (ValidateCosts(s, costs).Error != 0 || !_buffs.CanSpendEnergy(s, row)) return false;
        var lease = s.Attachment;
        long scene = s.SceneGeneration, worldScene = s.WorldScene;
        bool Current() => lease?.Cancelled != true && ReferenceEquals(s.Attachment, lease) &&
            s.SceneGeneration == scene && s.WorldScene == worldScene && !s.SceneLoading && !s.Dead;
        if (!await AdmitCastCostAsync(s, row, request, costs, id) || !Current()) return false;
        await PayCostsAsync(s, costs);
        await _buffs.SpendEnergyAsync(s, row);
        return Current();
    }

    private async Task StartCastAsync(GameSession s, JsonElement row, CastSkillReq req, long id, SkillObject? actor = null, CastCosts? chantCosts = null, float? arrivalSlack = null, IReadOnlyList<CooldownStamp>? cooldowns = null)
    {
        var origin = actor?.Pos ?? s.Position.Clone();
        var target = FindMonster(s.WorldScene, req.TargetId);
        float face = target != null ? MonsterService.Angle(origin.X, origin.Z, target.X, target.Z) : req.Face;
        var action = new ActiveSkillCast
        {
            Id = id, OwnerId = s.Role!.Id, WorldScene = s.WorldScene, SceneGeneration = s.SceneGeneration,
            Attachment = s.Attachment, Request = req, Row = row, Origin = origin, StartedWalking = actor == null && s.Walking, WalkStart = origin.Clone(), ArrivalSlack = arrivalSlack ?? ArrivalSlack(s),
            StartMs = Now, ChantMs = ChantTime(s, row), ActionMs = SkillMs(s, row), Operator = actor,
            Cooldowns = cooldowns,
        };
        if (chantCosts is { } owed) action.ChantCost = () => PayChantCostAsync(s, action, owed);
        s.SkillCast = action;
        if (actor == null && GameData.Int(row, "_iInterruptedType") == 1) s.LastNormalAttack = action;
        var template = _data.SkillTemplate(GameData.Int(row, "_iScriptShow"));
        var points = template is { } t ? Numbers(t, "m_kAtkPoints") : [];
        double point = points.FirstOrDefault(0.2);
        int at = Script(row) == "Resurrection" ? action.ChantMs : ImpactOffset(template, point, action.ChantMs, action.ActionMs);
        // A Lock skill (_bMovement 0) roots the caster until its hit. The client asks to cast while still walking
        // (LC_Skill_ActionPreCheck.CheckSkillSendServer sends its position, then USE_SKILL_REQ, without stopping) and
        // keeps walking unless the server forbids it: its joystick and path controllers stop only on StatusForbidden
        // Walk (LC_Unit_Player.IsSkillBlockMove). Without it the next step cancelled the cast as a move.
        // A hit inside the first loop tick (Grimtooth: m_kAtkPoints [0]) has nothing to hold still for, and the lock
        // flipping on and off around its dash (PerformEffect_Agent.SetBlockInputState shares the same StopRayCastMove /
        // LJ_BlockMove flags) left the client unable to walk until its next such skill (in game 10.18, inferred cause).
        bool locked = actor == null && GameData.Int(row, "_bMovement") == 0 && at > GameLoop.TickInterval.TotalMilliseconds;
        if (locked) await LockCastMovementAsync(s, id);
        try { await AnnounceAndScheduleCastAsync(s, row, req, action, actor, face, origin, at); }
        catch
        {
            // Nothing is scheduled to lift the lock if the announcement failed.
            if (locked) await ReleaseCastMovementAsync(s, id);
            throw;
        }
    }

    private async Task AnnounceAndScheduleCastAsync(GameSession s, JsonElement row, CastSkillReq req, ActiveSkillCast action,
        SkillObject? actor, float face, Vec3 origin, int at)
    {
        long id = action.Id;
        s.WorldState.ResurrectionCastId = Script(row) == "Resurrection" ? id : 0;
        s.Face = face;
        req.Face = face;
        await SendCastTimelineAsync(s, new UseInSkillInfo
        {
            SrcID = action.SourceId, SkillID = req.SkillId, TargetId = req.TargetId, TargetPos = req.TargetPos ?? origin,
            Face = face, SessionId = id, ChantMs = action.ChantMs, SkillMs = action.ActionMs, StartTime = action.StartMs, PlayAct = true,
        }, highLevel: GameData.Int(row, "_iSkillEffectLevel") > 0, extra: actor != null || req.IsExtraSkill);
        if (action.ChantCost != null)
            ScheduleRuntime(action.WorldScene, TimeSpan.FromMilliseconds(Math.Min(at, action.ChantMs)), async () =>
            {
                if (Live(s, action) && action.ChantCost is { } pay) await pay();
            });
        ScheduleRuntime(action.WorldScene, TimeSpan.FromMilliseconds(at), async () =>
        {
            if (!Live(s, action)) return;
            // An impact on the chant boundary can run before the chant-end callback: settle the cost first.
            if (action.ChantCost is { } pay && !await pay()) return;
            action.Resolved = true;
            // Past its hit the client lets a move break the skill (LC_State_Unit_Skill ReadyToEnd + IsCanMoveBreak).
            await ReleaseCastMovementAsync(s, action.Id);
            await ResolveCastAsync(s, action);
        });
        ScheduleRuntime(action.WorldScene, TimeSpan.FromMilliseconds(Math.Max(at, action.ChantMs + action.ActionMs) + 1), async () =>
        {
            if (ReferenceEquals(s.SkillCast, action)) s.SkillCast = null;
            await ReleaseCastMovementAsync(s, action.Id); // a cast that never reached its hit (death, refused chant cost)
        });
    }

    private async Task LockCastMovementAsync(GameSession s, long castId)
    {
        s.CastMoveLockId = castId;
        await _buffs.RefreshCastLockAsync(s);
    }

    /// <summary>Lifts the cast's movement lock; a newer cast's lock stays.</summary>
    private async Task ReleaseCastMovementAsync(GameSession s, long castId)
    {
        if (s.CastMoveLockId != castId || castId == 0) return;
        s.CastMoveLockId = 0;
        await _buffs.RefreshCastLockAsync(s);
    }

    /// <summary>Piecewise template time mapping; same scale as LC_Skill_RunTimeContext (inferred for attack points).</summary>
    internal static int ImpactOffset(JsonElement? template, double point, int chantMs, int actionMs)
    {
        double chant = 0, end = 1;
        if (template is { } t && t.TryGetProperty("m_kState", out var state))
        {
            var times = Numbers(state, "Time");
            if (times.Length >= 3) { chant = times[1]; end = times[2]; }
        }
        double ms = point < chant && chant > 0 ? point / chant * chantMs : chantMs + Math.Clamp((point - chant) / Math.Max(0.001, end - chant), 0, 1) * actionMs;
        return (int)Math.Max(0, Math.Round(ms));
    }

    private static bool Live(GameSession s, ActiveSkillCast action) => !action.Cancelled && ReferenceEquals(s.SkillCast, action)
        && !s.Dead && !s.SceneLoading && s.Role?.Id == action.OwnerId && s.WorldScene == action.WorldScene
        && (action.Operator == null || !action.Operator.Removing && action.Operator.ExpiresMs > Now);

    private int SkillMs(GameSession s, JsonElement row)
    {
        double aspd = Math.Clamp(_character.Stats(s)[115] / 100.0, 0.1, 5);
        double influence = GameData.Int(row, "_iActionTimeInfluence") / 100.0;
        return (int)Math.Round(Math.Max(1, GameData.Int(row, "_iActionTime")) / Math.Max(0.1, 1 + (aspd - 1) * influence));
    }

    public async Task InterruptAsync(GameSession s, SkillInterruptReq? req = null)
    {
        bool valid = req == null || req.InterruptSkillId == 0 || req.InterruptSkillId == s.SkillCast?.Request.SkillId
            || req.InterruptSkillId == s.Attack?.SkillId || _eventAttacks.TryGetValue(s, out var eventAttack) && req.InterruptSkillId == eventAttack.SkillId;
        // A bare request is move input (see MoveBreaksCast): inside the grace window the cast carries on.
        if (valid && req is { InterruptSkillId: 0, NxtSkill: null } && s.SkillCast is { Cancelled: false } live
            && Now < live.StartMs + live.ChantMs + live.ActionMs && !MoveBreaksCast(live)) valid = false;
        bool interrupted = valid && (s.SkillCast != null || s.Attack != null || _eventAttacks.ContainsKey(s));
        if (interrupted)
        {
            s.Attack = null;
            _eventAttacks.Remove(s);
            await CancelCastAsync(s, req?.NxtSkill != null ? SkillInterruptedInfo.Types.SkillInterruptType.Skill : SkillInterruptedInfo.Types.SkillInterruptType.Move);
        }
        await s.SendGameAsync(SkillInterrupt, new SkillInterruptResp { IsInterruptSucc = interrupted });
        if (interrupted && req?.NxtSkill is { } next) await CastAsync(s, next);
    }

    private async Task CancelCastAsync(GameSession s, SkillInterruptedInfo.Types.SkillInterruptType cause, int result = 0, long parameter = 0)
    {
        s.WorldState.ResurrectionCastId = 0;
        if (s.SkillCast is not { } action) return;
        action.Cancelled = true;
        s.SkillCast = null;
        await ReleaseCastMovementAsync(s, action.Id);
        if (!action.Resolved && !action.Launched) await RefundCooldownsAsync(s, action);
        await ToPlayerAndViewersAsync(s, SkillEnd, new SkillEndNotify
        {
            SrcID = action.SourceId, SessionId = action.Id, Type = SkillEndNotify.Types.SkillEndType.Interrupted,
            ClearEffect = !action.Launched, InterruptedInfo = new SkillInterruptedInfo { Type = cause }, Result = result,
            Params = { parameter == 0 ? [] : new[] { parameter } },
        });
    }

    public void PlayerMoved(GameSession s)
    {
        SwiftWargOnMove(s);
        if (_eventAttacks.TryGetValue(s, out var eventAttack)
            && MonsterService.Distance(eventAttack.StartX, eventAttack.StartZ, s.Position.X, s.Position.Z) > 1.5f) _eventAttacks.Remove(s);
        if (s.Attack is { } a && MonsterService.Distance(a.StartX, a.StartZ, s.Position.X, s.Position.Z) > 0.1f)
        {
            // Clicking an enemy from afar: the client attacks on reaching range and stops a few ms later (in-game
            // 11.10), so that first stop is where the auto-attack starts, not a step away that ends it.
            if (a.StartedWalking && !s.Walking && Now - a.StartMs <= 300
                && MonsterService.Distance(a.StartX, a.StartZ, s.Position.X, s.Position.Z) <= a.ArrivalSlack)
            {
                a.StartX = s.Position.X; a.StartZ = s.Position.Z;
                a.StartedWalking = false;
            }
            else s.Attack = null;
        }
        if (s.SkillCast is not { } cast || Distance(cast.Origin, s.Position) <= 0.1f || GameData.Int(cast.Row, "_bMovement") != 0) return;
        // The client casts while it still walks and stops only once it hears of the cast (in-game 11.10: 1 ms later on
        // reaching range; a cast pressed mid-walk stops on the movement lock). Its steps until that stop are the cast's
        // real origin, within the distance the walk can have covered, not a move that interrupts it.
        if (cast.StartedWalking && Now - cast.StartMs <= 300
            && Distance(cast.WalkStart ?? cast.Origin, s.Position) <= cast.ArrivalSlack)
        {
            cast.Origin = s.Position.Clone();
            if (!s.Walking) cast.StartedWalking = false;
            return;
        }
        _loop.TrackRuntimeDetached("cast movement cancellation", () => CancelCastAsync(s, SkillInterruptedInfo.Types.SkillInterruptType.Move));
    }
}
