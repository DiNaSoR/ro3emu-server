using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Fixture-only GM authorization for the existing tests' direct bag/purse setup.
/// Production market commands declare their semantic fees and supplies before admission.</summary>
internal static class MarketFixtureEconomy
{
    internal static void Edit(MarketTransaction tx, MarketParticipant participant, Action<MarketParticipant> edit)
    {
        var before = Holdings(participant);
        edit(participant);
        var after = Holdings(participant);
        foreach (var asset in before.Keys.Concat(after.Keys).Distinct())
        {
            long delta = checked(after.GetValueOrDefault(asset) - before.GetValueOrDefault(asset));
            if (delta > 0) tx.Economy.Source(asset, delta, EconomyReason.GmGrant);
            else if (delta < 0) tx.Economy.Sink(asset, -delta, EconomyReason.GmRemove);
        }
    }

    private static Dictionary<EconomyAsset, long> Holdings(MarketParticipant participant) =>
        participant.Currency.Select(x => (Asset: new EconomyAsset(EconomyAssetKind.Currency, x.Key), Quantity: x.Value))
            .Concat(participant.Items.Select(x => (new EconomyAsset(EconomyAssetKind.Item, x.Entry), (long)x.Stack)))
            .GroupBy(x => x.Item1).ToDictionary(g => g.Key, g => g.Sum(x => x.Item2));
}
