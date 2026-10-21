using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;
using static Ro3Emu.Server.Game.ReconstructedDungeons;

namespace Ro3Emu.Server.Game;

/// <summary>
/// A story boss encounter (Reconstructed/story-encounters.json, docs/systems/dungeon.md "Story encounters") on top of
/// the scene's ordinary plan: the plan still spawns the boss and settles the run on its death. This adds the boss's own
/// skill kit with chosen hit areas, the allied companions (camp-1 story templates with their SkillConfig kits) who fight
/// beside the player, the stages that move the quest tracker, and the scene's ChatConfig lines through the client's
/// dungeon actions (30404 DungeonEventInvokeClientFunc: LC_DunGeon_Actions.Shouting / ShowShoutingChatBubble).
/// Every rotation, target choice and timing here is (inferred): the server's scripts are not in the client.
/// </summary>
public sealed partial class DungeonService
{
    private const int TickMs = 250;
    private const float AllyReach = 30f;     // companions help and are helped within this distance (inferred)
    private const int HealBelowPct = 95;     // MainStory_MuShi FilterTargetGroupWithHp(1, 95, ...)
    private const int SanctuaryCount = 3;    // ... its third argument; read as "at least 3 hurt" for Sanctuary (inferred)
    private static long _encounterCasts = 1L << 40;

    internal sealed class EncounterRun
    {
        public required Encounter Def { get; init; }
        public required MonsterUnit Boss { get; init; }
        public List<AllyState> Allies { get; } = new();
        public int Stage { get; set; }
        public long StageStartMs { get; set; }
        public bool Ending { get; set; }
        public bool Done { get; set; }
        public Dictionary<int, long> BossReady { get; } = new();
        public long BossNextCastMs { get; set; }
        public long NextRetargetMs { get; set; }
        public MonsterUnit? Provoker { get; set; }
        public long ProvokeUntilMs { get; set; }
        public HashSet<int> SaidSkillLines { get; } = new();
        public int Quest => Def.Stages[Math.Min(Stage, Def.Stages.Count - 1)].Quest;
    }

    internal sealed class AllyState
    {
        public required MonsterUnit Unit { get; init; }
        public required EncounterKit Kit { get; init; }
        public Dictionary<int, long> Ready { get; } = new();
    }

    private readonly Random _encounterRandom = new();

    private ReconstructedDungeons? _reconstructed;

    /// <summary>From the constructor (optional: a fixture without the reconstruction leaves it null).</summary>
    private void UseReconstruction(ReconstructedDungeons? value)
    {
        _reconstructed = value;
        _combat.NonPlayerKillCredit.Add(KillCredit);
    }

    /// <summary>A companion's lethal hit on the boss is settled as the first living participant's kill.</summary>
    private GameSession? KillCredit(MonsterUnit target)
    {
        if (_instances.Find(target.InstanceId) is not { } room) return null;
        var members = room.ActiveMembers.Select(m => m.Session).ToArray();
        if (members.Select(s => s.DungeonAttempt).FirstOrDefault(a => a?.Encounter != null) is not { } a) return null;
        return Present(a).FirstOrDefault(s => !s.Dead && a.Loaded.Contains(s)) ?? Present(a).FirstOrDefault(a.Loaded.Contains);
    }

    private static int CurrentQuest(Attempt a) => a.Encounter is { } run ? run.Quest
        : a.Wave < a.Plan.Waves.Count ? a.Plan.Waves[a.Wave].Quest : 0;

    private async Task BeginEncounterAsync(Attempt a)
    {
        if (_reconstructed?.EncounterFor(a.Plan.SceneEntry) is not { } def || a.Settled || a.Room.Closed) return;
        if (a.Room.Monsters.FirstOrDefault(m => m.Template.NpcId == def.Boss && !m.Dead) is not { } boss) return;
        boss.ScriptedTargeting = true;
        var run = new EncounterRun { Def = def, Boss = boss, StageStartMs = MonsterService.NowMs };
        a.Encounter = run;
        foreach (var skill in def.BossSkills) run.BossReady[skill.Skill] = run.StageStartMs + 3000; // a short opening (inferred)
        _log.LogInformation("Story encounter {Name} starts in room {Room}", def.Name, a.Room.Id);
        await BroadcastAsync(a, BaseInfo, Snapshot(a)); // the tracker shows stage 1's quest
        await BeginStageAsync(a, run);
        ScheduleEncounterTick(a, run);
    }

    private void ScheduleEncounterTick(Attempt a, EncounterRun run) => _loop.ScheduleLegacy(TimeSpan.FromMilliseconds(TickMs), async () =>
    {
        if (run.Done || a.Settled || a.Room.Closed || !ReferenceEquals(a.Encounter, run)) return;
        try { await TickEncounterAsync(a, run); }
        catch (Exception e) { _log.LogError(e, "Story encounter {Name} tick failed", run.Def.Name); }
        ScheduleEncounterTick(a, run);
    });

    private async Task BeginStageAsync(Attempt a, EncounterRun run)
    {
        var stage = run.Def.Stages[run.Stage];
        run.StageStartMs = MonsterService.NowMs;
        run.Ending = false;
        // Eddga cannot fall before the companions arrive: a stage that ends at a boss HP holds the boss there
        // until the next stage starts (inferred; the original's script is not in the client).
        bool more = run.Stage + 1 < run.Def.Stages.Count;
        run.Boss.HpFloor = more && stage.EndBossHpPct > 0 ? Math.Max(1, run.Boss.Template.MaxHp * stage.EndBossHpPct / 100)
            : more ? 1 : 0;
        foreach (var spec in stage.Allies)
        {
            if (a.Settled || a.Room.Closed) return;
            if (await SpawnAllyAsync(a, run, spec) is { } ally && ally.Unit.Template.NpcId == stage.FirstTarget)
                _monsters.Engage(run.Boss, ally.Unit);
        }
        run.NextRetargetMs = MonsterService.NowMs + run.Def.RetargetMs;
        if (run.Stage > 0) await BroadcastAsync(a, BaseInfo, Snapshot(a)); // the tracker moves to the next quest
        SayLines(a, run, stage.Lines);
    }

    private async Task<AllyState?> SpawnAllyAsync(Attempt a, EncounterRun run, EncounterAlly spec)
    {
        if (_catalog.ScenePoint(a.Plan.SceneEntry, spec.Point) is not { } at || _monsters.Template(spec.NpcId) is not { } template
            || !run.Def.Kits.TryGetValue(spec.NpcId, out var kit)) return null;
        // The kit's first damaging row is the companion's swing (the story templates name none of their own).
        var normal = kit.Skills.Select(id => _data.Row("SkillConfig", id)).FirstOrDefault(r => r is { } row
            && GameData.Int(row, "_iDmgType") == 1);
        if (normal is { } swing)
        {
            int ms = (int)Math.Max(300, GameData.Int(swing, "_iActionTime"));
            float hitPoint = _data.SkillTemplate(GameData.Int(swing, "_iScriptShow")) is { } tpl && tpl.TryGetProperty("m_kAtkPoints", out var pts)
                && pts.ValueKind == JsonValueKind.Array && pts.GetArrayLength() > 0 && GameData.Number(pts[0]) is { } hp ? (float)hp : 0.2f;
            template = template with
            {
                NormalSkill = (int)GameData.Int(swing, "_iID"),
                SkillMs = ms,
                HitPointSeconds = hitPoint,
                SwingIntervalMs = (int)Math.Max(ms, Math.Max(1500, GameData.Int(swing, "_iCD"))),
                AttackRange = Math.Max(1f, GameData.Int(swing, "_iDistanceMax") / 100f),
                Aggressive = false,
                RoamDistance = 0,
            };
        }
        else template = template with { NormalSkill = 0, Aggressive = false, RoamDistance = 0, SpeedCm = 0 };
        if (await _instances.SpawnAsync(a.Room, spec.NpcId, at, customTemplate: template) is not { } unit) return null;
        unit.ScriptedTargeting = true;
        var ally = new AllyState { Unit = unit, Kit = kit };
        run.Allies.Add(ally);
        if (normal != null) _monsters.Engage(unit, run.Boss);
        return ally;
    }

    private async Task TickEncounterAsync(Attempt a, EncounterRun run)
    {
        long now = MonsterService.NowMs;
        var boss = run.Boss;
        if (boss.Dead || boss.Hp <= 0) return;
        var stage = run.Def.Stages[run.Stage];
        if (!run.Ending && run.Stage + 1 < run.Def.Stages.Count
            && (stage.EndBossHpPct > 0 && boss.Hp * 100 <= boss.Template.MaxHp * stage.EndBossHpPct
                || stage.EndMs > 0 && now - run.StageStartMs >= stage.EndMs))
        {
            run.Ending = true;
            SayLines(a, run, stage.EndLines);
            _loop.ScheduleLegacy(TimeSpan.FromMilliseconds(stage.EndDelayMs), async () =>
            {
                if (run.Done || a.Settled || a.Room.Closed || boss.Dead) return;
                run.Stage++;
                await BeginStageAsync(a, run);
            });
        }
        await TickBossAsync(a, run, now);
        foreach (var ally in run.Allies.ToArray()) await TickAllyAsync(a, run, ally, now);
    }

    /// <summary>TEST AID, not the original: start the next stage now (its end lines and delay are skipped).</summary>
    internal async Task<bool> SkipEncounterStageAsync(Attempt a)
    {
        if (a.Encounter is not { Done: false } run || run.Stage + 1 >= run.Def.Stages.Count || a.Settled || a.Room.Closed) return false;
        run.Ending = true;
        run.Stage++;
        await BeginStageAsync(a, run);
        return true;
    }

    private async Task EncounterGmAsync(GameSession s, string[] args)
    {
        if (Active(s) is not { Encounter: { } run } a) throw new ArgumentException("not in a story encounter");
        if (args.Length == 1 && args[0].Equals("next", StringComparison.OrdinalIgnoreCase))
        {
            if (!await SkipEncounterStageAsync(a)) throw new ArgumentException("the encounter is on its last stage");
            _log.LogWarning("GM test aid: {Role} skips story encounter {Name} to stage {Stage}", s.Role?.Id, run.Def.Name, run.Stage + 1);
        }
        var boss = run.Boss;
        await GmCommands.ReplyAsync(s, FormattableString.Invariant(
            $"{run.Def.Name}: stage {run.Stage + 1}/{run.Def.Stages.Count} (quest {run.Quest}), boss {boss.Hp}/{boss.Template.MaxHp} hp, ")
            + string.Join(", ", run.Allies.Select(x => FormattableString.Invariant($"{x.Unit.Template.NpcId} {x.Unit.Hp}/{x.Unit.Template.MaxHp}"))));
    }

    // ---- the boss ----

    private async Task TickBossAsync(Attempt a, EncounterRun run, long now)
    {
        var boss = run.Boss;
        if (now < boss.BusyUntilMs) return;
        var players = Present(a).Where(p => p.Role != null && !p.Dead && !p.SceneLoading && p.WorldScene == boss.WorldScene).ToArray();
        var allies = run.Allies.Select(s => s.Unit).Where(u => !u.Dead && MonsterService.Distance(boss.X, boss.Z, u.X, u.Z) <= AllyReach).ToArray();
        bool targetLost = boss.Target is { } t ? t.Dead || !players.Contains(t) : boss.MonsterTarget is not { Dead: false };
        if (now >= run.NextRetargetMs || targetLost)
        {
            run.NextRetargetMs = now + run.Def.RetargetMs;
            if (run.Provoker is { Dead: false } provoker && now < run.ProvokeUntilMs) _monsters.Engage(boss, provoker);
            else
            {
                // Weighted: a player counts twice a companion (inferred: the scene is the player's fight).
                var pool = players.Select(p => (Player: (GameSession?)p, Unit: (MonsterUnit?)null, Weight: 2))
                    .Concat(allies.Select(u => (Player: (GameSession?)null, Unit: (MonsterUnit?)u, Weight: 1))).ToArray();
                int total = pool.Sum(p => p.Weight), roll = total > 0 ? _encounterRandom.Next(total) : 0;
                foreach (var (player, unit, weight) in pool)
                {
                    if ((roll -= weight) >= 0) continue;
                    if (player != null) _monsters.Retarget(boss, player); else _monsters.Engage(boss, unit!);
                    break;
                }
            }
        }
        if (now < run.BossNextCastMs || run.Boss.Forbids(4)) return; // a stunned or silenced boss holds its skills
        (float X, float Z)? aim = boss.Target is { } p ? (p.Position.X, p.Position.Z)
            : boss.MonsterTarget is { } m ? (m.X, m.Z) : null;
        if (aim is not { } at) return;
        float distance = MonsterService.Distance(boss.X, boss.Z, at.X, at.Z);
        foreach (var skill in run.Def.BossSkills)
        {
            if (now < run.BossReady.GetValueOrDefault(skill.Skill) || _data.Row("SkillConfig", skill.Skill) is not { } row) continue;
            if (distance > Math.Max(4f, GameData.Int(row, "_iDistanceMax") / 100f)) continue;
            await CastBossSkillAsync(a, run, skill, row, at, now);
            return;
        }
    }

    private async Task CastBossSkillAsync(Attempt a, EncounterRun run, EncounterSkill skill, JsonElement row, (float X, float Z) at, long now)
    {
        var boss = run.Boss;
        int actionMs = (int)Math.Max(500, GameData.Int(row, "_iActionTime"));
        long castId = Interlocked.Increment(ref _encounterCasts);
        if (boss.Moving) { boss.Advance(now); boss.Moving = false; }
        boss.Face = MonsterService.Angle(boss.X, boss.Z, at.X, at.Z);
        boss.BusyUntilMs = now + actionMs;
        boss.NextSwingMs = Math.Max(boss.NextSwingMs, boss.BusyUntilMs + 300);
        run.BossReady[skill.Skill] = now + Math.Max(1000, GameData.Int(row, "_iCD"));
        run.BossNextCastMs = boss.BusyUntilMs + run.Def.BossSkillGapMs;
        await _world.MoveUnitAsync(boss, new MovementChangeNtf { Uid = boss.Uid, MovementInfo = boss.CurrentMovement(now) });
        await _combat.SendMonsterTimelineAsync(boss, new UseInSkillInfo
        {
            SrcID = boss.Uid, SkillID = skill.Skill, TargetId = boss.Target?.Role?.Id ?? boss.MonsterTarget?.Uid ?? 0,
            TargetPos = new Vec3 { X = at.X, Y = boss.Y, Z = at.Z }, Face = boss.Face, SessionId = castId,
            SkillMs = actionMs, StartTime = now,
        }, highLevel: GameData.Int(row, "_iSkillEffectLevel") > 0);
        if (skill.Line != 0 && run.SaidSkillLines.Add(skill.Line)) SayLines(a, run, [new EncounterLine(skill.Line, 0, 0)]);
        (float X, float Z, float Face) origin = (boss.X, boss.Z, boss.Face);
        foreach (var hit in skill.Hits)
            _loop.ScheduleLegacy(TimeSpan.FromMilliseconds(hit.Ms), async () =>
            {
                if (run.Done || a.Settled || a.Room.Closed || boss.Dead || boss.Hp <= 0) return;
                var players = Present(a).Where(p => p.Role != null && !p.Dead && !p.SceneLoading && p.WorldScene == boss.WorldScene
                    && InArea(hit, origin, p.Position.X, p.Position.Z)).ToArray();
                if (players.Length > 0) await _combat.MonsterSkillHitAsync(boss, row, players, boss.Uid, session: castId, scaleBp: hit.ShareBp);
                foreach (var ally in run.Allies.Select(s => s.Unit).Where(u => !u.Dead && InArea(hit, origin, u.X, u.Z)).ToArray())
                    await _combat.MonsterDamagesMonsterAsync(boss, ally, row, castId, hit.ShareBp);
            });
    }

    /// <summary>Whether (x, z) is inside a hit's area, laid out from the boss's position and facing at the cast start.</summary>
    internal static bool InArea(EncounterHit hit, (float X, float Z, float Face) origin, float x, float z)
    {
        const float body = 0.5f; // a target's own size (inferred)
        double rad = origin.Face * Math.PI / 180; // 0 = +z, clockwise toward +x (MonsterService.Angle)
        float fx = (float)Math.Sin(rad), fz = (float)Math.Cos(rad);
        float dx = x - origin.X, dz = z - origin.Z;
        switch (hit.Shape)
        {
            case HitShape.Circle:
                float cx = dx - fx * hit.Forward, cz = dz - fz * hit.Forward;
                return MathF.Sqrt(cx * cx + cz * cz) <= hit.Radius + body;
            case HitShape.Cone:
                float d = MathF.Sqrt(dx * dx + dz * dz);
                if (d > hit.Radius + body) return false;
                if (d < 0.01f) return true;
                double cos = (dx * fx + dz * fz) / d;
                return Math.Acos(Math.Clamp(cos, -1, 1)) * 180 / Math.PI <= hit.Angle / 2;
            case HitShape.Rect:
                float along = dx * fx + dz * fz, side = MathF.Abs(dx * fz - dz * fx);
                return along >= -body && along <= hit.Length + body && side <= hit.Width / 2 + body;
        }
        return false;
    }

    // ---- the companions ----

    private async Task TickAllyAsync(Attempt a, EncounterRun run, AllyState ally, long now)
    {
        var unit = ally.Unit;
        if (unit.Dead || now < unit.BusyUntilMs) return;
        if (ally.Kit.Support)
        {
            await TickSupportAsync(a, run, ally, now);
            return;
        }
        if (unit.MonsterTarget == null && !run.Boss.Dead) _monsters.Engage(unit, run.Boss);
        float distance = MonsterService.Distance(unit.X, unit.Z, run.Boss.X, run.Boss.Z);
        foreach (int id in ally.Kit.Skills)
        {
            if (id == unit.Template.NormalSkill || now < ally.Ready.GetValueOrDefault(id) || _data.Row("SkillConfig", id) is not { } row) continue;
            bool damaging = GameData.Int(row, "_iDmgType") != 0;
            float reach = GameData.Int(row, "_iDistanceMax") / 100f;
            if (damaging && distance > Math.Max(unit.Template.AttackRange, reach) + 0.5f) continue;
            if (!damaging && Tuples(row, "_kBuff1").Length == 0) continue;
            await CastAllySkillAsync(a, run, ally, row, now, damaging);
            return;
        }
    }

    /// <summary>MainStory_MuShi (tree 10803): Blessing and Increase AGI when ready; then, of the camp-1 group below 95% HP,
    /// Sanctuary when at least three are hurt, else Heal on the most hurt (sorted, at most the row's _iTargetMax).</summary>
    private async Task TickSupportAsync(Attempt a, EncounterRun run, AllyState ally, long now)
    {
        var unit = ally.Unit;
        var players = Present(a).Where(p => p.Role != null && !p.Dead && !p.SceneLoading && p.WorldScene == unit.WorldScene
            && MonsterService.Distance(unit, p) <= AllyReach).ToArray();
        var units = run.Allies.Select(s => s.Unit).Where(u => !u.Dead && MonsterService.Distance(unit.X, unit.Z, u.X, u.Z) <= AllyReach).ToArray();
        foreach (int id in ally.Kit.Skills)
        {
            if (now < ally.Ready.GetValueOrDefault(id) || _data.Row("SkillConfig", id) is not { } row) continue;
            int max = (int)Math.Max(1, GameData.Int(row, "_iTargetMax"));
            if (GameData.Int(row, "_iDmgType") == 0)
            {
                if (Tuples(row, "_kBuff1").Length == 0) continue;
                await CastSupportAsync(ally, row, now, players.Take(max).ToArray(), units.Take(Math.Max(0, max - players.Length)).ToArray(), heal: false);
                return;
            }
            var hurt = players.Select(p => (Player: (GameSession?)p, Unit: (MonsterUnit?)null, Pct: HpPct(p)))
                .Concat(units.Select(u => (Player: (GameSession?)null, Unit: (MonsterUnit?)u, Pct: u.Hp * 100.0 / Math.Max(1, u.Template.MaxHp))))
                .Where(h => h.Pct < HealBelowPct).OrderBy(h => h.Pct).ToArray();
            bool area = GameData.Int(row, "_iRangeType") == 2;
            if (hurt.Length == 0 || area && hurt.Length < SanctuaryCount) continue;
            var picked = hurt.Take(max).ToArray();
            await CastSupportAsync(ally, row, now, picked.Where(h => h.Player != null).Select(h => h.Player!).ToArray(),
                picked.Where(h => h.Unit != null).Select(h => h.Unit!).ToArray(), heal: true);
            return;
        }
    }

    private double HpPct(GameSession p)
    {
        var stats = _character.Stats(p);
        return stats[2000] * 100.0 / Math.Max(1, stats[101]);
    }

    private async Task CastSupportAsync(AllyState ally, JsonElement row, long now, GameSession[] players, MonsterUnit[] units, bool heal)
    {
        var unit = ally.Unit;
        int id = (int)GameData.Int(row, "_iID");
        int actionMs = (int)Math.Max(500, GameData.Int(row, "_iActionTime"));
        ally.Ready[id] = now + Math.Max(1000, GameData.Int(row, "_iCD"));
        unit.BusyUntilMs = now + actionMs;
        long castId = Interlocked.Increment(ref _encounterCasts);
        var first = players.Select(p => (Id: p.Role!.Id, Pos: p.Position.Clone())).Concat(units.Select(u => (Id: u.Uid, Pos: u.Pos))).FirstOrDefault();
        await _combat.SendMonsterTimelineAsync(unit, new UseInSkillInfo
        {
            SrcID = unit.Uid, SkillID = id, TargetId = first.Id, TargetPos = first.Pos ?? unit.Pos, Face = unit.Face,
            SessionId = castId, SkillMs = actionMs, StartTime = now,
        });
        int hitMs = HitMs(row, actionMs);
        _loop.ScheduleLegacy(TimeSpan.FromMilliseconds(hitMs), async () =>
        {
            if (unit.Dead) return;
            if (heal) await _combat.MonsterHealsAsync(unit, row, players, units, castId);
            else await _combat.MonsterBuffsAsync(unit, row, players, units);
        });
    }

    private async Task CastAllySkillAsync(Attempt a, EncounterRun run, AllyState ally, JsonElement row, long now, bool damaging)
    {
        var unit = ally.Unit;
        var boss = run.Boss;
        int id = (int)GameData.Int(row, "_iID");
        int actionMs = (int)Math.Max(500, GameData.Int(row, "_iActionTime"));
        ally.Ready[id] = now + Math.Max(1000, GameData.Int(row, "_iCD"));
        unit.BusyUntilMs = now + actionMs;
        unit.NextSwingMs = Math.Max(unit.NextSwingMs, unit.BusyUntilMs);
        if (damaging) unit.Face = MonsterService.Angle(unit.X, unit.Z, boss.X, boss.Z);
        long castId = Interlocked.Increment(ref _encounterCasts);
        await _combat.SendMonsterTimelineAsync(unit, new UseInSkillInfo
        {
            SrcID = unit.Uid, SkillID = id, TargetId = damaging ? boss.Uid : unit.Uid, TargetPos = damaging ? boss.Pos : unit.Pos,
            Face = unit.Face, SessionId = castId, SkillMs = actionMs, StartTime = now,
        });
        _loop.ScheduleLegacy(TimeSpan.FromMilliseconds(HitMs(row, actionMs)), async () =>
        {
            if (unit.Dead || run.Done || a.Settled || a.Room.Closed) return;
            if (!damaging)
            {
                // A self buff; a party buff (_iTargetMax above 1) also reaches the players and companions near it.
                float radius = (float)(Numbers(row, "_kRange").FirstOrDefault() / 100);
                bool party = GameData.Int(row, "_iTargetMax") > 1;
                var players = party ? Present(a).Where(p => p.Role != null && !p.Dead && MonsterService.Distance(unit, p) <= radius).ToArray() : [];
                var units = party ? run.Allies.Select(s => s.Unit).Where(u => !u.Dead && MonsterService.Distance(unit.X, unit.Z, u.X, u.Z) <= radius)
                    .Cast<WorldUnit>().ToArray() : [unit];
                await _combat.MonsterBuffsAsync(unit, row, players, units);
                return;
            }
            if (boss.Dead || boss.Hp <= 0) return;
            await _combat.MonsterDamagesMonsterAsync(unit, boss, row, castId);
            // Provoke (its _kBuff1 1109401 is the taunt): the boss turns on the swordman for the buff's time (inferred).
            if (Script(row) == "Provoke" && Tuples(row, "_kBuff1").FirstOrDefault() is { ValueKind: JsonValueKind.Array } taunt)
            {
                run.Provoker = unit;
                run.ProvokeUntilMs = MonsterService.NowMs + (long)(GameData.Number(taunt[2]) ?? 10000);
                run.NextRetargetMs = 0;
                await _combat.MonsterBuffsAsync(unit, row, [], [boss]);
            }
        });
    }

    /// <summary>The first hit mark of the row's timeline (m_kAtkPoints), else 40% of its action time (as tower pets).</summary>
    private int HitMs(JsonElement row, int actionMs) =>
        _data.SkillTemplate(GameData.Int(row, "_iScriptShow")) is { } tpl && tpl.TryGetProperty("m_kAtkPoints", out var pts)
        && pts.ValueKind == JsonValueKind.Array && pts.GetArrayLength() > 0 && GameData.Number(pts[0]) is { } s && s > 0
            ? (int)(s * 1000) : (int)(actionMs * 0.4);

    private static string Script(JsonElement row) => row.TryGetProperty("_kScriptLogic", out var s) && s.ValueKind == JsonValueKind.String
        ? s.GetString() ?? "" : "";

    private static JsonElement[] Tuples(JsonElement row, string key) => row.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array
        ? list.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.Array && t.GetArrayLength() >= 3).ToArray() : [];

    private static double[] Numbers(JsonElement row, string key) => row.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array
        ? list.EnumerateArray().Select(v => GameData.Number(v) ?? 0).ToArray() : [];

    // ---- lines ----

    /// <summary>
    /// ChatConfig lines through 30404 DungeonEventInvokeClientFunc. Type 2 (enum_SystemMessageType Bubbling) needs a
    /// speaker: LC_DunGeon_Actions.ShowShoutingChatBubble(chatId, unitId) -> LC_SystemMessageHelper.ShowChatBubble; the
    /// others (7 Secretary, the named portrait panel; 16; 47) go through Shouting(chatId) -> MessagePush. Reading the
    /// bubble's second argument as the speaker's entity id is (inferred).
    /// </summary>
    private void SayLines(Attempt a, EncounterRun run, IReadOnlyList<EncounterLine> lines)
    {
        foreach (var line in lines)
            _loop.ScheduleLegacy(TimeSpan.FromMilliseconds(line.Ms), async () =>
            {
                if (run.Done && line.Ms > 0 || a.Room.Closed) return;
                await SayAsync(a, run, line);
            });
    }

    private async Task SayAsync(Attempt a, EncounterRun run, EncounterLine line)
    {
        if (_data.Row("ChatConfig", line.Chat) is not { } chat) return;
        var speaker = line.By == 0 ? null
            : line.By == run.Boss.Template.NpcId ? run.Boss
            : run.Allies.Select(s => s.Unit).FirstOrDefault(u => u.Template.NpcId == line.By && !u.Dead);
        bool bubble = GameData.Int(chat, "_iType") == 2;
        if (bubble && speaker == null) return; // its speaker is not in the fight
        var call = new DungeonEventNotify
        {
            DungeonEntry = a.Plan.SceneEntry, EventType = (DungeonEventType)6, StageNum = a.Wave + 1, LevelNum = 1,
            CliFuncname = bubble ? "ShowShoutingChatBubble" : "Shouting",
            Params = { new EventParams { IntParams = { line.Chat } } },
        };
        if (bubble) call.Params.Add(new EventParams { IntParams = { speaker!.Uid } });
        await BroadcastAsync(a, Event, call);
    }

    /// <summary>The run ends with its attempt; a won fight says its closing lines first.</summary>
    private async Task EndEncounterAsync(Attempt a, bool won)
    {
        if (a.Encounter is not { Done: false } run) return;
        run.Done = true;
        run.Boss.HpFloor = 0;
        if (won) foreach (var line in run.Def.WinLines) await SayAsync(a, run, line);
    }
}
