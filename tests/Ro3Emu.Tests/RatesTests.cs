using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>ServerOptions.Rates on monster kill rewards (docs/guide/rates.md).</summary>
public sealed class RatesTests
{
    private static Task<T> WithRates<T>(Action<RatesOptions> rates, Func<RewardService, T> act) =>
        WithRates<RewardService, T>(rates, act);

    private static async Task<T> WithRates<TService, T>(Action<RatesOptions> rates, Func<TService, T> act) where TService : notnull
    {
        string dir = Path.Combine(ClientData.RepoRoot, ".loadtest-temp", "rates-" + Guid.NewGuid().ToString("N"));
        var (web, micro, game) = TestPorts.FreePorts();
        WebApplication app = ServerHost.Create([], o =>
        {
            o.DataDir = dir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
            rates(o.Rates);
        });
        try { return act(app.Services.GetRequiredService<TService>()); }
        finally
        {
            await app.DisposeAsync(); SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    // Zeny (1) and Minted Coin (2) are currencies (ItemConfig _iType 1); Beginner Potion 1000000 is an item.
    private static readonly List<(int Entry, int Count)> Loot = [(1, 7), (2, 3), (1000000, 2)];

    [ClientDataFact]
    public async Task DefaultRatesKeepTheTablesAndJobExpEqualsBaseExp()
    {
        var (items, baseExp, jobExp) = await WithRates(_ => { }, r => r.ApplyRates(Loot, 30));
        Assert.Equal(Loot, items);
        Assert.Equal(30, baseExp);
        Assert.Equal(30, jobExp);
    }

    [ClientDataFact]
    public async Task RatesScaleExpAndCurrencyButNotItemCounts()
    {
        var (items, baseExp, jobExp) = await WithRates(r => { r.BaseExp = 2; r.JobExp = 5; r.CurrencyDrop = 3; },
            r => r.ApplyRates(Loot, 30));
        Assert.Equal([(1, 21), (2, 9), (1000000, 2)], items);
        Assert.Equal(60, baseExp);
        Assert.Equal(150, jobExp);
    }

    [ClientDataFact]
    public async Task SmallRatesRoundButNeverToZeroAndZeroRemovesTheReward()
    {
        var low = await WithRates(r => { r.BaseExp = 0.1; r.JobExp = 0; r.CurrencyDrop = 0.01; }, r => r.ApplyRates(Loot, 4));
        Assert.Equal(1, low.BaseExp);
        Assert.Equal(0, low.JobExp);
        Assert.Equal([(1, 1), (2, 1), (1000000, 2)], low.Items);
        var none = await WithRates(r => r.CurrencyDrop = 0, r => r.ApplyRates(Loot, 4));
        Assert.Equal([(1000000, 2)], none.Items);
    }

    [ClientDataFact]
    public async Task BaseExpRateScalesQuestExpAndJobExpFollowsAt20Percent()
    {
        Assert.Equal(70_000, await WithRates<CharacterService, long>(_ => { }, c => c.QuestExp(70_000)));
        var (quest, job) = await WithRates<CharacterService, (long, long)>(r => { r.BaseExp = 3; r.JobExp = 9; },
            c => (c.QuestExp(70_000), c.JobExpFromBase(c.QuestExp(70_000))));
        Assert.Equal((210_000, 42_000), (quest, job));
        Assert.Equal(0, await WithRates<CharacterService, long>(r => r.BaseExp = 0, c => c.QuestExp(70_000)));
        Assert.Equal(0, await WithRates<CharacterService, long>(r => r.BaseExp = 3, c => c.QuestExp(0)));
    }

    [ClientDataFact]
    public async Task SoloKillerHasNoPartyDropBonus()
    {
        var solo = new GameSession(null!, null!);
        Assert.Equal(10000, await WithRates<TeamService, int>(_ => { }, t => t.DropBonus(solo)));
    }

    [Fact]
    public void ItemDropRateScalesRollChancesUpToCertainty()
    {
        Assert.Equal(50_000, RewardService.ScaledChance(50_000, 1));
        Assert.Equal(150_000, RewardService.ScaledChance(50_000, 3));
        Assert.Equal(1_000_000, RewardService.ScaledChance(400_000, 5));
        Assert.Equal(1_000_000, RewardService.ScaledChance(1_000_000, 2)); // EXP rolls are already certain
        Assert.Equal(0, RewardService.ScaledChance(50_000, -1));
    }

    [Fact]
    public void RatesBindFromConfiguration()
    {
        var o = ServerHost.ResolveOptions(["--Ro3Emu:Rates:BaseExp=3", "--Ro3Emu:Rates:JobExp=4.5",
            "--Ro3Emu:Rates:ItemDrop=2", "--Ro3Emu:Rates:CurrencyDrop=0.5"]);
        Assert.Equal((3d, 4.5d, 2d, 0.5d), (o.Rates.BaseExp, o.Rates.JobExp, o.Rates.ItemDrop, o.Rates.CurrencyDrop));
    }
}
