using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Skills whose hit comes later, when a buff runs out, and the Madogear's Firepower:
/// <list type="bullet">
/// <item>Venom Splasher 15110 "Plants timed Dynamite ... After ${2} seconds, deals ... damage ... to the designated enemy and
/// up to ${4} other enemies within a ${3}-meter radius": the cast only plants 1511901-1511910 (no hit of its own); when
/// that debuff runs out, its _kLogicData row (VenomSplasher2 1511301-1511310) strikes the monster and its neighbours.</item>
/// <item>Energy Release 16317 (BombSuicide1): "Consumes HP equal to ${7}% of your Max HP and converts it into a 【Shield】.
/// Immediately removes all Control effects from you, then begins a ${1}-second countdown ... You have 【Immunity】 during
/// the countdown." The countdown buff 1634401 carries the shield (it lasts the countdown, inferred) and Immunity 903201
/// runs beside it. When the countdown ends, its _kLogicData group 16352 (BombSuicide2 1635201, 10 targets in 10 m)
/// blasts around the player, then "You then lose ${5}% of your current Max HP. This HP loss cannot be fatal".</item>
/// <item>Firepower Loaded: every ${6} (15) casts of Vulcan Arm (its 【Firepower Ready】 1631301 piles) and every cast of
/// Flame Launcher load it ${7}/@{1} (1) time for ${9} (3) Arm Cannon attacks. While loaded, 1631701's _kLogicData buff
/// 1632001 switches Arm Cannon to its mode-1 row (ArmCannonPro, 8 shells); each Arm Cannon cast spends one attack.
/// Before, every Vulcan Arm cast granted 1631701 and nothing switched the mode.</item>
/// <item>High-Energy Impact 16307: the mode-0 cast (AxeLeapAttack) only takes off: "become Flying and gain a Shield equal to
/// ${1}% of your Max HP for ${2} seconds ... and have 【Immunity】" (the shield on 1634601, Immunity 903201) and the mode
/// switch 1632901 turns the next press into its mode-1 row (AxeLeapAttack2, the attack below). "Tap the skill again or
/// wait until the Flying duration ends": a switch that runs out casts that row by itself. The attack ends the flight and
/// arms 1633601, Axe Tornado's Enhanced mode (AxeTornadoPro), until the next Axe Tornado. Its per-hit shield stacks and
/// the pulse count are not built.</item>
/// </list>
/// </summary>
public sealed partial class CombatService
{
    private const int HighEnergyShieldBuff = 1634601, FlightModeBuff = 1632901, AxeTornadoArmBuff = 1633601, AxeTornadoGroup = 16304;

    /// <summary>High-Energy Impact's take-off rows (mode 0 and its talent modes 2/4); +10 is the matching attack row.</summary>
    private static bool FlightScript(string script) => script is "AxeLeapAttack" or "AxeLeapAttackS1BD1" or "AxeLeapAttackS1BD2";

    private static bool FlightAttackScript(string script) => script.StartsWith("AxeLeapAttack2", StringComparison.Ordinal);

    /// <summary>The take-off of High-Energy Impact instead of its hits; false for any other row.</summary>
    private async Task<bool> StartFlightAsync(GameSession s, JsonElement row)
    {
        if (!FlightScript(Script(row))) return false;
        var desc = Numbers(row, "_kDescData");
        var stats = _character.Stats(s);
        int ms = (int)((desc.Length > 1 ? desc[1] : 10) * 1000);
        long shield = (long)Math.Round(stats[101] * (desc.Length > 0 ? desc[0] : 10) / 100);
        s.FlightAttackRowId = (int)GameData.Int(row, "_iID") + 10;
        await _buffs.AddSupportAsync(s, HighEnergyShieldBuff, ms, s.Role!.Id, shield);
        await _buffs.AddAsync(s, ImmunityBuff, ms, s.Role!.Id);
        await _buffs.AddAsync(s, FlightModeBuff, ms, s.Role!.Id);
        return true;
    }

    private async Task LandFlightAsync(GameSession s)
    {
        foreach (var buff in s.Buffs.Where(b => b.ConfigId is FlightModeBuff or ImmunityBuff && b.SourceId == s.Role!.Id).ToArray())
            await _buffs.RemoveAsync(s, buff);
        await _buffs.AddLastingAsync(s, AxeTornadoArmBuff, s.Role!.Id);
        if (_data.Row("SkillConfig", s.FlightAttackRowId - 10) is { } takeoff) await StartCooldownAsync(s, takeoff, landing: true);
    }

    private async Task FlightEndedAsync(GameSession s, ActiveBuff buff)
    {
        if (buff.ConfigId != FlightModeBuff || s.Dead || s.Role == null || _data.Row("SkillConfig", s.FlightAttackRowId) is not { } attack
            || !FlightAttackScript(Script(attack))) return;
        await LandFlightAsync(s);
        await ProcCastAsync(s, attack, null, s.Face, area: true);
    }

    private const int VenomSplasherBombGroup = 15119, EnergyReleaseCountdownGroup = 16344, EnergyReleaseCountdownBuff = 1634401,
        ImmunityBuff = 903201, FirepowerReadyBuff = 1631301, FirepowerLoadedBuff = 1631701, VulcanArmGroup = 16313,
        FlameLauncherGroup = 16315, ArmCannonGroup = 16312;

    /// <summary>Casts whose damage lands later (the buff above), so the cast itself deals none.</summary>
    private static readonly HashSet<string> DelayedHitScripts = ["VenomSplasher", "BombSuicide1"];

    private void WireDelayedSkills()
    {
        _buffs.UnitExpired.Add(VenomSplasherBombAsync);
        _buffs.Expired.Add(EnergyReleaseBlastAsync);
        _buffs.Expired.Add(FlightEndedAsync);
        _buffs.Changed.Add(FirepowerReadyAsync);
        CastLanded.Add(DelayedCastLandedAsync);
    }

    private GameSession? SessionOfRole(long roleId) => _sessions.All().FirstOrDefault(p => p.Role?.Id == roleId);

    private async Task VenomSplasherBombAsync(WorldUnit unit, ActiveBuff buff)
    {
        if (unit is not MonsterUnit monster || monster.Dead || monster.Hp <= 0
            || _data.Row("BuffConfig", buff.ConfigId) is not { } row || GameData.Int(row, "_iBuffID") != VenomSplasherBombGroup
            || Numbers(row, "_kLogicData") is not [var bombId, ..] || _data.Row("SkillConfig", (long)bombId) is not { } bomb
            || SessionOfRole(buff.SourceId) is not { } source || source.Dead || source.WorldScene != monster.WorldScene) return;
        await ProcCastAsync(source, bomb, monster);
    }

    private async Task DelayedCastLandedAsync(GameSession s, JsonElement row)
    {
        switch (Script(row))
        {
            case "BombSuicide1":
                await StartEnergyReleaseAsync(s, row);
                break;
            case "FlameLauncher":
                if (Numbers(row, "_kDescData") is { Length: > 8 } flame) await LoadFirepowerAsync(s, (int)flame[8]);
                break;
        }
        if (FlightAttackScript(Script(row))) await LandFlightAsync(s);
        if (GameData.Int(row, "_iSkillID") == AxeTornadoGroup && GameData.Int(row, "_iMode") % 2 == 1)
            foreach (var arm in s.Buffs.Where(b => b.ConfigId == AxeTornadoArmBuff).ToArray()) await _buffs.RemoveAsync(s, arm);
        if (GameData.Int(row, "_iSkillID") == ArmCannonGroup && s.FirepowerUses > 0 && --s.FirepowerUses == 0)
            await UnloadFirepowerAsync(s);
    }

    private async Task StartEnergyReleaseAsync(GameSession s, JsonElement row)
    {
        var desc = Numbers(row, "_kDescData");
        var stats = _character.Stats(s);
        long cost = desc.Length > 6 ? (long)Math.Round(stats[101] * desc[6] / 100) : 0;
        if (cost > 0) await _character.SetHpSpAsync(s, Math.Max(1, stats[2000] - cost), null);
        foreach (var buff in s.Buffs.Where(b => _data.Row("BuffConfig", b.ConfigId) is { } br && HasCategory(br, [25])).ToArray())
            await _buffs.RemoveAsync(s, buff);
        int countdown = (int)((desc.Length > 0 ? desc[0] : 3) * 1000);
        await _buffs.AddAsync(s, ImmunityBuff, countdown, s.Role!.Id);
        await _buffs.AddSupportAsync(s, EnergyReleaseCountdownBuff, countdown, s.Role!.Id, Math.Max(0, cost));
    }

    private async Task EnergyReleaseBlastAsync(GameSession s, ActiveBuff buff)
    {
        if (s.Dead || s.Role == null || s.Progress == null || _data.Row("BuffConfig", buff.ConfigId) is not { } row
            || GameData.Int(row, "_iBuffID") != EnergyReleaseCountdownGroup || Numbers(row, "_kLogicData") is not [var blastGroup, ..]
            || _data.Row("SkillConfig", _skills.RealId((int)blastGroup, 1)) is not { } blast) return;
        await ProcCastAsync(s, blast, null, s.Face, area: true);
        if (s.Dead || Numbers(blast, "_kDescData") is not { Length: > 4 } desc) return;
        var stats = _character.Stats(s);
        long loss = (long)Math.Round(stats[101] * desc[4] / 100);
        if (loss > 0) await _character.SetHpSpAsync(s, Math.Max(1, stats[2000] - loss), null);
    }

    /// <summary>Vulcan Arm's 【Firepower Ready】 piles: at the cast count they turn into one Firepower load.</summary>
    private async Task FirepowerReadyAsync(GameSession s)
    {
        var ready = s.Buffs.FirstOrDefault(b => b.ConfigId == FirepowerReadyBuff && b.ExpireMs > Now);
        if (ready == null || s.Progress == null
            || _data.Row("SkillConfig", _skills.RealId(VulcanArmGroup, Math.Max(1, _skills.Level(s.Progress, VulcanArmGroup)))) is not { } vulcan
            || Numbers(vulcan, "_kDescData") is not { Length: > 8 } desc || ready.PileCount < desc[5]) return;
        await _buffs.RemoveAsync(s, ready);
        await LoadFirepowerAsync(s, (int)(Math.Max(1, desc[6]) * desc[8]));
    }

    private async Task LoadFirepowerAsync(GameSession s, int attacks)
    {
        if (attacks <= 0 || _data.Row("BuffConfig", FirepowerLoadedBuff) is not { } loaded) return;
        s.FirepowerUses += attacks;
        await _buffs.AddLastingAsync(s, FirepowerLoadedBuff, s.Role!.Id);
        foreach (double mode in Numbers(loaded, "_kLogicData")) await _buffs.AddLastingAsync(s, (int)mode, s.Role!.Id);
    }

    private async Task UnloadFirepowerAsync(GameSession s)
    {
        if (_data.Row("BuffConfig", FirepowerLoadedBuff) is not { } loaded) return;
        var ids = Numbers(loaded, "_kLogicData").Select(v => (int)v).Append(FirepowerLoadedBuff).ToHashSet();
        foreach (var buff in s.Buffs.Where(b => ids.Contains(b.ConfigId)).ToArray()) await _buffs.RemoveAsync(s, buff);
    }
}
