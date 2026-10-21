using System.Text.Json;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Kill rewards: the monster's drop chains are rolled for the killer
/// (NPCConfig._iDrop / _iNormalDrop → MultiverseGroupConfig → NPCDropConfig[env 0 reduced / env 1 Bonus Time, inferred]
/// / additive NpcNormalDropConfig → DropPoolConfig slots → weighted DropGroupConfig rows). Item 100 rows are Base EXP,
/// scaled by LvExpCorrectionConfig; Job EXP is BaseToJobConversionPercent (20%) of Base EXP. ServerOptions.Rates then scales EXP, roll
/// chances and currency amounts. Currencies go to the currency map, everything
/// else to the bag; then one 31322 drop notify makes
/// the client animate the loot from the corpse.
/// </summary>
public sealed class RewardService
{
    private const int BaseExpItem = 100, JobExpItem = 101;
    private const int RewardDrop = 8; // ManifestType Drop

    private readonly CharacterService _character;
    private readonly TeamService _teams;
    private readonly GameData _data;
    private readonly ILogger<RewardService> _log;
    private readonly IOptions<ServerOptions> _options;
    private readonly Random _random = new();
    private Dictionary<int, long>? _expCorrection;
    private readonly DropAttenuation _attenuation;

    /// <summary>Field AFK selects the enhanced _iDrop table only for qualified ordinary field kills.
    /// Other kills use environment 0, preserving ordinary rewards (localization 10960000077; mapping inferred).</summary>
    public Func<GameSession, MonsterUnit, Task<bool>>? QualifiesForBonus { get; set; }
    /// <summary>Actual committed kill drops, after EXP correction; excludes GM, quest and other item grants.</summary>
    public List<Func<GameSession, MonsterUnit, IReadOnlyList<(int Entry, long Count)>, Task>> KillDropsGranted { get; } = new();
    internal Action<GameSession, MonsterUnit, RewardSettlement, IReadOnlyList<(int Entry, int Count)>, long, long>? StageAfkKill { get; set; }
    /// <summary>Monster Hunt: the area drop rolled with an ordinary field kill (MonsterHuntService).</summary>
    internal Func<GameSession, MonsterUnit, IEnumerable<(int Entry, int Count)>>? ExtraKillDrops { get; set; }
    /// <summary>Monster Hunt: its kill counters join the kill's own transaction, after the AFK child (MonsterHuntService).</summary>
    internal Action<GameSession, MonsterUnit, RewardSettlement>? StageHuntKill { get; set; }

    public RewardService(CombatService combat, CharacterService character, TeamService teams, GameData data, ILogger<RewardService> log,
        IOptions<ServerOptions> options)
    {
        this._options = options;
        this._character = character;
        this._teams = teams;
        this._data = data;
        this._log = log;
        _attenuation = new DropAttenuation(data);
        combat.BeforeKilled.Add(PrepareKilledAsync);
        combat.Killed.Add(KilledAsync);
    }

    private sealed class KillOutcome
    {
        internal required Role Role;
        internal required List<(int Entry, int Count)> Items;
        internal long BaseExp;
        internal long JobExp;
        internal bool Published;
        /// <summary>The committed bag instances of this kill's goods (per-instance equipment rows in the drop notice).</summary>
        internal List<InvItem>? Granted;
    }
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<MonsterUnit, KillOutcome> _outcomes = new();
    internal Func<GameSession, MonsterUnit, (List<(int Entry, int Count)> Items, long BaseExp, long JobExp)>? ResolveKillForTest { get; set; }

    /// <summary>Resolve and admit once before any lethal hit/death packet. A rejected pre-admission hit is
    /// not a successful kill. An accepted write retains its role owner through detach and durable completion.</summary>
    internal Task PrepareKilledAsync(GameSession player, MonsterUnit monster)
    {
        if (monster.RewardSettlement?.Committed == true) return Task.CompletedTask;
        if (monster.RewardCommit != null) return monster.RewardCommit;
        if (player.Role == null) throw new OperationCanceledException("Kill has no recipient.");
        if (monster.RewardRecipient != 0 && monster.RewardRecipient != player.Role.Id)
            throw new InvalidOperationException("Staged kill belongs to another recipient.");
        monster.RewardRecipient = player.Role.Id;
        monster.RewardSource ??= new RewardSourceId(_character.RewardRealmId, RewardSourceId.ProcessRun, monster.SpawnGeneration);
        monster.RewardSettlement ??= new RewardSettlement(monster.RewardSource.Key, "monster-kill", autoDecompose: true)
        { DeferPresentation = true };
        return monster.RewardCommit = CommitKillAsync(player, monster);
    }

    private async Task CommitKillAsync(GameSession player, MonsterUnit monster)
    {
        var role = player.Role!;
        var afk = player.WildAfk;
        bool SameRole() => ReferenceEquals(player.Role, role) && ReferenceEquals(player.WildAfk, afk)
            && player.Progress != null && player.Attachment?.Cancelled != true;
        var settlement = monster.RewardSettlement!;
        try
        {
            if (await _character.RewardSourceCommittedAsync(settlement.SourceKey))
            { settlement.Committed = true; return; }
            // Bonus eligibility may wait on the AFK owner; no successful lethal output precedes admission.
            bool bonus = QualifiesForBonus != null && await QualifiesForBonus(player, monster);
            var taskParticipants = _character.GetKillTaskParticipantKeys?.Invoke(player, monster) ?? [];
            using var held = await CharacterService.Lock(player, taskParticipants);
            if (!SameRole()) throw new OperationCanceledException("Kill recipient detached before reward admission.");
            if (_outcomes.TryGetValue(monster, out var stale) && stale.Role.Id != role.Id) _outcomes.Remove(monster);
            if (!_outcomes.TryGetValue(monster, out var outcome))
            {
                var (items, baseExp, jobExp) = ResolveKill(player, monster, bonus);
                outcome = new KillOutcome { Role = role, Items = items, BaseExp = baseExp, JobExp = jobExp };
                _outcomes.Add(monster, outcome);
            }
            _character.StageKillTaskObligations?.Invoke(player, monster, settlement);
            // A retry re-stages: the later stage is undone first, so the AFK reset never restores a stale wrapper.
            settlement.ResetHuntStage?.Invoke();
            StageAfkKill?.Invoke(player, monster, settlement, outcome.Items, outcome.BaseExp, outcome.JobExp);
            StageHuntKill?.Invoke(player, monster, settlement);
            settlement.LibraryMonster = monster.Template.NpcId;
            outcome.Granted = await _character.ExchangeLockedAsync(player, [], outcome.Items, popup: false, sync: outcome.Items.Count > 0,
                baseExp: outcome.BaseExp, jobExp: outcome.JobExp, rewardSettlement: settlement,
                persist: settlement.Enlist, committed: settlement.Applied);
        }
        catch
        {
            // A failed apply/notice cannot resurrect a committed source. The owned writer quarantines
            // ambiguous/failed live state; restart and login reload the durable bag and progression.
            try
            {
                if (await _character.RewardSourceCommittedAsync(settlement.SourceKey))
                {
                    settlement.Committed = true;
                    // Failed Apply cannot safely run room/quest callbacks on an old owner snapshot.
                    // Abort the attachment; process recovery reloads the committed outcome and durable obligations.
                    player.CancelPendingCharacterIntents();
                    player.AbortForRecovery();
                }
                else monster.RewardCommit = null;
            }
            catch
            {
                settlement.OutcomeUnknown = true;
                player.CancelPendingCharacterIntents();
                player.AbortForRecovery();
            }
            throw;
        }
    }

    private (List<(int Entry, int Count)> Items, long BaseExp, long JobExp) ResolveKill(GameSession player, MonsterUnit monster, bool bonus)
    {
        if (ResolveKillForTest != null) return ResolveKillForTest(player, monster);
        // Team-boss personal awards have their own durable dungeon obligation; ordinary loot stays empty.
        if (player.DungeonAttempt is { Plan.Multi: not null } dungeon && monster.InstanceId == dungeon.Room.Id)
            return ([], 0, 0);
        if (_data.Row("NPCConfig", monster.Template.NpcId) is not { } npc) return ([], 0, 0);
        var drops = new List<(int Entry, int Count)>();
        // The party's item drop bonus (TeamService.DropBonus, +30% per extra member on the map) multiplies the server rate.
        double chanceRate = Rate(_options.Value.Rates.ItemDrop) * _teams.DropBonus(player) / 10000;
        // (inferred) NPCDropConfig environment 0 is reduced and 1 is Bonus Time: the former carries
        // small EXP at player levels 1-99, the latter larger EXP. Fatigue environment 2 has no proven trigger.
        // _iNormalDrop remains additive under its own level/map conditions; no numeric multiplier is invented.
        if (Resolve(GameData.Int(npc, "_iDrop")) is var dropId and > 0 && _data.Row("NPCDropConfig", dropId) is { } nd
            && nd.ValueKind == JsonValueKind.Object && nd.TryGetProperty(bonus ? "1" : "0", out var env))
            RollPools(env, player, drops, monster.Template.Level, chanceRate);
        int dropTableCount = drops.Count;
        if (npc.TryGetProperty("_iNormalDrop", out var normals) && normals.ValueKind == JsonValueKind.Array)
        {
            foreach (var n in normals.EnumerateArray())
            {
                if (GameData.Number(n) is not { } nid || Resolve((long)nid) is not (var id and > 0) || _data.Row("NpcNormalDropConfig", id) is not { } row) continue;
                if (row.TryGetProperty("_iMapId", out var maps) && maps.ValueKind == JsonValueKind.Array && maps.GetArrayLength() > 0
                    && !maps.EnumerateArray().Any(m => GameData.Number(m) == player.SceneEntry))
                    continue;
                RollPools(row, player, drops, monster.Template.Level, chanceRate);
            }
        }

        // Beta footage (Poring 5, Boa 23, Willow 9 Base EXP) matches the _iNormalDrop amount alone, not the sum with the
        // _iDrop environment's EXP row. So when the normal drops granted Base EXP, the _iDrop table's EXP is dropped;
        // its items stay. Above player level 36 the normal EXP rows stop and the _iDrop EXP applies again (inferred).
        if (drops.Skip(dropTableCount).Any(d => d.Entry == BaseExpItem))
            for (int i = dropTableCount - 1; i >= 0; i--)
                if (drops[i].Entry == BaseExpItem) drops.RemoveAt(i);

        if (ExtraKillDrops != null) drops.AddRange(ExtraKillDrops(player, monster));
        long baseExp = drops.Where(d => d.Entry == BaseExpItem).Sum(d => (long)d.Count);
        var items = drops.Where(d => d.Entry is not (BaseExpItem or JobExpItem) && _data.Row("ItemConfig", d.Entry) != null).ToList();
        if (baseExp > 0)
        {
            long gap = player.Progress!.Level - monster.Template.Level;
            baseExp = baseExp * Correction((int)Math.Clamp(gap, -200, 200)) / 10000;
            baseExp = Math.Max(1, baseExp * _teams.ExpBonus(player) / 10000);
        }
        // Job EXP is 20% of the kill's Base EXP before the catch-up bonus (CharacterService.JobExpFromBase).
        // BaseExpAddConfig catch-up below the server level raises Base EXP only (CharacterService.LevelRules);
        // the server rates then scale both.
        return ApplyRates(items, _character.WithPursueBonus(player.Progress!.Level, baseExp), _character.JobExpFromBase(baseExp));
    }

    /// <summary>ServerOptions.Rates on a resolved kill: currency amounts, then Base and Job EXP (Job defaults to Base).</summary>
    internal (List<(int Entry, int Count)> Items, long BaseExp, long JobExp) ApplyRates(List<(int Entry, int Count)> items, long baseExp,
        long? jobExp = null)
    {
        var rates = _options.Value.Rates;
        double currencyRate = Rate(rates.CurrencyDrop);
        var scaled = items
            .Select(d => IsCurrency(d.Entry) ? (d.Entry, Count: (int)Math.Min(int.MaxValue, Scale(d.Count, currencyRate))) : d)
            .Where(d => d.Count > 0).ToList();
        return (scaled, Scale(baseExp, Rate(rates.BaseExp)), Scale(jobExp ?? baseExp, Rate(rates.JobExp)));
    }

    /// <summary>A drop roll's chance per million under the ItemDrop rate, capped at 100%.</summary>
    internal static double ScaledChance(double chance, double rate) => rate == 1 ? chance : Math.Min(1_000_000, chance * Rate(rate));

    /// <summary>A configured rate: negative or NaN reads as 0, and anything over 1000x is capped.</summary>
    internal static double Rate(double rate) => double.IsNaN(rate) || rate <= 0 ? 0 : Math.Min(rate, 1000);

    /// <summary>A positive amount times a rate, rounded, at least 1 while the rate is above 0.</summary>
    internal static long Scale(long amount, double rate) =>
        rate == 1 ? amount : rate <= 0 || amount <= 0 ? 0 : Math.Max(1, (long)Math.Min(long.MaxValue / 2, Math.Round(amount * rate)));

    private bool IsCurrency(int entry) => _data.Row("ItemConfig", entry) is { } row && GameData.Int(row, "_iType") == ItemTypeCurrency;
    private const int ItemTypeCurrency = 1;

    private async Task KilledAsync(GameSession player, MonsterUnit monster)
    {
        await PrepareKilledAsync(player, monster); // also covers existing service/test callbacks
        if (!_outcomes.TryGetValue(monster, out var outcome) || outcome.Published) return;
        outcome.Published = true;
        var role = outcome.Role;
        if (!ReferenceEquals(player.Role, role) || player.Attachment?.Cancelled == true) return;
        using (await CharacterService.Lock(player))
        {
            if (!ReferenceEquals(player.Role, role) || player.Attachment?.Cancelled == true) return;
            await monster.RewardSettlement!.PresentAsync();
            if (outcome.Items.Count > 0)
            {
                var notify = new GetRewardNotify
                {
                    Position = monster.Pos, TargetEntry = monster.Template.NpcId,
                    TargetUid = role.Id, RewardType = { RewardDrop }
                };
                var shown = new HashSet<InvItem>(ReferenceEqualityComparer.Instance);
                foreach (var (entry, count) in outcome.Items)
                    if (_character.AddAcquiredEquipmentRows(player, notify.Rewards, entry, count, 0, outcome.Granted, shown) is var left and > 0)
                        notify.Rewards.Add(new ItemBaseInfo
                        {
                            ItemEntry = entry, Stack = left,
                            ItemInfos = { [10001] = new ItemAttrValue { LongVal = 0 } }
                        });
                await player.SendGameAsync(CharacterService.GetRewardNotify, notify);
            }
        }
        var granted = outcome.Items.Select(i => (i.Entry, Count: (long)i.Count)).ToList();
        if (outcome.BaseExp > 0) granted.Add((BaseExpItem, outcome.BaseExp));
        if (outcome.JobExp > 0) granted.Add((JobExpItem, outcome.JobExp));
        foreach (var handler in KillDropsGranted)
        {
            if (!ReferenceEquals(player.Role, role) || player.Attachment?.Cancelled == true) return;
            await handler(player, monster, granted);
        }
        _log.LogDebug("{Name} durable loot from {Npc}: exp {Exp}, source {Source}",
            role.Name, monster.Template.NpcId, outcome.BaseExp, monster.RewardSource!.Key);
    }
    /// <summary>MultiverseGroupConfig id → the playbook's value, else _iValue_default (playbook 0 here).</summary>
    private long Resolve(long multiverseId) =>
        multiverseId > 0 && _data.Row("MultiverseGroupConfig", multiverseId) is { } mg ? GameData.Int(mg, "_iValue_default") : 0;

    /// <summary>
    /// Every _iDropPoolN of a row; each pool slot [group, chance per million, rolls, picks] is rolled "rolls" times
    /// independently (MVP first-strike pool 21001 = [2020, 100%, 30, 1] = 30 x 1000 Minted Coin), and each successful
    /// roll draws "picks" distinct rows (<see cref="DropPicks"/>).
    /// </summary>
    /// <summary>A kill's pools: <paramref name="monsterLevel"/> is the slain monster's level (DropAttenuationConfig);
    /// <paramref name="chanceRate"/> is the server's ItemDrop rate.</summary>
    private void RollPools(JsonElement row, GameSession player, List<(int, int)> drops, int monsterLevel, double chanceRate = 1)
    {
        foreach (var p in row.EnumerateObject().Where(p => p.Name.StartsWith("_iDropPool", StringComparison.Ordinal)))
            if (GameData.Number(p.Value) is { } poolId)
                RollPool((long)poolId, player.Progress!, drops, null, monsterLevel, chanceRate, task => QuestActive(player, task));
    }

    /// <summary>DropGroupConfig._iQuestLimit: the row drops only while the killer has that quest accepted and not yet
    /// handed in (TaskConfig 10102100 "The Fate of an Egg Thief" drops the Card: Poring its next quest's card guide uses).
    /// Finished counts too: the kill that completes the quest's objective still rolls its quest drop.</summary>
    internal static bool QuestActive(GameSession player, long task) =>
        player.Tasks?.Infos.TryGetValue(task, out var info) == true && info.Status is TaskState.Running or TaskState.Finish;

    private void RollPool(long poolId, GameSession player, List<(int, int)> drops, List<(int Entry, int Count, int Bind)>? bound = null)
        => RollPool(poolId, player.Progress!, drops, bound);

    private void RollPool(long poolId, RoleProgress progress, List<(int, int)> drops, List<(int Entry, int Count, int Bind)>? bound = null,
        int monsterLevel = DropAttenuation.NoMonster, double chanceRate = 1, Func<long, bool>? questActive = null)
    {
        if (_data.Row("DropPoolConfig", poolId) is not { } pool) return;
        for (int slot = 1; slot <= 16; slot++)
        {
            if (!pool.TryGetProperty($"_iDropGroup_{slot}", out var g) || g.ValueKind != JsonValueKind.Array || g.GetArrayLength() < 2) continue;
            long group = (long)(GameData.Number(g[0]) ?? 0);
            double chance = GameData.Number(g[1]) ?? 0;
            chance = ScaledChance(chance, chanceRate);
            int rolls = g.GetArrayLength() >= 3 ? Math.Clamp((int)(GameData.Number(g[2]) ?? 1), 1, 1000) : 1;
            int picks = g.GetArrayLength() >= 4 ? Math.Max(1, (int)(GameData.Number(g[3]) ?? 1)) : 1;
            for (int r = 0; r < rolls; r++)
            {
                if (_random.NextDouble() * 1_000_000 >= chance) continue;
                PickFromGroup(group, progress, drops, bound, monsterLevel, picks, questActive);
            }
        }
    }

    /// <summary>
    /// An award id (MultiverseGroupConfig → AwardConfig[0]._iDropPool → DropPoolConfig), rolled for this player;
    /// same item ids merged. Used for MVP rewards.
    /// </summary>
    public List<(int Entry, int Count)> RollAward(long multiverseId, GameSession player)
    {
        var drops = new List<(int, int)>();
        if (Resolve(multiverseId) is var awardId and > 0 && _data.Row("AwardConfig", awardId) is { ValueKind: JsonValueKind.Array } rows
            && rows.GetArrayLength() > 0 && GameData.Int(rows[0], "_iDropPool") is var pool and > 0)
            RollPool(pool, player, drops);
        return drops.GroupBy(d => d.Item1).Select(g => (g.Key, g.Sum(d => d.Item2))).ToList();
    }

    /// <summary>One DropPoolConfig draw for this player, same item ids merged (GM panel GmDropPool).</summary>
    public List<(int Entry, int Count)> RollDropPool(long poolId, GameSession player)
    {
        var drops = new List<(int, int)>();
        if (_data.Row("DropPoolConfig", poolId) != null) RollPool(poolId, player, drops);
        return drops.GroupBy(d => d.Item1).Select(g => (g.Key, g.Sum(d => d.Item2))).ToList();
    }

    /// <summary>One DropPoolConfig draw for this player, each chosen DropGroupConfig._iBind retained, same goods merged.</summary>
    public List<(int Entry, int Count, int Bind)> RollPoolBound(long poolId, RoleProgress progress)
    {
        var bound = new List<(int Entry, int Count, int Bind)>();
        RollPool(poolId, progress, new List<(int, int)>(), bound);
        return bound.GroupBy(x => (x.Entry, x.Bind)).Select(g => (g.Key.Entry, g.Sum(x => x.Count), g.Key.Bind)).ToList();
    }

    /// <summary>Stage-box pool draw with each chosen DropGroupConfig._iBind retained (draw rules inferred).</summary>
    public List<(int Entry, int Count, int Bind)> RollAwardBound(long awardId, GameSession player)
        => RollAwardBound(awardId, player.Progress!);

    /// <summary>Roll the same configured award for a committed offline dungeon participant.</summary>
    public List<(int Entry, int Count, int Bind)> RollAwardBound(long awardId, RoleProgress progress)
    {
        var bound = new List<(int Entry, int Count, int Bind)>();
        if (_data.Row("AwardConfig", awardId) is { } raw)
        {
            var row = raw.ValueKind == JsonValueKind.Array && raw.GetArrayLength() > 0 ? raw[0] : raw;
            if (row.ValueKind == JsonValueKind.Object && GameData.Int(row, "_iDropPool") is var pool and > 0)
                RollPool(pool, progress, new List<(int, int)>(), bound);
        }
        return bound.GroupBy(x => (x.Entry, x.Bind)).Select(g => (g.Key.Entry, g.Sum(x => x.Count), g.Key.Bind)).ToList();
    }

    /// <summary>
    /// Auction source awards include fixed _kAward grants as well as _iDropPool draws. Lua_CFG_AwardConfig
    /// GetAwardList reads fixed tuples including binding; guild award arrays carry _kServerOpenDay ranges.
    /// (inferred) Those ranges are inclusive and choose the first matching row. Keep the pool-only overload above
    /// unchanged because DailyActivityService already grants its fixed rewards separately.
    /// </summary>
    public List<(int Entry, int Count, int Bind)> RollAuctionAwardBound(long awardId, RoleProgress progress, int serverOpenDay)
    {
        if (_data.Row("AwardConfig", awardId) is not { } raw)
            throw new InvalidOperationException($"Invalid auction award {awardId}.");
        var candidates = raw.ValueKind == JsonValueKind.Array ? raw.EnumerateArray().ToArray() : [raw];
        var row = candidates.FirstOrDefault(candidate => candidate.ValueKind == JsonValueKind.Object
            && (!candidate.TryGetProperty("_kServerOpenDay", out var days) || days.ValueKind != JsonValueKind.Array
                || days.GetArrayLength() < 2 || serverOpenDay >= (GameData.Number(days[0]) ?? 1)
                    && serverOpenDay <= (GameData.Number(days[1]) ?? int.MaxValue)));
        if (row.ValueKind != JsonValueKind.Object) return [];
        var goods = new List<(int Entry, int Count, int Bind)>();
        if (row.TryGetProperty("_kAward", out var fixedGoods) && fixedGoods.ValueKind == JsonValueKind.Array)
            foreach (var tuple in fixedGoods.EnumerateArray())
            {
                if (tuple.ValueKind != JsonValueKind.Array || tuple.GetArrayLength() < 2)
                    throw new InvalidOperationException($"Invalid fixed auction award {awardId}.");
                goods.Add((checked((int)(GameData.Number(tuple[0]) ?? 0)),
                    checked((int)(GameData.Number(tuple[1]) ?? 0)),
                    tuple.GetArrayLength() > 2 ? checked((int)(GameData.Number(tuple[2]) ?? 0)) : 0));
            }
        if (GameData.Int(row, "_iDropPool") is var pool and > 0)
            RollPool(pool, progress, [], goods);
        return goods.GroupBy(g => (g.Entry, g.Bind)).Select(g => (g.Key.Entry, g.Sum(x => x.Count), g.Key.Bind)).ToList();
    }

    private void PickFromGroup(long group, GameSession player, List<(int, int)> drops, List<(int Entry, int Count, int Bind)>? bound = null)
        => PickFromGroup(group, player.Progress!, drops, bound);

    /// <summary>Draws <paramref name="picks"/> distinct eligible rows of a group (<see cref="DropPicks"/>). A row with
    /// _iQuestLimit is eligible only when <paramref name="questActive"/> says that quest is running (kills only).</summary>
    internal void PickFromGroup(long group, RoleProgress progress, List<(int, int)> drops, List<(int Entry, int Count, int Bind)>? bound = null,
        int monsterLevel = DropAttenuation.NoMonster, int picks = 1, Func<long, bool>? questActive = null)
    {
        if (_data.Row("DropGroupConfig", group) is not { ValueKind: JsonValueKind.Array } rows) return;
        var p = progress;
        var candidates = rows.EnumerateArray().Where(r =>
        {
            if (GameData.Int(r, "_iWeights") <= 0 || GameData.Int(r, "_iActivityid") != 0) return false;
            if (GameData.Int(r, "_iQuestLimit") is var quest and not 0 && questActive?.Invoke(quest) != true) return false;
            long min = GameData.Int(r, "_iMinLevelLimit"), max = GameData.Int(r, "_iMaxLevelLimit");
            if (p.Level < min || max > 0 && p.Level > max) return false;
            return !r.TryGetProperty("_iJobLimit", out var jobs) || jobs.ValueKind != JsonValueKind.Array || jobs.GetArrayLength() == 0
                   || jobs.EnumerateArray().Any(j => GameData.Number(j) == p.Career);
        }).ToList();
        foreach (var r in DropPicks.Distinct(candidates, picks, _random))
        {
            int count = _random.Next((int)GameData.Int(r, "_iMinNumber"), (int)Math.Max(GameData.Int(r, "_iMinNumber"), GameData.Int(r, "_iMaxNumber")) + 1);
            int entry = (int)GameData.Int(r, "_kDropItem");
            int amount = _attenuation.Apply(r, Math.Max(1, count), p.Level, monsterLevel, _random);
            if (amount <= 0) continue;
            drops.Add((entry, amount));
            bound?.Add((entry, amount, (int)GameData.Int(r, "_iBind")));
        }
    }

    /// <summary>LvExpCorrectionConfig: player level − monster level → ratio per 10000.</summary>
    private long Correction(int gap)
    {
        _expCorrection ??= _data.Table("LvExpCorrectionConfig").Values
            .GroupBy(r => (int)GameData.Int(r, "_iBaseLvGap"))
            .ToDictionary(g => g.Key, g => GameData.Int(g.First(), "_iLvDifferenceCorrection"));
        return _expCorrection.GetValueOrDefault(gap, 10000);
    }
}
