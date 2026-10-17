using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Profile card (function 63, Profile, base level 20): AUTO_SYNC 5016 DBPersonalInfo and the editor's 82553 appearance,
/// 82559 current preset and 82560 preset name. LC_DAL_Personal._processPersonalInfo (lua-src Logic/Asset/LC_DAL_Personal.lua:64-241)
/// projects 5016 into PersonalUnlockInfo (unlockInfos), PersonalUpvote (upvoteTimes), PersonalPlanInfo (curPlanId, the
/// presets' names and backgrounds) and PersonalPlayerInfo (planInfos[curPlanId]); the 82553 reply handler is empty (:456-459)
/// and 82559's only raises PersonalChangePBOver (:649-662), so every change reaches the client through 5016. The editor
/// (UI_Sys_PersonalPanelModel.ApplyEditChange, the sticker panel, the friend window's signature and mood) sends the whole
/// preset back in 82553. Other players read the current preset as UserDataService's "Personal" field
/// (LC_DAL_RolePublic.lua:874-900) and its avatar, frame and title through Base.
/// <para>Stored: presets, current preset and upvote times (role blob "personal"). unlockInfos are derived each time from
/// the unlock conditions of the tables the editor checks (theme, background, action).</para>
/// </summary>
public sealed class PersonalService
{
    public const uint Sync = 5016, UpdateAppearance = 82553, UpdateCurPlan = 82559, UpdatePlanName = 82560;
    // ErrorCodeConfig 3351-3367, the profile family.
    public const int ErrConfig = 3351, ErrParameter = 3352, ErrFunction = 3354, ErrLocked = 3357, ErrStickers = 3358,
        ErrPreset = 3363, ErrSignLength = 3364, ErrSignSensitive = 3365, ErrNameLength = 3366, ErrNameSensitive = 3367;
    private const int Function = 63;
    // enum_PersonalUnlockType (Lua_GameDefine.lua:13645-13660); the editor's lists check Theme, BackGround and Action
    // (UI_Sys_Personal_EditTheme/EditBg/EditRoleAction_ListWidgetCtrl via LC_PersonalHelper.CheckItemIsOpen).
    private const int UnlockTheme = 4, UnlockBackground = 5, UnlockAction = 6;
    // PersonalHomepageImageConfig._iImageType = enum_PersonalImageType: 1 theme, 2 background, 3 mood.
    private const int ImageTheme = 1, ImageBackground = 2, ImageMood = 3;
    // EPersonalShowcaseType (PBEnumDefine.lua:1905-1915): 1 card, 2 page (book), 3 honor.
    private const int ShowcaseCard = 1, ShowcasePage = 2, ShowcaseHonor = 3;

    /// <summary>
    /// (inferred) Three presets, ids 1-3: LC_DAL_Personal._getPlanName names only 1-3 (UIWords PersonalPlanName1-3) and
    /// the preset list walks PersonalPlanInfo.m_kPlanInfo with ipairs (UI_Sys_Personal_EditStencilWidgetCtrl._refreshPlanInfo),
    /// so the ids are contiguous from 1.
    /// </summary>
    public const int Presets = 3;

    private readonly GameData data;
    private readonly RoleStore roles;
    private readonly FunctionService functions;
    private readonly CharacterService character;
    private readonly IOptions<ServerOptions> options;

    public PersonalService(GameData data, RoleStore roles, FunctionService functions, CharacterService character,
        IOptions<ServerOptions> options)
    {
        this.data = data; this.roles = roles; this.functions = functions; this.character = character; this.options = options;
        // Action 1002 unlocks at base level 50 (PersonalPerformanceConfig, condition 18000001).
        character.ProgressHandlers.Add(RefreshAsync);
    }

    // ------------------------------------------------------------------ load / 5016

    public async Task LoginAsync(GameSession s)
    {
        var bytes = roles.LoadPersonal(s.Role!.Id);
        var info = bytes is { Length: > 0 } ? DBPersonalInfo.Parser.ParseFrom(bytes) : new DBPersonalInfo();
        Shape(info);
        s.Personal = info;
        await SyncAsync(s);
    }

    /// <summary>
    /// 5016. The full handler walks unlockInfos, each unlockMap, upvoteTimes and planInfos with pairs() unguarded
    /// (LC_DAL_Personal.lua:83-98,144-148,182-204), and lua-protobuf (no_decode_default_array, NW_GameServerHandler.lua:343-345)
    /// decodes an empty map as nil. Unlocks and presets are never empty; a role that upvoted nobody gets a wire-only
    /// upvoteTimes key 0, deleted by the next increment (ParseMapTypeIncrement creates the map before the del), as
    /// SeasonRefineService seeds its empty maps (inferred, PersonalUpvote keeps the harmless roleId 0 entry).
    /// </summary>
    public async Task SyncAsync(GameSession s)
    {
        if (s.Personal == null) return;
        var now = Build(s);
        if (!s.Synced.ContainsKey(Sync) && now.UpvoteTimes.Count == 0)
        {
            var wire = now.Clone();
            wire.UpvoteTimes[0] = 0;
            await CharacterService.SyncAsync(s, Sync, wire);
        }
        await CharacterService.SyncAsync(s, Sync, now);
    }

    /// <summary>1023 replay: a full 5016 again, with the same empty-map seed.</summary>
    public Task ReplayAsync(GameSession s)
    {
        s.Synced.Remove(Sync);
        return SyncAsync(s);
    }

    private Task RefreshAsync(GameSession s) => s.Personal != null && s.Synced.ContainsKey(Sync) ? SyncAsync(s) : Task.CompletedTask;

    private DBPersonalInfo Build(GameSession s)
    {
        var info = s.Personal!.Clone();
        foreach (var (type, ids) in Unlocks(s))
        {
            var unlock = new DBPersonalUnlockInfo();
            foreach (int id in ids) unlock.UnlockMap[id] = true;
            if (unlock.UnlockMap.Count > 0) info.UnlockInfos[type] = unlock;
        }
        return info;
    }

    /// <summary>
    /// The unlocked theme, background and action ids. A row with an _iUnlockCondition is unlocked when the condition holds
    /// (all are base level conditions: [1002,1] for the default theme, background and idle action, [1002,50] for walk).
    /// A row without one (0) is obtained elsewhere (_kImageObtain: the Fashion Shop, Growth Support rewards), which this
    /// server does not sell yet, so it stays locked (inferred).
    /// </summary>
    private IEnumerable<(int Type, List<int> Ids)> Unlocks(GameSession s)
    {
        var images = data.Table("PersonalHomepageImageConfig").Values;
        yield return (UnlockTheme, images.Where(r => GameData.Int(r, "_iImageType") == ImageTheme && Unlocked(s, r)).Select(Id).ToList());
        yield return (UnlockBackground, images.Where(r => GameData.Int(r, "_iImageType") == ImageBackground && Unlocked(s, r)).Select(Id).ToList());
        yield return (UnlockAction, data.Table("PersonalPerformanceConfig").Values.Where(r => Unlocked(s, r)).Select(Id).ToList());
    }

    private static int Id(JsonElement row) => (int)GameData.Int(row, "_iID");

    private bool Unlocked(GameSession s, JsonElement row) =>
        GameData.Int(row, "_iUnlockCondition") is var condition and > 0 && s.Progress != null && functions.ConditionMet(s, condition);

    private bool IsUnlocked(GameSession s, int type, int id) => Unlocks(s).Any(u => u.Type == type && u.Ids.Contains(id));

    /// <summary>Every preset present, the current one valid: a missing preset is the default card.</summary>
    private void Shape(DBPersonalInfo info)
    {
        for (int plan = 1; plan <= Presets; plan++)
            if (!info.PlanInfos.ContainsKey(plan)) info.PlanInfos[plan] = DefaultPlan();
        if (info.CurPlanId is < 1 or > Presets) info.CurPlanId = 1;
    }

    /// <summary>
    /// The server-side defaults of PublicParamConfig, which the client never reads (inferred to be the original server's):
    /// PersonalDefaultTheme 1001, PersonalDefaultBackground 2001, PersonalEmoEmpty 3001 (the "no mood" image) and
    /// PersonalShowcaseDefault [6301]. 6301 is FunctionConfig "Profile Card Album Display", the card showcase.
    /// </summary>
    private PersonalInfo DefaultPlan()
    {
        var plan = new PersonalInfo
        {
            ThemeId = (int)Param("PersonalDefaultTheme", 1001), BackgroundId = (int)Param("PersonalDefaultBackground", 2001),
            Mood = (int)Param("PersonalEmoEmpty", 3001),
        };
        foreach (long function in ParamList("PersonalShowcaseDefault"))
            if (ShowcaseOf(function) is { } showcase && !plan.ShowcaseVisibleList.Contains(showcase)) plan.ShowcaseVisibleList.Add(showcase);
        return plan;
    }

    /// <summary>LC_DAL_Personal.FunctionTypeToEPersonalShowcaseType (:845-870), plus the 630x display functions (inferred).</summary>
    private static int? ShowcaseOf(long function) => function switch
    {
        6301 or 630331 => ShowcaseCard,
        6302 or 630332 => ShowcasePage,
        6307 => ShowcaseHonor,
        _ => null,
    };

    private double Param(string key, double fallback) =>
        data.Table("PublicParamConfig").TryGetValue(key, out var value) && GameData.Number(value) is { } number ? number : fallback;

    private IEnumerable<long> ParamList(string key) =>
        data.Table("PublicParamConfig").TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).Where(v => v > 0) : [];

    // ------------------------------------------------------------------ public card

    /// <summary>The current preset, for Base (avatar, frame, title) and the "Personal" field of others' view.</summary>
    public static PersonalInfo? Current(DBPersonalInfo? info) =>
        info != null && info.PlanInfos.TryGetValue(info.CurPlanId, out var plan) ? plan : null;

    /// <summary>
    /// Whether the client can parse the card: LC_DAL_Personal.ParsePersonalInfo walks decalInfos and showcaseVisibleList with
    /// pairs() unguarded (:724-747), and an empty repeated field decodes as nil. A card without stickers or showcases
    /// would abort LC_DAL_RolePublic's whole public-card parse (:874-900, before Library, GuildSnap and Team), so it is
    /// left out of the public data. The own 5016 has no such choice: an unpatched client builds no PersonalPlayerInfo for
    /// it; Ro3Emu.ClientHooks.PersonalCards reads the missing lists as empty. Kept until that patch is proven in game.
    /// </summary>
    public static bool Parseable(PersonalInfo plan) => plan.DecalInfos.Count > 0 && plan.ShowcaseVisibleList.Count > 0;

    // ------------------------------------------------------------------ 82553 / 82559 / 82560

    /// <summary>
    /// 82553: the whole preset as the editor holds it. Checked against what the editor itself enforces (unlocked theme,
    /// background and actions; PersonalMaxSticker 20 stickers, as many of one sticker as owned, LC_ItemHelper.GetItemCount;
    /// PersonalMaxCabinet 3 showcases; PersonalMaxCharacter 1 figure; PersonalSignatureLimit 30). The preset name is kept:
    /// the editor sends the localized default name for an unnamed preset, and 82560 renames (inferred).
    /// </summary>
    public async Task UpdateAppearanceAsync(GameSession s, UpdatePersonalAppearanceReq req)
    {
        using var held = await CharacterService.Lock(s);
        int result = Gate(s);
        if (result == 0 && !Preset(req.PlanId)) result = ErrPreset;
        if (result == 0 && req.Info == null) result = ErrParameter;
        if (result == 0) result = Validate(s, req.Info!);
        if (result == 0)
        {
            var next = s.Personal!.Clone();
            var plan = req.Info!.Clone();
            plan.PlanName = next.PlanInfos[req.PlanId].PlanName;
            next.PlanInfos[req.PlanId] = plan;
            await CommitAsync(s, next, "appearance");
        }
        await s.SendGameAsync(UpdateAppearance, new UpdatePersonalAppearanceResp { Result = result, Req = req });
        if (result == 0) await SyncAsync(s);
    }

    /// <summary>82559: the shown preset. The reply echoes planId; the client only raises PersonalChangePBOver.</summary>
    public async Task UpdateCurPlanAsync(GameSession s, UpdateCurPersonalPlanReq req)
    {
        using var held = await CharacterService.Lock(s);
        int result = Gate(s);
        if (result == 0 && !Preset(req.PlanId)) result = ErrPreset;
        if (result == 0 && s.Personal!.CurPlanId != req.PlanId)
        {
            var next = s.Personal.Clone();
            next.CurPlanId = req.PlanId;
            await CommitAsync(s, next, "current");
        }
        await s.SendGameAsync(UpdateCurPlan, new UpdateCurPersonalPlanResp { Result = result, PlanId = req.PlanId });
        if (result == 0) await SyncAsync(s);
    }

    /// <summary>
    /// 82560: rename a preset. UI_Com_InputContentPanel caps the input at PersonalTemplateLimit (7) and refuses an empty
    /// one (UI_Sys_Personal_EditStencil_ListWidgetCtrl._onEditClick); LC_DAL_Personal registers no reply handler, the
    /// name arrives with 5016 (PersonalPlanInfo.kPlanName).
    /// </summary>
    public async Task UpdatePlanNameAsync(GameSession s, UpdatePersonalPlanNameReq req)
    {
        using var held = await CharacterService.Lock(s);
        string name = req.PlanName ?? "";
        int length = new StringInfo(name).LengthInTextElements;
        int result = Gate(s);
        if (result == 0 && !Preset(req.PlanId)) result = ErrPreset;
        if (result == 0 && (name.Trim().Length == 0 || length > Param("PersonalTemplateLimit", 7))) result = ErrNameLength;
        if (result == 0 && Sensitive(name)) result = ErrNameSensitive;
        if (result == 0 && s.Personal!.PlanInfos[req.PlanId].PlanName != name)
        {
            var next = s.Personal.Clone();
            next.PlanInfos[req.PlanId].PlanName = name;
            await CommitAsync(s, next, "name");
        }
        await s.SendGameAsync(UpdatePlanName, new UpdatePersonalPlanNameResp { Result = result, Req = req });
        if (result == 0) await SyncAsync(s);
    }

    private int Gate(GameSession s) => s.Personal == null || s.Progress == null ? ErrorCodes.FeatureUnavailable
        : !functions.IsOpen(s, Function) ? ErrFunction : 0;

    private static bool Preset(int plan) => plan is >= 1 and <= Presets;

    private int Validate(GameSession s, PersonalInfo info)
    {
        if (!IsUnlocked(s, UnlockTheme, info.ThemeId) || !IsUnlocked(s, UnlockBackground, info.BackgroundId)) return ErrLocked;
        // Mood: a PersonalHomepageImageConfig mood image whose condition holds (all are [1002,1]); 0 is no mood.
        if (info.Mood != 0 && (data.Row("PersonalHomepageImageConfig", info.Mood) is not { } mood
            || GameData.Int(mood, "_iImageType") != ImageMood)) return ErrConfig;
        if (info.Mood != 0 && !Unlocked(s, data.Row("PersonalHomepageImageConfig", info.Mood)!.Value)) return ErrLocked;
        if (info.AvatarId != 0 && data.Row("PersonalAvatarConfig", info.AvatarId) == null) return ErrConfig;
        if (info.AvatarFrameId != 0 && data.Row("PersonalAvatarFrameConfig", info.AvatarFrameId) == null) return ErrConfig;
        // PersonalTitleConfig exports no rows, so no title can be chosen.
        if (info.PersonalTitleId != 0 && data.Row("PersonalTitleConfig", info.PersonalTitleId) == null) return ErrConfig;
        if (new StringInfo(info.Sign ?? "").LengthInTextElements > Param("PersonalSignatureLimit", 30)) return ErrSignLength;
        if (Sensitive(info.Sign ?? "")) return ErrSignSensitive;
        if (info.ShowcaseVisibleList.Count > Param("PersonalMaxCabinet", 3)
            || info.ShowcaseVisibleList.Any(v => v is < ShowcaseCard or > ShowcaseHonor)
            || info.ShowcaseVisibleList.Distinct().Count() != info.ShowcaseVisibleList.Count) return ErrParameter;
        if (info.RoleInfos.Count > Param("PersonalMaxCharacter", 1)) return ErrParameter;
        // The pose list is PerformanceConfig (UI_Sys_Personal_EditRoleAction_ListWidgetCtrl reads m_kCFG_PerformanceConfig),
        // so a pose is one of the role's unlocked emotes (5006 emojiUnlocks); the client preselects one (50001) unasked.
        if (info.RoleInfos.Any(r => r.ActionId != 0 && s.EmojiPlay?.EmojiUnlocks.Contains(r.ActionId) != true
            && !IsUnlocked(s, UnlockAction, r.ActionId))) return ErrLocked;
        if (info.DecalInfos.Count > Param("PersonalMaxSticker", 20)) return ErrParameter;
        // Stickers are currencies of item subtype 123 (ItemSubTypePersonalDecal); each placement uses one.
        foreach (var group in info.DecalInfos.GroupBy(d => d.DecalId))
        {
            if (data.Row("ItemConfig", group.Key) is not { } item || GameData.Int(item, "_iSubType") != 123) return ErrConfig;
            if (character.CountOf(s, group.Key) < group.Count()) return ErrStickers;
        }
        return 0;
    }

    private bool Sensitive(string text) => options.Value.GuildBlockedWords.Any(word =>
        !string.IsNullOrWhiteSpace(word) && text.Contains(word, StringComparison.OrdinalIgnoreCase));

    private async Task CommitAsync(GameSession s, DBPersonalInfo next, string action)
    {
        var transaction = roles.NewTransaction();
        transaction.RoleBlob(s.Role!.Id, "personal", next.ToByteArray());
        await roles.CommitAsync(transaction, "personal/" + action, _ => s.Personal = next);
    }
}
