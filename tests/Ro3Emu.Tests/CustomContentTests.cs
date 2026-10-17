using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Tests;

/// <summary>content/*.json overlaid on the client's tables (CustomContent) and the payload the client hook gets.</summary>
public sealed class CustomContentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ro3emu-custom-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private GameData Data(string content)
    {
        Directory.CreateDirectory(Path.Combine(_root, "data", "config"));
        Directory.CreateDirectory(Path.Combine(_root, "content"));
        File.WriteAllText(Path.Combine(_root, "data", "config", "TaskConfig.json"), """
            {"100": {"_iTaskID": 100, "_iTaskRank": 100, "_iName": [1], "_iTaskType": 1, "_kPreTask": [99], "_kEXP": {}}}
            """);
        File.WriteAllText(Path.Combine(_root, "data", "config", "Localization_en.json"), """{"1": {"_iID": 1, "_kDes": "Old"}}""");
        File.WriteAllText(Path.Combine(_root, "content", "quest.json"), content);
        return new GameData(Options.Create(new ServerOptions
        {
            DataDir = Path.Combine(_root, "data"),
            CustomContentDir = Path.Combine(_root, "content"),
        }), NullLogger<GameData>.Instance);
    }

    [Fact]
    public void BaseRowIsCopiedWithTheNewIdAndOverrides()
    {
        var data = Data("""
            {
              // comments are allowed
              "texts": {"500": "Custom quest"},
              "rows": {"TaskConfig": {"200": {"_base": 100, "_iName": [500], "_iTaskType": 2, "_kPreTask": {}}}}
            }
            """);
        var row = data.Row("TaskConfig", 200)!.Value;
        Assert.Equal(200, row.GetProperty("_iTaskID").GetInt64());
        Assert.Equal(100, row.GetProperty("_iTaskRank").GetInt64()); // only _i*ID fields follow the id
        Assert.Equal(2, row.GetProperty("_iTaskType").GetInt64());
        Assert.Equal(JsonValueKind.Object, row.GetProperty("_kPreTask").ValueKind);
        Assert.Equal("Custom quest", data.TextOf(row, "_iName"));
        Assert.Equal("Old", data.Text(1));
        Assert.NotNull(data.Row("TaskConfig", 100)); // the base stays
    }

    [Fact]
    public void ArrayRowsAndPreloadWork()
    {
        var data = Data("""
            {"rows": {"DialogueConfig": {"7": [{"_iID": 7, "_iText": [1]}, {"_iID": 7, "_iSortID": 2}]}},
             "npcs": [{"map": 10100, "npc": 9, "x": 1.5, "z": 2}]}
            """);
        data.Preload();
        var rows = data.Table("DialogueConfig")["7"];
        Assert.Equal(2, rows.GetArrayLength());
        var npc = Assert.Single(data.Custom.Npcs);
        Assert.Equal((10100L, 9, 1.5f, (float?)null), (npc.Map, npc.NpcId, npc.X, npc.Y));
    }

    [Fact]
    public void ClientPayloadHoldsResolvedRowsAndTexts()
    {
        var data = Data("""
            {"texts": {"500": "Hi"}, "rows": {"TaskConfig": {"200": {"_base": 100}}}}
            """);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(data.Custom.ClientPayload(data.Table)));
        Assert.Equal("Hi", doc.RootElement.GetProperty("texts").GetProperty("500").GetString());
        var row = doc.RootElement.GetProperty("rows").GetProperty("TaskConfig").GetProperty("200");
        Assert.Equal(200, row.GetProperty("_iTaskID").GetInt64());
        Assert.Equal(1, row.GetProperty("_iTaskType").GetInt64());
    }

    [Fact]
    public void BrokenFileOrRowIsSkipped()
    {
        var data = Data("""{"rows": {"TaskConfig": {"200": {"_base": 12345}}}}""");
        Assert.Null(data.Row("TaskConfig", 200));
        Assert.NotNull(data.Row("TaskConfig", 100));
    }

    [Fact]
    public void ShippedContentFilesParse()
    {
        var dir = Path.Combine(ClientData.RepoRoot, "content");
        foreach (var file in Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories) : [])
        {
            var loaded = CustomContent.Load(Path.GetDirectoryName(file)!, NullLogger.Instance);
            Assert.False(loaded.IsEmpty, file);
        }
    }

    [ClientDataFact]
    public void PrototypeQuestResolvesAgainstTheClientData()
    {
        var options = Options.Create(new ServerOptions
        {
            GameDataDir = Path.Combine(ClientData.RepoRoot, "data"),
            CustomContentDir = Path.Combine(ClientData.RepoRoot, "content"),
        });
        var data = new GameData(options, NullLogger<GameData>.Instance);
        var npc = data.Row("NPCConfig", 90000001)!.Value;
        Assert.Equal("Elias", data.TextOf(npc, "_kName"));
        Assert.Equal(90000001, npc.GetProperty("_iID").GetInt64());
        Assert.Equal(100079, npc.GetProperty("_iUnitID").GetInt64());
        Assert.Equal(90000001, data.Row("NPCInteractConfig", 90000001)!.Value.GetProperty("_iInterID").GetInt64());
        foreach (long task in new long[] { 90000001, 90000002, 90000003 })
            Assert.Equal(task, data.Row("TaskConfig", task)!.Value.GetProperty("_iTaskID").GetInt64());
        Assert.Equal("A Scholar's Request", data.TextOf(data.Row("TaskConfig", 90000001)!.Value, "_iName"));

        var world = new Ro3Emu.Server.Maps.WorldData(options, data, NullLogger<Ro3Emu.Server.Maps.WorldData>.Instance);
        var placed = Assert.Single(world.Map(10100)!.Npcs, n => n.NpcId == 90000001);
        Assert.Equal("Elias", placed.Name);
        Assert.Same(placed, world.Npc(placed.Uid));
        Assert.InRange(placed.Y, -2f, 3f); // on Prontera's ground
    }
}
