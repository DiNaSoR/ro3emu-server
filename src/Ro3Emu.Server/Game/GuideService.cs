using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Tutorial guides (LC_GuideManager): the client runs them and reports 84000 UPDATE_GUIDE {guideIds, records} and 31210
/// CLIENT_COMPLETE_GUIDE {guildId (sic)}; the server keeps the finished set in DBGuideInfo (AUTO_SYNC 5055), which the
/// client reads at login so a finished guide does not run again. Without it every login replays the guides.
/// Both guideIds and the record keys are GuideConfig _iID values (LC_GuideManager sends its finished and triggered
/// lists), so ids the table doesn't have are dropped, and the stored set and each message are capped: a modified client
/// could otherwise send ~200k ids per frame and grow the blob that every login re-sends.
/// </summary>
public sealed class GuideService(RoleStore roles, GameData data, ILogger<GuideService> log)
{
    public const uint GuideInfoSync = 5055;
    public const uint UpdateGuide = 84000;
    public const uint CompleteGuide = 31210;

    /// <summary>Ids read from one 84000 message (and record entries); the rest are ignored. GuideConfig has ~160 rows.</summary>
    internal const int MaxPerMessage = 256;

    /// <summary>Finished ids and records kept per role; only reached without client data, where ids can't be checked.</summary>
    internal const int MaxStored = 1024;

    /// <summary>
    /// LC_DAL_Guide._allDBGuideInfoProcess walks finishedGuideIds / guideFinishRecord without nil checks, and empty
    /// fields arrive as nil (inferred from the bag datasets): a fresh role carries this id, which no GuideConfig row uses.
    /// </summary>
    private const long Placeholder = 0;

    public async Task LoginAsync(GameSession session)
    {
        var saved = roles.LoadGuides(session.Role!.Id);
        var guides = saved != null ? DBGuideInfo.Parser.ParseFrom(saved) : new DBGuideInfo();
        session.Guides = Clean(guides, Known);
        if (saved != null && !guides.Equals(session.Guides)) roles.SaveGuides(session.Role.Id, session.Guides.ToByteArray());
        await SyncAsync(session);
    }

    public async Task UpdateAsync(GameSession session, UpdateGuideReq req)
    {
        if (session.Guides == null) return;
        if (Apply(session.Guides, req.GuideIds.Take(MaxPerMessage), req.Records.Take(MaxPerMessage), Known))
            await SaveAndSyncAsync(session, req.GuideIds.Count);
    }

    public async Task CompleteAsync(GameSession session, ClientCompleteGuideReq req)
    {
        if (session.Guides == null) return;
        if (Apply(session.Guides, [req.GuildId], [], Known)) await SaveAndSyncAsync(session, 1);
    }

    /// <summary>A GuideConfig row id; any positive id when the server runs without client data.</summary>
    private bool Known(long id)
    {
        if (id <= Placeholder) return false;
        var table = data.Table("GuideConfig");
        return table.Count == 0 || table.ContainsKey(id.ToString());
    }

    /// <summary>Adds the known finished ids and records to <paramref name="guides"/> up to the caps; true if it changed.</summary>
    internal static bool Apply(DBGuideInfo guides, IEnumerable<long> ids, IEnumerable<KeyValuePair<long, long>> records, Func<long, bool> known)
    {
        bool changed = false;
        var finished = guides.FinishedGuideIds.ToHashSet();
        foreach (var id in ids)
        {
            if (finished.Count >= MaxStored) break;
            if (!known(id) || !finished.Add(id)) continue;
            guides.FinishedGuideIds.Add(id);
            changed = true;
        }
        foreach (var (id, value) in records)
        {
            if (!known(id)) continue;
            if (guides.GuideFinishRecord.TryGetValue(id, out var old) ? old == value : guides.GuideFinishRecord.Count >= MaxStored) continue;
            guides.GuideFinishRecord[id] = value;
            changed = true;
        }
        return changed;
    }

    /// <summary>A saved blob reduced to known, distinct ids within the caps (roles saved before the limits existed).</summary>
    internal static DBGuideInfo Clean(DBGuideInfo saved, Func<long, bool> known)
    {
        var clean = new DBGuideInfo { Ro3DefineSyncVersion = saved.Ro3DefineSyncVersion };
        Apply(clean, saved.FinishedGuideIds, saved.GuideFinishRecord, known);
        return clean;
    }

    private async Task SaveAndSyncAsync(GameSession session, int sent)
    {
        var guides = session.Guides!;
        roles.SaveGuides(session.Role!.Id, guides.ToByteArray());
        log.LogDebug("{Name} guides: {Sent} reported, {Finished} finished, {Records} records", session.Role.Name, sent,
            guides.FinishedGuideIds.Count, guides.GuideFinishRecord.Count);
        await SyncAsync(session);
    }

    private static Task SyncAsync(GameSession session)
    {
        var now = session.Guides!.Clone();
        if (now.FinishedGuideIds.Count == 0) now.FinishedGuideIds.Add(Placeholder);
        if (now.GuideFinishRecord.Count == 0) now.GuideFinishRecord[Placeholder] = 0;
        return CharacterService.SyncAsync(session, GuideInfoSync, now);
    }
}
