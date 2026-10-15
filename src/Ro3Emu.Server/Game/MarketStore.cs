using System.Text.Json;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Micro;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Shared ownership boundary for trade and stalls.
/// Escrow, attachments, bag changes and payment use one SQLite transaction. Detached InvItem.Clone candidates
/// become visible only after commit (inferred persistence design; the original backend is not available).
/// Capture selects every role/mailbox and listing collection before one all-or-none reservation.
/// SQL guards revalidate the detached capture; publication precedes releasing keys and sending notices.
/// </summary>
public sealed class MarketStore
{
    private readonly AccountStore accounts;
    private readonly SessionRegistry sessions;
    private readonly OwnerRegistry owners;
    private readonly CharacterService character;
    private readonly MailService mail;
    private readonly ILogger<MarketStore> log;
    private readonly TimeProvider clock;

    public MarketStore(AccountStore accounts, ItemStore items, RoleStore roles, SessionRegistry sessions, OwnerRegistry owners, CharacterService character,
        MailService mail, TimeProvider clock, ILogger<MarketStore> log)
    {
        this.accounts = accounts;
        this.sessions = sessions;
        this.owners = owners;
        this.character = character;
        this.mail = mail;
        this.log = log;
        this.clock = clock;
        MigrateLegacyMail();
    }

    internal void MigrateLegacyMail()
    {
        // No traffic can reach this service before construction completes. Upgrade old escrow mail atomically.
        mail.EnterTransactionAsync().GetAwaiter().GetResult();
        try
        {
            var snapshot = accounts.ReadSync<MarketRecordSnapshot>(new MarketRecordSnapshotReadPlan("mail"));
            var rows = snapshot.Rows;
            var migration = accounts.NewTransaction();
            migration.Guard(new MarketRecordGuardPlan("mail", null, snapshot));
            mail.EnlistLegacyMigration(migration, rows);
            foreach (var row in rows) migration.Add(new MarketRecordWritePlan("mail", row.Id, null));
            if (rows.Count > 0)
                migration.WithEscrowEconomy(new EscrowEconomyIntent("market/migrate-legacy-mail",
                    sourceKey: "market/legacy-mail-v1", transferReason: EconomyReason.MailDelivery));
            migration.Commit(accounts, "market/migrate-legacy-mail");
        }
        finally { mail.LeaveTransaction(); }
    }

    /// <summary>Returning a refusal rolls back every staged change. No success reply is sent before commit.</summary>
    /// <summary>Committed market transactions that wrote anything; a reader can tell whether records changed since.</summary>
    public long Version { get; private set; }
    internal void CommittedForTest() => Version++;

    public Task<int> RunAsync(GameSession requester, Func<MarketTransaction, int> action,
        Func<MarketTransaction, Task>? afterCommit = null, [CallerMemberName] string command = "transaction")
        => RunCoreAsync(requester, action, afterCommit, command);

    /// <summary>Run periodic settlement without borrowing a socket attachment or an unrelated role reservation.</summary>
    public Task<int> RunMaintenanceAsync(Func<MarketTransaction, int> action,
        Func<MarketTransaction, Task>? afterCommit = null, [CallerMemberName] string command = "maintenance")
        => RunCoreAsync(null, action, afterCommit, command);

    /// <summary>Refresh a post-commit projection while its caller holds the market's Realm scope.</summary>
    internal T? ReadCurrent<T>(string kind, string key)
    {
        var rows = accounts.ReadSync<MarketRecordSnapshot>(new MarketRecordSnapshotReadPlan(kind, key)).Rows;
        return rows.Count == 0 ? default : JsonSerializer.Deserialize<T>(rows[0].Data);
    }

    private async Task<int> RunCoreAsync(GameSession? requester, Func<MarketTransaction, int> action,
        Func<MarketTransaction, Task>? afterCommit, string command)
    {
        LegacyOwnerScope? scope = null;
        bool committed = false;
        try
        {
            var role = requester?.Role;
            var lease = requester?.Attachment;
            var boundaryOwners = requester == null ? owners : requester.Owners;
            bool currentRequest() => requester == null || ReferenceEquals(requester.Role, role)
                && ReferenceEquals(requester.Attachment, lease) && lease?.Cancelled != true;
            MarketTransaction tx;
            long queuedAt = 0;
            while (true)
            {
                if (!currentRequest()) return ErrorCodes.Unknown;
                // A refused reservation held no participants. Discard the detached candidate and
                // capture from current state after its predecessor has published every live Apply.
                var live = (requester == null ? sessions.All() : sessions.All().Append(requester)).Distinct()
                    .Where(s => s.Role != null && s.Progress != null)
                    .OrderBy(s => s.Role!.Id).ToList();
                tx = new MarketTransaction(accounts, mail, live, clock.GetUtcNow(),
                    new EscrowEconomyIntent("market/" + command, requester?.Account ?? "", role?.Id ?? 0,
                        effectiveUtcMs: clock.GetUtcNow().ToUnixTimeMilliseconds()));
                int code;
                try
                {
                    code = action(tx);
                    if (code == 0) tx.StageWrites();
                }
                catch (OwnerRevisionConflictException) when (boundaryOwners != null)
                {
                    // A prior commit can be durable while its live Progress Apply still owns a role.
                    // Probe only the roles/entities already discovered by this failed capture. If none
                    // is busy, the capture conflict is real and must remain a refusal.
                    var discovered = tx.ReservationKeys.Concat(role == null ? [] : new[] { ReservationKey.Role(role.Id) })
                        .Distinct().ToArray();
                    var probe = boundaryOwners.TryEnter("market", discovered);
                    if (probe != null) { probe.Dispose(); throw; }
                    if (queuedAt == 0) queuedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    await boundaryOwners.Reservations.Changed;
                    continue;
                }
                // Stage G: a successful capture that writes nothing (a query) is final as read. Its reads were
                // synchronous on the loop, so they saw one committed state; reserving and committing guards for it only
                // queued queries behind market writers and the writer (32602 p99 0.9 s at 500 players). A refusal still
                // takes the reservation below: it may rest on live state a predecessor's Apply has not published yet.
                if (code == 0 && !tx.HasWrites)
                {
                    if (!currentRequest()) return ErrorCodes.Unknown;
                    if (afterCommit != null) await NotifyAsync(() => afterCommit(tx));
                    return 0;
                }
                var keys = tx.ReservationKeys.Concat(role == null ? [] : new[] { ReservationKey.Role(role.Id) }).Distinct().ToArray();
                if (boundaryOwners is { } currentOwners)
                {
                    scope = currentOwners.TryEnter("market", keys);
                    if (scope == null)
                    {
                        if (queuedAt == 0) queuedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                        await currentOwners.Reservations.Changed;
                        continue;
                    }
                    if (queuedAt != 0) currentOwners.RecordQueueAge(queuedAt);
                }
                if (!currentRequest()) return ErrorCodes.Unknown;
                if (code != 0) return code;
                break;
            }
            tx.RebindLive((requester == null ? sessions.All() : sessions.All().Append(requester)).Distinct()
                .Where(s => s.Role != null && s.Progress != null).ToArray());
            // Submit this admitted capture once. SQL refusal is final; it is never retried.
            await tx.PersistAsync(() => committed = true);
            Version++;
            var notices = new List<Func<Task>>();
            foreach (var p in tx.Participants.Where(p => p.Session != null))
                foreach (var entry in p.Items.Where(i => p.Before.All(b => b.Guid != i.Guid)).Select(i => i.Entry).Distinct())
                    foreach (var gained in character.ItemGained)
                    {
                        int count = (int)Math.Clamp(p.Items.Where(i => i.Entry == entry).Sum(i => (long)i.Stack)
                            - p.Before.Where(i => i.Entry == entry).Sum(i => (long)i.Stack), 1, int.MaxValue);
                        try { gained(p.Session!, entry, count); }
                        catch (Exception e) { log.LogError(e, "Market committed; item-gained hook failed for role {Role} item {Item}", p.RoleId, entry); }
                    }
            foreach (var p in tx.Participants.Where(p => p.Session != null))
                notices.Add(character.CaptureEconomicItemsNotice(p.Session!));
            notices.AddRange(tx.CaptureTradeNotices());
            scope?.Dispose();
            scope = null;
            await Task.WhenAll(notices.Select(NotifyAsync));
            if (afterCommit != null) await NotifyAsync(() => afterCommit(tx));
            foreach (long id in tx.MailRecipients)
                await NotifyAsync(() => mail.NotifyMarketAsync(id));
            return 0;
        }
        catch (Exception e) when (!committed && e is SqliteException or InvalidOperationException)
        {
            log.LogError(e, "Market transaction did not reach publication; committed Apply failures remain quarantined");
            return ErrorCodes.Unknown;
        }
        finally
        {
            scope?.Dispose();
        }
    }

    private async Task NotifyAsync(Func<Task> notify)
    {
        try { await notify(); }
        catch (Exception e)
        { log.LogError(e, "Market committed; post-commit notification failed; recipient can reload durable state"); }
    }
}

/// <summary>Detached bag and currency state, enlisted only when an operation needs to change this role.</summary>
public sealed class MarketParticipant
{
    public required long RoleId { get; init; }
    public required Role Role { get; init; }
    public GameSession? Session { get; internal set; }
    internal List<InvItem> Before { get; init; } = new();
    public required List<InvItem> Items { get; init; }
    public required Dictionary<int, long> Currency { get; init; }
}

/// <summary>A single transaction context; never retain it beyond MarketStore.RunAsync.</summary>
public sealed class MarketTransaction
{
    private readonly AccountStore accounts;
    private readonly RoleItemPlanBuilder plan;
    private readonly MailService mail;
    private IReadOnlyList<GameSession> live;
    private readonly Dictionary<long, MarketParticipant> participants = new();
    private readonly Dictionary<(string Kind, string Key), string?> records = new();
    private readonly HashSet<string> completeKinds = new(StringComparer.Ordinal);
    private readonly Dictionary<(long Role, string Column), byte[]> blobs = new();
    private bool writes;
    private readonly bool economicCommand;
    public EscrowEconomyIntent Economy { get; }
    public IEnumerable<MarketParticipant> Participants => participants.Values;
    public HashSet<long> MailRecipients { get; } = new();
    public DateTimeOffset Now { get; }

    internal MarketTransaction(AccountStore accounts, MailService mail,
        IReadOnlyList<GameSession> live, DateTimeOffset now, EscrowEconomyIntent? economy = null)
    {
        this.accounts = accounts; this.mail = mail; this.live = live; Now = now; plan = accounts.NewTransaction();
        economicCommand = economy != null;
        Economy = economy ?? new EscrowEconomyIntent("market/transaction", effectiveUtcMs: now.ToUnixTimeMilliseconds());
    }

    /// <summary>Enlist a canonical system template mail in the same escrow transaction.</summary>
    public string DeliverMail(long roleId, int templateId, IEnumerable<InvItem>? items = null,
        IReadOnlyDictionary<int, long>? currency = null, params string[] contentParams)
    {
        string id = mail.DeliverMarket(plan, Now, roleId, templateId, items, currency, contentParams);
        writes = true;
        MailRecipients.Add(roleId);
        return id;
    }

    public MarketParticipant Role(long id)
    {
        if (participants.TryGetValue(id, out var found)) return found;
        var original = accounts.ReadSync<MarketRoleSnapshot>(new MarketRoleReadPlan(id));
        plan.Guard(new MarketRoleGuardPlan(id, original));
        var session = live.FirstOrDefault(s => s.Role!.Id == id);
        var role = session?.Role ?? original.Role;
        var before = (session?.Items ?? original.Items).Select(i => i.Clone()).ToList();
        var currency = new Dictionary<int, long>();
        if (session?.Progress is { } progress)
        {
            // Preserve the live snapshot's source token. A pending predecessor can already be
            // committed on disk while its Apply is queued; its reservation still fences admission.
            if (progress.SourceRevision is { } source)
                plan.BindProgressSource(id, progress);
            foreach (var (key, value) in progress.Currency) currency[key] = value;
        }
        else foreach (var pair in original.Currency.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split(':');
            if (kv.Length == 2 && int.TryParse(kv[0], out int key) && long.TryParse(kv[1], out long value)) currency[key] = value;
        }
        var p = new MarketParticipant
        {
            RoleId = id, Role = role, Session = session, Before = before,
            Items = before.Select(i => i.Clone()).ToList(),
            Currency = currency,
        };
        participants.Add(id, p);
        return p;
    }

    public T? Get<T>(string kind, string key)
    {
        return ReadRecord(kind, key) is { } value ? JsonSerializer.Deserialize<T>(value) : default;
    }

    private string? ReadRecord(string kind, string key)
    {
        if (records.TryGetValue((kind, key), out var value)) return value;
        if (!completeKinds.Contains(kind))
        {
            var snapshot = accounts.ReadSync<MarketRecordSnapshot>(new MarketRecordSnapshotReadPlan(kind, key));
            var rows = snapshot.Rows;
            plan.Guard(new MarketRecordGuardPlan(kind, key, snapshot));
            value = rows.Count == 0 ? null : rows[0].Data;
        }
        records[(kind, key)] = value;
        return value;
    }

    public IReadOnlyList<T> All<T>(string kind)
    {
        if (completeKinds.Add(kind))
        {
            var snapshot = accounts.ReadSync<MarketRecordSnapshot>(new MarketRecordSnapshotReadPlan(kind));
            var rows = snapshot.Rows;
            plan.Guard(new MarketRecordGuardPlan(kind, null, snapshot));
            foreach (var row in rows) records.TryAdd((kind, row.Id), row.Data);
        }
        var result = new List<T>();
        foreach (var entry in records.Where(x => x.Key.Kind == kind && x.Value is not null).OrderBy(x => x.Key.Key, StringComparer.Ordinal))
            if (JsonSerializer.Deserialize<T>(entry.Value!) is { } row) result.Add(row);
        return result;
    }

    public void Put<T>(string kind, string key, T value)
    {
        ReadRecord(kind, key);
        string captured = JsonSerializer.Serialize(value);
        records[(kind, key)] = captured;
        plan.Add(new MarketRecordWritePlan(kind, key, captured));
        writes = true;
    }

    public void Delete(string kind, string key)
    {
        ReadRecord(kind, key);
        records[(kind, key)] = null;
        plan.Add(new MarketRecordWritePlan(kind, key, null));
        writes = true;
    }

    public byte[]? LoadBlob(long id, string column)
    {
        CheckColumn(column);
        return blobs.TryGetValue((id, column), out var staged) ? (byte[])staged.Clone() : plan.ReadRoleBlob(accounts, id, column);
    }

    public void SaveBlob(long id, string column, byte[] value)
    {
        CheckColumn(column);
        plan.ReadRoleBlob(accounts, id, column);
        blobs[(id, column)] = (byte[])value.Clone();
        plan.Add(new MarketBlobWritePlan(id, column, value));
        writes = true;
    }

    private static void CheckColumn(string column)
    {
        if (column is not ("trade_info" or "stalls_info")) throw new ArgumentOutOfRangeException(nameof(column));
    }

    private bool staged;
    internal ReservationKey[] ReservationKeys => plan.ReservationKeys;
    internal bool HasWrites => writes;
    internal void StageWrites()
    {
        if (staged) return;
        staged = true;
        foreach (var p in participants.Values)
        {
            plan.Add(new MarketItemsWritePlan(p.RoleId, p.Before, p.Items), result =>
            {
                var guids = (long[])result!;
                for (int i = 0; i < guids.Length; i++) p.Items[i].Guid = guids[i];
            });
            plan.Add(new MarketCurrencyWritePlan(p.RoleId, p.Currency));
            writes = true;
        }
        if (writes && economicCommand)
            plan.WithEscrowEconomy(Economy.Evidence(new
            {
                Records = records.OrderBy(x => x.Key.Kind).ThenBy(x => x.Key.Key)
                    .Select(x => new { x.Key.Kind, x.Key.Key, x.Value }).ToArray(),
                Roles = participants.Keys.Order().ToArray(), MailRecipients = MailRecipients.Order().ToArray()
            }));
    }
    internal void Persist() { StageWrites(); plan.Commit(accounts, "market/transaction"); }
    internal Task<object?[]> PersistAsync(Action? acknowledged = null)
    {
        StageWrites();
        return plan.CommitAsync(accounts, "market/transaction", _ => { acknowledged?.Invoke(); Publish(); });
    }

    /// <summary>Attachment handover may finish while a speculative capture waits. Rebind publication
    /// under the full participant reservation, never to the stale socket captured during discovery.</summary>
    internal void RebindLive(IReadOnlyList<GameSession> current)
    {
        live = current;
        foreach (var participant in participants.Values)
            participant.Session = live.FirstOrDefault(s => s.Role?.Id == participant.RoleId && s.Progress != null);
    }

    internal void Publish()
    {
        foreach (var ((role, column), value) in blobs)
            if (live.FirstOrDefault(s => s.Role?.Id == role) is { } session)
            {
                if (column == "trade_info") session.Trade = Romsg.DBUserTradeInfo.Parser.ParseFrom(value);
                else if (column == "stalls_info") session.Stalls = Romsg.DBStallsInfo.Parser.ParseFrom(value);
            }
        foreach (var p in participants.Values)
        {
            if (p.Session is not { Progress: { } progress } session || session.Role?.Id != p.RoleId) continue;
            foreach (var item in p.Items.Where(i => p.Before.All(b => b.Guid != i.Guid))) session.NewItems.Add(item.Guid);
            session.Items = p.Items;
            progress.Currency.Clear();
            foreach (var (id, amount) in p.Currency) progress.Currency[id] = amount;
            progress.SourceRevision = plan.CommittedVector(p.RoleId, RevisionStore.Progress);
        }
    }

    internal IEnumerable<Func<Task>> CaptureTradeNotices()
    {
        foreach (var ((role, column), _) in blobs)
            if (column == "trade_info" && live.FirstOrDefault(s => s.Role?.Id == role) is { Trade: { } trade } session)
                yield return CharacterService.CaptureEconomicSync(session, TradeService.TradeSync, trade.Clone());
    }
}
