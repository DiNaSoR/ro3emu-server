using System.Runtime.CompilerServices;
using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    private enum GuardianWindowKind { ExtraHit, HealOnHit, HealLowHp, BuffEnemy, BuffSelf, ControlledBonus, DefenceOnHit, CurseOnKill, Stealth }
    private sealed class GuardianWindow
    {
        public required GuardianPetRules.Skill Pet { get; init; }
        public required ActiveSkillCast Cast { get; init; }
        public required GuardianWindowKind Kind { get; init; }
        public long Expires, Next;
        public int IntervalMs, Remaining = int.MaxValue, BuffId, BuffDuration, Maximum = 1, Hits = 1;
        public double Chance = 100, Coefficient, HpThreshold, Radius, GuaranteedDistance;
        public bool NormalOnly;
        public JsonElement? Effect;
    }
    private readonly ConditionalWeakTable<GameSession, List<GuardianWindow>> _guardianWindows = [];

    private void RegisterGuardianWindow(GameSession source, GuardianWindow window)
    {
        var windows = _guardianWindows.GetOrCreateValue(source);
        var prior = windows.FirstOrDefault(w => w.Pet.PetId == window.Pet.PetId && w.Kind == window.Kind);
        if (prior != null) { window.Next = prior.Next; windows.Remove(prior); }
        windows.Add(window);
    }

    private GuardianWindow[] LiveGuardianWindows(GameSession source)
    {
        var windows = _guardianWindows.GetOrCreateValue(source);
        windows.RemoveAll(w => w.Expires <= Now || w.Remaining <= 0 || !CurrentCastMap(source, w.Cast)
            || !_pets.GuardianEquipped(source, w.Pet));
        return [.. windows];
    }

    /// <summary>BuffConfig controller links and primary description parameters. (inferred) The window opens after
    /// the first pet impact, applies to the owner's ordinary attacks/skills, and is revoked with the source lineup.</summary>
    private void GuardianPrimaryWindows(GameSession source, GuardianPetRules.Skill pet, ActiveSkillCast cast)
    {
        double[] desc = Numbers(pet.Active, "_kDescData");
        GuardianWindow New(GuardianWindowKind kind, double seconds) => new()
        { Pet = pet, Cast = cast, Kind = kind, Expires = Now + (long)(seconds * 1000) };
        GuardianWindow? window = null;
        switch (GuardianPetRules.Script(pet.Effect))
        {
            case "BaXiLiSiKe_HuZhu" when desc.Length >= 5:
                window = New(GuardianWindowKind.DefenceOnHit, desc[0]);
                window.BuffId = GuardianPetRules.BuffAtStar(_data, 23412, pet.Star); window.BuffDuration = (int)(desc[3] * 1000);
                break;
            case "Pet_AncientMummy_23122" when desc.Length >= 6:
                window = New(GuardianWindowKind.CurseOnKill, desc[4]);
                window.Effect = GuardianSkillAtStar(23126, pet.Star); window.Remaining = (int)desc[5];
                break;
            case "renfamizong" when desc.Length >= 5:
                window = New(GuardianWindowKind.Stealth, desc[2]); window.Remaining = (int)desc[4];
                break;
            case "tutujianglin" when desc.Length >= 4:
                window = New(GuardianWindowKind.HealLowHp, desc[0]);
                window.HpThreshold = desc[2]; window.Coefficient = desc[3]; window.Remaining = 1;
                break;
            case "WuShiZhiNu" when desc.Length >= 7:
                window = New(GuardianWindowKind.ExtraHit, desc[4]);
                window.Effect = GuardianSkillAtStar(23275, pet.Star); window.IntervalMs = (int)(desc[6] * 1000);
                break;
            case "NvZhanShiLuoLa_HuZhu" when desc.Length >= 11:
                window = New(GuardianWindowKind.ExtraHit, desc[6]);
                window.Effect = GuardianSkillAtStar(23266, pet.Star); window.IntervalMs = (int)(desc[10] * 1000);
                break;
            case "Pet_MmSarah_23342" when desc.Length >= 7:
                window = New(GuardianWindowKind.ExtraHit, desc[4]);
                window.Effect = GuardianSkillAtStar(23346, pet.Star); window.IntervalMs = (int)(desc[6] * 1000);
                window.Hits = (int)GuardianPetRules.DescriptionLiterals(pet.Active).ElementAtOrDefault(1);
                break;
            case "BlueprintYinYueJiangLin2" when desc.Length >= 9:
                window = New(GuardianWindowKind.BuffSelf, desc[4]);
                window.Chance = desc[5]; window.GuaranteedDistance = desc[8];
                window.BuffId = GuardianPetRules.BuffAtStar(_data, 23033, pet.Star); window.BuffDuration = (int)(desc[6] * 1000);
                break;
            case "PetrifyingGaze" when desc.Length >= 8:
                window = New(GuardianWindowKind.BuffEnemy, desc[4]);
                window.NormalOnly = true; window.Chance = desc[5]; window.BuffId = 902101;
                window.BuffDuration = (int)(desc[6] * 1000); window.IntervalMs = (int)(desc[7] * 1000);
                break;
            case "xingxingshike" when desc.Length >= 9:
                window = New(GuardianWindowKind.BuffEnemy, desc[4]);
                window.Chance = desc[7]; window.BuffId = GuardianPetRules.BuffAtStar(_data, 23362, pet.Star);
                window.BuffDuration = (int)(desc[8] * 1000);
                break;
        }
        if (window != null && (window.Kind != GuardianWindowKind.ExtraHit || window.Effect != null)) RegisterGuardianWindow(source, window);
    }

    private JsonElement? GuardianSkillAtStar(int group, int star)
    {
        var rows = GuardianTables.Of(_data).Skills(group, star);
        return rows.Count == 1 ? rows[0] : null;
    }

    private void GuardianReactiveWindow(GameSession source, GuardianPetRules.Skill pet, ActiveSkillCast action)
    {
        double Desc(int index) => GuardianPetRules.Description(pet, index);
        bool heal = pet.Reactive == GuardianPetRules.Reaction.AliotHealingWindow;
        bool lola = pet.Reactive == GuardianPetRules.Reaction.LolaWindow;
        if (lola && (action.GuardianDamage?.Count ?? 0) == 0) return;
        var window = new GuardianWindow
        {
            Pet = pet, Cast = action, Kind = heal ? GuardianWindowKind.HealOnHit : GuardianWindowKind.ExtraHit,
            Expires = Now + (long)(Desc(0) * 1000), IntervalMs = (int)(Desc(heal || lola ? 4 : 2) * 1000),
            Radius = heal ? Desc(1) : 0, Maximum = heal ? (int)Desc(2) + 1 : 1, Coefficient = heal ? Desc(3) : 0,
            Effect = heal ? null : GuardianSkillAtStar(lola ? 23267 : 23276, pet.Star),
        };
        if (heal || window.Effect != null) RegisterGuardianWindow(source, window);
    }

    private Task GuardianWindowMonsterHitAsync(GameSession source, MonsterUnit target, JsonElement row, long damage) =>
        GuardianWindowHitAsync(source, target.Uid, target.Pos, row, damage, target.Dead || target.Hp <= 0);

    private async Task GuardianWindowHitAsync(GameSession source, long targetId, Romsg.Vec3 position, JsonElement row, long damage, bool dead)
    {
        if (damage <= 0 || row.TryGetProperty("_ro3GuardianProc", out _) || row.TryGetProperty("_ro3GuardianFixedHit", out _)
            || GameData.Int(row, "_iSystemType") == GuardianPetRules.EffectType) return;
        await ConsumeGuardianDamageChargeAsync(source);
        await EndGuardianStealthAsync(source);
        foreach (var window in LiveGuardianWindows(source))
        {
            if (window.Kind is GuardianWindowKind.CurseOnKill)
            {
                if (dead) await GuardianKillCurseAsync(source, window, position);
                continue;
            }
            if (window.Kind is GuardianWindowKind.HealLowHp or GuardianWindowKind.ControlledBonus
                or GuardianWindowKind.DefenceOnHit or GuardianWindowKind.Stealth || window.Next > Now
                || window.NormalOnly && GameData.Int(row, "_iIsNormalAtk") != 1) continue;
            if (dead && window.Kind is GuardianWindowKind.BuffEnemy or GuardianWindowKind.ExtraHit) continue;
            bool guaranteed = window.GuaranteedDistance > 0 && Distance(source.Position, position) > window.GuaranteedDistance;
            if (!guaranteed && Rolls.Proc(10000) >= window.Chance * 100) continue;
            window.Next = Now + window.IntervalMs; window.Remaining--;
            await ExecuteGuardianWindowAsync(source, targetId, window);
        }
    }

    private async Task ExecuteGuardianWindowAsync(GameSession source, long targetId, GuardianWindow window)
    {
        if (!CurrentCastMap(source, window.Cast) || !_pets.GuardianEquipped(source, window.Pet)) return;
        switch (window.Kind)
        {
            case GuardianWindowKind.ExtraHit when window.Effect is { } effect:
                // Sarah's auxiliary coefficient is the TOTAL of the literal hit count, unlike its primary per-hit coefficient.
                int hits = Math.Clamp(window.Hits, 1, 50);
                var one = hits == 1 ? effect : GuardianPetRules.PulseRow(effect,
                    new(0, Numbers(effect, "_kDamageParam1").FirstOrDefault() / hits));
                for (int i = 0; i < hits && CurrentCastMap(source, window.Cast); i++)
                    await ApplyEffectsAsync(source, GuardianProcAction(source, window.Pet, window.Cast, one, targetId), source.Position);
                break;
            case GuardianWindowKind.HealOnHit:
                await GuardianAreaHealAsync(source, window.Pet, window.Cast, (float)window.Radius, window.Maximum, window.Coefficient);
                break;
            case GuardianWindowKind.BuffSelf:
                await _buffs.AddGuardianAsync(source, window.BuffId, window.BuffDuration, window.Cast.OwnerId);
                break;
            case GuardianWindowKind.BuffEnemy:
                var monster = FindMonster(source.WorldScene, targetId);
                var player = EventTarget(source, targetId);
                var tuple = JsonSerializer.SerializeToElement(new[] { window.BuffId, 10000, window.BuffDuration });
                if (!RollHostileBuff(tuple, source, null, player, monster, out int id, out int ms)) break;
                if (monster is { Dead: false, Hp: > 0 }) await _buffs.AddGuardianAsync(monster, id, ms, window.Cast.OwnerId);
                else if (player != null) await _buffs.AddGuardianAsync(player, id, ms, window.Cast.OwnerId);
                break;
        }
    }

    private async Task GuardianTakenAsync(GameSession target, long damage)
    {
        if (damage <= 0 || target.Dead || target.SceneLoading) return;
        await _buffs.BreakHitControlsAsync(target);
        await _buffs.ConsumeGuardianGroupAsync(target, 23019);
        foreach (var window in LiveGuardianWindows(target).Where(w => w.Kind is GuardianWindowKind.HealLowHp
            or GuardianWindowKind.DefenceOnHit or GuardianWindowKind.Stealth))
        {
            if (window.Kind == GuardianWindowKind.DefenceOnHit)
            { await _buffs.AddGuardianAsync(target, window.BuffId, window.BuffDuration, window.Cast.OwnerId); continue; }
            if (window.Kind == GuardianWindowKind.Stealth)
            { if (--window.Remaining <= 0) await EndGuardianStealthAsync(target); continue; }
            var stats = _character.Stats(target);
            if (stats[GuardianPetRules.CurrentHp] >= stats[GuardianPetRules.Hp] * window.HpThreshold / 100 || window.Next > Now) continue;
            window.Remaining--;
            long amount = (long)Math.Floor(stats[GuardianPetRules.Hp] * window.Coefficient / 100);
            await GuardianHealAsync(target, target, window.Pet.EffectId, window.Cast.Id, amount,
                () => CurrentCastMap(target, window.Cast) && _pets.GuardianEquipped(target, window.Pet));
        }
    }

    private long GuardianControlledDamage(GameSession source, bool affected, long damage)
    {
        if (!affected || damage <= 0) return damage;
        double percent = LiveGuardianWindows(source).Where(w => w.Kind == GuardianWindowKind.ControlledBonus).Sum(w => w.Coefficient);
        return (long)Math.Clamp(Math.Floor(damage * Math.Max(0, 1 + percent / 100)), 0, long.MaxValue);
    }

    private bool GuardianControlTarget(MonsterUnit target) => _buffs.UnitHasGroup(target, 9021)
        || target.SpeedCm(Now) < target.Template.SpeedCm;

    private bool GuardianControlTarget(GameSession target) => target.Buffs.Any(b => b.ExpireMs > Now
        && (_data.Row("BuffConfig", b.ConfigId) is { } row && GameData.Int(row, "_iBuffID") == 9021
            || b.Changes.Any(c => c.Attr is 1131 or 1132 && c.Value < 0)));
}
