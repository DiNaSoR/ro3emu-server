using System.Runtime.CompilerServices;
using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class TaskService
{
    // the client, ordered by usage where an authoritative owner exists in this server.
    private const int TMapKill = 103043, TUse = 103042, TEquipment = 302002, TGuide = 106003,
        TCardStars = 103044, TCardQuality = 103007, TCraft = 103024, TMvpOnce = 103022,
        TResonance = 103023, TTotalRefine = 103033, TSubmit = 103051, TRefineSlots = 302001,
        TTreasure = 314001, TSlotRefine = 103021, TCraftQuality = 103006, TCollect = 103050,
        TEquipmentQuality = 106002, TStrength = 103004, TRefineCount = 103005, TMvp = 103011,
        TSkillRank = 311001, TCargo = 103053, TPydDownload = 106701, TAfkSeconds = 106901, TRestrictUse = 106906,
        TOnePieceCount = 103049, TTimedBattle = 106509, TCaravan = 104011, TDungeonPass = 101018, TAreaAction = 106903;

    // Growth-manual child targets (TaskService.GrowthManual.cs). Displays from ProgressConfig/Localization: 326002
    // "Activate ${1} talents" (zh_CN 激活的天赋达到${1}, its unmet tip 天赋等级不足 "talent level too low": counted as
    // talent levels, inferred), 103034 CheckSetUpPetCount "Deploy ${1} pets" and 101014 CheckClimbTowerLayer "Clear Floor
    // ${1} of the Pet Tower" (Logic/ConditionSystem/LC_ConditionDefine.lua:125,215).
    private const int TTalentLevels = 326002, TPetsDeployed = 103034, TTowerFloor = 101014;

    // 309003 RentCareerSpecialSubType [309003, CartRemouldTable._iSubType] ("Rent a Cart Merchant / Falcon / Peco Peco",
    // TaskConfig 10200150-10200200): R is 1 (LC_InteractCondition.GetRentCareerSpecialSubTypeDes). Read as the lease held
    // now, like the other Check* state targets (inferred).
    private const int TRentCareerSpecial = 309003;

    // 334001 CheckHeroicSpiritChallengedId [334001, HeroicSpiritLayerConfig id] ("Clear Valhalla ${1}", the Tower Trial
    // side quests 302040/304050/305050/384050/385050): R is 1 (LC_InteractCondition.lua:4851-4875); the client condition
    // reads the layer as completed (:4830-4848), so it is the cleared state, like the other Check* targets.
    private const int TValhallaLayer = 334001;

    // 318004 [318004, AreaConcurConfig id, n] "Reach @{1} progress toward the <Area> Monster Hunt full collection reward"
    // (growth manual 703040/704050/705030/706030): R is p2 (LC_InteractCondition.lua:5246-5266). The area's collection
    // progress, its claimed monster stages (MonsterHuntService; inferred: the original counter is not recovered).
    private const int TMonsterHunt = 318004;

    // 210011 [210011, n]: TaskConfig 1602120 "Guild Activities - Hunting Challenge" (type 16, _kEXECAction 115 3015) is
    // its only user. The client has no formatter for it; R is the trailing count, as the sibling 104011 caravan departure
    // (inferred). Credited once per event when a member takes part (GuildHuntingService).
    private const int TGuildHunt = 210011;

    // 210010 [210010, n] "Guild Activities - Wedding Crashers" (guild quest 1602110, accepted by the hidden DailyActivity
    // 3014's _iTaskReceiveAction ["31","1","1602110"]): one per Wedding Crashers clear (DraculaWeddingService). Its
    // TaskTargetConfig row is not exported; counting a successful rescue, not an entry, is (inferred).
    private const int TDraculaWedding = 210010;

    private readonly ConditionalWeakTable<MonsterUnit, HashSet<long>> _participants = new();
    private readonly HashSet<GameSession> _refreshQueued = new();

    private void QueueRefresh(GameSession session)
    {
        if (session.Tasks == null || !_refreshQueued.Add(session)) return;
        loop.PostOwned(session, async () => { _refreshQueued.Remove(session); await RefreshAsync(session); },
            discarded: () => _refreshQueued.Remove(session));
    }

    /// <summary>
    /// Exact formatter R, independent of the domain predicate.
    /// Unsupported/malformed targets return -1. In particular, a trailing filter 0 is never a completion threshold.
    /// </summary>
    private long Required(GameSession? session, long[] t) => t switch
    {
        [TDialog, _, _, _] or [TArea, _, _] or [TJobChange, _] or [TTaskDone, _] or [TJobRank, _] or [TAreaAction, _, _, _]
            or [TGuide, _] or [TPydDownload, 1] or [TRentCareerSpecial, _] or [TValhallaLayer, _] => 1,
        [TPlot or TBaseLevel or TSkillPoints or TStatPoints or TResonance or TTotalRefine or TRefineSlots
            or TRefineCount or TMvp or TMvpOnce or TCargo or TAfkSeconds or TTimedBattle or TCaravan
            or TTalentLevels or TPetsDeployed or TTowerFloor or TGuildHunt or TDraculaWedding, var n] => n,
        [TOnePieceCount, _, _, var limit] => data.Row("RestrictConfig", limit) is { } restrict
            ? GameData.Int(restrict, "_iMaxRestrictNum") : -1,
        [TKillOnMap, var n, _] => n,
        [TMonsterHunt, _, var n] => n,
        [TDungeonPass, _, var n] => n,
        [TSaplingChosen or TDeclaration, var n] => n, // Adventure Group (TaskService.AdventureGroup.cs)
        [TWaterSapling, var n, _] => n,
        [TKill or TKillInvolved, _, _, ..] => t[^1],
        [TClientInteract or TMapKill or TUse or TCardStars or TCardQuality or TTreasure or TSlotRefine
            or TCraftQuality or TCollect or TEquipmentQuality or TStrength or TSkillRank or TRestrictUse, _, var n] => n,
        [TEquipment, _, _, var n] => n,
        [TCraft, _, _, var n, _] => n,
        [TSubmit, _, var n, _] => n,
        _ => -1,
    };

    private bool Supported(long[] t)
    {
        if (Required(null, t) <= 0) return false;
        return t[0] switch
        {
            // Mode 1 is the connected chapter's inventory turn-in (inferred). Mode 0's resource rules are
            // unresolved; keep it pending, including the formerly false-completing two-slot task 201120.
            TSubmit => t[3] == 1 && ItemExists(t[1]),
            TOnePieceCount => data.Row("DailyActivityConfig", t[1]) != null && ItemExists(t[2])
                && data.Row("RestrictConfig", t[3]) != null,
            TUse or TCollect => ItemExists(t[1]),
            TRestrictUse => t[1] == LifeJobService.VigorId,
            TDialog => t[1] > 0 && data.Row("MapConfig", t[2]) != null && data.Row("DialogueConfig", t[3]) != null,
            TArea => data.Row("MapConfig", t[1]) != null && t[2] > 0,
            TMapKill => data.Row("MapConfig", t[1]) != null,
            TKillOnMap => data.Row("MapConfig", t[2]) != null,
            TDungeonPass => data.Row("DungeonConfig", t[1]) != null,
            TAreaAction => data.Row("MapConfig", t[1]) != null && t[2] > 0 && data.Row("PerformanceConfig", t[3]) != null,
            TKill or TKillInvolved => t[1..^1].All(npc => data.Row("NPCConfig", npc) != null),
            TJobRank => t[1] > 0,
            TJobChange => data.Row("JobConfig", t[1]) != null,
            TTaskDone => Defs().ContainsKey(t[1]),
            TGuide => GuideExists(t[1]),
            TSlotRefine or TStrength => t[1] is >= 1 and <= 7,
            TEquipment => t[1] >= 0 && t[2] >= 0,
            TCraft => t[1] >= 0 && t[2] > 0 && t[4] is >= 0 and <= 10,
            TValhallaLayer => data.Row("HeroicSpiritLayerConfig", t[1]) != null,
            TMonsterHunt => data.Row("AreaConcurConfig", t[1]) != null,
            _ => true, // Only the explicit formatter cases above can reach here.
        };
    }

    private bool ItemExists(long id) => id > 0 && id <= int.MaxValue && data.Row("ItemConfig", id) != null;

    private bool GuideExists(long group) => group > 0 && data.Row("GuideConfig", group) != null;

    /// <summary>
    /// Snapshot objectives read committed owner state, at acceptance/login/change and again before delivery.
    /// Minimum quality/star comparisons, currently-owned collection, positive refinement slots, and totals across
    /// grids 1..7 are inferred server predicates; the client proves their R and parameter positions, not these rules.
    /// </summary>
    private long PassiveValue(GameSession s, long[] t)
    {
        var p = s.Progress!;
        return t[0] switch
        {
            TBaseLevel => p.Level,
            // 500130 [106901,3600] displays whole consumed minutes. Reading the committed
            // daily counter also repairs a disconnect between 5035 persistence and the quest sync (inferred).
            TAfkSeconds => s.WildAfk is { } afk && s.Daily?.DailyEpoch == afk.DailyEpoch ? afk.ConsumedTodaySeconds : 0,
            TJobRank => data.Row("JobConfig", p.Career) is { } job && GameData.Int(job, "_iJobRank") >= t[1] ? 1 : 0,
            TSkillPoints => skills.SpentPoints(p),
            TSkillRank => skills.SpentPoints(p, (int)t[1]),
            TStatPoints => character.SpentStatPoints(p),
            TTaskDone => IsDelivered(s, t[1]) ? 1 : 0,
            TJobChange => IsDescendant(p.Career, t[1]) ? 1 : 0,
            TGuide => s.TaskRuntime.CompletedGuides.Contains(t[1]) ? 1 : 0,
            TCollect or TSubmit => character.CountOf(s, (int)t[1]),
            // TaskConfig[500070] [106906,1004,100]: committed consumption, not current balance or item 5.
            TRestrictUse => LifeJobService.DailyEnergyUsed(s, t[1]),
            TEquipment => s.Items.LongCount(i => i.Grid > 0 && ItemQuality(i.Entry) >= t[1]
                && data.Row("ItemConfig", i.Entry) is { } row && GameData.Int(row, "_iType") == 5 && GameData.Int(row, "_iLevelNeed") >= t[2]),
            TEquipmentQuality => s.Items.LongCount(i => i.Grid > 0 && ItemQuality(i.Entry) >= t[1]
                && data.Row("ItemConfig", i.Entry) is { } row && GameData.Int(row, "_iType") == 5),
            TCardQuality => s.Items.LongCount(i => i.Grid < 0 && character.IsCard(i.Entry) && ItemQuality(i.Entry) >= t[1]),
            TCardStars => s.Items.LongCount(i => i.Grid < 0 && character.IsCard(i.Entry) && (i.Attrs?.GetValueOrDefault(502) ?? 0) >= t[1]),
            TTreasure => ReferenceEquals(s.CardLibraryReadyAttachment, s.Attachment)
                ? s.Library?.Cards.Values.LongCount(c => c.TreasureUnlock && ItemQuality((int)c.CardId) >= t[1]) ?? 0 : 0,
            TResonance => character.Resonance(s, "RefiningEffectTable"),
            TTotalRefine => Enumerable.Range(1, 7).Sum(g => (long)s.EquipLevels.Refine.GetValueOrDefault(g)),
            TRefineSlots => Enumerable.Range(1, 7).LongCount(g => s.EquipLevels.Refine.GetValueOrDefault(g) > 0),
            TSlotRefine => s.EquipLevels.Refine.GetValueOrDefault((int)t[1]),
            // The active plan's purchased talent levels (the Battle Oath is its own track).
            TTalentLevels => s.SeasonTalent is { } talent && talent.Plan.TryGetValue(talent.CurPlanId, out var plan)
                ? plan.TalentNodes.Values.Sum(n => n.CurrentLevel) : 0,
            TPetsDeployed => s.Pets?.SetupPlans.GetValueOrDefault(s.Pets.CurrentPlan)?.SetUpPets.Values.LongCount(id => id != 0) ?? 0,
            TTowerFloor => s.Tower is { LayerConfID: > 0 } tower && data.Table("PetFloorConfig").Values
                .FirstOrDefault(r => GameData.Int(r, "_iID") == tower.LayerConfID) is { ValueKind: JsonValueKind.Object } floor
                ? GameData.Int(floor, "_iFloor") : 0,
            TStrength => s.EquipLevels.Strength.GetValueOrDefault((int)t[1]),
            TRentCareerSpecial => CareerSpecialService.HoldsSubType(s, data, t[1]) ? 1 : 0,
            TValhallaLayer => DungeonService.ValhallaCleared(s.Dungeons).Contains(t[1]) ? 1 : 0,
            TMonsterHunt => MonsterHuntService.AreaProgress(s, t[1]),
            _ => -1,
        };
    }

    private long ItemQuality(int entry) => data.Row("ItemConfig", entry) is { } row ? GameData.Int(row, "_iQuality") : -1;

    /// <summary>
    /// 103024's last parameter is the crafted item's ItemConfig _iEquipPart (TaskConfig: 2 weapons, 4 tops, 7 accessories;
    /// EquipCraftConfig 2070 makes only part 7), not the recipe's _iEquipType (70) the craft request carries. Part 2 is
    /// read as any weapon, two-handed part 1 included (inferred: the weapon recipes make both).
    /// </summary>
    private bool CraftPartMatches(int entry, long part)
    {
        long made = data.Row("ItemConfig", entry) is { } row ? GameData.Int(row, "_iEquipPart") : -1;
        return part == 0 || made == part || (part == 2 && made == 1);
    }

    private bool IsDescendant(int career, long ancestor)
    {
        var seen = new HashSet<long>();
        long current = career;
        while (seen.Add(current) && data.Row("JobConfig", current) is { } job)
        {
            current = GameData.Int(job, "_iParentId");
            if (current == ancestor) return true;
            if (current <= 0) break;
        }
        return false;
    }

    /// <summary>
    /// Since-accept counters (inferred). Killer credit is separate from participation: each positive-damage
    /// contributor still on the death's map receives one 104014/103043 event, irrespective of the last hit.
    /// Weak monster keys disappear on despawn; no claim can be made by sending 31214.
    /// </summary>
    private ReservationKey[] GetKillTaskParticipantKeys(GameSession killer, MonsterUnit monster)
    {
        var settlement = monster.RewardSettlement ?? throw new InvalidOperationException("Kill source was not staged.");
        if (settlement.TaskObligationsStaged || settlement.AfkRecordStaged || settlement.HuntStaged)
        {
            // A failed lethal admission restores HP. Rebuild the callbacks from the next admitted state rather
            // than replaying stale task/AFK/Monster Hunt snapshots over intervening owner work (latest stage first).
            settlement.ResetHuntStage?.Invoke();
            settlement.ResetAfkStage?.Invoke();
            settlement.ResetTaskStage?.Invoke();
            settlement.TaskParticipantIds = null;
        }
        if (settlement.TaskParticipantIds == null)
        {
            var credited = _participants.TryGetValue(monster, out var ids) ? ids : [];
            settlement.TaskParticipantIds = sessions.All()
                .Where(s => s.Role != null && s.Tasks != null && s.WorldScene == monster.WorldScene &&
                    (ReferenceEquals(s, killer) || credited.Contains(s.Role.Id)))
                .Select(s => s.Role!.Id).Distinct().Order().ToArray();
        }
        return settlement.TaskParticipantIds.Select(id => ReservationKey.Role(id)).ToArray();
    }

    /// <summary>Freeze the exact running targets under all participant role reservations before the kill writer
    /// starts. A later task acceptance cannot consume this already committed occurrence.</summary>
    private void StageKillTaskObligations(GameSession killer, MonsterUnit monster, RewardSettlement settlement)
    {
        settlement.ResetTaskStage?.Invoke();
        if (killer.Role == null) throw new InvalidOperationException("Kill task source has no killer role.");
        var context = killer.Owners?.Current ?? throw new InvalidOperationException("Kill task progress requires its owner.");
        var candidates = settlement.TaskParticipantIds ?? throw new InvalidOperationException("Kill task roles were not reserved.");
        var frozen = new List<(long Role, byte[]? BeforeTasks, byte[] AfterTasks)>();
        var applied = new List<(GameSession Session, object? Attachment, DBTaskInfo Tasks)>();
        foreach (long role in candidates)
        {
            context.AssertRole(role);
            var session = sessions.All().SingleOrDefault(s => s.Role?.Id == role && s.Tasks != null &&
                s.WorldScene == monster.WorldScene && s.Attachment?.Cancelled != true);
            if (session?.Owner is not { } owner || !context.CanPin(owner)) continue;
            context.Pin(owner);
            var next = session.Tasks!.Clone();
            bool changed = false;
            foreach (var info in next.Infos.Values.Where(x => x.Status == Running))
            {
                if (!Defs().TryGetValue(info.TaskId, out var def)) continue;
                for (int index = 0; index < def.Targets.Length && index < info.CompleteNum.Count; index++)
                {
                    var target = def.Targets[index];
                    bool matches = target[0] switch
                    {
                        TKill => ReferenceEquals(session, killer) && target.Length >= 3 && target[1..^1].Contains(monster.Template.NpcId),
                        TKillInvolved => target.Length >= 3 && target[1..^1].Contains(monster.Template.NpcId),
                        TMapKill => target.Length == 3 && target[1] == monster.SceneEntry,
                        TKillOnMap => ReferenceEquals(session, killer) && target.Length == 3 && target[2] == monster.SceneEntry,
                        _ => false
                    };
                    if (!matches) continue;
                    long required = Required(session, target);
                    if (required <= 0 || info.CompleteNum[index] >= required) continue;
                    info.CompleteNum[index] = Math.Min(required, checked(info.CompleteNum[index] + 1));
                    changed = true;
                }
            }
            if (!changed) continue;
            byte[]? beforeTasks = roles.LoadTasks(role);
            frozen.Add((role, beforeTasks, next.ToByteArray()));
            applied.Add((session, session.Attachment, next));
        }
        var priorAfterCards = settlement.EnlistAfterCards;
        var priorApplied = settlement.Applied;
        settlement.ResetTaskStage = () =>
        {
            settlement.EnlistAfterCards = priorAfterCards;
            settlement.Applied = priorApplied;
            settlement.TaskObligationsStaged = false;
            settlement.ResetTaskStage = null;
        };
        if (frozen.Count > 0)
        {
            settlement.EnlistAfterCards = builder =>
            {
                priorAfterCards?.Invoke(builder);
                var values = frozen.Select(x =>
                {
                    byte[]? beforeFollowup = roles.ReadTaskFollowup(builder, x.Role);
                    var pending = beforeFollowup is { Length: > 0 }
                        ? JsonSerializer.Deserialize<HashSet<string>>(beforeFollowup) ?? [] : new HashSet<string>();
                    pending.Add("kill/" + settlement.SourceKey);
                    return new KillTaskProgress(x.Role, x.BeforeTasks, x.AfterTasks, beforeFollowup,
                        JsonSerializer.SerializeToUtf8Bytes(pending));
                }).ToArray();
                builder.Add(new KillTaskProgressPlan(settlement.SourceKey, values));
            };
            settlement.Applied = () =>
            {
                priorApplied?.Invoke();
                foreach (var (session, attachment, tasks) in applied)
                    if (ReferenceEquals(session.Attachment, attachment) && session.Role != null)
                        session.Tasks = tasks;
            };
        }
        settlement.TaskObligationsStaged = true;
    }

    private async Task OnKillAsync(GameSession killer, MonsterUnit monster)
    {
        var participants = _participants.TryGetValue(monster, out var credited) ? credited.ToHashSet() : new HashSet<long>();
        if (killer.Role != null) participants.Add(killer.Role.Id);
        _participants.Remove(monster);
        if (monster.RewardSettlement?.TaskObligationsStaged == true)
        {
            await RoleFanout.EachAsync(sessions.All().Where(s => s.Role != null &&
                monster.RewardSettlement.TaskParticipantIds?.Contains(s.Role.Id) == true),
                async s => { using var gate = await LockTasksAsync(s); await SettleAsync(s); });
            return;
        }
        await RoleFanout.EachAsync(sessions.All().Where(s => s.Role != null && s.WorldScene == monster.WorldScene && participants.Contains(s.Role.Id)),
            s => ProgressAsync(s, null, t => t[0] switch
            {
                TKill when s == killer && t.Length >= 3 && t[1..^1].Contains(monster.Template.NpcId) => 1,
                TKillInvolved when t.Length >= 3 && t[1..^1].Contains(monster.Template.NpcId) => 1,
                TMapKill when t.Length == 3 && t[1] == monster.SceneEntry => 1,
                TKillOnMap when s == killer && t.Length == 3 && t[2] == monster.SceneEntry => 1,
                _ => 0,
            }));
    }

    private Task OnItemUsedAsync(GameSession session, int entry, int count) =>
        ProgressAsync(session, TUse, t => t.Length == 3 && t[1] == entry ? count : 0);

    /// <summary>Only EquipmentService's successful paid craft is a source, never possession or a GM item grant.</summary>
    private Task OnCraftedAsync(GameSession s, int entry, int rank, int subtype) => ProgressAsync(s, null, t => t[0] switch
    {
        TCraft when t.Length == 5 && ItemQuality(entry) >= t[1] && rank == t[2] && CraftPartMatches(entry, t[4]) => 1,
        TCraftQuality when t.Length == 3 && ItemQuality(entry) >= t[1] => 1,
        _ => 0,
    });

    /// <summary>31210 is completion. 84000 arbitrary guide records do not satisfy this objective (§4 C5).</summary>
    public async Task OnGuideCompleteAsync(GameSession session, long guide)
    {
        using var gate = await LockTasksAsync(session);
        if (BeforeProgress is { } before) await before(session);
        if (session.Tasks == null || !GuideExists(guide)) return;
        session.TaskRuntime.CompletedGuides.Add(guide);
        await SettleAsync(session);
    }

    /// <summary>
    /// 83651 carries no task, attempt id, or proof. Accept a local clear only for running 10103150 on its tracked
    /// map, one of three persisted slots (inferred). Network replay within those three cannot be distinguished
    /// from another local clear; completion/reward beyond three is rejected, including after reconnect.
    /// </summary>
    public async Task GoodsStackAsync(GameSession session)
    {
        using var gate = await LockTasksAsync(session);
        if (BeforeProgress is { } before) await before(session);
        bool ok = session.Tasks?.Infos.TryGetValue(10103150, out var info) == true && info.Status == Running
            && ContextMapMatches(session, Defs()[10103150]) && Defs()[10103150].Targets.Any(t => t is [TCargo, > 0]);
        if (ok) await ProgressCoreAsync(session, TCargo, _ => 1, d => d.Id == 10103150);
        await session.SendGameAsync(GoodsStack, new TaskGoodsStackResp { Result = ok ? 0 : ErrorCodes.TaskRequirements });
    }

    private static bool ContextMapMatches(GameSession s, TaskDef def)
    {
        var maps = def.Track.Where(a => a.Length >= 3 && a[0] is "142" or "143")
            .Select(a => long.TryParse(a[1], out long map) ? map : 0).Where(m => m > 0).ToList();
        return maps.Count == 0 || maps.Contains(s.SceneEntry);
    }

    internal static async Task<IDisposable> LockTasksAsync(GameSession s)
    {
        var role = s.Role;
        var attachment = s.Attachment;
        var owner = s.Owner;
        long? epoch = owner?.LifecycleEpoch;
        Ownership.LegacyOwnerScope? scope = null;
        if (role != null && s.Owners is { } owners)
            scope = await owners.EnterAsync("Task.TaskLock", Ownership.ReservationKey.Role(role.Id));
        bool acquired = false;
        try
        {
            await s.TaskLock.WaitAsync();
            acquired = true;
            if (!ReferenceEquals(s.Role, role) || !ReferenceEquals(s.Attachment, attachment)
                || !ReferenceEquals(s.Owner, owner) || owner?.LifecycleEpoch != epoch)
                throw new OperationCanceledException("Role changed before the task gate was acquired.");
            if (owner != null) scope?.Context.Pin(owner);
            return new TaskLockReleaser(s.TaskLock, scope);
        }
        catch
        {
            if (acquired) s.TaskLock.Release();
            scope?.Dispose();
            throw;
        }
    }

    private sealed class TaskLockReleaser(SemaphoreSlim gate, IDisposable? scope) : IDisposable
    {
        public void Dispose() { gate.Release(); scope?.Dispose(); }
    }
}

/// <summary>Extra authoritative quest source state; stored alongside DBTaskInfo. No new client dataset.</summary>
public sealed class TaskRuntimeState
{
    public HashSet<long> CompletedGuides { get; set; } = new();
    public bool InCutScene { get; set; }
}
