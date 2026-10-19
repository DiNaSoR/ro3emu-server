using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The title rank ladder (function 14, Role panel -> Title tab, LC_DAL_Title / UI_Sys_Role_TitleWidgetCtrl). A role holds
/// one TitleConfig id and climbs one row at a time: 43102 TitleLevelReq {} asks for the next row (_iNextLev), whose
/// _iLev (base level), _iPower (PlayerChangePower.maxHistoryPower) and _kSpend items the client checks before sending
/// (OnClickUpgrade). The answer is TitleLevelRsp {result, title_id}, but the UI moves only on the 5031 DBTitleInfo
/// autosync (full: SetTitleId, update: the upgrade effect). A titleId of 0 shows an empty list, so every role holds at
/// least row 1 "Beginner" (level 0, power 0, no cost; inferred default). The title's effect is its _iPowerAdd in the
/// power package (PowerService.TitleScore; every _kAttr is 0). Other players read it as RoleAdditionBaseInfo.title
/// (global cache "Base", the head-info Title line and the chat icon).
/// </summary>
public sealed class TitleService(GameData data, RoleStore roles, CharacterService character, PowerService power)
{
    public const uint Upgrade = 43102, Sync = 5031;
    public const long FirstTitle = 1;

    public static long Current(GameSession s) => s.Title?.TitleId is > 0 and var id ? id : FirstTitle;

    /// <summary>The stored title (blob "title"), or the first row.</summary>
    public static long Stored(byte[]? blob) =>
        blob is { Length: > 0 } && DBTitleInfo.Parser.ParseFrom(blob).TitleId is > 0 and var id ? id : FirstTitle;

    public void Load(GameSession s) => s.Title = new DBTitleInfo { TitleId = Stored(roles.LoadTitle(s.Role!.Id)) };

    public Task LoginAsync(GameSession s) => SyncAsync(s);

    private static Task SyncAsync(GameSession s) => CharacterService.SyncAsync(s, Sync, s.Title!.Clone());

    /// <summary>43102.</summary>
    public async Task UpgradeAsync(GameSession s)
    {
        int result;
        long title = Current(s);
        using (await CharacterService.Lock(s))
        {
            long next = data.Row("TitleConfig", title) is { } row ? GameData.Int(row, "_iNextLev") : 0;
            if (next <= 0 || data.Row("TitleConfig", next) is not { } target) result = ErrorCodes.FeatureUnavailable;
            else if (s.Progress!.Level < GameData.Int(target, "_iLev")) result = ErrorCodes.Parameter;
            else if (PowerService.Total(PowerService.History(s)) < GameData.Int(target, "_iPower")) result = ErrorCodes.Parameter;
            else
            {
                var cost = GameData.Pairs(target, "_kSpend").Select(p => ((int)p.Id, (long)p.Value)).ToList();
                var updated = new DBTitleInfo { TitleId = next };
                var paid = await character.ExchangeLockedAsync(s, cost, [],
                    persist: tx => tx.RoleBlob(s.Role!.Id, "title", updated.ToByteArray()), committed: () => s.Title = updated);
                result = paid == null ? character.ShortageOf(s, cost) : 0;
                if (result == 0)
                {
                    title = next;
                    await SyncAsync(s);
                    await power.PublishAsync(s);
                }
            }
        }
        await s.SendGameAsync(Upgrade, new TitleLevelRsp { Result = result, TitleId = title });
    }
}
