using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Skills whose button turns into the next skill for a while. In beta footage, Shadow Guard's button becomes Thousand
/// Shadows with a countdown, a press there becomes Shadow Loop, and the button then goes back to Shadow Guard (Steven,
/// 2026-10-19). The data carries it as a "status" buff from each cast whose _kLogicData names the next row:
/// <list type="bullet">
/// <item>Shadow Guard 15323 grants 1532201-1532210 (group 15322 "Thousand Shadows Status", 6 s) naming Thousand
/// Shadows 1532201-1532210; Thousand Shadows grants 1532301-1532310 (group 15323 "Shadow Loop Status") naming Shadow
/// Loop 1532001-1532010. Neither follow-up is in the skill tree: its level is the status buff's.</item>
/// <item>Move back Shadow Echo 15329 grants 1532901-1532905 (group 15329, 6 s) naming its return 1533001-1533005:
/// "Cast again within ${2} seconds to return to the shadow". The return has no move effect of its own, so the
/// player is put back on the shadow as a same-map teleport.</item>
/// </list>
/// While the status lasts, the hotbar slot of the first skill shows the named row (31027, as for a mode switch,
/// <see cref="RefreshTemporarySlotsAsync"/>) and that row can be cast; casting it spends the status. The countdown
/// on the button is the client's own. The client never reads these _kLogicData rows, so the swap is the server's.
/// </summary>
public sealed partial class CombatService
{
    /// <summary>Status buff group -> the skill group whose slot it turns.</summary>
    private static readonly IReadOnlyDictionary<int, int> ChainStatusSlots = new Dictionary<int, int>
    {
        [15322] = 15323, // Thousand Shadows Status on Shadow Guard's slot
        [15323] = 15323, // Shadow Loop Status, still Shadow Guard's slot
        [15329] = 15329, // Move back Shadow Echo's return
    };

    private const string ShadowEchoScript = "RetreatingShadows", ShadowEchoReturnScript = "RetreatingShadows2";

    private void WireChains()
    {
        CastLanded.Add(ChainCastLandedAsync);
        _buffs.Changed.Add(DropOlderChainStatusAsync);
    }

    /// <summary>The live status buff on <paramref name="slotGroup"/>'s slot and the row it offers.</summary>
    private (ActiveBuff Buff, JsonElement Row)? ChainOffer(GameSession s, int slotGroup)
    {
        long now = Now;
        // The newest status wins (Thousand Shadows' Shadow Loop Status over the one it spends).
        foreach (var buff in s.Buffs.OrderByDescending(b => b.ExpireMs == 0 ? long.MaxValue : b.ExpireMs))
        {
            if (buff.ExpireMs != 0 && buff.ExpireMs <= now || _data.Row("BuffConfig", buff.ConfigId) is not { } row
                || !ChainStatusSlots.TryGetValue((int)GameData.Int(row, "_iBuffID"), out int slot) || slot != slotGroup
                || Numbers(row, "_kLogicData") is not [var next, ..] || _data.Row("SkillConfig", (long)next) is not { } offered) continue;
            return (buff, offered);
        }
        return null;
    }

    /// <summary>A follow-up row is castable while a status buff names it.</summary>
    private bool ChainOffers(GameSession s, JsonElement row) => ChainStatusSlots.Values.Distinct()
        .Any(slot => ChainOffer(s, slot) is { } offer && GameData.Int(offer.Row, "_iID") == GameData.Int(row, "_iID"));

    /// <summary>Only the newest status on a slot stays.</summary>
    private async Task DropOlderChainStatusAsync(GameSession s)
    {
        foreach (int slot in ChainStatusSlots.Values.Distinct())
        {
            if (ChainOffer(s, slot) is not { } newest) continue;
            var older = s.Buffs.Where(b => !ReferenceEquals(b, newest.Buff) && _data.Row("BuffConfig", b.ConfigId) is { } row
                && ChainStatusSlots.TryGetValue((int)GameData.Int(row, "_iBuffID"), out int other) && other == slot).ToArray();
            foreach (var buff in older) await _buffs.RemoveAsync(s, buff);
        }
    }

    private async Task ChainCastLandedAsync(GameSession s, JsonElement row)
    {
        long id = GameData.Int(row, "_iID");
        // A follow-up that grants the next status (Thousand Shadows) leaves the spent one to DropOlderChainStatusAsync,
        // so the slot goes straight to the next skill; the last one (Shadow Loop) spends it now and the slot goes back.
        bool continues = Tuples(row, "_kBuff1").Any(tuple => tuple.ValueKind == JsonValueKind.Array && tuple.GetArrayLength() > 0
            && _data.Row("BuffConfig", (long)(GameData.Number(tuple[0]) ?? 0)) is { } granted
            && ChainStatusSlots.ContainsKey((int)GameData.Int(granted, "_iBuffID")));
        if (!continues)
            foreach (int slot in ChainStatusSlots.Values.Distinct())
                if (ChainOffer(s, slot) is { } offer && GameData.Int(offer.Row, "_iID") == id)
                    await _buffs.RemoveAsync(s, offer.Buff);
        string script = Script(row);
        if (script.StartsWith(ShadowEchoScript, StringComparison.Ordinal) && script != ShadowEchoReturnScript)
            s.ShadowEchoPos = s.Position.Clone();
        else if (script == ShadowEchoReturnScript && s.ShadowEchoPos is { } shadow)
        {
            s.ShadowEchoPos = null;
            await _scene.TeleportInMapAsync(s, new MapPoint(shadow.X, shadow.Y, shadow.Z, s.Face));
        }
    }
}
