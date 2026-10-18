using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Tests;

/// <summary>The plain-text quest format (CustomContent.Quests.cs): whole NPCs and quest chains turned into rows.</summary>
public sealed class CustomContentQuestFormatTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3emu-quest-format-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private CustomContent Load(params string[] files)
    {
        Directory.CreateDirectory(_dir);
        for (int i = 0; i < files.Length; i++) File.WriteAllText(Path.Combine(_dir, $"{i}.json"), files[i]);
        return CustomContent.Load(_dir, NullLogger.Instance);
    }

    private const string Npc = """
        {"npcs": [{"id": 91000005, "name": "Mira", "model": 100079, "map": 10100, "x": 1, "z": 2, "face": 90,
                   "greeting": "Hello there."}]}
        """;

    private static JsonObject Obj(CustomContent c, string table, long id) => c.Rows[table][id.ToString()].AsObject();
    private static JsonArray Arr(CustomContent c, string table, long id) => c.Rows[table][id.ToString()].AsArray();
    private static (long, string) Tag(JsonObject task) =>
        (task["_iShowTag"]![0]!.GetValue<long>(), task["_iShowTag"]![1]!.GetValue<string>());
    private static string Text(CustomContent c, JsonNode? field) => c.Texts[field![0]!.GetValue<long>()];

    [Fact]
    public void NpcBecomesConfigInteractionDialogueAndPlacement()
    {
        var c = Load(Npc);
        Assert.Empty(c.Errors);
        var npc = Obj(c, "NPCConfig", 91000005);
        Assert.Equal(200014, npc["_base"]!.GetValue<long>());
        Assert.Equal("Mira", Text(c, npc["_kName"]));
        Assert.Equal("", Text(c, npc["_kTitle"]));
        Assert.Equal(100079, npc["_iUnitID"]!.GetValue<long>());
        Assert.Equal("temphead_npc_100079.png", npc["_kHeadIcon"]!.GetValue<string>());

        long greeting = CustomContent.NpcDialogBase + 5 * 100 + 1;
        Assert.Equal($$"""[["53","{{greeting}}"]]""", Obj(c, "NPCInteractConfig", 91000005)["_iEXECAction"]!.ToJsonString());
        var pages = Arr(c, "DialogueConfig", greeting);
        Assert.Equal("Hello there.", Text(c, pages[0]!["_iText"]));
        Assert.Equal("Goodbye", Text(c, pages[1]!["_iText"]));

        var placed = Assert.Single(c.Npcs);
        Assert.Equal((10100L, 91000005, 90f), (placed.Map, placed.NpcId, placed.Face));
    }

    [Fact]
    public void QuestChainBecomesLinkedTasksWithDialogueAndTargets()
    {
        var c = Load(Npc, """
            {"quests": [{"id": 91000010, "steps": [
              {"name": "Meet Mira", "description": "Find Mira.", "objective": "Talk to Mira", "talk": 91000005,
               "dialog": [{"say": "Hi!", "reply": "Hello"}, "Please defeat <color=#CC762A>3 Porings</color>."]},
              {"name": "Porings", "description": "Defeat 3 Porings.", "objective": "Defeat Porings",
               "kill": [5000001, 5000002], "count": 3, "solo": true, "award": 31105},
              {"name": "Done", "description": "Report back.", "objective": "Talk to Mira", "talk": 91000005,
               "dialog": ["Thanks!"], "exp": 500, "fields": {"_iShowOnMap": 0}}
            ]}]}
            """);
        Assert.Empty(c.Errors);

        var talk = Obj(c, "TaskConfig", 91000010);
        Assert.Equal(10101130, talk["_base"]!.GetValue<long>());
        Assert.Equal(2000008, talk["_iCondition"]!.GetValue<long>()); // first step, no "after": offered to everyone
        Assert.Equal("{}", talk["_kPreTask"]!.ToJsonString());
        long first = CustomContent.QuestDialogBase + 10 * 100 + 1;
        Assert.Equal($"[[104001,91000005,10100,{first}]]", talk["_kProgressTarget"]!.ToJsonString());
        Assert.Equal($$"""[["143","10100","91000005","9","{{first}}"]]""", talk["_kTrack"]!.ToJsonString());
        Assert.Equal(talk["_iTaskDescribe"]!.ToJsonString(), talk["_iBeforeDescribe"]!.ToJsonString()); // one text

        var page1 = Arr(c, "DialogueConfig", first);
        Assert.Equal("Hello", Text(c, page1[1]!["_iText"]));
        Assert.Equal($$"""[["7","{{first + 1}}"]]""", page1[1]!["_iEXECAction"]!.ToJsonString());
        var page2 = Arr(c, "DialogueConfig", first + 1).Single()!;
        Assert.Equal($$"""[["17","{{first}}"]]""", page2["_iEXECAction"]!.ToJsonString());
        // Colour tags move into the field's values, the way the client's strings carry them.
        Assert.Equal("Please defeat ^{1}3 Porings^{2}.", Text(c, page2["_iText"]));
        Assert.Equal("<color=#CC762A>", page2["_iText"]![1]!.GetValue<string>());

        var kill = Obj(c, "TaskConfig", 91000011);
        Assert.Equal(10101050, kill["_base"]!.GetValue<long>());
        Assert.False(kill.ContainsKey("_iCondition"));
        Assert.Equal("[91000010]", kill["_kPreTask"]!.ToJsonString());
        Assert.Equal("[[104002,5000001,5000002,3]]", kill["_kProgressTarget"]!.ToJsonString());
        Assert.Equal("Defeat Porings*", Text(c, kill["_iTargetDescribe"])); // * shows the 3/3 counter
        Assert.Equal("""[[""]]""", kill["_kTrack"]!.ToJsonString()); // no "map": nowhere to walk
        Assert.Equal(31105, kill["_iAward"]!.GetValue<long>());

        var report = Obj(c, "TaskConfig", 91000012);
        // No "type": a side quest, with the blue tag and the blue bubble over Mira.
        Assert.Equal(2, report["_iTaskType"]!.GetValue<int>());
        Assert.Equal((13150300008, "<color=#6890d2>"), Tag(report));
        Assert.Equal("task_icon_npc_blue02.png", report["_kNPCProgressMark"]!.GetValue<string>());
        Assert.Equal("[91000011]", report["_kPreTask"]!.ToJsonString());
        Assert.Equal("[1,500]", report["_kEXP"]!.ToJsonString());
        Assert.Equal(0, report["_iShowOnMap"]!.GetValue<int>());
    }

    [Fact]
    public void AfterLinksToAnotherQuestAndClientNpcsNeedAMap()
    {
        var c = Load("""
            {"quests": [{"id": 91000020, "after": 10101130, "steps": [
              {"name": "A", "description": "B", "objective": "C", "talk": 200014, "map": 10100, "dialog": ["Hi"]}]}]}
            """);
        Assert.Empty(c.Errors);
        var task = Obj(c, "TaskConfig", 91000020);
        Assert.Equal("[10101130]", task["_kPreTask"]!.ToJsonString());
        Assert.False(task.ContainsKey("_iCondition"));
    }

    [Theory]
    [InlineData("main", 1, 13150300005, "#ec8c2f", "task_icon_npc_yellew02.png")]
    [InlineData("Side", 2, 13150300008, "#6890d2", "task_icon_npc_blue02.png")]
    [InlineData("guide", 3, 13150300010, "#62af51", "task_icon_npc_green02.png")]
    public void QuestTypeSetsTheTagAndTheNpcBubble(string type, int taskType, long tag, string colour, string mark)
    {
        var c = Load(Npc, $$"""
            {"quests": [{"id": 91000050, "type": "{{type}}", "steps": [
              {"name": "A", "description": "B", "objective": "C", "talk": 91000005, "dialog": ["Hi"]},
              {"name": "D", "description": "E", "objective": "F", "kill": 5000001, "count": 1}
            ]}]}
            """);
        Assert.Empty(c.Errors);
        foreach (long id in new[] { 91000050L, 91000051L })
        {
            var task = Obj(c, "TaskConfig", id);
            Assert.Equal(taskType, task["_iTaskType"]!.GetValue<int>());
            Assert.Equal((tag, $"<color={colour}>"), Tag(task));
            Assert.Equal(mark, task["_kNPCProgressMark"]!.GetValue<string>());
        }
    }

    [Fact]
    public void UnknownQuestTypeIsSkippedWithAReason()
    {
        var c = Load("""
            {"quests": [{"id": 91000060, "type": "daily", "steps": [{"name": "A", "description": "B", "objective": "C", "kill": 5000001, "count": 1}]}]}
            """);
        Assert.Contains("\"main\", \"side\" or \"guide\"", Assert.Single(c.Errors));
    }

    [Theory]
    [InlineData("""{"name": "A", "description": "B", "objective": "C"}""", "either \"talk\" or \"kill\"")]
    [InlineData("""{"name": "A", "description": "B", "objective": "C", "talk": 200014, "dialog": ["Hi"]}""", "needs a \"map\"")]
    [InlineData("""{"name": "A", "description": "B", "objective": "C", "talk": 200014, "map": 1, "dialog": [{"say": "x", "reply": "y"}]}""", "last dialog page")]
    [InlineData("""{"name": "A", "objective": "C", "kill": 5000001, "count": 5}""", "\"description\"")]
    [InlineData("""{"name": "A", "description": "B", "objective": "C", "kill": 5000001}""", "\"count\"")]
    public void BrokenQuestIsSkippedWithAReason(string step, string reason)
    {
        var c = Load($$"""{"quests": [{"id": 91000030, "steps": [{{step}}]}]}""", """
            {"quests": [{"id": 91000040, "steps": [{"name": "A", "description": "B", "objective": "C", "kill": 5000001, "count": 1}]}]}
            """);
        Assert.Contains(reason, Assert.Single(c.Errors));
        Assert.False(c.Rows["TaskConfig"].ContainsKey("91000030"));
        Assert.True(c.Rows["TaskConfig"].ContainsKey("91000040")); // the other quest still loads
    }

    [Fact]
    public void IdsEndingInTheSameSixDigitsClash()
    {
        var c = Load("""
            {"quests": [
              {"id": 91000050, "steps": [{"name": "A", "description": "B", "objective": "C", "kill": 1, "count": 1}]},
              {"id": 92000050, "steps": [{"name": "A", "description": "B", "objective": "C", "kill": 1, "count": 1}]}]}
            """);
        Assert.Contains("clash", Assert.Single(c.Errors));
    }

    [ClientDataFact]
    public void KillStepWithAMapWalksToTheSpawnAndTheCheckFindsBadIds()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "q.json"), """
            {"npcs": [{"id": 91000060, "name": "Broken", "model": 999999999, "greeting": "Hi"}],
             "quests": [{"id": 91000060, "steps": [
              {"name": "A", "description": "B", "objective": "C", "kill": 5000001, "count": 5, "map": 10111},
              {"name": "A", "description": "B", "objective": "C", "kill": 999999999, "count": 5, "award": 999999999}]}]}
            """);
        var data = new GameData(Options.Create(new ServerOptions
        {
            GameDataDir = Path.Combine(ClientData.RepoRoot, "data"), CustomContentDir = _dir,
        }), NullLogger<GameData>.Instance);
        // The South Gate region next to the Poring spawn, as the client's own Poring quest (10101050) tracks.
        Assert.Equal("""[["142","10111","1010105001","-1"]]""", data.Row("TaskConfig", 91000060)!.Value.GetProperty("_kTrack").GetRawText());

        var problems = CustomContentCheck.Problems(data);
        Assert.Contains(problems, p => p.Contains("NPCConfig 91000060: model 999999999 is not in UnitConfig"));
        Assert.Contains(problems, p => p.Contains("TaskConfig 91000061: monster 999999999 is not in NPCConfig"));
        Assert.Contains(problems, p => p.Contains("TaskConfig 91000061: reward 999999999 is not in AwardConfig"));
        Assert.Equal(3, problems.Count);
    }

    [ClientDataFact]
    public void ShippedContentHasNoProblems()
    {
        var data = new GameData(Options.Create(new ServerOptions
        {
            GameDataDir = Path.Combine(ClientData.RepoRoot, "data"), CustomContentDir = Path.Combine(ClientData.RepoRoot, "content"),
        }), NullLogger<GameData>.Instance);
        Assert.Empty(CustomContentCheck.Problems(data));
        Assert.Equal("""[["142","10111","1010105001","-1"]]""", data.Row("TaskConfig", 90000002)!.Value.GetProperty("_kTrack").GetRawText());
    }
}
