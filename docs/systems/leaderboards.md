# Leaderboards and rank rewards

Code: `src/Ro3Emu.Server/Micro/RankService.cs` (the boards) and `RankService.Rewards.cs` (rank rewards). Tests:
`RankServiceTests` (client data) and `RankRewardTests` (synthetic fixture).

## Boards

The client reads the boards over the micro gate (`RankService`: `rangeUserRank`, `queryUserRank`,
`getUserRankSurrounding`, LC_DAL_Rank.lua). The server builds every board from committed role and guild state,
offline roles included, and caches it for `PublicParamConfig.LeaderBoardRefreshIntervalTs` seconds. The class summary
lists each board's source and tie-break.

## Rank rewards

A `LeaderBoardsConfig` row pays when it names gift rows (`_iRankRewards`) and a letter (`_iMailId`). In the client
data that is two boards, each paying ranks 1-10 daily at 05:00:

| Board | Gift | Letter | Reward (`_kReward`) |
| --- | --- | --- | --- |
| 1011 Spirit Tower | 8001 | 1104 "Elf Tower: Race to the Top Ranking Rewards" | 130001 |
| 1301 Fashion Rating | 130101 | 1057 "Fashion Rating Ranking" | 8010 |

- **When:** `_kPrizesTimeNew`, read the way the board's footer shows it (UI_Sys_RankPowerWidgetCtrl): `[1, hour]` is
  daily at that hour (UIWords 23716), `[2, weekday, hour]` weekly (23714, weekday 1-7 = Monday-Sunday). `[3, activity]`
  ("after the activity ends") and `[4, 0]` have no time and never settle here. Times are the server's local time.
- **Who:** a gift row covers the ranks `_kParaNum [min, max]` (Lua_CFG_LeaderBoardsGiftConfig `GetRange`).
- **What:** `_kReward` is a MultiverseGroupConfig id, resolved through the season's AwardConfig row to its fixed
  `_kAward` grants (`GetGiftReward` → `GetSeasonAwardPreview`), the same route as a letter's `_iAward`.
- **How:** the letter from `_iMailId`, with the rank as its `${1}` ("You placed No. ${1} ...").

Settlement *(inferred: the original's server side is not in the client)*:

- At the first check after a boundary (the loop checks every second) the board is rebuilt from committed state.
  Its winners for that period are saved to `server_state` `rank_rewards` before any letter goes out.
- Each letter is delivered once per board, period and role (delivery key `rank:<board>:<period>:<role>`). After a
  restart the saved period's letters are sent again, which only completes an interrupted settlement: the board is not
  ranked again, so a role that climbed in the meantime waits for the next period.
- Boundaries missed while the server was down settle once, with the board as it is at the next check.
- Guild boards and the equipment board (rows are guilds and items) do not pay; none of them has gift rows.
