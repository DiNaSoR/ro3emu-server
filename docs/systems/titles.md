# Titles

The title (client `Title`, 称号) is a single rank ladder in the Role panel's Title tab (function 14). A role holds one
`TitleConfig` row and climbs one row at a time; each row adds combat power and shows its name over the head and an
icon in chat. Server: `TitleService`, route in `TitleMessages`, tests in `TitleTests`.

This is not the life job titles (`LifeJobService.Titles`), the Growth Classroom emblem titles or the profile-card
`PersonalTitleConfig` (empty in the export).

## Client sources

| Piece | Client |
|---|---|
| State | 5031 `DBTitleInfo` auto-sync (`LC_DAL_Title`: full sync sets the title, an update plays the upgrade effect) |
| Request | 43102 `TitleLevelReq {}` → `TitleLevelRsp {result, title_id}` (`MG_Title.proto`) |
| Panel | `UI_Sys_Role_TitleWidgetCtrl` (`OnClickUpgrade`, `RefreshShow`), `UI_Sys_Role_Title_CellWidgetCtrl` |
| Others' view | global cache `Base` `RoleAdditionBaseInfo.title` → `LC_DAL_RolePublic` → head info Title line, chat icon |
| Config | `TitleConfig` (21 rows): `_iNextLev`, `_iLev`, `_iPower`, `_iPowerAdd`, `_kSpend` |

## Behaviour

- **Default:** every role starts on row 1 "Beginner" (level 0, power 0, no cost). The client shows an empty list for
  title 0, so the server always syncs a real row *(inferred default)*.
- **Rank up** (43102) promotes to `_iNextLev`. The target row's `_iLev` (base level), `_iPower` (highest recorded
  power, `maxHistoryPower`) and `_kSpend` items are checked and the items are taken. A level or power shortfall answers
  82, missing items answer 154, and the top row answers 28. The client checks the same things before sending, and a
  failure leaves its button locked until relog, so these answers are only for a modified client.
- **Power:** the held row's `_iPowerAdd` is the package's `TitleScore` (`PowerService.TitleId`). Every `_kAttr` is 0,
  and `TitleBuffConfig` is not read by the client.
- **Saved** as the role blob `title` (`DBTitleInfo`).
- **Other players** see it in `RoleAdditionBaseInfo.title` and `RolePublicBaseInfo.title`. There is no push: they see
  a new title on their next fetch of that player's info.
- **Opening:** function 14's open condition is level 9999 in our client data, so `OpenContent.Functions` opens it.

## Limits

- Rows 4 and up cost crest items 1250801-1250808 ("Crest of Journey" and the rest). Nothing in the export drops or
  sells them, so players reach row 3 at most unless a server adds a source (a shop row, a drop or GM `!item`).
- 43103 `TitleInfoNtf` is routed by the client but has no handler; the server does not send it.
