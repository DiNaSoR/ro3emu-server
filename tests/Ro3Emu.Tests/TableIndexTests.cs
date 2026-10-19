using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Tests;

/// <summary>
/// GameData.Index: lookups by a field other than the row id, without a scan of the table. It must answer what the
/// scans it replaces answered (the first row in table order), and follow tables that tests change.
/// </summary>
public sealed class TableIndexTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3-tableindex-" + Guid.NewGuid().ToString("N"));
    private readonly GameData _data;

    public TableIndexTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "config"));
        File.WriteAllText(Path.Combine(_dir, "config", "SkillConfig.json"), """
            {
              "1600101": { "_iID": 1600101, "_iSkillID": 16001, "_iLevel": 1, "_kSpSkillParam": [2, 100] },
              "1600102": { "_iID": 1600102, "_iSkillID": 16001, "_iLevel": 2, "_kSpSkillParam": [2, 200] },
              "1600199": { "_iID": 1600199, "_iSkillID": 16001, "_iLevel": 2, "_iMode": 1 },
              "1600501": { "_iID": 1600501, "_iSkillID": 16005, "_iLevel": 1 }
            }
            """);
        _data = new GameData(Options.Create(new ServerOptions { DataDir = _dir, CustomContentDir = Path.Combine(_dir, "content") }),
            NullLogger<GameData>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static long Id(JsonElement? row) => row is { } r ? GameData.Int(r, "_iID") : 0;

    [Fact]
    public void FindsTheFirstRowInTableOrderLikeAScan()
    {
        var scan = _data.Table("SkillConfig").Values
            .FirstOrDefault(r => GameData.Int(r, "_iSkillID") == 16001 && GameData.Int(r, "_iLevel") == 2);

        Assert.Equal(GameData.Int(scan, "_iID"), Id(_data.SkillLevelRow(16001, 2)));
        Assert.Equal(1600102, Id(_data.SkillLevelRow(16001, 2)));
        Assert.Null(_data.SkillLevelRow(16001, 3));

        var bySkill = _data.Index("SkillConfig", "_iSkillID", r => GameData.Int(r, "_iSkillID"));
        Assert.Equal([1600101, 1600102, 1600199], bySkill.All(16001).Select(r => GameData.Int(r, "_iID")));
        Assert.True(bySkill.Contains(16005));
        Assert.False(bySkill.Contains(16009));
        Assert.Same(bySkill, _data.Index("SkillConfig", "_iSkillID", r => GameData.Int(r, "_iSkillID")));
    }

    [Fact]
    public void FollowsRowsAddedOrReplacedAfterTheIndexWasBuilt()
    {
        Assert.Null(_data.SkillLevelRow(16005, 2));
        var table = (Dictionary<string, JsonElement>)_data.Table("SkillConfig");

        table["1600502"] = JsonSerializer.SerializeToElement(new { _iID = 1600502, _iSkillID = 16005, _iLevel = 2 });
        Assert.Equal(1600502, Id(_data.SkillLevelRow(16005, 2)));

        table["1600102"] = JsonSerializer.SerializeToElement(new { _iID = 1600102, _iSkillID = 16001, _iLevel = 2, _kSpSkillParam = new[] { 2, 250 } });
        Assert.Equal(250, _data.SkillLevelRow(16001, 2)!.Value.GetProperty("_kSpSkillParam")[1].GetInt32());
    }
}
