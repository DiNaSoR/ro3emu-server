using System.Text.Json;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    private int ChantMs(GameSession s, JsonElement row) => ChantMilliseconds(_data, row, _character.Stats(s));

    /// <summary>LC_DAL_PlayerInfo.GetRealPropertyValue(PreVarChant): per_varchant - A*cspd/(B+C*cspd).
    /// AttributeConfig 201..204 names additive/percentage variable/fixed CT; PublicParamConfig supplies A/B/C.
    /// (inferred) Apply the displayed variable factor to the configured variable time, fixed separately, floor ms.
    /// A negative factor cannot schedule a hit before acceptance. Absent formula inputs preserve the base timing.</summary>
    internal static int ChantMilliseconds(GameData data, JsonElement row, StatSheet stats)
    {
        const int CastSpeed = 116, Variable = 201, VariablePercent = 202, Fixed = 203, FixedPercent = 204;
        var parameters = data.Table("PublicParamConfig");
        double Parameter(string key) => parameters.TryGetValue(key, out var value) ? GameData.Number(value) ?? 0 : 0;
        double speed = Math.Max(0, stats[CastSpeed]);
        if (parameters.TryGetValue("FinalAtkSpdThdAndCastSpdThd", out var limits) && limits.ValueKind == JsonValueKind.Array
            && limits.GetArrayLength() > 1) speed = Math.Min(speed, GameData.Number(limits[1]) ?? speed);
        double denominator = Parameter("VariableChantPerParamB") + Parameter("VariableChantPerParamC") * speed;
        double reduction = denominator > 0 ? Parameter("VariableChantPerParamA") * speed / denominator : 0;
        double variable = Math.Max(0, GameData.Int(row, "_iChantVar") + stats[Variable])
            * Math.Max(0, 1 + stats[VariablePercent] / 10000.0 - reduction);
        double fixedTime = Math.Max(0, GameData.Int(row, "_iChantFix") + stats[Fixed])
            * Math.Max(0, 1 + stats[FixedPercent] / 10000.0);
        return (int)Math.Clamp(Math.Floor(variable + fixedTime), 0, int.MaxValue);
    }
}
