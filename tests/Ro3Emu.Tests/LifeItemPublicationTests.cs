using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class LifeItemPublicationTests : IAsyncLifetime
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "ro3emu-life-publish-" + Guid.NewGuid().ToString("N"));
    private WebApplication app = null!;
    private string http = "";

    public async Task InitializeAsync()
    {
        var (h, m, g) = TestPorts.FreePorts();
        app = ServerHost.Create([], o =>
        {
            o.DataDir = dir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = h;
            o.MicroGatePort = m;
            o.GameGatePort = g;
        });
        await app.StartAsync();
        http = $"http://127.0.0.1:{h}";
    }

    public async Task DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    [ClientDataFact]
    public async Task FirstSpecializedExchangeHookSeesCommittedLifeAndCooldown()
    {
        await using var bot = new BotClient(http, File.ReadAllText(Path.Combine(dir, "gate_rsa_public.pem")));
        await bot.LoginAsync("alice", "pw");
        await bot.EnterGateAsync();
        long role = (await bot.CreateRoleAsync("Alice")).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(role);
        await Gm("!lvl 34");
        await Gm("!item 1001801 1");

        await app.Services.GetRequiredService<GameLoop>().InvokeAsync(async () =>
        {
            var session = app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice");
            long guid = session.Items.Single(i => i.Entry == 1001801).Guid;
            var character = app.Services.GetRequiredService<CharacterService>();
            int calls = 0;
            void Observe(GameSession changed)
            {
                if (!ReferenceEquals(changed, session)) return;
                calls++;
                if (calls != 1) return;
                Assert.Equal(1, session.LifeJob!.MagicFoodTimes[1001801]);
                long cooldown = session.ItemCds[1001801];
                Assert.True(cooldown > 0);
                Assert.Equal(cooldown, session.BagState.Info.ItemCds!.Data[1001801]);
            }
            character.StateChanged.Add(Observe);
            try { await character.UseItemAsync(session, new ItemUseReq { ItemGuid = guid, ItemCount = 1 }); }
            finally { character.StateChanged.Remove(Observe); }
            Assert.True(calls >= 2);
        });
    }

    private async Task Gm(string line)
    {
        using var client = new HttpClient();
        using var response = await client.PostAsync($"{http}/ro3emu/gm?account=alice", new StringContent(line));
        response.EnsureSuccessStatusCode();
    }

    private static string RepoRoot()
    {
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path != null && !File.Exists(Path.Combine(path.FullName, "Ro3Emu.slnx"))) path = path.Parent;
        return path?.FullName ?? throw new DirectoryNotFoundException("repository");
    }
}
