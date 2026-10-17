using Microsoft.Extensions.Logging.Abstractions;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// Exported files keep the client's letter case (MapLinkData.json, sc_pm_pveSecretLair_001_sceneeditor.json) while the
/// server names them in another. On a Linux host an exact lookup missed them: every Kafra teleport was refused.
/// </summary>
public sealed class DataFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3-datafiles-" + Guid.NewGuid().ToString("N"));
    private string SceneEditor => Path.Combine(_dir, "sceneeditor");

    public DataFilesTests() => Directory.CreateDirectory(SceneEditor);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void FindsAFileWhoseNameDiffersOnlyInCase()
    {
        var exported = Path.Combine(SceneEditor, "sc_pm_pveSecretLair_001_sceneeditor.json");
        File.WriteAllText(exported, "{}");

        Assert.Equal(exported, DataFiles.Find(SceneEditor, "sc_pm_pveSecretLair_001_sceneeditor.json"));
        Assert.Equal(exported, DataFiles.Find(SceneEditor, "sc_pm_pvesecretlair_001_sceneeditor.json"), ignoreCase: true);
        Assert.Null(DataFiles.Find(SceneEditor, "sc_pm_pveSecretLair_002_sceneeditor.json"));
        Assert.Null(DataFiles.Find(Path.Combine(_dir, "missing"), "MapLinkData.json"));
    }

    [Fact]
    public void KafraDestinationsComeFromMapLinkDataInTheExportsCase()
    {
        File.WriteAllText(Path.Combine(SceneEditor, "MapLinkData.json"), """{ "Capra": { "10101": {}, "10111": {} } }""");
        var options = Microsoft.Extensions.Options.Options.Create(new ServerOptions { DataDir = _dir });
        var world = new WorldData(options, new GameData(options, NullLogger<GameData>.Instance), NullLogger<WorldData>.Instance);

        Assert.True(world.IsKafraDestination(10111));
        Assert.True(world.IsKafraDestination(10101));
        Assert.False(world.IsKafraDestination(10100));
    }
}
