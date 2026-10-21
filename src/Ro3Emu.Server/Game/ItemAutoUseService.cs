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
/// A refused use is silent and the item stays usable by hand. A passing refusal (dead, a use-forbidding buff, a map
/// limit) is tried again a minute later; any other waits for login or a level change. A second unit on the item's
/// cooldown waits it out.
/// </summary>
public sealed class ItemAutoUseService
{
    private const int AutoUse = 3;

    private readonly CharacterService _character;
    private readonly GameData _data;
    private readonly GameLoop _loop;
    private readonly ILogger<ItemAutoUseService> _log;
    private readonly ConditionalWeakTable<GameSession, State> _states = new();
    private Dictionary<int, bool>? _autoUse;

    /// <summary>When a use refused for a passing reason is tried again (inferred).</summary>
    internal static TimeSpan RefusalRetry { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Refusals that pass on their own: dead, a use-forbidding buff, a map that forbids the item. Any other
    /// refusal (owned look or mount, level, job, use count) lasts until login or a level change.</summary>
    private static readonly HashSet<int> Passing = [ErrorCodes.UseWhileDead, ErrorCodes.ItemUseForbidden, ErrorCodes.ItemUseAreaForbidden];

    private sealed class State
    {
        /// <summary>GUIDs refused for a lasting reason; login and a level change clear it.</summary>
        internal readonly HashSet<long> Refused = new();
        /// <summary>GUIDs waiting on a timer (a cooldown or a passing refusal), which takes them out and looks again.</summary>
        internal readonly HashSet<long> Waiting = new();
        internal bool Scheduled;
    }

    public ItemAutoUseService(CharacterService character, GameData data, GameLoop loop, ILogger<ItemAutoUseService> log)
    {
        this._character = character;
        this._data = data;
        this._loop = loop;
        this._log = log;
        // A use refused below the item's _iLevelNeed is retried when the level changes.
        character.ProgressHandlers.Add(session => { Retry(session); return Task.CompletedTask; });
    }

    /// <summary>Tests of the manual 31323 path (MountTests) keep auto-use items in the bag.</summary>
    internal bool Paused { get; set; }

    /// <summary>Uses tried so far (tests check that a cooldown is waited out, not polled).</summary>
    internal int Attempts => _attempts;
    private int _attempts;

    /// <summary>At the end of login, after every owner snapshot (a use may sync mounts, wardrobe and rewards) and after
    /// GearPromptService.Attach, which replaces the session's BeforeGameSend: watch every 5009 from now on.</summary>
    internal void Attach(GameSession session)
    {
        _states.AddOrUpdate(session, new State());
        session.BeforeGameSend += (msgId, _) => { if (msgId == CharacterService.ItemsSync) SeeBag(session); };
    }

    /// <summary>True for an item the server uses on receipt: _QuickUse [3, 0] with a server-side use effect.</summary>
    internal bool IsAutoUse(int entry)
    {
        _autoUse ??= _data.Table("ItemConfig").Values
            .Where(r => GameData.Int(r, "_iID") > 0)
            .GroupBy(r => (int)GameData.Int(r, "_iID"))
            .ToDictionary(g => g.Key, g => Qualifies(g.First()) && !Tradable(g.First()));
        return _autoUse.GetValueOrDefault(entry);
    }

    /// <summary>Tradable or stall-sellable: ItemConfig._iTrade, or the TradeConfig row the trade and stall checks read.</summary>
    private bool Tradable(JsonElement row) => GameData.Int(row, "_iTrade") != 0
        || _data.Row("TradeConfig", GameData.Int(row, "_iID")) is { } trade && GameData.Int(trade, "_iTrade") != 0;

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
        if (_states.TryGetValue(session, out var state)) state.Refused.Clear();
        SeeBag(session);
    }

    /// <summary>Auto-use items in their bag pack (not the cart or storage), minus those refused or waiting.</summary>
    private IEnumerable<InvItem> Held(GameSession session, State? state) =>
        session.Items.Where(i => i.Grid == 0 && i.Guid > 0 && IsAutoUse(i.Entry)
            && _data.Row("ItemConfig", i.Entry) is { } row && i.Pack == CharacterService.ConfigPack(row)
            && state?.Waiting.Contains(i.Guid) != true && state?.Refused.Contains(i.Guid) != true);

    /// <summary>Leaves a GUID alone for <paramref name="wait"/>, then looks at the bag again if the same role is still
    /// on the same connection.</summary>
    private void RetryLater(GameSession session, State? state, long guid, TimeSpan wait)
    {
        if (state == null || !state.Waiting.Add(guid)) return;
        long role = session.Role?.Id ?? 0;
        var attachment = session.Attachment;
        _loop.Schedule(wait, () =>
        {
            state.Waiting.Remove(guid);
            if (session.Role?.Id == role && ReferenceEquals(session.Attachment, attachment) && attachment?.Cancelled != true)
                SeeBag(session);
            return Task.CompletedTask;
        });
    }

    /// <summary>When the item's own _iCD or its _kCDGroup cooldown ends (Unix ms), whichever is later.</summary>
    private long CooldownEnd(GameSession session, int entry)
    {
        long end = session.ItemCds.GetValueOrDefault(entry);
        if (_data.Row("ItemConfig", entry) is { } row && row.TryGetProperty("_kCDGroup", out var g)
            && g.ValueKind == JsonValueKind.Array && g.GetArrayLength() > 0 && GameData.Number(g[0]) is { } group and > 0)
            end = Math.Max(end, session.ItemGroupCds.GetValueOrDefault((int)group));
        return end;
    }

    private void SeeBag(GameSession session)
    {
        _states.TryGetValue(session, out var state);
        if (Paused || state?.Scheduled == true || !Held(session, state).Any()) return;
        long role = session.Role?.Id ?? 0;
        var attachment = session.Attachment;
        if (role == 0) return;
        if (state != null) state.Scheduled = true;
        // Runs after the sending operation releases the role, on the same reservation the 31323 route uses.
        _loop.TrackLegacyDetached("item auto use", async () =>
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
            bool group = _data.Row("ItemConfig", entry) is { } row && GameData.Int(row, "_iGroupUse") != 0;
            int uses = group ? 1 : stack, count = group ? Math.Min(stack, 999) : 1;
            for (int n = 0; n < uses; n++)
            {
                Interlocked.Increment(ref _attempts);
                int result = await _character.AutoUseItemAsync(session, guid, count);
                if (result == ErrorCodes.Cooldown
                    && CooldownEnd(session, entry) - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() is var left and > 0)
                {
                    // A second unit of the same entry waits out the cooldown instead of staying in the bag.
                    RetryLater(session, state, guid, TimeSpan.FromMilliseconds(left));
                    break;
                }
                if (result == ErrorCodes.Cooldown || Passing.Contains(result))
                {
                    RetryLater(session, state, guid, RefusalRetry);
                    break;
                }
                if (result != 0)
                {
                    state?.Refused.Add(guid);
                    _log.LogInformation("{Name} keeps auto-use item {Item}: use refused ({Result})", session.Role?.Name, entry, result);
                    break;
                }
                _log.LogInformation("{Name} auto-used item {Item} x{Count}", session.Role?.Name, entry, count);
            }
        }
    }
}
