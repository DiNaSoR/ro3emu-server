using System.Text.Json;
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
/// The use runs the normal 31323 path after the grant commits, so cooldowns, level checks and the mount/box owners
/// apply unchanged; a refused use is silent and the item stays usable by hand. Login and level changes retry
/// anything still held (saves from before this existed). A GM !item is not an award: it stays in the bag until one of
/// those, so the manual 31323 path can still be exercised.
/// </summary>
public sealed class ItemAutoUseService
{
    private const int AutoUse = 3;

    private readonly CharacterService character;
    private readonly GameData data;
    private readonly GameLoop loop;
    private readonly ILogger<ItemAutoUseService> log;
    private Dictionary<int, bool>? autoUse;

    public ItemAutoUseService(CharacterService character, GameData data, GameLoop loop, ILogger<ItemAutoUseService> log)
    {
        this.character = character;
        this.data = data;
        this.loop = loop;
        this.log = log;
        character.AwardItemsGained.Add((session, gained) => { if (gained.Any(g => IsAutoUse(g.Entry))) Schedule(session); });
        // A use refused below the item's _iLevelNeed is retried when the level changes.
        character.ProgressHandlers.Add(session => { Login(session); return Task.CompletedTask; });
    }

    /// <summary>True for an item the server uses on receipt: _QuickUse [3, 0] with a server-side use effect.</summary>
    internal bool IsAutoUse(int entry)
    {
        autoUse ??= data.Table("ItemConfig").Values
            .Where(r => GameData.Int(r, "_iID") > 0)
            .GroupBy(r => (int)GameData.Int(r, "_iID"))
            .ToDictionary(g => g.Key, g => Qualifies(g.First()));
        return autoUse.GetValueOrDefault(entry);
    }

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

    /// <summary>After login or a level change: use anything granted before this existed, or refused earlier.</summary>
    internal void Login(GameSession session)
    {
        if (session.Items.Any(i => i.Grid == 0 && IsAutoUse(i.Entry))) Schedule(session);
    }

    /// <summary>Runs after the granting operation releases the role, on the same reservation the 31323 route uses.</summary>
    private void Schedule(GameSession session)
    {
        long role = session.Role?.Id ?? 0;
        var attachment = session.Attachment;
        if (role == 0) return;
        loop.TrackLegacyDetached("item auto use", async () =>
        {
            if (session.Role?.Id != role || !ReferenceEquals(session.Attachment, attachment) || attachment?.Cancelled == true) return;
            await UseHeldAsync(session);
        });
    }

    private async Task UseHeldAsync(GameSession session)
    {
        var pending = session.Items.Where(i => i.Grid == 0 && i.Guid > 0 && IsAutoUse(i.Entry))
            .Select(i => (i.Guid, i.Entry, i.Stack)).ToList();
        foreach (var (guid, entry, stack) in pending)
        {
            bool group = data.Row("ItemConfig", entry) is { } row && GameData.Int(row, "_iGroupUse") != 0;
            int uses = group ? 1 : stack, count = group ? Math.Min(stack, 999) : 1;
            for (int n = 0; n < uses; n++)
            {
                int result = await character.AutoUseItemAsync(session, guid, count);
                if (result != 0)
                {
                    log.LogInformation("{Name} keeps auto-use item {Item}: use refused ({Result})", session.Role?.Name, entry, result);
                    break;
                }
                log.LogInformation("{Name} auto-used item {Item} x{Count}", session.Role?.Name, entry, count);
            }
        }
    }
}
