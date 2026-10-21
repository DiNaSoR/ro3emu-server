using System.Text.Json;
using System.Text.Json.Nodes;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    private static bool IsGuardianEffect(JsonElement row) => GameData.Int(row, "_iSystemType") == GuardianPetRules.EffectType
        || row.TryGetProperty("_ro3GuardianProc", out _);

    /// <summary>Guardian monster/player selectors share range, height, face and targeted center. The rectangle
    /// interpretation for Eve's final throw is inferred from its "ahead" description; other shapes retain shared combat rules.</summary>
    private static bool GuardianAreaContains(JsonElement row, Vec3 origin, Vec3 requested, float face, Vec3 point)
    {
        int release = (int)GameData.Int(row, "_iRelease");
        Vec3 center = release is 1 or 7 or 8 ? requested : origin;
        float height = GameData.Int(row, "_iAreaHeightRange") / 100f;
        if (height > 0 && MathF.Abs(point.Y - center.Y) > height) return false;
        var range = Numbers(row, "_kRange");
        double radius = range.FirstOrDefault(300) / 100;
        double dx = point.X - center.X, dz = point.Z - center.Z, radians = face * Math.PI / 180;
        double forward = dx * Math.Sin(radians) + dz * Math.Cos(radians), side = dx * Math.Cos(radians) - dz * Math.Sin(radians);
        if (row.TryGetProperty("_ro3GuardianForwardRectangle", out _))
            return Math.Abs(side) <= radius / 2 && forward >= 0 && forward <= range.ElementAtOrDefault(1) / 100;
        if (release is 3 or 5 or 6 or 7)
            return Math.Abs(side) <= radius / 2 && Math.Abs(forward) <= (range.Length > 1 ? range[1] / 200 : radius);
        return Distance(center, point) <= radius + .1 && (release != 4
            || Math.Atan2(Math.Abs(side), forward) <= (range.Length > 1 ? range[1] : 90) * Math.PI / 360);
    }

    private GameSession[] GuardianPlayerTargets(GameSession source, ActiveSkillCast cast, Vec3 center) => [.. _sessions.All()
        .Where(p => CanPlayerHit(source, p) && GuardianAreaContains(cast.Row, cast.Origin, center, cast.Request.Face, p.Position))];

    private long GuardianOwlTarget(GameSession source, GuardianPetRules.Skill pet, ActiveSkillCast cast)
    {
        var desc = Numbers(pet.Active, "_kDescData");
        if (desc.Length < 4) return 0;
        Vec3 center = FindMonster(source.WorldScene, cast.Request.TargetId)?.Pos
            ?? EventTarget(source, cast.Request.TargetId)?.Position ?? cast.Request.TargetPos ?? cast.Origin;
        return GuardianRandomEnemy(source, pet, cast, center, desc[3]);
    }

    private long GuardianRandomEnemy(GameSession source, GuardianPetRules.Skill pet, ActiveSkillCast cast, Vec3 center, double radius)
    {
        var node = GuardianPetRules.CopyObject(pet.Effect);
        node["_iRangeType"] = 2; node["_iRelease"] = 8; node["_iTargetMax"] = int.MaxValue;
        node["_kRange"] = new JsonArray(radius * 100);
        var area = GuardianProcAction(source, pet, cast, JsonSerializer.SerializeToElement(node));
        var targets = SelectEnemies(source, area, center).Select(m => m.Uid)
            .Concat(GuardianPlayerTargets(source, area, center).Select(p => p.Role!.Id)).Order().ToArray();
        return targets.Length > 0 ? targets[Rolls.Proc(targets.Length)] : 0;
    }
}
