using System.Text.Json;
using System.Text.RegularExpressions;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Immunities with a limit or a source beyond the category rule (BuffService.Control.Blocks):
/// <list type="bullet">
/// <item>Charged immunity: Hymn of the Lamb's 1430201 (categories 21/20) and its chicken copy 15400901 read "Gain @{1}
/// chances to become immune to movement-impairing status effects" (@{1} = 3, the skill's "Immunity to up to ${3}"). Each
/// debuff it stops spends a chance; the last one ends the buff. A debuff another immunity also stops spends none.</item>
/// <item>King's Grace: "you and the selected allies gain Debuff Immunity" while the Crusader carries the aura (group
/// 11614) or an ally holds its 【King's Grace】 link (11615): every debuff (category 37) is stopped (inferred: the rows
/// carry no immunity category).</item>
/// </list>
/// </summary>
public sealed partial class BuffService
{
    private const int KingsGraceCarrierGroup = 11614, KingsGraceLinkGroup = 11615, DebuffCategory = 37;

    [GeneratedRegex(@"Gain @\{1\} chances to become immune", RegexOptions.IgnoreCase)]
    private static partial Regex ChargedImmunityText();

    /// <summary>The chances a buff row grants (its description's first parameter), or 0 for an unlimited one.</summary>
    internal int ImmunityCharges(JsonElement row) =>
        row.TryGetProperty("_iDescription", out var desc) && desc.ValueKind == JsonValueKind.Array && desc.GetArrayLength() > 1
        && desc[0].TryGetInt64(out long text) && ChargedImmunityText().IsMatch(_data.Text(text))
            ? (int)Math.Max(1, DescriptionNumber(desc, 1, 1)) : 0;

    /// <summary>Does the player shrug this buff off; a charged immunity that stops it spends a chance.</summary>
    private async Task<bool> ImmuneAsync(GameSession p, JsonElement row)
    {
        long now = MonsterService.NowMs;
        var live = p.Buffs.Where(b => b.ExpireMs > now).Select(b => (Buff: b, Row: _data.Row("BuffConfig", b.ConfigId)))
            .Where(x => x.Row != null).Select(x => (x.Buff, Row: x.Row!.Value, Charges: ImmunityCharges(x.Row!.Value))).ToList();
        var unlimited = live.Where(x => x.Charges == 0).SelectMany(x => Categories(x.Row)).ToHashSet();
        if (Blocks(unlimited, row)) return true;
        bool debuff = GameData.Int(row, "_iType") == 2 && Categories(row).Contains(DebuffCategory);
        if (debuff && live.Any(x => GameData.Int(x.Row, "_iBuffID") is KingsGraceCarrierGroup or KingsGraceLinkGroup)) return true;
        foreach (var (buff, buffRow, charges) in live)
        {
            if (charges == 0 || !Blocks(Categories(buffRow), row)) continue;
            int left = (p.ImmunityCharges.TryGetValue(buff.Guid, out int used) ? used : charges) - 1;
            if (left <= 0)
            {
                p.ImmunityCharges.Remove(buff.Guid);
                await RemoveAsync(p, buff);
            }
            else p.ImmunityCharges[buff.Guid] = left;
            return true;
        }
        return false;
    }
}
