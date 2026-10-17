using Ro3Emu.Server.Data;

namespace Ro3Emu.Tests;

public class DataFileTests
{
    [Fact]
    public void FindsExportedFilesWhateverTheCaseOfTheName()
    {
        string dir = Directory.CreateTempSubdirectory("ro3emu-datafile").FullName;
        try
        {
            string sceneEditor = Path.Combine(dir, "sceneeditor");
            Directory.CreateDirectory(sceneEditor);
            File.WriteAllText(Path.Combine(sceneEditor, "MapLinkData.json"), "{}");
            File.WriteAllText(Path.Combine(sceneEditor, "sc_pm_pveSecretLair_001_sceneeditor.json"), "{}");

            // The export keeps the client's names; callers ask in lower case (or the config's own case).
            Assert.True(File.Exists(DataFile.Resolve(sceneEditor, "maplinkdata.json")));
            Assert.True(File.Exists(DataFile.Resolve(dir, "sceneeditor", "sc_pm_pvesecretlair_001_sceneeditor.json")));

            // A file exported after the folder was first looked at is still found.
            File.WriteAllText(Path.Combine(sceneEditor, "Sc_Late_001_sceneeditor.json"), "{}");
            Assert.True(File.Exists(DataFile.Resolve(sceneEditor, "sc_late_001_sceneeditor.json")));

            // A missing file comes back as the plain path, so callers' File.Exists checks keep working.
            Assert.Equal(Path.Combine(sceneEditor, "nope.json"), DataFile.Resolve(sceneEditor, "nope.json"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
