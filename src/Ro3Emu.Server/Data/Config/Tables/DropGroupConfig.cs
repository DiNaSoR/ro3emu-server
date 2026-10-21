using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's DropGroupConfig (data/config/DropGroupConfig.json).</summary>
[ConfigTable("DropGroupConfig", Grouped = true)]
public readonly partial record struct DropGroupConfig(JsonElement Row)
{
    [ConfigField("_iActivityid")] public partial long Activityid { get; }
    [ConfigField("_iBind")] public partial long Bind { get; }
    [ConfigField("_iJobLimit")] public partial long[] JobLimit { get; }
    [ConfigField("_iMaxLevelLimit")] public partial long MaxLevelLimit { get; }
    [ConfigField("_iMaxNumber")] public partial long MaxNumber { get; }
    [ConfigField("_iMinLevelLimit")] public partial long MinLevelLimit { get; }
    [ConfigField("_iMinNumber")] public partial long MinNumber { get; }
    [ConfigField("_iQuestLimit")] public partial long QuestLimit { get; }
    [ConfigField("_iWeights")] public partial long Weights { get; }
    [ConfigField("_kCoefficient")] public partial long Coefficient { get; }
    [ConfigField("_kDropItem")] public partial long DropItem { get; }
    [ConfigField("_kIsBindGive")] public partial long[] IsBindGive { get; }
    [ConfigField("_kServerLevel")] public partial JsonElement? ServerLevel { get; }
}
