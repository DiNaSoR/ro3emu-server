# Survivor Battle shops, Rescue Wagons, Magic Dice, follow and spectating

Kafra Survivor Battle ("Chicken", scene 2240101) already runs its stages, items, poison zone and revives in
`TimedBattleService.Chicken*` (see [timed activities](activities-timed-events.md)). This page covers the match's
service NPCs, Magic Dice, team follow and spectating: `TimedBattleService.ChickenShop`, `.ChickenAdventure`, `.ChickenFollow` and `.ChickenWatch`, tests in `SurvivorBattleTests`.

## Service NPCs

At the battle start the server places the NPCs of the scene's respawn regions (`NpcLevelRegionRespawnConfig` rows of
2240101, groups from `NpcRespawnGroupConfig`):

| Region | Name in the scene editor | NPCs |
|---|---|---|
| 877 | 商店和救护车刷新区域 (shops and ambulances) | 3 × shop NPC 15000054 (shop 801), 2 × Rescue Wagon 15000055 |
| 866 | 武器商人（大圈） (weapon merchant) | 5 × shop NPC 15000060 (shop 802) |
| 899 | a single point (426, 415) | 50 × Magic Die 15000301, spread over the island instead *(inferred)* |

The regions are 400 m and 380 m squares with no inner points, so each NPC stands at a random walkable point inside
its region and the island circle *(inferred)*. Interacting (32700) is answered by the match: the client only waits
for the echo, then opens the panel itself.

## Shops

| Step | Message |
|---|---|
| Open | interact 2900003 / 2900011 opens `UI_Sys_Chicken_ShopNpcPanel` for shop 801 / 802; the panel sends 82334 with the NPC guid (no shop id) |
| List | 82334 `GetDungeonShopResp`: commodity id → `remainCount`, `canBuyCount` |
| Buy | 82332 with `npcId` = the NPC guid; the answer is followed by 5036 and a fresh 82334 |

- A shop's goods are rolled once per match and shared by all its NPCs *(inferred)*. Each `ShopConfig` tab
  (`_iShopType` 8) lists `_kGroup [group, n]`: n different `ShopCommodityConfig` rows of that `_iGroup`, weighted by
  `_iWeight` *(inferred draw)*. Shop 801 has 33 goods (4 consumables, 6 + 3 equipment, 5 + 15 skill books), shop 802
  has 4.
- Prices (`_kItemPerPrice`) are paid in the match currency 10006 from the match pack; the goods go into the pack
  (`_iItemId` × `_iItemPerMount`, within the pack weight). Short of currency answers 192, a full pack 159.
- `_iPurchaseNum` is the limit per player per match (`_iPurchaseTimesLimit` 6, *inferred*); both counts in the list
  are what this player may still buy, so 0 shows "sold out". Over the limit answers 2304.

## Rescue Wagon

Interact 2900004 opens `UI_Com_ChickenAmbulanceRevivalPanel`, which needs one ID card 50001 and sends 82662 with the
wagon's guid. Every fallen teammate whose ID card the player carries comes back at once, one card each, with the
revive map opened at the wagon *(inferred: like a finished 20 s channel, without the channel)*. No card answers 154.

## Follow

82628 `SelectFollowReq {type, roleId, followedRoleId}` from the map panel and the team tips:

| Type | Meaning |
|---|---|
| Invite (1) | the sender asks `roleId` (0 = every teammate) to follow it; the invitee gets 82674 with the inviter. One invite per invitee every `InviteCdChicken` 5 s (else 7107) |
| Followed (2) | `roleId` follows `followedRoleId`; sent by the follower, or by the leader calling the team *(inferred)*. A circle is refused |
| Cancle (0) | `roleId` stops following |

Every change sends the team 82673: `followedMap` follower → followed, and the receiver's own invite cooldowns.
At the landing a follower lands with the end of its follow chain when that player picked a point.

## Head signs

82672 `ChickenOpenPanelReq {panelNum, isOpen}`: the panel numbers are the custom data ids 5000008-10 (shop,
adventure, ambulance). The others in the match get 31018 with that id at 1 (open) or 0 (closed), which shows or
hides the sign over the player's head.

## Spectating

A player out of the battle (dead, no revive map open, not given up) watches a teammate who still stands:

- The elimination sends 82675 `ChickenWatchNotify {autoWatchTime}` (seconds) when a teammate still stands. The
  client shows "auto spectate in N s" while it watches nobody. After `EatChickenObserve` 10 s *(inferred: the same
  value as the revive map time)* the server picks the first standing teammate in team order.
- The watch itself is custom data 1002 (`Unit_CameraBindUnitCustomData`) on the spectator's own player, sent only to
  it: the client binds its camera to that unit and opens the spectating widget. 0 ends it.
- 82640 `ChickenChangeWatchPlayerReq {player_id}` (the widget's arrows, the team list) switches to another standing
  teammate. Anyone else is ignored (the response is NullArg).
- *(inferred)* On the server the spectator's view point moves with the watched teammate (every tick, once 4 m off),
  and World hides the spectator from every other player, so its client gets the units around the watched teammate.
  The Death crate and ID tag stay where it fell.
- When the watched teammate falls or leaves, the next standing one is watched, or the watch ends. A revive map, a
  revive, giving up or leaving the match also ends it, and the view goes back to where the player fell.

## Magic Dice

The in-match adventure (奇遇) is a card pick, not a task list. A die's interact 2900010 runs action 325
TriggerAdventureEvent 10001 and 3 RemoveInteractionNpc: the die disappears, and the player gets a pick from
AdventureDeckConfig 1003.

- The pick travels only in the 5039 `DBAdventureInfo` autosync (`LC_DAL_Roguelike`):
  `functionInfos[1003].deck[match].needSelectMap[operateId]` = `{poolId 1003, refreshCount, chooseCount, cards 1..n,
  endTime (s)}`. The client queues the picks and opens `UI_Sys_Chicken_AdventurePanel`; an empty map closes it.
  `deck.cards` holds the cards already taken (the decks panel).
- `_kRewardStagePersonal [3, 1]`: 3 different cards drawn by `_iWeight` from AdventureCardConfig pool 1003, choose 1
  *(inferred meaning)*.
- 83421 `AdventureEventSelectReq {operateId, cardId[]}` takes exactly `chooseCount` offered cards (else 82).
- 83422 `AdventureRefreshReq {operateId}` redraws for Kaka Coin 10006: 1000, 2000, 3000, then 4000 each
  (`_kRewardStageAuction`), at most `_iLimit` 10 times (2304), 192 without the coin.
- After `_iAutoSelection` 10 s the server takes the first card *(inferred)*.
- Card effects (`_kEffect`): 203 a buff (8 MVP cards), 326 Kaka Coin (Golden Thief Bug, 5000), 338 UpgradeList
  (Baphomet: every worn equipment becomes its `_iUpgradeItem`, one quality up, in the same slot).
- Leaving the match sends an empty 5039. 82671 `ChickenSelectTaskReq` is never sent by the client.

## Not done

- The messenger monsters 15000058 (region 855): camera NPCs with no client or config link found.
- The adventure pre-announcement (dungeon var `CChickenAdventureTimerTips`): the dice are all placed at the start.
- The "Go, Kafra, Go!" weekly tasks (TaskConfig type 10, 2501-2507) are ordinary quests and not counted yet.
