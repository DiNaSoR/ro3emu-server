using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class EconomyTransactionTests : IDisposable
{
    private readonly string directory;
    private readonly AccountStore accounts;
    private readonly RoleStore roles;
    private readonly ItemStore items;
    private readonly long role;
    private static readonly EconomyCatalog Catalog = new(new Dictionary<long, EconomyAssetKind>
    { [1] = EconomyAssetKind.Currency, [3] = EconomyAssetKind.Item });

    public EconomyTransactionTests()
    {
        // Exact, unique worktree-local fixture path. Never touch a shared data/client junction.
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        directory = Path.Combine(root, ".loadtest-temp", "e01-core-" + Guid.NewGuid().ToString("N"));
        accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = directory }));
        roles = new RoleStore(accounts);
        items = new ItemStore(accounts);
        role = roles.Create("e01-account", 1, "E01" + Guid.NewGuid().ToString("N")[..8], 1, 1000, 1)!.Id;
        var progress = roles.LoadProgress(role);
        progress.Currency[1] = 100;
        roles.SaveProgress(role, progress);
    }

    private RoleItemPlanBuilder Purchase(string operation, string source, string fingerprint = "purchase:3:2:10",
        long cost = 10, long goods = 2, bool applyCost = true, bool applyGoods = true,
        string actorAccount = "e01-account", byte[]? outcome = null, string commandKind = "npc-shop-buy")
    {
        var tx = roles.NewTransaction();
        var before = items.Load(role);
        var after = before.Select(x => x.Clone()).ToList();
        if (applyGoods) after.Add(new InvItem { Entry = 3, Stack = 2, Pack = 1, Sort = 1 });
        tx.ItemDelta(role, before, after);
        var progress = roles.LoadProgress(role);
        if (applyCost) progress.Currency[1] -= 10;
        tx.Progress(role, progress);
        tx.RoleBlob(role, "shop", JsonSerializer.SerializeToUtf8Bytes(new ShopService.ShopState
        {
            Bought = new() { [118] = new ShopService.Counter { Count = 1, Period = "" } }
        }));
        tx.WithEconomy(new EconomyMutation(operation, actorAccount, role, commandKind, fingerprint,
            source, 1_000_000, [new(new(EconomyAssetKind.Currency, 1), cost)],
            [new(new(EconomyAssetKind.Item, 3), goods)], outcome ?? [7, 8, 9]));
        return tx;
    }

    [Fact]
    public async Task NpcEconomyEnvelopeSupportsAsyncCommitAndSyncReplayWithoutDuplicateMovement()
    {
        string op = Guid.NewGuid().ToString("N"), source = "npc:" + Guid.NewGuid().ToString("N");
        var tx = Purchase(op, source);
        var replay = Purchase(op, source);
        long opening = EconomySnapshotReader.Read(accounts.DatabasePath, Catalog, "auto").Cut.CommitSequence!.Value;
        int ownedCommit = 0, ownedApply = 0, writerCommit = 0, published = 0;
        accounts.BeforeOwnedCommitForTest = _ =>
        {
            ownedCommit++;
            Assert.Equal(100, roles.LoadProgress(role).Currency[1]);
            Assert.Empty(items.Load(role));
            Assert.Null(roles.LoadShop(role));
            Assert.Null(EconomyReceiptReader.Read(accounts.DatabasePath, op));
            return Task.CompletedTask;
        };
        accounts.BeforeOwnedApplyForTest = _ =>
        {
            ownedApply++;
            Assert.Equal(90, roles.LoadProgress(role).Currency[1]);
            Assert.Equal(2, Assert.Single(items.Load(role)).Stack);
            Assert.NotNull(roles.LoadShop(role));
            Assert.NotNull(EconomyReceiptReader.Read(accounts.DatabasePath, op));
            Assert.Equal(0, published);
            return Task.CompletedTask;
        };
        accounts.BeforeCommitForTest = _ => writerCommit++;

        object?[] first = await roles.CommitAsync(tx, "test/e01-async", _ => published++);
        Assert.Equal((1, 1, 1, 1), (ownedCommit, ownedApply, writerCommit, published));
        await Assert.ThrowsAsync<InvalidOperationException>(() => roles.CommitAsync(tx, "test/e01-resubmit"));
        var committedRevisions = roles.ReadRevisionVector(role);
        accounts.BeforeCommitForTest = null;
        object?[] duplicate = roles.Commit(replay, "test/e01-sync-replay");
        Assert.Equal((long[])first[0]!, (long[])duplicate[0]!);
        Assert.Equal(committedRevisions.Parts.OrderBy(p => p.Key), roles.ReadRevisionVector(role).Parts.OrderBy(p => p.Key));
        Assert.Equal(90, roles.LoadProgress(role).Currency[1]);
        Assert.Equal(2, Assert.Single(items.Load(role)).Stack);
        var receipt = EconomyReceiptReader.Read(accounts.DatabasePath, op)!;
        Assert.NotNull(receipt);
        Assert.Equal(source, receipt.SourceKey);
        Assert.Equal((long[])first[0]!, receipt.ChildReceipts[0].GeneratedIds!.ToArray());
        Assert.Equal(opening + 1, EconomySnapshotReader.Read(accounts.DatabasePath, Catalog, "auto").Cut.CommitSequence);
    }

    [Fact]
    public async Task UnconvertedEconomyEnvelopeRejectsAsyncCommitBeforeSubmission()
    {
        string op = Guid.NewGuid().ToString("N"), source = "unsupported:" + Guid.NewGuid().ToString("N");
        var tx = Purchase(op, source, commandKind: "unconverted-command");
        long opening = EconomySnapshotReader.Read(accounts.DatabasePath, Catalog, "auto").Cut.CommitSequence!.Value;
        int ownedCommit = 0, ownedApply = 0, writerCommit = 0, published = 0;
        accounts.BeforeOwnedCommitForTest = _ => { ownedCommit++; return Task.CompletedTask; };
        accounts.BeforeOwnedApplyForTest = _ => { ownedApply++; return Task.CompletedTask; };
        accounts.BeforeCommitForTest = _ => writerCommit++;
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            roles.CommitAsync(tx, "test/unconverted-async-reject", _ => published++));
        Assert.Equal((0, 0, 0, 0), (ownedCommit, ownedApply, writerCommit, published));
        Assert.Equal(100, roles.LoadProgress(role).Currency[1]);
        Assert.Empty(items.Load(role));
        Assert.Null(roles.LoadShop(role));
        Assert.Null(EconomyReceiptReader.Read(accounts.DatabasePath, op));
        Assert.Equal(opening, EconomySnapshotReader.Read(accounts.DatabasePath, Catalog, "auto").Cut.CommitSequence);
    }

    [Fact]
    public void PurchaseReplayUsesOriginalIdsAndPreservesCAndDRevisions()
    {
        string op = Guid.NewGuid().ToString("N"), source = "npc:" + Guid.NewGuid().ToString("N");
        var first = Purchase(op, source);
        var duplicate = Purchase(op, source); // same captured old revisions, deliberately committed later
        var beforeRevision = roles.ReadRevisionVector(role);
        var opening = EconomySnapshotReader.Read(accounts.DatabasePath, Catalog, "auto");
        object?[] firstResults = roles.Commit(first, "test/e01-first");
        var committedRevision = roles.ReadRevisionVector(role);
        object?[] replayResults = roles.Commit(duplicate, "test/e01-replay");
        var replayRevision = roles.ReadRevisionVector(role);
        var closing = EconomySnapshotReader.Read(accounts.DatabasePath, Catalog, "auto");
        var ledger = EconomyLedgerReader.Read(accounts.DatabasePath, Catalog, "auto",
            opening.Cut.CommitSequence!.Value, closing.Cut.CommitSequence!.Value);

        Assert.Equal(90, roles.LoadProgress(role).Currency[1]);
        Assert.Equal(2, Assert.Single(items.Load(role)).Stack);
        Assert.Equal((long[])firstResults[0]!, (long[])replayResults[0]!);
        Assert.Equal(committedRevision.Epoch, replayRevision.Epoch);
        Assert.Equal(committedRevision.Parts.OrderBy(x => x.Key), replayRevision.Parts.OrderBy(x => x.Key));
        Assert.Equal(beforeRevision.Revision("inventory") + 1, committedRevision.Revision("inventory"));
        Assert.Equal(beforeRevision.Revision("progress") + 1, committedRevision.Revision("progress"));
        Assert.Equal(beforeRevision.Revision("shop") + 1, committedRevision.Revision("shop"));
        Assert.Equal(opening.Cut.CommitSequence + 1, closing.Cut.CommitSequence);
        Assert.True(ledger.CompleteHistory, string.Join("; ", ledger.Issues.Select(x => x.Detail)));
        Assert.Equal(2, ledger.Legs.Count);
        Assert.Equal(new long[] { -10, 2 }, ledger.Legs.Select(x => x.Delta).Order().ToArray());
        var check = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.All(check.Assets, asset => Assert.Equal("0", asset.Residual));
        Assert.All(check.Locations, location => Assert.Equal("0", location.Residual));
        Assert.DoesNotContain(check.Issues, issue => issue.Kind == EconomyIssueKind.Corruption);
        Assert.NotNull(EconomyReceiptReader.Read(accounts.DatabasePath, op));
        Assert.Equal((long[])firstResults[0]!, EconomyReceiptReader.Read(accounts.DatabasePath, op)!.ChildReceipts[0].GeneratedIds!.ToArray());
    }

    [Fact]
    public void ChangedIntentOrActorAndReusedSourceConflictBeforeStaleRevision()
    {
        string op = Guid.NewGuid().ToString("N"), source = "npc:" + Guid.NewGuid().ToString("N");
        var differentPayload = Purchase(op, source, "different-intent");
        var changedCostSameFingerprint = Purchase(op, source, cost: 11);
        var changedOutcomeSameFingerprint = Purchase(op, source, outcome: [1, 2, 3]);
        var differentSource = Purchase(Guid.NewGuid().ToString("N"), source);
        var wrongActor = Purchase(op, source, actorAccount: "wrong-account");
        roles.Commit(Purchase(op, source), "test/e01-original");
        Assert.Throws<EconomyOperationConflictException>(() => roles.Commit(differentPayload, "test/e01-payload"));
        Assert.Throws<EconomyOperationConflictException>(() => roles.Commit(changedCostSameFingerprint, "test/e01-cost"));
        Assert.Throws<EconomyOperationConflictException>(() => roles.Commit(changedOutcomeSameFingerprint, "test/e01-outcome"));
        Assert.Throws<EconomyOperationConflictException>(() => roles.Commit(differentSource, "test/e01-source"));
        Assert.Throws<EconomyOperationConflictException>(() => roles.Commit(wrongActor, "test/e01-actor"));
        Assert.Equal(90, roles.LoadProgress(role).Currency[1]);
    }

    [Fact]
    public void LastLedgerInsertFailureRollsBackAssetsQuotaReceiptSourceAndSequence()
    {
        string op = Guid.NewGuid().ToString("N"), source = "npc:" + Guid.NewGuid().ToString("N");
        long cut = EconomySnapshotReader.Read(accounts.DatabasePath, Catalog, "auto").Cut.CommitSequence!.Value;
        FixtureSql.Execute(accounts, "CREATE TRIGGER reject_last_e01_leg BEFORE INSERT ON economy_entry WHEN NEW.leg_no=1 BEGIN SELECT RAISE(ABORT,'last-leg'); END");
        Assert.Throws<SqliteException>(() => roles.Commit(Purchase(op, source), "test/e01-last-leg"));
        FixtureSql.Execute(accounts, "DROP TRIGGER reject_last_e01_leg");
        Assert.Equal(100, roles.LoadProgress(role).Currency[1]);
        Assert.Empty(items.Load(role));
        Assert.Null(roles.LoadShop(role));
        Assert.Null(EconomyReceiptReader.Read(accounts.DatabasePath, op));
        Assert.Equal(cut, EconomySnapshotReader.Read(accounts.DatabasePath, Catalog, "auto").Cut.CommitSequence);
        roles.Commit(Purchase(op, source), "test/e01-retry-after-rollback");
        Assert.Equal(90, roles.LoadProgress(role).Currency[1]);
    }

    [Fact]
    public void WriterRejectsCostGoodsThatDisagreeWithCommittedAssetDelta()
    {
        string op = Guid.NewGuid().ToString("N"), source = "npc:" + Guid.NewGuid().ToString("N");
        Assert.Throws<InvalidOperationException>(() => roles.Commit(Purchase(op, source, cost: 11), "test/e01-mismatch"));
        Assert.Equal(100, roles.LoadProgress(role).Currency[1]);
        Assert.Empty(items.Load(role));
        Assert.Null(EconomyReceiptReader.Read(accounts.DatabasePath, op));
    }

    [Fact]
    public void LostAcknowledgementAfterCommitResolvesFromReceiptWithoutSecondExecution()
    {
        string op = Guid.NewGuid().ToString("N"), source = "npc:" + Guid.NewGuid().ToString("N");
        var first = Purchase(op, source);
        var retry = Purchase(op, source);
        long before = roles.ReadRevisionVector(role).Revision("inventory");
        accounts.FailNextEconomyAcknowledgementForTest();
        object?[] committedResult = roles.Commit(first, "test/e01-lost-ack");
        var committed = EconomyReceiptReader.Read(accounts.DatabasePath, op)!;
        Assert.NotNull(committed);
        Assert.Equal(90, roles.LoadProgress(role).Currency[1]);
        Assert.Equal(before + 1, roles.ReadRevisionVector(role).Revision("inventory"));
        Assert.Equal((long[])committedResult[0]!, committed.ChildReceipts[0].GeneratedIds!.ToArray());
        var replay = roles.Commit(retry, "test/e01-resolve");
        Assert.Equal(committed.ChildReceipts[0].GeneratedIds!.ToArray(), (long[])replay[0]!);
        Assert.Equal(before + 1, roles.ReadRevisionVector(role).Revision("inventory"));
        Assert.Equal(2, EconomyLedgerReader.Read(accounts.DatabasePath, Catalog, "auto", 0, 1).Legs.Count);
    }

    [Fact]
    public void ExplicitSameAssetCostAndGoodsRetainBothLegsWhenNetBalanceIsZero()
    {
        string op = Guid.NewGuid().ToString("N"), source = "npc:" + Guid.NewGuid().ToString("N");
        var tx = roles.NewTransaction();
        tx.ItemDelta(role, [], []);
        tx.Progress(role, roles.LoadProgress(role));
        tx.RoleBlob(role, "shop", JsonSerializer.SerializeToUtf8Bytes(new ShopService.ShopState()));
        tx.WithEconomy(new EconomyMutation(op, "e01-account", role, "npc-shop-buy", "currency-exchange",
            source, 1_000_000, [new(new(EconomyAssetKind.Currency, 1), 10)],
            [new(new(EconomyAssetKind.Currency, 1), 10)], [1]));
        roles.Commit(tx, "test/e01-net-zero");
        var legs = EconomyLedgerReader.Read(accounts.DatabasePath, Catalog, "auto", 0, 1).Legs;
        Assert.Equal(new long[] { -10, 10 }, legs.Select(x => x.Delta).ToArray());
        Assert.Equal(100, roles.LoadProgress(role).Currency[1]);
    }

    [Fact]
    public void ReplayAfterStoreReopenReadsDurableOriginalGeneratedIds()
    {
        string op = Guid.NewGuid().ToString("N"), source = "npc:" + Guid.NewGuid().ToString("N");
        var retry = Purchase(op, source);
        object?[] first = roles.Commit(Purchase(op, source), "test/e01-before-reopen");
        string realm = EconomySnapshotReader.Read(accounts.DatabasePath, Catalog, "auto").Cut.RealmId;
        accounts.Dispose();
        using var reopened = new AccountStore(Options.Create(new ServerOptions { DataDir = directory }));
        var reopenedRoles = new RoleStore(reopened);
        _ = new ItemStore(reopened);
        object?[] replay = reopenedRoles.Commit(retry, "test/e01-after-reopen");
        Assert.Equal((long[])first[0]!, (long[])replay[0]!);
        Assert.Equal(realm, EconomySnapshotReader.Read(reopened.DatabasePath, Catalog, "auto").Cut.RealmId);
        Assert.Equal(1, EconomySnapshotReader.Read(reopened.DatabasePath, Catalog, "auto").Cut.CommitSequence);
        Assert.True(EconomyLedgerReader.Read(reopened.DatabasePath, Catalog, "auto", 0, 1).CompleteHistory);
    }

    [Fact]
    public async Task ConcurrentIdenticalOperationHasOneCommitAndOneSourceClaim()
    {
        string op = Guid.NewGuid().ToString("N"), source = "npc:" + Guid.NewGuid().ToString("N");
        var left = Purchase(op, source);
        var right = Purchase(op, source);
        var results = await Task.WhenAll(Task.Run(() => roles.Commit(left, "test/e01-left")),
            Task.Run(() => roles.Commit(right, "test/e01-right")));
        Assert.Equal((long[])results[0][0]!, (long[])results[1][0]!);
        Assert.Equal(90, roles.LoadProgress(role).Currency[1]);
        Assert.Equal(1, EconomySnapshotReader.Read(accounts.DatabasePath, Catalog, "auto").Cut.CommitSequence);
        var ledger = EconomyLedgerReader.Read(accounts.DatabasePath, Catalog, "auto", 0, 1);
        Assert.True(ledger.CompleteHistory);
        Assert.Single(ledger.Receipts);
        Assert.Equal(2, ledger.Legs.Count);
    }

    [Fact]
    public void RestackingCannotSmuggleBindingChangeThroughQuantityLeg()
    {
        var owned = new InvItem { Entry = 3, Stack = 2, Pack = 1, Sort = 1 };
        items.Insert(role, owned);
        string op = Guid.NewGuid().ToString("N"), source = "npc:" + Guid.NewGuid().ToString("N");
        var tx = roles.NewTransaction();
        var next = owned.Clone();
        next.Stack = 3;
        next.BindType = 1;
        tx.ItemDelta(role, [owned], [next]);
        tx.Progress(role, roles.LoadProgress(role));
        tx.RoleBlob(role, "shop", JsonSerializer.SerializeToUtf8Bytes(new ShopService.ShopState()));
        tx.WithEconomy(new EconomyMutation(op, "e01-account", role, "npc-shop-buy", "restack",
            source, 1_000_000, [], [new(new(EconomyAssetKind.Item, 3), 1)], [1]));
        Assert.Throws<InvalidOperationException>(() => roles.Commit(tx, "test/e01-rebind"));
        Assert.Equal(2, Assert.Single(items.Load(role)).Stack);
        Assert.Equal(0, Assert.Single(items.Load(role)).BindType);
        Assert.Null(EconomyReceiptReader.Read(accounts.DatabasePath, op));
    }

    [Theory]
    [InlineData("prior-leg")]
    [InlineData("receipt-gap")]
    [InlineData("command")]
    [InlineData("owner")]
    [InlineData("revision-role")]
    [InlineData("intent")]
    [InlineData("child-type")]
    [InlineData("watermark")]
    public void PersistedReaderRejectsCorruptedReceiptManifestIncludingHistoryBeforeOpening(string damage)
    {
        roles.Commit(Purchase("first", "source-first"), "test/reader-first");
        roles.Commit(Purchase("second", "source-second"), "test/reader-second");
        Assert.True(EconomyLedgerReader.Read(accounts.DatabasePath, Catalog, "auto", 1, 2).CompleteHistory);
        switch (damage)
        {
            case "prior-leg": FixtureSql.Execute(accounts, "DELETE FROM economy_entry WHERE commit_seq=1 AND leg_no=1"); break;
            case "receipt-gap": FixtureSql.Execute(accounts, """
                DELETE FROM economy_entry WHERE commit_seq=1;
                DELETE FROM operation_source WHERE operation_id='first';
                DELETE FROM operation_receipt WHERE commit_seq=1
                """); break;
            case "command": FixtureSql.Execute(accounts, "UPDATE operation_receipt SET command_kind='gm-mint' WHERE commit_seq=1"); break;
            case "owner": FixtureSql.Execute(accounts, "UPDATE economy_entry SET owner_id='999999' WHERE commit_seq=1"); break;
            case "revision-role":
                var receipt = EconomyReceiptReader.Read(accounts.DatabasePath, "first")!;
                FixtureSql.Execute(accounts, "UPDATE operation_receipt SET before_revisions=$before,after_revisions=$after WHERE commit_seq=1",
                    ("$before", receipt.BeforeRevisions.Replace("\"roleId\":" + role, "\"roleId\":999999", StringComparison.Ordinal)),
                    ("$after", receipt.AfterRevisions.Replace("\"roleId\":" + role, "\"roleId\":999999", StringComparison.Ordinal)));
                break;
            case "intent": FixtureSql.Execute(accounts, "UPDATE operation_receipt SET intent_payload=$bad WHERE commit_seq=1", ("$bad", new byte[] { 123, 125 })); break;
            case "child-type":
                using (var bytes = new MemoryStream())
                {
                    using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, leaveOpen: true))
                    {
                        writer.Write(3);
                        for (int i = 0; i < 3; i++) { writer.Write((byte)1); writer.Write(1); }
                    }
                    FixtureSql.Execute(accounts, "UPDATE operation_receipt SET child_receipts=$bad WHERE commit_seq=1", ("$bad", bytes.ToArray()));
                }
                break;
            case "watermark": FixtureSql.Execute(accounts, "UPDATE economy_meta SET next_sequence=1"); break;
        }
        var ledger = EconomyLedgerReader.Read(accounts.DatabasePath, Catalog, "auto", damage == "watermark" ? 0 : 1,
            damage == "watermark" ? 1 : 2);
        Assert.False(ledger.CompleteHistory);
        Assert.Contains(ledger.Issues, issue => issue.Kind == EconomyIssueKind.Corruption);
    }

    public void Dispose()
    {
        accounts.Dispose();
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".loadtest-temp"));
        if (!Path.GetFullPath(directory).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fixture directory escaped the worktree temp root.");
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
