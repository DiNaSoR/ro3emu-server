# Connect Quests and activity Intimacy

Connect Quests are TaskConfig type 19 (1901000-1905110, 53 rows such as "Connect Quest - Defeat the MVP Angeling with
a Party"). "Connect" is the English client's name for the friendship system (Localization 10500000002). They are how
friends gain Intimacy from playing together: Localization 10960000069 says Intimacy comes from gifts and from
"participating in activities with friends", and that the activity amount is limited each day.

Code: `Game/TaskService.ConnectQuests.cs` (quests and targets), `Game/FriendExtraService.cs`
(`GrantActivityIntimacyAsync`), `Accounts/Persistence/FriendPlans.cs` (`FriendActivityIntimacyPlan`).

## How players receive them

The player never sees them. `enum_TaskType` (`Config/Define/Lua_GameDefine.lua`) has no 19, and both the quest log
(`UI_Sys_TaskWidgetCtrl._refreshTaskInfo`) and the HUD tracker (`LC_TaskHelper.GetCurrentMainTrackTask`) list only the
types `LC_TaskHelper.CheckShowMainTask` allows: Main, Side, Guide, OnePiece, Dungeon, Delivery, Explore, Branch and
RingTask. Every row is Auto accept and Auto delivery (`_kAcceptTask [1]`, `_kDeliverTask [1]`) with no pre-task,
condition, award or EXP. Its only effect is the server-host action 116 `UpdateSocialityScore [116, id]`, which names a
`SocialRelationsConfig` row.

So the server keeps all 53 running for every role (they are in 5027 like any quest; the client just does not list
them), and each delivery re-arms its quest in the same transaction. *(inferred)* Repeating: `_iIfRepeat` is 0, but a
single pass over all 53 (670 Intimacy in all) could not fill a scale that reaches 180,000 (FriendlinessConfig).

## Targets

The four target ids are not in the client's `enum_Progress` (`LC_ConditionDefine`), so their meaning comes from the
quest text and every rule here is *(inferred)*. "In a party" means a TeamService party of at least two.

| Target | Quests | Counted |
|---|---|---|
| 106012 `[106012, n, npc]` | 1901000-1901190, field MVPs | MvpService's participant settlement of that MVP NPC while in a party |
| 107005 `[107005, n, boss]` | 1904000-1905110, five- and ten-player bosses | a committed clear of that `TenGameplayBossConfig._iBoss` with another player in the run |
| 106013 `[106013, n]` | 1903000, "battle royale as a party" | a settled Kafra Survivor match (MatchConfig 2) while in a party |
| 106014 `[106014, minutes]` | 1902000, "Auto-battle in a party" | Field Bonus Time seconds consumed while in a party; R is minutes x 60 |

Producer Yu (9300201, 1901180 and 1901190) is not a field MVP in MvpChallengeConfig, so those two only count if that
NPC is ever settled as one.

## Intimacy

`SocialRelationsConfig._iFriendValue [[107 GainIntimacy, amount, 1]]` is the Intimacy (5 to 20). After the delivery
commits, the server adds it to the pair (both directions) for every online party member who is the role's friend, and pushes
`onRelationDataChange` to both. Both friends complete the quest on the same kill, so a pair gains the amount twice.
The trailing 1 has no recovered meaning, and the sworn sibling, couple and mentor columns are empty in every row.

*(inferred)* The daily limit is `PublicParamConfig.FriendIntimacyLimit` (1000) per pair, reset at 05:00 local time
(`FriendIntimacyResetTime [1, 1, 5]`); the client never reads either value. It is kept on the friend rows
(`activity_day`, `activity_gain`). Gifts are not limited. The maximum is 180,000, as for gifts.

The Intimacy write is its own commit after the quest's, so a crash between the two loses that grant (the quest
still counts as delivered).
