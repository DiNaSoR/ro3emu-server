using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class TerritoryPersistenceCaptureTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ro3-territory-capture-" + Guid.NewGuid().ToString("N"));
    private readonly AccountStore accounts;
    private readonly RoleStore roles;
    private readonly GuildStore guilds;

    public TerritoryPersistenceCaptureTests()
    {
        accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = directory }));
        roles = new RoleStore(accounts);
        guilds = new GuildStore(accounts);
        accounts.WriteSync<int>(new TerritorySchemaPlan(RealmStateKind.TerritoryWars));
        accounts.WriteSync<int>(new TerritorySchemaPlan(RealmStateKind.TerritoryEscort));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingGuildRejectsRealmAndRoleUpdatesTogether(bool wars)
    {
        var kind = wars ? RealmStateKind.TerritoryWars : RealmStateKind.TerritoryEscort;
        string column = wars ? "territory_wars" : "territory_escort";
        long role = roles.Create("territory", 1, wars ? "WarsRole" : "EscortRole", 1, 1000, 1)!.Id;
        byte[] initialRealm = [1, 2], initialRole = [3, 4];
        accounts.WriteLegacySync<int>(new RealmStatePlan(kind, initialRealm));
        if (wars) roles.SaveTerritoryWars(role, initialRole);
        else roles.SaveTerritoryEscort(role, initialRole);

        byte[] candidateRealm = [5, 6], candidateRole = [7, 8];
        var transaction = new RoleItemPlanBuilder(accounts);
        transaction.Add(new RealmStatePlan(kind, candidateRealm));
        transaction.RoleBlob(role, column, candidateRole);
        guilds.EnlistSave(new GuildStore.State { Id = long.MaxValue, Name = "VanishedGuild" }, transaction,
            "Territory guild disappeared");
        candidateRealm[0] = 99;
        candidateRole[0] = 99;

        Assert.Throws<InvalidOperationException>(() => transaction.Commit(accounts, "test/territory-missing-guild"));
        Assert.Equal(initialRealm, accounts.ReadSync<byte[]?>(new TerritoryRealmReadPlan(kind)));
        Assert.Equal(initialRole, wars ? roles.LoadTerritoryWars(role) : roles.LoadTerritoryEscort(role));
    }

    [Fact]
    public void MalformedTargetSeasonCannotCommitOldArchiveOrReplaceCurrentRealm()
    {
        byte[] current = JsonSerializer.SerializeToUtf8Bytes(new TerritoryWarsService.WorldState { Epoch = "current" });
        byte[] fallback = JsonSerializer.SerializeToUtf8Bytes(new TerritoryWarsService.WorldState { Epoch = "fallback" });
        accounts.WriteLegacySync<int>(new RealmStatePlan(RealmStateKind.TerritoryWars, current));
        accounts.WriteLegacySync<byte[]>(new TerritoryWarsSeasonTransitionPlan("corrupt", Encoding.UTF8.GetBytes("{broken"),
            "absent", fallback));
        accounts.WriteLegacySync<int>(new RealmStatePlan(RealmStateKind.TerritoryWars, current));

        Assert.Throws<JsonException>(() => accounts.WriteLegacySync<byte[]>(
            new TerritoryWarsSeasonTransitionPlan("attempt", current, "corrupt", fallback)));

        Assert.Equal(current, accounts.ReadSync<byte[]?>(new TerritoryRealmReadPlan(RealmStateKind.TerritoryWars)));
        // If the failed transaction had archived "attempt", this would find it and restore current instead of fallback.
        var next = accounts.WriteLegacySync<byte[]>(new TerritoryWarsSeasonTransitionPlan("probe", current, "attempt", fallback));
        Assert.Equal("fallback", JsonSerializer.Deserialize<TerritoryWarsService.WorldState>(next)!.Epoch);
    }

    [Fact]
    public void ReturningToArchivedSeasonMovesHoldingsBackToActiveRowOnce()
    {
        var first = new TerritoryWarsService.WorldState { Epoch = "first" };
        first.WorldDrops[7] = new TerritoryWarsService.WorldDrop { Id = 7, Scene = 30311, Amount = 42 };
        byte[] firstBytes = JsonSerializer.SerializeToUtf8Bytes(first);
        byte[] secondBytes = JsonSerializer.SerializeToUtf8Bytes(new TerritoryWarsService.WorldState { Epoch = "second" });
        accounts.WriteLegacySync<int>(new RealmStatePlan(RealmStateKind.TerritoryWars, firstBytes));

        accounts.WriteLegacySync<byte[]>(new TerritoryWarsSeasonTransitionPlan("first", firstBytes, "second", secondBytes));
        Assert.NotNull(accounts.ReadSync<byte[]?>(new TerritoryWarsSeasonArchiveReadPlan("first")));
        byte[] second = accounts.ReadSync<byte[]?>(new TerritoryRealmReadPlan(RealmStateKind.TerritoryWars))!;
        var restored = accounts.WriteLegacySync<byte[]>(new TerritoryWarsSeasonTransitionPlan("second", second, "first", secondBytes));

        Assert.Null(accounts.ReadSync<byte[]?>(new TerritoryWarsSeasonArchiveReadPlan("first")));
        Assert.NotNull(accounts.ReadSync<byte[]?>(new TerritoryWarsSeasonArchiveReadPlan("second")));
        Assert.Equal(42, JsonSerializer.Deserialize<TerritoryWarsService.WorldState>(restored)!.WorldDrops[7].Amount);
    }

    public void Dispose()
    {
        accounts.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }
}
