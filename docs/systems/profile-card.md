# Profile card

The profile card (client `Personal`, function 63 "Profile", base level 20) is the page other players open from a
role's head or name: a theme, a background, a mood, a signature, a pose, stickers and up to three showcases, kept as
three presets. Friend gifts give it popularity and other players can like it. Server: `PersonalService`, route in
`SocialMessages`, others' view in `UserDataService`, tests in `PersonalTests`.

The head icon, frame, chat bubble, title and honor shown on the card are social outfits, not part of the card: see
[social-outfits.md](social-outfits.md).

## Client sources

| Piece | Client |
|---|---|
| State | 5016 `DBPersonalInfo` auto-sync (`LC_DAL_Personal._processPersonalInfo`, `LC_DAL_Personal.lua:64-241`): `unlockInfos`, `upvoteTimes`, `curPlanId`, `planInfos` |
| Editor | 82553 `UpdateAppearance` (the whole preset; `UI_Sys_PersonalPanelModel.ApplyEditChange`, the sticker panel, the friend window's signature and mood), 82559 current preset, 82560 preset name |
| Likes | 82554 `Upvote {roleId}` (`UI_Sys_PersonalPanelCtrl._onLickBtnClick`, `LC_DAL_Personal.SendPersonalUpvote`) |
| Others' view | global cache `Personal` (`romsg.PersonalInfo`, `LC_DAL_RolePublic.lua:874-900`), `Extra` (`romsg.RoleExpandInfo`), `PopularityStar`, `PersonalUpvoteCount`, `PersonalUpvoteList`; avatar, frame and title through `Base` |
| Popularity | `UI_Sys_Friend_GiftToFriendsPanelCtrl` (item effects 107 intimacy, 108 popularity, 109 affection), `UI_Sys_Friend_GiftRecordListWidgetCtrl` |
| Config | `PersonalHomepageImageConfig` (themes, backgrounds, moods), `PersonalPerformanceConfig` (actions), `PerformanceConfig` (poses), `FriendlyItemConfig` (gifts), `PublicParamConfig` `Personal*` limits and defaults |
| Errors | `ErrorCodeConfig` 3351-3367 (`PersonalService.Err*`) |

## Behaviour

- **Presets:** three, ids 1-3 *(inferred: the client names only 1-3 and walks the list with `ipairs`)*. A missing
  preset is the default card: theme `PersonalDefaultTheme` 1001, background `PersonalDefaultBackground` 2001, mood
  `PersonalEmoEmpty` 3001 and showcases `PersonalShowcaseDefault` [6301], the card album. The client never reads these
  `PublicParamConfig` values, so they are taken as the original server's *(inferred)*.
- **Unlocks:** a theme, background or action with an `_iUnlockCondition` is unlocked while the condition holds (all
  are base level conditions; walk, action 1002, opens at level 50). These are derived on every sync, not stored. A row
  without a condition only comes from item action 620 `[620, type, id]` (types 4 theme, 5 background, 6 action):
  items 2120001-2120003 open themes 1003-1005, 2110001/2110002 backgrounds 2003/2004. The unlock is stored in
  `unlockInfos` and the item is taken in the same commit. An id already open answers 3356, an id without a config row
  (the test card 1960003, action 50004) answers 3351; both keep the item.
- **Editing** (82553) checks what the editor itself enforces: an unlocked theme, background, mood and pose (3357; a
  pose is an unlocked emote or action); at most `PersonalMaxSticker` 20 stickers, each a currency of item subtype 123
  with one owned per placement (3358 when short); at most `PersonalMaxCabinet` 3 showcases, each one of types 1-3 and
  none twice, and at most `PersonalMaxCharacter` 1 figure (3352 for any of these limits or more than 20 stickers); a
  signature of at most `PersonalSignatureLimit` 30 characters (3364) without a blocked word (3365, the guild's blocked-word list). The preset name is kept: 82560
  renames, capped at `PersonalTemplateLimit` 7 (3366/3367). Every change reaches the client through 5016; the 82553 and
  82560 replies carry nothing the client uses.
- **Likes** (82554) store the liker in the target's `personal_upvote` list and the time in the liker's `upvoteTimes`.
  The client never clears a lit heart, so a like is kept for good *(inferred)*: a second like answers 3359, liking
  yourself 3361, two likes within `PersonalLikeCD` 1000 ms 3362. There is no daily reset and the liked role gets no
  notice. A like completes quest target 106902. Likes add no popularity.
- **Popularity** comes from friend gifts (`FriendlyItemConfig` items): effect 108 times the count goes to the
  receiver's weekly and total popularity, 109 to the sender's affection, 107 to the pair's intimacy. The weekly value
  restarts on Monday 05:00 realm time *(inferred, as the guild activity week)*.
- **Saved** as the role blobs `personal` (presets, current preset, upvote times, 620 unlocks), `personal_upvote` (likes
  received) and `popularity` (`DBPopularityStar`).
- **Attributes:** the attribute view of another player's card (`UI_Sys_Personal_PropertyleftWidgetCtrl`
  `_refreshOtherPlayerPropertyInfo`) reads `Extra` (`romsg.RoleExpandInfo.selfproperty`, the character window's full
  property list). It goes out only while the role's own `SettingShowDetailInfo` is ShowAll, and `Base.isShowDetail`
  says the same; a role that never chose is NoShow, the enum's zero value. The own card uses the local stats. The
  setting arrives as `{showDetailInfo}` alone (`LC_SettingServer`), so NoShow is an empty submessage: under type
  ShowDetailInfo the sent value replaces the stored one instead of being merged.
- **Other players** get the current preset as `Personal` only when they ask for it, and `PopularityStar` and the like
  count with every field present (the card re-asks until they arrive). The avatar, frame and title ids go in `Base`.
  `PersonalAvatarConfig` and `PersonalAvatarFrameConfig` export only placeholder rows and `PersonalTitleConfig` is
  empty, so no avatar, frame or card title can be chosen.

## Limits

- **Public card gate** ([#214](https://github.com/akreao/ro3emu-server/issues/214)): `ParsePersonalInfo` walks the
  stickers and showcases with an unguarded `pairs()`, and an empty list decodes as nil, which would abort the whole
  public-card parse. `PersonalService.Parseable` therefore leaves out a card with no sticker or no showcase, so a card
  without stickers is never shown to others. The own 5016 has no such choice; `Ro3Emu.ClientHooks.PersonalCards` reads
  the missing lists as empty. The gate stays until that hook is proven in game.
- **Showcase fields not sent:** the other player's card album and book showcase need `Library`
  ([#215](https://github.com/akreao/ro3emu-server/issues/215)) and the honor showcase needs `SeasonPage`
  ([#216](https://github.com/akreao/ro3emu-server/issues/216)). `UserDataService` sends neither, so these are empty
  on another player's card.
- **Attributes of an offline role:** `Extra` is computed from the live character, so an offline role's attribute view
  is empty even when it shows its details *(inferred: the client does not say what the original server kept)*.
- The themes and backgrounds behind 620 items are also listed under `_kImageObtain` (the Fashion Shop, Growth Support
  rewards); the shops do not sell them yet.
