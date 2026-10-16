using Ro3Emu.ClientTools;
using Ro3Emu.DataExport;

namespace Ro3Emu.Setup.Tests;

public class DataExportTests
{
    // Reference values from tools/WorldData/vfsget.js (lowerHash), covering every tail length class.
    [Theory]
    [InlineData("", 0UL)]
    [InlineData("a", 9607679276477937801UL)]
    [InlineData("abcdefgh", 14738604482492337154UL)]
    [InlineData("abcdefghi", 380484692874131812UL)]
    [InlineData("ABCDEFGHIJKLMNO", 9997468155419308027UL)]
    [InlineData("abcdefghijklmnop", 14180213048082216739UL)]
    [InlineData("abcdefghijklmnopq", 8459014091212432983UL)]
    [InlineData("sc_prt_fild08_001_dynamicmapdata.bytes", 7007743005348290919UL)]
    [InlineData("Sc_Prontera_001data.bytes", 4666445406827049485UL)]
    public void VfsHashMatchesTheJavaScriptExport(string name, ulong expected) => Assert.Equal(expected, Vfs.LowerHash(name));

    [Theory]
    [InlineData("sc_prt_fild08_001data.bytes", "sc_prt_fild08_001_dynamicmapdata.bytes")]
    [InlineData("sc_x_dynamicmapdata.bytes", "sc_x_dynamicmapdata.bytes")]
    [InlineData("a data.bytes data.bytes", "a _dynamicmapdata.bytes data.bytes")]
    public void NavFileNameReplacesTheFirstSuffix(string navName, string file) => Assert.Equal(file, NavExporter.FileName(navName));

    [Fact]
    public void NavNamesAreDistinctInTableOrder()
    {
        var dir = Directory.CreateTempSubdirectory("ro3emu-nav-");
        try
        {
            var json = Path.Combine(dir.FullName, "MapResource.json");
            File.WriteAllText(json, """{"1":{"_kNavDataName":"b data.bytes"},"2":{"_kNavDataName":"a data.bytes"},"3":{"_kNavDataName":"b data.bytes"},"4":{"_kNavDataName":""},"5":{}}""");
            Assert.Equal(["b data.bytes", "a data.bytes"], NavExporter.NavNames(json));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void LocMapTakesTheClientsMostFrequentEnglishThenOurTranslations()
    {
        var dir = Directory.CreateTempSubdirectory("ro3emu-loc-");
        try
        {
            var zh = Path.Combine(dir.FullName, "zh.json");
            var en = Path.Combine(dir.FullName, "en.json");
            // Keys out of numeric order: the first-seen tie break follows Object.entries (numeric keys ascending).
            File.WriteAllText(zh, """{"10":{"_kDes":"确定"},"2":{"_kDes":"确定"},"3":{"_kDes":"确定"},"4":{"_kDes":"取消"},"5":{"_kDes":"英文"},"6":{"_kDes":"无"}}""");
            File.WriteAllText(en, """{"10":{"_kDes":"OK"},"2":{"_kDes":"Confirm"},"3":{"_kDes":"OK"},"4":{"_kDes":"Cancel"},"5":{"_kDes":"中文"},"6":{"_kDes":"None"}}""");
            var map = new LocMapBuilder();
            map.AddClientTables(zh, en);
            map.AddTranslations(["# comment\n取消\tBack\n新\\t行\tNew\\nline\n坏\t坏\n"]);
            var outFile = Path.Combine(dir.FullName, "loc_en.tsv");
            map.Write(outFile);
            var lines = File.ReadAllLines(outFile);
            Assert.StartsWith(ClientPatcher.GeneratedLocHeader, lines[0]);
            Assert.Equal(["确定\tOK", "取消\tBack", "新\\t行\tNew\\nline"], lines[1..]);
            Assert.Equal((2, 2), (map.FromTables, map.Ours));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void ReferenceCheckFindsWhatAnAssemblyNeedsAndLacks()
    {
        var own = typeof(Vfs).Assembly.Location;
        var alone = Directory.CreateTempSubdirectory("ro3emu-refs-");
        try
        {
            // Without KeraLua.dll next to it (or in the folder searched), every KeraLua type and member it uses is reported.
            var copy = Path.Combine(alone.FullName, Path.GetFileName(own));
            File.Copy(own, copy);
            var missing = ClientPatcher.UnresolvedReferences(copy, alone.FullName);
            Assert.Contains(missing, r => r.Contains("KeraLua.Lua") && r.Contains("assembly KeraLua"));
            Assert.Empty(ClientPatcher.UnresolvedReferences(own, Path.GetDirectoryName(typeof(object).Assembly.Location)!));
        }
        finally { alone.Delete(true); }
    }
}
