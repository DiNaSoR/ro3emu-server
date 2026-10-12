using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Romsg;

namespace Ro3Emu.Tests;

public sealed class AuctionWriteCaptureTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ro3-auction-capture-" + Guid.NewGuid().ToString("N"));
    private readonly AccountStore accounts;
    private readonly AuctionStore auctions;
    private readonly RoleStore roles;

    public AuctionWriteCaptureTests()
    {
        accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = directory }));
        roles = new RoleStore(accounts);
        auctions = new AuctionStore(accounts);
        foreach (long role in new long[] { 11, 12 })
            FixtureSql.Execute(accounts, """
                INSERT INTO role(id,account,area,name,sex,career,level,hair,create_time,last_login_time)
                VALUES($id,'auction-capture',1,$name,1,1000,1,1,0,0)
                """, ("$id", role), ("$name", $"AuctionCapture{role}"));
        accounts.WriteSync<int>(new RoleSchemaPlan());
    }

    private long Role(string name) => roles.Create("capture", 1, name, 1, 1000, 1)!.Id;

    private AuctionStore.Item Seed(string key)
    {
        var item = new AuctionStore.Item
        {
            SourceKey = key, Type = AuctionType.Dungeon, Entry = 1200000,
            Price = 500, Eligible = [11, 12], State = AuctionState.Bidding,
        };
        var builder = new RoleItemPlanBuilder(accounts);
        auctions.EnlistSave(item, builder);
        builder.Commit(accounts, "test/auction-seed");
        return item;
    }

    [Fact]
    public void ItemPlanCapturesNestedStateAndReturnsSourceBytesOnlyAfterCommit()
    {
        var item = new AuctionStore.Item
        {
            SourceKey = "captured", Type = AuctionType.Dungeon, Entry = 1200000,
            Eligible = [11], Price = 500,
        };
        var builder = new RoleItemPlanBuilder(accounts);
        auctions.EnlistSave(item, builder);
        item.Eligible.Add(99);
        item.Price = 900;
        Assert.Equal(0, item.Id);
        Assert.Null(item.OriginalBytes);

        builder.Commit(accounts, "test/auction-capture");

        Assert.True(item.Id > 0);
        Assert.NotNull(item.OriginalBytes);
        var persisted = auctions.Get(item.Id)!;
        Assert.Equal(500, persisted.Price);
        Assert.Equal(new long[] { 11 }, persisted.Eligible);
        Assert.Equal(item.OriginalBytes, persisted.OriginalBytes);
    }

    [Fact]
    public void StaleLotRejectsWholePaymentAndMailboxPlan()
    {
        long recipient = Role("AuctionBidder");
        var initial = Seed("lot");
        var stale = auctions.Get(initial.Id)!;
        stale.Owner = recipient;
        stale.Reserve = 900;
        var builder = new RoleItemPlanBuilder(accounts);
        auctions.EnlistBonus(recipient, "payment", 900, 0, builder);
        builder.RoleBlob(recipient, "mailbox", [5]);
        auctions.EnlistSave(stale, builder);

        var winner = auctions.Get(initial.Id)!;
        winner.Owner = 12;
        winner.Reserve = 1000;
        var concurrent = new RoleItemPlanBuilder(accounts);
        auctions.EnlistSave(winner, concurrent);
        concurrent.Commit(accounts, "test/auction-concurrent-bid");

        Assert.ThrowsAny<RolePlanConflictException>(() => builder.Commit(accounts, "test/auction-stale-bid"));
        Assert.Equal(12, auctions.Get(initial.Id)!.Owner);
        Assert.Empty(auctions.Bonuses(recipient));
        Assert.Null(roles.LoadMailbox(recipient));
    }

    [Fact]
    public void AuctionRowRevisionRejectsAbaDespiteIdenticalSourceBytes()
    {
        var item = Seed("aba-lot");
        var stale = auctions.Get(item.Id)!;
        stale.Price = 700;
        var pending = accounts.NewTransaction();
        auctions.EnlistSave(stale, pending);

        var sameBytes = auctions.Get(item.Id)!;
        var intervening = accounts.NewTransaction();
        auctions.EnlistSave(sameBytes, intervening);
        intervening.Commit(accounts, "test/auction-aba-intervening");

        Assert.Throws<OwnerRevisionConflictException>(() => pending.Commit(accounts, "test/auction-aba-stale"));
        Assert.Equal(500, auctions.Get(item.Id)!.Price);
    }

    [Fact]
    public void StalePrebuyRejectsWholeLotAndMailPlan()
    {
        long recipient = Role("PrebuyBidder");
        var item = Seed("prebuy-lot");
        var insert = new RoleItemPlanBuilder(accounts);
        auctions.EnlistPrebuy(new AuctionStore.Prebuy(0, recipient, AuctionType.Dungeon, 5001,
            1200000, 1, 900, 1, 1, false, item.Id, false), insert);
        insert.Commit(accounts, "test/prebuy-seed");
        var stale = Assert.Single(auctions.Prebuys());
        var builder = new RoleItemPlanBuilder(accounts);
        var lot = auctions.Get(item.Id)!;
        lot.Owner = recipient;
        auctions.EnlistSave(lot, builder);
        builder.RoleBlob(recipient, "mailbox", [7]);
        auctions.EnlistPrebuy(stale with { Processed = true }, builder);

        var concurrent = new RoleItemPlanBuilder(accounts);
        auctions.EnlistPrebuy(stale with { Paused = true }, concurrent);
        concurrent.Commit(accounts, "test/prebuy-concurrent-pause");

        Assert.ThrowsAny<RolePlanConflictException>(() => builder.Commit(accounts, "test/prebuy-stale-settle"));
        Assert.Equal(0, auctions.Get(item.Id)!.Owner);
        Assert.True(Assert.Single(auctions.Prebuys()).Paused);
        Assert.Null(roles.LoadMailbox(recipient));
    }

    [Fact]
    public void DuplicateSourceAndStaleRunRollbackEarlierAuctionRows()
    {
        var source = new RoleItemPlanBuilder(accounts);
        auctions.EnlistSource("guild:once", source);
        source.Commit(accounts, "test/auction-source");
        var candidate = new AuctionStore.Item { SourceKey = "candidate", Type = AuctionType.Guild };
        var duplicate = new RoleItemPlanBuilder(accounts);
        auctions.EnlistSave(candidate, duplicate);
        auctions.EnlistSource("guild:once", duplicate);
        Assert.Throws<AuctionSourceDuplicateException>(() => duplicate.Commit(accounts, "test/auction-duplicate"));
        Assert.Equal(0, candidate.Id);
        Assert.Empty(auctions.Items());

        var run = new AuctionStore.Run { Id = 201, Eligible = [11] };
        var start = new RoleItemPlanBuilder(accounts);
        auctions.EnlistRunSave(run, null, start);
        start.Commit(accounts, "test/run-seed");
        var stale = auctions.GetRunSnapshot(run.Id);
        stale.Run!.Ready = true;
        var pending = new RoleItemPlanBuilder(accounts);
        auctions.EnlistSave(candidate, pending);
        auctions.EnlistRunSave(stale.Run, stale.Raw, pending);
        var newer = auctions.GetRunSnapshot(run.Id);
        newer.Run!.Eligible.Add(12);
        var concurrent = new RoleItemPlanBuilder(accounts);
        auctions.EnlistRunSave(newer.Run, newer.Raw, concurrent);
        concurrent.Commit(accounts, "test/run-concurrent");

        Assert.ThrowsAny<RolePlanConflictException>(() => pending.Commit(accounts, "test/run-stale"));
        Assert.Equal(0, candidate.Id);
        Assert.Empty(auctions.Items());
        Assert.Equal(new long[] { 11, 12 }, auctions.GetRun(run.Id)!.Eligible);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }
}
