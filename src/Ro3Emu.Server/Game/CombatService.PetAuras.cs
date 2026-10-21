using System.Text.Json;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Pet Main Skills that leave a timed aura on the master and act from it on a cadence:
/// <list type="bullet">
/// <item>Demon Attendant (2308201..06): the guardian row grants 【Demonic Aura】 2308101 for 5 s. The buff's _kLogicData
/// [23086] names the extra _kAutoLearnSkill row (2308601..06, "emodiyu", 600 cm, 5 targets, P1 4500..9000), and the
/// description says "Every ${7} seconds, deals ... Adaptive ATK*${3}% to up to ${6} enemies within ${1} meters" (0.5 s).</item>
/// <item>Chimera (2309201..06): 【Holy Light Field】 2309101..06 (_kLogicData [23097]) lasts ${6} = 5 s; "Every ${1} sec.,
/// it grants the master and up to ${3} allies within ${2} meters a shield" (2309701..06, buff 2309201..06,
/// T_AXadd0_etc Max Life * 2..4%).</item>
/// </list>
/// The tick casts that extra row as a server follow-up from the master's current position while the aura buff it
/// granted is still on them (inferred: the field follows the master; the first tick is one interval after the cast).
/// </summary>
public sealed partial class CombatService
{
    /// <summary>Main Skill group -> the _kDescData index holding the aura's tick in seconds (from its description).</summary>
    private static readonly IReadOnlyDictionary<int, int> PetAuraTickDesc = new Dictionary<int, int>
    {
        [23082] = 6, // Demon Attendant: "Every ${7} seconds"
        [23092] = 0, // Chimera: "Every ${1} sec."
    };

    private readonly HashSet<long> _petAuraLoops = new();
    private HashSet<long>? _petAuraRows, _petAuraGuardians;

    internal int PetAuraCount => _petAuraLoops.Count;

    /// <summary>The extra rows a Main Skill's aura casts (not GuardianSkill, named by a GuardianSkill buff's
    /// _kLogicData). They scale from Adaptive ATK like the guardian rows.</summary>
    private bool PetAuraRow(JsonElement row) => PetAuraRows().Rows.Contains(GameData.Int(row, "_iID"));

    /// <summary>A guardian row that only lays an aura. The Holy Light Field's P1 [10] is not a heal (its description
    /// names none), so the friendly heal branch skips it.</summary>
    private bool PetAuraGuardian(JsonElement row) => PetAuraRows().Guardians.Contains(GameData.Int(row, "_iID"));

    private (HashSet<long> Rows, HashSet<long> Guardians) PetAuraRows()
    {
        if (_petAuraRows == null || _petAuraGuardians == null)
        {
            HashSet<long> rows = new(), guardians = new();
            foreach (var (_, main) in _data.Table("SkillConfig"))
                if (GameData.Int(main, "_iSystemType") == ActiveGuardSkill && PetAuraTickDesc.ContainsKey((int)GameData.Int(main, "_iSkillID")))
                    foreach (var id in Numbers(main, "_kAutoLearnSkill"))
                        if (_data.Row("SkillConfig", (long)id) is { } extra)
                            (GameData.Int(extra, "_iSystemType") == GuardianSkill ? guardians : rows).Add((long)id);
            (_petAuraRows, _petAuraGuardians) = (rows, guardians);
        }
        return (_petAuraRows, _petAuraGuardians);
    }

    /// <summary>After the guardian row landed: start a tick for each aura buff it granted the master.</summary>
    private void StartPetAuras(GameSession s, JsonElement main, JsonElement guardian)
    {
        if (!PetAuraTickDesc.TryGetValue((int)GameData.Int(main, "_iSkillID"), out int index)
            || Numbers(main, "_kDescData").ElementAtOrDefault(index) is not (> 0 and var seconds)) return;
        int interval = (int)Math.Round(seconds * 1000);
        foreach (var tuple in Tuples(guardian, "_kBuff1"))
        {
            if (tuple.GetArrayLength() < 1 || GameData.Number(tuple[0]) is not { } buffId
                || _data.Row("BuffConfig", (long)buffId) is not { } config
                || Numbers(config, "_kLogicData") is not [var group, ..]
                || ExtraRow(main, (long)group) is not { } extra) continue;
            long now = MonsterService.NowMs;
            var aura = s.Buffs.Where(b => b.ConfigId == (int)buffId && b.ExpireMs > now).MaxBy(b => b.ExpireMs);
            if (aura == null || !_petAuraLoops.Add(aura.Guid)) continue;
            SchedulePetAura(s, s.Role!, extra, aura.Guid, aura.ExpireMs, interval, aura.ExpireMs - aura.DurationMs + interval);
        }
    }

    private JsonElement? ExtraRow(JsonElement main, long group)
    {
        foreach (var id in Numbers(main, "_kAutoLearnSkill"))
            if (_data.Row("SkillConfig", (long)id) is { } row && GameData.Int(row, "_iSystemType") != GuardianSkill
                && GameData.Int(row, "_iSkillID") == group) return row;
        return null;
    }

    /// <summary>One tick at <paramref name="due"/>; the next is due one interval later, so a slow loop or a slow
    /// cast does not stretch the cadence (5 s at 0.5 s stays 10 ticks).</summary>
    private void SchedulePetAura(GameSession s, object role, JsonElement extra, long guid, long expire, int interval, long due)
    {
        long scene = s.WorldScene, generation = s.SceneGeneration;
        // The loop runs a callback on the tick after its delay, so aim one tick early (as the bolts do).
        var delay = TimeSpan.FromMilliseconds(Math.Max(0, due - MonsterService.NowMs - GameLoop.TickInterval.TotalMilliseconds));
        ScheduleRuntime(scene, delay, async () =>
        {
            // A tick due at the expiry moment still lands, though the buff may have just been removed for expiring;
            // one removed early (dispelled, cleared) stops the aura.
            var buff = s.Buffs.FirstOrDefault(b => b.Guid == guid);
            bool live = ReferenceEquals(s.Role, role) && !s.Dead && !s.SceneLoading && s.WorldScene == scene
                && s.SceneGeneration == generation && _sessions.Contains(s)
                && (buff != null ? buff.ExpireMs >= due : due == expire && MonsterService.NowMs >= expire - GameLoop.TickInterval.TotalMilliseconds);
            if (!live)
            {
                _petAuraLoops.Remove(guid);
                return;
            }
            try { await ProcCastAsync(s, extra, null, face: s.Face, area: true, at: s.Position.Clone()); }
            finally
            {
                if (s.Buffs.FirstOrDefault(b => b.Guid == guid) is { } current) expire = current.ExpireMs;
                if (due + interval <= expire) SchedulePetAura(s, role, extra, guid, expire, interval, due + interval);
                else _petAuraLoops.Remove(guid);
            }
        });
    }
}
