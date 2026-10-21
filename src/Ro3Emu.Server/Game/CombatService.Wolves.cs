using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The Ranger's wolf skills that pick their form or wait for the player:
/// <list type="bullet">
/// <item>Warg Hunt 13323 names its two forms in _kLogicData [melee row, ranged row] (Warg Hunt (Melee) 13329
/// "WolvesShadowNear", (Ranged) 13330 "WolvesShadowFar", each with the main row's damage). "If you are more than ${5}m
/// from the target, you also dash in front of it": within that reach the melee form lands; beyond it the ranged form,
/// whose show carries the dash (13330: 1333014, 133301: 13330113), moves the Ranger in front of the target first. The
/// form's hit replaces the main row's (inferred: the same damage, shown once). The client has no logic for these rows.</item>
/// <item>Swift Warg 13326: "After casting 【Warg Charge】 or 【Warg Hunt】, your next movement instead casts this skill in
/// the movement direction." The cooldown reset was already there; now the cast arms it, and the next step the player
/// takes casts Swift Warg facing the way they walk. Lv1 gets this only "When this skill reaches Lv. @{1}" (inferred:
/// armed for <see cref="SwiftWargArmMs"/>, and only while it could be cast, so it never shows a cooldown or mount
/// error).</item>
/// </list>
/// </summary>
public sealed partial class CombatService
{
    /// <summary>(inferred) How long a Warg Charge or Warg Hunt keeps Swift Warg on the next movement.</summary>
    internal const int SwiftWargArmMs = 3000;

    /// <summary>Warg Hunt's melee or ranged form in place of its own hit; false when the row has no forms.</summary>
    private async Task<bool> WargHuntAsync(GameSession s, ActiveSkillCast action)
    {
        var row = action.Row;
        if (action.Proc || GameData.Int(row, "_iSkillID") != WargHuntGroup || Numbers(row, "_kLogicData") is not [var nearId, var farId, ..]
            || FindMonster(s.WorldScene, action.Request.TargetId) is not { } target || target.Dead || target.Hp <= 0) return false;
        double reach = Numbers(row, "_kDescData").ElementAtOrDefault(4);
        if (reach <= 0) reach = 5;
        bool far = Distance(s.Position, target.Pos) > reach;
        if (_data.Row("SkillConfig", (long)(far ? farId : nearId)) is not { } form) return false;
        if (far)
        {
            var request = action.Request.Clone();
            request.SkillId = (int)GameData.Int(form, "_iID");
            await MoveCasterAsync(s, form, request, (int)GameData.Int(form, "_iActionTime"));
            if (!CurrentCastMap(s, action)) return true;
        }
        await ProcCastAsync(s, form, target);
        return true;
    }

    /// <summary>After Warg Charge or Warg Hunt: the next movement casts Swift Warg at its top level. Lv1 says "When this
    /// skill reaches Lv. @{1}, ..." and the Lv2 row (_iMaxLevel 2) states it as its own effect (inferred: @{1} is 2).</summary>
    private void ArmSwiftWarg(GameSession s)
    {
        if (s.Progress == null || _skills.Level(s.Progress, SwiftWargGroup) is not (var level and > 0)
            || _data.Row("SkillConfig", _skills.RealId(SwiftWargGroup, level)) is not { } row
            || level < GameData.Int(row, "_iMaxLevel")) return;
        s.SwiftWargArmedMs = Now + SwiftWargArmMs;
    }

    /// <summary>On a step: an armed Swift Warg casts once the current cast is over.</summary>
    private void SwiftWargOnMove(GameSession s)
    {
        if (s.SwiftWargArmedMs == 0 || !s.Walking) return;
        if (Now > s.SwiftWargArmedMs || s.Dead || s.Progress == null) { s.SwiftWargArmedMs = 0; return; }
        if (s.SkillCast is { Cancelled: false } live && Now < live.StartMs + live.ChantMs + live.ActionMs) return;
        s.SwiftWargArmedMs = 0;
        int level = _skills.Level(s.Progress, SwiftWargGroup);
        if (level <= 0 || _data.Row("SkillConfig", _skills.RealId(SwiftWargGroup, level)) is not { } row
            || !SkillConditionMet(s, row) || s.SkillCds.GetValueOrDefault(SwiftWargGroup) > Now) return;
        var request = new CastSkillReq
        {
            SkillId = (int)GameData.Int(row, "_iID"), Face = s.Face, TargetPos = s.Position.Clone(), IsAutoCast = true,
        };
        _loop.TrackRuntimeDetached("swift warg", () => CastAsync(s, request));
    }
}
