using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Ground fields (SkillObject) beyond the original families (CombatService.Effects.CreateSkillObjectAsync):
/// <list type="bullet">
/// <item>Magnus Exorcismus (_kLogicData [22000009, ...], NPCConfig 22000009 "Magnus Exorcismus") and Sanctuary (NPC
/// 22000003 "Sanctuary"; its row names no NPC): "For the next ${4} sec., ... every ${5} sec." (_kDescData [3], [4]: 4 s,
/// 0.5 s / 0.6 s) over the "${2}m*${3}m" area (_kRange [800, 800] / [700, 700], the release 7 rectangle; allies within
/// half its width). Sanctuary heals up to ${6} allies a tick and hurts only "Undead-property and Demon Race monsters".</item>
/// <item>Gravitation Field (ZhongLiYuanYe, NPC 1200013 "Gravitation Field"): _kLogicData [500, 3900, 550] = a wave every
/// 500 ms for 3.9 s, "strikes a total of ${7} times" (_kDescData [6] = 8), each wave rolling its Immobilize.</item>
/// <item>Fire Wall: "knocks the enemy back ${5} meters", "disappear early after being touched by enemies ${7} times",
/// "Up to ${8} Fire Walls can exist at once" (_kDescData [4], [6], [7]); a new wall replaces the oldest.</item>
/// <item>Ganbantein (GaBanTaYin): "Removes up to ${3} ground effects from a designated area with a radius of ${2}
/// meters" (_kDescData [2], [1]): every live skill field there, whoever owns it (inferred, as in classic RO).</item>
/// </list>
/// </summary>
public sealed partial class CombatService
{
    private const int SanctuaryNpc = 22000003, GravitationFieldNpc = 1200013;

    /// <summary>(npc, duration ms, interval ms, ally radius m, waves) of the holy fields and Gravitation Field.</summary>
    private static (int Npc, int Duration, int Interval, float Radius, int Waves) HolyAndGravityField(string script, JsonElement row,
        double[] logic, float radius)
    {
        var desc = Numbers(row, "_kDescData");
        var range = Numbers(row, "_kRange");
        if (script == "ZhongLiYuanYe")
            return (GravitationFieldNpc, logic.Length > 1 ? (int)logic[1] : 3900, logic.Length > 0 && logic[0] > 0 ? (int)logic[0] : 500,
                radius, desc.Length > 6 && desc[6] > 0 ? (int)desc[6] : 0);
        int npc = script == "Sanctuary" ? SanctuaryNpc : logic.Length > 0 ? (int)logic[0] : 0;
        int duration = desc.Length > 3 && desc[3] > 0 ? (int)(desc[3] * 1000) : 4000;
        int interval = desc.Length > 4 && desc[4] > 0 ? (int)Math.Round(desc[4] * 1000) : 500;
        return (npc, duration, interval, range.Length > 0 && range[0] > 0 ? (float)(range[0] / 200) : radius, 0);
    }

    /// <summary>Fire Wall's touch limit and knockback (_kDescData [6], [4]).</summary>
    private static (int Touches, float Push) FireWallLimits(JsonElement row)
    {
        var desc = Numbers(row, "_kDescData");
        return (desc.Length > 6 && desc[6] > 0 ? (int)desc[6] : 0, desc.Length > 4 && desc[4] > 0 ? (float)desc[4] : 0);
    }

    /// <summary>Whether a field's hostile side may hit this monster: Sanctuary only hurts the Undead property and the
    /// Demon race; every other skill leaves the choice to its selector.</summary>
    private static bool GroundFieldHits(JsonElement row, MonsterUnit m) =>
        Script(row) != "Sanctuary" || m.Template.Element == UndeadElement || m.Template.Race == DemonRace;

    /// <summary>Before a new Fire Wall: end the caster's oldest walls so at most "${8}" stand with the new one.</summary>
    private async Task LimitFieldsAsync(GameSession s, JsonElement row)
    {
        var desc = Numbers(row, "_kDescData");
        int limit = desc.Length > 7 && desc[7] > 0 ? (int)desc[7] : 1;
        var walls = s.SkillObjects.Where(u => !u.Removing && Script(u.Cast.Row) == Script(row)).OrderBy(u => u.Uid).ToList();
        for (int i = 0; i <= walls.Count - limit; i++) await RemoveSkillObjectAsync(walls[i]);
    }

    /// <summary>A field's knockback: the touched enemy slides away from the field's centre (along the cast's facing when
    /// it stands on the centre), with the show's displacement effect (Fire Wall 1201004).</summary>
    private async Task PushFromFieldAsync(GameSession s, ActiveSkillCast action, SkillObject field, MonsterUnit target)
    {
        float dx = target.X - field.X, dz = target.Z - field.Z;
        float length = MathF.Sqrt(dx * dx + dz * dz);
        if (length < 0.05f)
        {
            double radians = action.Request.Face * Math.PI / 180;
            (dx, dz, length) = ((float)Math.Sin(radians), (float)Math.Cos(radians), 1);
        }
        var start = target.Pos;
        var end = WalkableEnd(s.SceneEntry, start, dx / length * field.PushMeters, dz / length * field.PushMeters);
        var (effectId, ms) = MovementEffect(GameData.Int(action.Row, "_iScriptShow"));
        target.Moving = false;
        target.X = end.X; target.Y = end.Y; target.Z = end.Z;
        await _world.ToUnitViewersAsync(target, SkillMove, new SkillStartMoveNotify
        {
            EntityID = (ulong)target.Uid, StartPos = start, EndPos = end, SkillConfigId = action.Request.SkillId,
            EffectId = effectId, MoveType = 2, StartTimestampMs = Now, Face = target.Face, MoveTimeMs = ms > 0 ? ms : 300,
        });
        await _world.MoveUnitAsync(target, new MovementChangeNtf { Uid = target.Uid, MovementInfo = target.CurrentMovement(Now) });
    }

    /// <summary>Ganbantein: remove up to _kDescData[2] live skill fields within _kDescData[1] m of the chosen point,
    /// nearest first.</summary>
    private async Task ClearGroundAsync(GameSession s, ActiveSkillCast action)
    {
        var desc = Numbers(action.Row, "_kDescData");
        float radius = desc.Length > 1 && desc[1] > 0 ? (float)desc[1] : 3;
        int max = desc.Length > 2 && desc[2] > 0 ? (int)desc[2] : 15;
        var center = action.Request.TargetPos ?? action.Origin;
        var fields = _world.Map(s.WorldScene).Units.OfType<SkillObject>()
            .Where(u => !u.Removing && u.InstanceId == s.InstanceId && Distance(u.Pos, center) <= radius)
            .OrderBy(u => Distance(u.Pos, center)).ThenBy(u => u.Uid).Take(max).ToList();
        foreach (var field in fields) await RemoveSkillObjectAsync(field);
    }

    // ------------------------------------------------------------------ party support

    /// <summary>The Blacksmith class: Blacksmith 6200, Mastersmith 6300 and Mechanic 6400 (the Merchant 6100 before it
    /// is not, inferred from "If the target is a Blacksmith class").</summary>
    private static bool BlacksmithLine(GameSession p) => p.Progress?.Career is 6200 or 6300 or 6400;

    /// <summary>Hymn of the Lamb (Praefatio) "removing [Status Effect] and [Movement Restriction] effects": its
    /// _kLogicData [37, 33] are those BuffConfig categories (Stun, Frozen, Silence ... carry 37; Frozen and Sluggish 33).</summary>
    /// King's Grace removes "all Control effects from you" (category 25, the caster only).
    private static int[] CleansedCategories(JsonElement row, bool self) =>
        Script(row).StartsWith("Praefatio", StringComparison.Ordinal) ? Numbers(row, "_kLogicData").Select(v => (int)v).ToArray()
        : Script(row) == "KingsGrace" && self ? [ControlCategory] : [];

    /// <summary>A debuff (_iType 2) with any of these _kBuffCategory entries.</summary>
    private static bool HasCategory(JsonElement buff, int[] categories) =>
        GameData.Int(buff, "_iType") == 2 && Numbers(buff, "_kBuffCategory").Any(c => categories.Contains((int)c));
}
