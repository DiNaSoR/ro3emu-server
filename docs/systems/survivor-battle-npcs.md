# Survivor Battle shops, Rescue Wagons and follow

Kafra Survivor Battle ("Chicken", scene 2240101) already runs its stages, items, poison zone and revives in
`TimedBattleService.Chicken*` (see [timed activities](activities-timed-events.md)). This page covers the match's
service NPCs and team follow: `TimedBattleService.ChickenShop` and `.ChickenFollow`, tests in `SurvivorBattleTests`.

## Service NPCs

At the battle start the server places the NPCs of the scene's respawn regions (`NpcLevelRegionRespawnConfig` rows of
2240101, groups from `NpcRespawnGroupConfig`):

| Region | Name in the scene editor | NPCs |
|---|---|---|
| 877 | 商店和救护车刷新区域 (shops and ambulances) | 3 × shop NPC 15000054 (shop 801), 2 × Rescue Wagon 15000055 |
| 866 | 武器商人（大圈） (weapon merchant) | 5 × shop NPC 15000060 (shop 802) |

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

## Not done

- Spectating after elimination (82640, 82675): the camera follows a unit through custom data 1002, which needs the
  server to show the spectator what the watched player sees.
- Kafra adventure tasks (adventure NPCs 15000301 in region 899, 82671) and the messenger monsters 15000058 (region 855).
