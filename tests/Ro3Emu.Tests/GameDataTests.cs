using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Tests;

/// <summary>Client config and skill JSON exports exported from the client</summary>
public sealed class GameDataTests
{
    private static readonly GameData Data = new(
        Options.Create(new ServerOptions { DataDir = Path.Combine(RepoRoot(), "data") }), NullLogger<GameData>.Instance);

    [ClientDataFact]
    public void ConfigRowsRetainAttributePairsAndLocalizedNames()
    {
        var novice = Data.Row("JobConfig", 1000);
        Assert.NotNull(novice);
        Assert.Equal(JsonValueKind.Array, novice.Value.GetProperty("_kAttri").ValueKind);
        Assert.Equal(new[] { (115L, 100d), (113L, 700d) }, GameData.Pairs(novice.Value, "_kAttri"));
        Assert.Equal(700, Data.JobAttributes(1000)[113]);
        Assert.Equal("Novice", Data.TextOf(novice.Value, "_iName"));
        Assert.Equal("Zeny", Data.ItemName(1));
        Assert.Equal("Prontera South Gate", Data.MapName(10111));
        Assert.Equal(1, Data.FindItems("Zeny").First().Id);
        // [textId, params...]: @{n} takes the n-th parameter (gathering node names).
        Assert.Equal("Lv. 1 Ore Vein", Data.TextOf(Data.Row("NPCConfig", 7010001)!.Value, "_kName"));
    }

    [ClientDataFact]
    public void SkillRowsRemainValidAfterFileParseAndConcurrentAccess()
    {
        var rows = Enumerable.Range(0, 12).AsParallel().Select(_ => Data.SkillTemplate(10000)).ToArray();
        Assert.All(rows, row =>
        {
            Assert.NotNull(row);
            Assert.Equal(1, row.Value.GetProperty("m_fTotalTime").GetDouble());
            Assert.Equal(0.2, GameData.Number(row.Value.GetProperty("m_kAtkPoints")[0]));
        });
        Assert.True(Data.SkillTemplateTable("Lua_Config_EffectData").ContainsKey("1000002"));
        Assert.Null(Data.SkillTemplate(-1));
        Assert.Null(Data.Row("NotAnExport", 1));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
