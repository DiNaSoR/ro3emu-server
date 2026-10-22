using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Skills that depend on the weapon: Holy Cross and Divine Insight's two-handed spear branch and Aspersio's Holy normal
/// attacks. The weapon and mount passives are CombatService.Passives and RidingEffects.
/// </summary>
public sealed class WeaponPassiveTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-weaponpassive-" + Guid.NewGuid().ToString("N"));
    private WebApplication? _app;
    private string _http = "";

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_app == null) return;
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name)
    {
        var ports = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = ports.Item1; o.MicroGatePort = ports.Item2; o.GameGatePort = ports.Item3;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{ports.Item1}";
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        var role = await bot.CreateRoleAsync(name);
        await bot.EnterWorldAsync(role.RoleBaseInfo.Id);
        return bot;
    }

    private GameSession Session(string name) => _app!.Services.GetRequiredService<SessionRegistry>().All()
        .Single(s => s.Account == name.ToLowerInvariant());
    private T Service<T>() where T : notnull => _app!.Services.GetRequiredService<T>();
    private Task<T> Run<T>(Func<T> work) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(work()));

    /// <summary>Holds <paramref name="entry"/> in the main hand (grid 1) or off hand (grid 2), replacing what was there.</summary>
    private static void Hold(GameSession s, int grid, int entry)
    {
        s.Items.RemoveAll(i => i.Grid == grid);
        if (entry != 0) s.Items.Add(new InvItem { Guid = 9_000_000 + grid, Entry = entry, Stack = 1, Pack = 1, Grid = grid });
    }

    private const int Halberd = 2040130, KnightSpear = 2050120;

    [ClientDataFact]
    public async Task TwoHandedSpearPicksHolyCrossAndDivineInsightBranches()
    {
        await using var bot = await InWorld("Lancer");
        var data = Service<Ro3Emu.Server.Data.GameData>();
        var divine = data.Row("SkillConfig", 1161605)!.Value;
        var cross = data.Row("SkillConfig", 1141105)!.Value;
        var (crossOneHand, crossTwoHand, buffOneHand, buffTwoHand) = await Run(() =>
        {
            var s = Session("Lancer");
            var combat = Service<CombatService>();
            Hold(s, 1, Halberd);
            int a = combat.DamageBranch(s, cross, null);
            int c = combat.Buff1(s, divine, "Inspiration").Single()[0].GetInt32();
            Hold(s, 1, KnightSpear);
            int b = combat.DamageBranch(s, cross, null);
            int d = combat.Buff1(s, divine, "Inspiration").Single()[0].GetInt32();
            return (a, b, c, d);
        });
        Assert.Equal(0, crossOneHand);
        Assert.Equal(1, crossTwoHand);
        Assert.Equal(1162305, buffOneHand); // Max Life +10%, PATK +5%
        Assert.Equal(1162405, buffTwoHand); // Max Life +5%, PATK +10%
    }

    [ClientDataFact]
    public async Task AspersioMakesNormalAttacksHoly()
    {
        await using var bot = await InWorld("Blessed");
        var s = Session("Blessed");
        var combat = Service<CombatService>();
        var character = Service<CharacterService>();
        Assert.Equal(0, await Run(() => combat.PlayerAttacker(s, character.Stats(s)).NormalAttackElement));
        await Service<GameLoop>().InvokeAsync(() => Service<BuffService>().AddAsync(s, 1411301, 60000, s.Role!.Id));
        Assert.Equal(7, await Run(() => combat.PlayerAttacker(s, character.Stats(s)).NormalAttackElement));
    }
}
