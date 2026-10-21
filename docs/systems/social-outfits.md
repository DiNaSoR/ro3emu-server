# Social outfits

Social outfits (client `SocialOutfit`) are the cosmetic slots around a role's name: head icon, head frame, chat
bubble, title, honor, card and theme. Each slot wears one owned outfit; outfits are owned by default or unlocked by
items, for good or for a time. Server: `SocialOutfitService`, route in `SocialMessages`, tests in `PersonalTests`
and `ItemActionTests`.

This is not the rank ladder in the Role panel (see [titles.md](titles.md)) or the profile card's theme and background
(see [profile-card.md](profile-card.md)).

## Client sources

| Piece | Client |
|---|---|
| State | 5044 `DBSocialOutfit` auto-sync (`LC_DAL_SocialOutfit._allDBSocialOutFitInfoProcess`, `LC_DAL_SocialOutfit.lua:148-347`): `socialOutfitEquip`, `socialOutfitContainer`, each timed entry's `expireTimestamp` (unix seconds) |
| Requests | 83480 `EquipSocialOutfitReq {cfgId, type}` (empty reply handler `_processEquip`), 83481 `RefreshSocialOutfitReq` (no reply handler) |
| Unlock notice | the 5044 container diff raises "Successfully activated" (72015, `LC_DAL_SocialOutfit.lua:350-511`) |
| Panels | `UI_Sys_Chat_Wear_WidgetBaseCtrl` (wear panels, countdown), the profile editor's bottom bar, `UI_Sys_Personal_InfoWidgetCtrl._refreshPlayerBaseInfoShow` |
| Others' view | global cache `Base` `RoleAdditionBaseInfo.socialOutfitData` → `LC_DAL_RolePublic.ParseRolePublicBaseInfo` |
| Config | `SocialOutfitConfig` (`_iType`, `_iSex`, `_iIsDefaultWear`, `_iIsDefaultUnLock`), `SocialOutfitWearConfig` (`_iSocialOutfit`, `_iTimeLimit`) |
| Enums | `enum_SocialOutfitType` (`Lua_ChatDefine.lua`): 1 head, 2 head frame, 3 bubble, 4 title, 5 honor, 6 card, 7 theme; `PBSocialOutfitStatus`: 0 lock, 1 condition, 2 time limit, 3 permanent |

## Behaviour

- **Defaults** *(inferred)*: each slot wears its `_iIsDefaultWear` row; for the head, the one of the role's career and
  sex, or the Novice's (job 1000) when the career has none. The `_iIsDefaultUnLock` rows of the role's sex are owned for
  good. Defaults are derived from the configuration on every sync, not stored.
- **Always sent:** the profile card indexes the worn title and honor without a nil check, so 5044 goes out at login
  even when nothing was ever unlocked.
- **Unlock** by item action 349 `[349, SocialOutfitWearConfig id]`. The wear row names the outfit and its duration in
  seconds (`_iTimeLimit`, -1 for permanent; 50015 and 50019 are the two 24 hour honors). An item may list one row per
  sex (1910003 "Composer" names heads 100003 male and 100004 female): only the role's sex applies, and an item with
  none for it answers 174. *(inferred)* An outfit already owned for good answers 7056 and keeps the item; a timed one
  used again extends from its current end; a permanent one replaces a timed one. The client checks nothing before
  using the item.
- **Wear** (83480): the panels send it only for an unlocked outfit. *(inferred)* An outfit not owned, of another type
  or of the other sex answers Parameter. The worn state reaches the client through the 5044 diff.
- **Expiry:** the client never drops a timed outfit itself. The server arms a deadline at the earliest end, removes
  what ran out and resyncs; a worn outfit that ran out falls back to the slot's default. 83481 does the same pruning
  on request.
- **Saved** as the role blob `social_outfit`: the item unlocks and the worn choices, laid over the defaults. A worn
  choice no longer owned falls back to the default.
- **Other players** see the worn outfits in `RoleAdditionBaseInfo.socialOutfitData`, defaults included, because the
  chat bubble, honor and profile card index them unguarded. There is no push: they see a change on their next fetch.

## Limits

- Items with action 349 are the only source of outfits beyond the defaults.
- The server never produces the condition status (1); every owned outfit is permanent or timed.
