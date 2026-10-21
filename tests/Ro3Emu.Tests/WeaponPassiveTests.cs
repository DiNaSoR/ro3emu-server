using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Passives and skills that depend on the weapon, the shield or the mount: masteries (SpecialEffectConfig opcode 16),
/// riding (opcode 17), Monster Resistance with a shield, Holy Cross and Divine Insight's two-handed spear branch and
/// Aspersio's Holy normal attacks.
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

    private const int Sword = 2020120, Halberd = 2040130, KnightSpear = 2050120, Shield = 2500120;

    [ClientDataFact]
    public async Task SwordMasteryNeedsASword()
    {
        await using var bot = await InWorld("Mastery");
        var (without, with, halberd) = await Run(() =>
        {
            var s = Session("Mastery");
            var character = Service<CharacterService>();
            Hold(s, 1, Sword);
            long baseAtk = character.Stats(s)[103];
            s.Progress!.Skills[11009] = 10; // Sword Mastery Lv10: [16,2,103,10] and [16,3,103,10]
            long swordAtk = character.Stats(s)[103];
            Hold(s, 1, Halberd);
            long halberdBase = character.Stats(s)[103];
            s.Progress.Skills.Remove(11009);
            long halberdAtk = character.Stats(s)[103];
            return (baseAtk, swordAtk, halberdBase - halberdAtk);
        });
        Assert.Equal(without + 10, with);
        Assert.Equal(0, halberd); // a halberd is not a sword
    }

    [ClientDataFact]
    public async Task RidingSpeedsUpOnlyOnAPeco()
    {
        await using var bot = await InWorld("Rider");
        var (onFoot, mounted, type) = await Run(() =>
        {
            var s = Session("Rider");
            var character = Service<CharacterService>();
            s.Progress!.Skills[11107] = 1; // Riding: [17,2,1131,500]
            long foot = character.Stats(s)[1131];
            s.Mounts ??= new DBMountsInfo();
            s.Mounts.MountIds[2] = new MountInfo { MountId = 1500201, IsRiding = true }; // Peco Peco
            return (foot, character.Stats(s)[1131], Service<CombatService>().RiddenMountType(s));
        });
        Assert.Equal(2, type);
        Assert.Equal(onFoot + 500, mounted);
    }

    [ClientDataFact]
    public async Task MonsterResistanceNeedsAShieldAndTheBestOneApplies()
    {
        await using var bot = await InWorld("Guard");
        var (none, shielded, best) = await Run(() =>
        {
            var s = Session("Guard");
            var combat = Service<CombatService>();
            s.Progress!.Skills[11421] = 1; // Monster Resistance: 30%
            int noShield = combat.MonsterResistanceBp(s);
            Hold(s, 2, Shield);
            int withShield = combat.MonsterResistanceBp(s);
            s.Progress.Skills[11508] = 1; // Advanced Monster Resistance: 40%, does not stack
            return (noShield, withShield, combat.MonsterResistanceBp(s));
        });
        Assert.Equal(0, none);
        Assert.Equal(3000, shielded);
        Assert.Equal(4000, best);
    }

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
