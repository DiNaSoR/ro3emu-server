using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's AwardConfig (data/config/AwardConfig.json).</summary>
[ConfigTable("AwardConfig", Grouped = true)]
public readonly partial record struct AwardConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_kPreview", Optional = true)] public partial long[][] Preview { get; }
    [ConfigField("_kAward", Optional = true)] public partial long[][] Award { get; }
    [ConfigField("_iDropPool", Optional = true)] public partial long DropPool { get; }
    [ConfigField("_kCondition", Optional = true)] public partial long[][] Condition { get; }
    [ConfigField("_kEXP", Optional = true)] public partial long[] Exp { get; }
    [ConfigField("_kServerOpenDay", Optional = true)] public partial long[] ServerOpenDay { get; }
}
