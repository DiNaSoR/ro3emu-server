using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Social outfit (head icon, head frame, chat bubble, title, honor, card; SocialOutfitConfig): AUTO_SYNC 5044 DBSocialOutfit,
/// the defaults only. LC_DAL_SocialOutfit._allDBSocialOutFitInfoProcess (lua-src Logic/Asset/LC_DAL_SocialOutfit.lua:148-347)
/// builds SocialOutfitEquipData from socialOutfitEquip and SocialUnlockInfo from socialOutfitContainer, each only when the
/// map is present. The profile card reads SocialOutfitEquipData[ChangeToTitle / ChangeToHonor] for the own role without a
/// nil check (UI_Sys_Personal_InfoWidgetCtrl._refreshPlayerBaseInfoShow), so without 5044 opening the own Profile raises
/// "attempt to index a nil value" on every refresh.
/// <para>(inferred) Worn: the _iIsDefaultWear row of each type, for the head the one of the role's career and sex (the
/// Novice's when the career has none). Owned, permanently: the _iIsDefaultUnLock rows of the role's sex; the head list
/// itself filters by career and its parents (LC_SocialOutfitHelper.GetHeadAvatarItemList). Nothing is stored: there is no
/// equip request (83480) yet, so the state is the configuration's for the current career.</para>
/// </summary>
public sealed class SocialOutfitService
{
    public const uint Sync = 5044;
    // enum_SocialOutfitType (Lua_ChatDefine.lua): 1 head, 2 head frame, 3 bubble, 4 title, 5 honor, 6 card, 7 theme.
    private const int Head = 1, NoviceJob = 1000;
    // PBSocialOutfitStatus: 0 lock, 1 condition, 2 time limit, 3 permanent.
    private const int Permanent = 3;

    private readonly GameData data;

    public SocialOutfitService(GameData data, CharacterService character)
    {
        this.data = data;
        character.JobChanged.Add(s => s.Synced.ContainsKey(Sync) ? LoginAsync(s) : Task.CompletedTask);
    }

    public Task LoginAsync(GameSession s) => CharacterService.SyncAsync(s, Sync, Build(s.Role!.Sex, s.Progress?.Career ?? s.Role.Career));

    internal DBSocialOutfit Build(int sex, int career)
    {
        var rows = data.Table("SocialOutfitConfig").Values.ToList();
        bool SexOf(JsonElement r) => GameData.Int(r, "_iSex") is var rowSex && (rowSex == 0 || rowSex == sex);
        var info = new DBSocialOutfit();
        foreach (var group in rows.Where(r => GameData.Int(r, "_iIsDefaultWear") == 1 && SexOf(r)).GroupBy(r => (int)GameData.Int(r, "_iType")))
        {
            var worn = group.Key != Head ? group.First()
                : group.FirstOrDefault(r => GameData.Int(r, "_iJobID") == career) is { ValueKind: JsonValueKind.Object } own ? own
                : group.FirstOrDefault(r => GameData.Int(r, "_iJobID") == NoviceJob);
            if (worn.ValueKind == JsonValueKind.Object) info.SocialOutfitEquip[group.Key] = new DBSocialOutfitEquip { CfgId = GameData.Int(worn, "_iID") };
        }
        var owned = rows.Where(r => GameData.Int(r, "_iIsDefaultUnLock") == 1 && SexOf(r))
            .Select(r => ((int)GameData.Int(r, "_iType"), GameData.Int(r, "_iID")))
            .Concat(info.SocialOutfitEquip.Select(e => (e.Key, e.Value.CfgId))).Distinct();
        foreach (var (type, id) in owned)
        {
            if (!info.SocialOutfitContainer.TryGetValue(type, out var container))
                info.SocialOutfitContainer[type] = container = new DBSocialOutfitContainer();
            container.SocialOutfitInfos[id] = new DBSocialOutfitInfo { CfgId = id, Status = Permanent };
        }
        return info;
    }
}
