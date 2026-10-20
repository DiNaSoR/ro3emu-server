using Ro3Emu.Desktop;

namespace Ro3Emu.Setup.Tests;

public class DesktopTests
{
    [Theory]
    [InlineData("203.0.113.10")]
    [InlineData("203.0.113.10:6000")]
    [InlineData("https://play.example.com:443")]
    [InlineData("[2001:db8::1]")]
    [InlineData("ftp://x")]
    [InlineData("")]
    public void ServerAddressesMatchRo3Emu(string address) => Assert.Equal(Flows.ServerUrl(address), Engine.ServerUrl(address));

    [Fact]
    public void JoinPassesNoPasswordOnTheCommandLine()
    {
        Assert.Equal(["join", "10.0.0.2", "--yes", "--account", "alice", "--client", @"D:\ROO_PC"],
            Engine.JoinArguments(" 10.0.0.2 ", "alice", @"D:\ROO_PC"));
        Assert.Equal(["join", "10.0.0.2", "--yes"], Engine.JoinArguments("10.0.0.2", null, ""));
        // RO3Emu.exe must accept what the launcher passes.
        var o = Options.Parse([.. Engine.JoinArguments("10.0.0.2", "alice", @"D:\ROO_PC")]);
        Assert.Equal(("join", "alice", @"D:\ROO_PC"), (o.Command, o.Account, o.ClientDir));
    }

    [Fact]
    public void SettingsRoundTrip()
    {
        var dir = Directory.CreateTempSubdirectory("ro3emu-desktop-");
        try
        {
            var path = Path.Combine(dir.FullName, LauncherSettings.FileName);
            var fresh = LauncherSettings.Load(path);
            Assert.Equal("127.0.0.1", Assert.Single(fresh.Servers).Address);
            fresh.Servers.Add(new ServerEntry { Name = "Friends", Address = "203.0.113.10" });
            fresh.Selected = "203.0.113.10";
            fresh.Account = "alice";
            fresh.Save(path);
            var back = LauncherSettings.Load(path);
            Assert.Equal(["127.0.0.1", "203.0.113.10"], back.Servers.Select(s => s.Address));
            Assert.Equal(("203.0.113.10", "alice"), (back.Selected, back.Account));
            File.WriteAllText(path, "{ not json");
            Assert.Single(LauncherSettings.Load(path).Servers);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task UpdateDllsKeepsABackupAndRefusesNonDlls()
    {
        var dir = Directory.CreateTempSubdirectory("ro3emu-desktop-");
        try
        {
            var client = Path.Combine(dir.FullName, "client");
            Directory.CreateDirectory(client);
            File.WriteAllText(Path.Combine(client, "Ro3Emu.ClientHooks.dll"), "MZ old hooks");
            var (url, done, requests) = SetupTests.Serve("0123456789abcdef0123\n", "MZ new hooks", "MZ new loc");
            var lines = new List<string>();
            var commit = await Engine.UpdateClientDllsAsync(lines.Add, CancellationToken.None, url + "/", client);
            await done;
            Assert.Equal("0123456789ab", commit);
            Assert.Equal(["/commit.txt ", "/Ro3Emu.ClientHooks.dll ", "/Ro3Emu.Loc.dll "], requests);
            Assert.Equal("MZ new hooks", File.ReadAllText(Path.Combine(client, "Ro3Emu.ClientHooks.dll")));
            Assert.Equal("MZ new loc", File.ReadAllText(Path.Combine(client, "Ro3Emu.Loc.dll")));
            var backup = Assert.Single(Directory.GetDirectories(Path.Combine(client, "backups")));
            Assert.Equal("MZ old hooks", File.ReadAllText(Path.Combine(backup, "Ro3Emu.ClientHooks.dll")));

            (url, done, _) = SetupTests.Serve("abc", "<html>not found</html>");
            await Assert.ThrowsAsync<InvalidDataException>(() => Engine.UpdateClientDllsAsync(_ => { }, CancellationToken.None, url + "/", client));
            await done;
            Assert.Equal("MZ new hooks", File.ReadAllText(Path.Combine(client, "Ro3Emu.ClientHooks.dll")));
        }
        finally { dir.Delete(recursive: true); }
    }
}
