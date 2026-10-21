using System.Runtime.CompilerServices;
using System.Text.Json;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Items whose ItemConfig._QuickUse is [3, 0] are used as soon as they reach the bag. 3 is enum_FastUseType.AutoUse
/// (Lua_GameDefine) and the second slot its delay (Lua_CFG_ItemConfig.GetAutoUseSec). For a non-equipment item with a
/// zero delay LC_FastUseManager.AddFastUse neither queues the quick-use prompt nor sends a use (the countdown prompt is
/// only for a non-zero delay), so the client leaves the item to the server. The data agrees: Rhine's mount reward
/// (DropGroup 1216, e.g. 1501014 "[201,0,60030020]") is followed on PC by GuideConfig 1811-1821 (task 10101251), which
/// start at the wardrobe with the mount already unlocked; the guides that walk the player through using the item
/// (42-47) are closed (_iIsOpen 0). Using it on receipt is inferred from that.
/// A voucher that can be traded or sold stays in the bag for its owner to use, trade or list (a project rule, Steven
/// 2026-10-21): ItemConfig._iTrade or TradeConfig._iTrade nonzero. That leaves 50 of the 159 [3, 0] items with a use
/// effect: 12 look vouchers (action 200, e.g. the Silverwing piece 3045501), 31 mount vouchers (201) and 7 pet hatchery
/// EXP items (603). The other 109 look vouchers, a Fashion suit's bonus vouchers 3016101, 3016701 and 3025101 among
/// them, are _iTrade 2.
/// Receipt is any 5009 bag sync that shows such an item, so every way into the bag counts (award, shop, gift claim,
/// mail, trade, auction, crafting, GM grant) without each path calling this. The client sees a new item only through
/// 5009, so no path can skip it. The use runs the normal 31323 path after the sync, so cooldowns, level checks and the
/// mount/box owners apply unchanged. Only the item's bag pack counts: a cart or storage copy is the player's to move.
/// A refused use is silent and the item stays usable by hand; it is retried at login, when the level changes, and by
/// the first sync a minute after the refusal. A second unit on the item's _iCD waits out the cooldown.
/// </summary>
public sealed class ItemAutoUseService
{
    private const int AutoUse = 3;

    private readonly CharacterService character;
    private readonly GameData data;
    private readonly GameLoop loop;
    private readonly ILogger<ItemAutoUseService> log;
    private readonly ConditionalWeakTable<GameSession, State> states = new();
    private Dictionary<int, bool>? autoUse;

    /// <summary>How long a refused use is left alone before a later 5009 tries it again (inferred): a refusal can be
    /// passing (dead, a use-forbidding buff, a map limit) or lasting (the look is owned).</summary>
    internal static TimeSpan RefusalRetry { get; set; } = TimeSpan.FromSeconds(60);

    private sealed class State
    {
        /// <summary>GUID -> when a refused use may be tried again; login and a level change clear it.</summary>
        internal readonly Dictionary<long, DateTimeOffset> Refused = new();
        /// <summary>GUIDs waiting out the item's _iCD; their timer takes them out and looks at the bag again.</summary>
        internal readonly HashSet<long> Waiting = new();
        internal bool Scheduled;
    }

    public ItemAutoUseService(CharacterService character, GameData data, GameLoop loop, ILogger<ItemAutoUseService> log)
    {
        this.character = character;
        this.data = data;
        this.loop = loop;
        this.log = log;
        // A use refused below the item's _iLevelNeed is retried when the level changes.
        character.ProgressHandlers.Add(session => { Retry(session); return Task.CompletedTask; });
    }

    /// <summary>Tests of the manual 31323 path (MountTests) keep auto-use items in the bag.</summary>
    internal bool Paused { get; set; }

    /// <summary>Uses tried so far (tests check that a cooldown is waited out, not polled).</summary>
    internal int Attempts => attempts;
    private int attempts;

    /// <summary>At the end of login, after every owner snapshot (a use may sync mounts, wardrobe and rewards) and after
    /// GearPromptService.Attach, which replaces the session's BeforeGameSend: watch every 5009 from now on.</summary>
    internal void Attach(GameSession session)
    {
        states.AddOrUpdate(session, new State());
        session.BeforeGameSend += (msgId, _) => { if (msgId == CharacterService.ItemsSync) SeeBag(session); };
    }

    /// <summary>True for an item the server uses on receipt: _QuickUse [3, 0] with a server-side use effect.</summary>
    internal bool IsAutoUse(int entry)
    {
        autoUse ??= data.Table("ItemConfig").Values
            .Where(r => GameData.Int(r, "_iID") > 0)
            .GroupBy(r => (int)GameData.Int(r, "_iID"))
            .ToDictionary(g => g.Key, g => Qualifies(g.First()) && !Tradable(g.First()));
        return autoUse.GetValueOrDefault(entry);
    }

    /// <summary>Tradable or stall-sellable: ItemConfig._iTrade, or the TradeConfig row the trade and stall checks read.</summary>
    private bool Tradable(JsonElement row) => GameData.Int(row, "_iTrade") != 0
        || data.Row("TradeConfig", GameData.Int(row, "_iID")) is { } trade && GameData.Int(trade, "_iTrade") != 0;

    private static bool Qualifies(JsonElement row)
    {
        if (!row.TryGetProperty("_QuickUse", out var quick) || quick.ValueKind != JsonValueKind.Array
            || quick.GetArrayLength() == 0 || GameData.Number(quick[0]) != AutoUse
            || quick.GetArrayLength() > 1 && (GameData.Number(quick[1]) ?? 0) != 0) return false;
        if (GameData.Int(row, "_iIsUse") == 0) return false;
        // The one listed card chest has an empty effect list: nothing to run, so it stays a bag item.
        return row.TryGetProperty("_iUseEffect", out var effects) && effects.ValueKind == JsonValueKind.Array
            && effects.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.Array && e.GetArrayLength() > 0
                && GameData.Number(e[0]) is > 0);
    }

    /// <summary>After login or a level change: use anything held, including uses refused earlier.</summary>
    internal void Login(GameSession session) => Retry(session);

    private void Retry(GameSession session)
    {
        if (states.TryGetValue(session, out var state)) state.Refused.Clear();
        SeeBag(session);
    }

    /// <summary>Auto-use items in their bag pack (not the cart or storage), minus those refused or on cooldown.</summary>
    private IEnumerable<InvItem> Held(GameSession session, State? state)
    {
        var now = DateTimeOffset.UtcNow;
        return session.Items.Where(i => i.Grid == 0 && i.Guid > 0 && IsAutoUse(i.Entry)
            && data.Row("ItemConfig", i.Entry) is { } row && i.Pack == CharacterService.ConfigPack(row)
            && state?.Waiting.Contains(i.Guid) != true
            && !(state != null && state.Refused.TryGetValue(i.Guid, out var until) && until > now));
    }

    private void SeeBag(GameSession session)
    {
        states.TryGetValue(session, out var state);
        if (Paused || state?.Scheduled == true || !Held(session, state).Any()) return;
        long role = session.Role?.Id ?? 0;
        var attachment = session.Attachment;
        if (role == 0) return;
        if (state != null) state.Scheduled = true;
        // Runs after the sending operation releases the role, on the same reservation the 31323 route uses.
        loop.TrackLegacyDetached("item auto use", async () =>
        {
            bool live() => session.Role?.Id == role && ReferenceEquals(session.Attachment, attachment) && attachment?.Cancelled != true;
            try { if (live()) await UseHeldAsync(session, state); }
            finally { if (state != null) state.Scheduled = false; }
            if (live()) SeeBag(session); // anything that arrived while the uses ran
        });
    }

    private async Task UseHeldAsync(GameSession session, State? state)
    {
        var pending = Held(session, state).Select(i => (i.Guid, i.Entry, i.Stack)).ToList();
        foreach (var (guid, entry, stack) in pending)
        {
            bool group = data.Row("ItemConfig", entry) is { } row && GameData.Int(row, "_iGroupUse") != 0;
            int uses = group ? 1 : stack, count = group ? Math.Min(stack, 999) : 1;
            for (int n = 0; n < uses; n++)
            {
                Interlocked.Increment(ref attempts);
                int result = await character.AutoUseItemAsync(session, guid, count);
                if (result == ErrorCodes.Cooldown && session.ItemCds.GetValueOrDefault(entry) is var end and > 0)
                {
                    // A second unit of the same entry waits out the item's _iCD instead of staying in the bag.
                    var wait = TimeSpan.FromMilliseconds(Math.Max(1, end - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                    if (state == null || state.Waiting.Add(guid))
                        loop.Schedule(wait, () => { state?.Waiting.Remove(guid); SeeBag(session); return Task.CompletedTask; });
                    break;
                }
                if (result != 0)
                {
                    if (state != null) state.Refused[guid] = DateTimeOffset.UtcNow + RefusalRetry;
                    log.LogInformation("{Name} keeps auto-use item {Item}: use refused ({Result})", session.Role?.Name, entry, result);
                    break;
                }
                log.LogInformation("{Name} auto-used item {Item} x{Count}", session.Role?.Name, entry, count);
            }
        }
    }
}
