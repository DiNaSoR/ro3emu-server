# Season clock and page implementation

Scope: playbook 101, using `SeasonService.Start`, never `MultiverseSchedule`. Datasets 5021/5022 and messages 82707/82708/82901/82902/82903. Equipment-grid refinement (5023/83220-83222) is documented in [season-refine-evidence.md](season-refine-evidence.md).

## Client/config evidence

- `LC_DAL_Season.lua:204-434,727-896` consumes the season identity, page and time messages. The DAL omits `SeasonInfo.stageId`, so 82901 also supplies `stageID`. Login sends 5021 before character assets that can resolve multiverse groups.
- `Lua_GameDefine.lua:7788-7805` and `LC_SeasonHelper.ConvertConfigTimeStamp:834-957` establish config time types 3 (season start), 5 (break start) and 9 (referenced stage end), with offsets in seconds. Neither the enum nor the converter implements 7 or 8 in the client Lua.
- `MultiverseStage` rows 101001-101005 define the stage graph. `MultiverseMainStage` has a season-1/day-1 row of length 77. The notice also uses `MultiverseConstant.MainStageCountDownTime` (168 hours) and `ServerMatchUpCircle` (28).
- `MultiverseConfig[101]._iDanSystem` supplies functions 23/65/67/60/17/36. `LC_SeasonHelper.CalFunctionRank:1072-1119` subtracts each `MultiverseDan._iExpLimit` successively; equality advances to the next tier. `GetSeasonDanRewardState:562-633` applies the three-function claim limit separately to each dan level.
- Normal rank claims resolve `_iNormalRewardGroup` through `MultiverseGroupConfig` and preserve the fixed `AwardConfig._kAward` triples, including binding. Claim history and inventory grants commit in one SQLite transaction. All configured `MultiverseDan._iMailRewardGroup` values are zero.
- Honor IDs refer to `ItemConfig` in `UI_Com_Tips_GloryPanelCtrl`. `MultiverseMedalMaxWear` is 2. Wear only changes an existing owned record; the request never awards a badge.
- `LC_DAL_Season.lua:262-434` iterates empty rank/history maps without nil guards. `LC_PbHelper.lua:1538-1556,1926-1958` creates map tables before applying deletions and retains an empty table afterwards. Full 5022 therefore carries a key-0 deletion for each empty map; this does not invent a persisted claim, honor or history entry.

## Explicit (inferred) choices

- Type 7 means referenced stage start + duration, aligned forward to the specified local weekday (1 = Monday); type 8 means referenced stage start + duration. These are **(inferred)** interpretations of the row shapes, not recovered original-server algorithms.
- The first season's break is `Start + 77 * 86400`, using the season-1/day-1 main-stage length. This single-server policy is **(inferred)**; no cross-server phase/match scheduler was recovered. Next-season and next-match times remain 0 because nothing is scheduled. Stage intervals include their start and exclude their end **(inferred)**. The existing season ID 1 and area ID 1 are **(inferred)**.
- Page scores read persisted currencies whose localized names identify MVP/WoE/league/life rank points (11/12/13/14), plus Survivor Battle net victory points (7), relative to the current cycle's saved baseline. This mapping and baseline are **(inferred)**. Function 23 accepts positive, idempotent authoritative score events through `RecordDungeonScoreAsync`; no exported dungeon completion supplies an amount, so ordinary clears do not invent points.
- Server enforcement of the client's Function 69 gate, atomic refusal of a mixed invalid claim batch, successful `receiveResult` echoing accepted dan/function pairs, and generic parameter refusal for a third worn badge are **(inferred)**. Repeating an already satisfied wear state succeeds without changing state **(inferred)**.
- Wire-only deletion markers for empty 5022 maps and the relative login/send order are **(inferred)** compatibility choices, supported by the client's decoder source but not by a running-client capture.
- The GM `!season` next-login contract is preserved. An explicit `!seasonnext 101 <yyyy-MM-dd>` creates a new persisted cycle after the current break; this is **(inferred)** server policy because stage 101005 otherwise has a 999999999-second tail and no configured next-cycle date. Active sessions keep their prior identity and cannot claim new-cycle rank rewards until login. Historical 5021 identity, 5022 score snapshots, per-cycle score baselines and once-only rank reissue are persisted. Honor records carry across cycles. Talent/refine state is retained with its existing eligibility rules; no unproven refund or reset is added.

## Settlement and acceptance limits

`MultiverseSettlementTask` has ten rows for 101: six reference task 11200060, followed by 11200070/80/90/100; all set `_iIncreaseInOffSeason=1`. Their `TaskConfig` entries are repeatable type-12 level tasks (60/65/70/75/80), using reward group 50123. No stage-to-task/mail dispatch rule is supplied. At day 77, the page records a score snapshot on its next login or 82901 request; off-season points can still increase before explicit rollover. At rollover, unclaimed configured `_iNormalRewardGroup` rank rewards are sent once through template 1081, with mailbox/page/epoch committed together. Rank and mail claim maps reset for the new cycle; the historical function scores remain in 5022. Owned commemorative-medal currency IDs 1300046-1300050 seed honor-wall records on login/page access **(inferred)**; wear still requires ownership. Templates for placement/trophy settlement have `_iAward=0` and no supplied rank results, so no placement payout is fabricated. All `MultiverseDan._iMailRewardGroup` values are zero; this path does not create a separate dan mail award.

The top-bar and panel behavior is backed by client source analysis and wire tests only; full original-server 1:1 behavior is not claimed.

## Verification

`tests/Ro3Emu.Tests` (`SeasonPageTests`, `SeasonRefineTests`) cover the clock/login boundary pushes, all three page messages, all six configured scores, dan thresholds, claim limits, inventory binding/persistence, duplicate refusals, transaction rollback/retry, badge ownership/wear limits, pre-role refusals and GM next-login behavior. Run them with:

```powershell
dotnet test tests/Ro3Emu.Tests/Ro3Emu.Tests.csproj --filter "FullyQualifiedName~SeasonPageTests|FullyQualifiedName~SeasonRefineTests"
```
