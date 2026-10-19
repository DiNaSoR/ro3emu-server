# Tavern

The Tavern (client "BarRoom", 酒馆) is the player-run chat and voice rooms opened from the chat window (function 2410,
chat channel 10). Server: `TavernService` (+ `.Rooms.cs`), routes in `TavernMessages`, tests in `TavernTests`.

## Client sources

| Piece | Client |
|---|---|
| Requests and pushes | `MG_Barroom.proto` 82481-82514, `LC_BarRoomServer` (senders), `LC_DAL_BarRoom` (handlers) |
| Room logic | `Logic/ChatBarroom/BarRoomLogic/LC_BarRoomHelper` |
| Voice | `LC_BarRoomTalk.JoinChannel` → `VoiceCall_Util.JoinChannel(tostring(roomId))` |
| Personal state | 5043 `DBBarRoomInfo` (auto-sync; `LC_BarRoomHelper.OnGetBarRoomID`) |
| Configs | `BarroomTypeConfig` (`_iType` 0 = room types 1-6, 1 = languages 21-28), `PublicParamConfig` `BarRoom*` / `Barroom*` |
| Errors | `ErrorCodeConfig` 2551-2580 (constants at the top of `TavernService`) |

## Rooms

Rooms live in memory: a restart clears them, and leaving the world leaves the room *(inferred: the client never
re-enters one on login)*. A room has an owner, members in join order, a mic list (`curSpeaks`, the owner always first),
requests to speak, a mute list, a block list and Popularity (`hotValue`). Room ids start at 2^40 so they never collide
with party ids in the voice relay.

- **Create** (82481) checks the type, the language (0 = unset is allowed, *inferred*) and the name
  (`BarRoomNameLen` 18 characters). Creating or entering leaves any room the player was in.
- **Enter** (82482): unknown room 2552, blocked 2553, full (`BarroomPersonNum` 1000) 2572. The others get 82507.
- **Exit** (82483) answers 0, on which the client drops the room and its chat channel. The owner's room passes to
  the earliest member *(inferred)*; an empty room closes.
- **Get** (82508) answers the player's room; not in a room answers 2578 (the client's handler reads `info` unguarded).
- **Invite** (82484/85/86): any member may invite online players. An accept joins the room; the answer goes to both
  the invited player (who then asks for 82508 and opens the window) and the inviter (tips 59224/59225).

Every change pushes 82506 and the room's 82508 to every member online. The client only re-asks for 82508 on 82506
while the Tavern window is open, so the server sends both.

## Room list

The list is the micro service `PartyService.getPartysSummary` with `partyType` 4. Each `GameSummaryData` is a
`BarRoomSummary`, busiest first *(inferred order)*. The Discover button adds `PartyIntCondition`
`BaroomCreateTime [now-1800, now+1800]` and `BaroomHeadCount [2, 30]` (`BarroomFindBarRoom`), which the server
applies. `getSubPartysCountByType` with party type 4 counts rooms per room type (`BarRoomAllTargetCount`, unused by the
UI).

## Owner tools

| Message | Rule |
|---|---|
| 82494 room info | type, language, name; once per `BarRoomSetInfoCd` (5 s), else 2573 |
| 82489 transfer | to a member not blocked (2557) or muted (2558); `BarRoomTransferRoomCd` (30 s, 2575); `transferCd` is the end time |
| 82490 mic mode | Free / Set / Order (`SpeakAuthorityType`), optionally with the new mic list |
| 82493 mic order | the listed members in `micOrder`, at most `BarroomSpeakNum` (10) with the owner |
| 82492 handle request | an approval puts the member on the mic (2559 when full) |
| 82498 / 82499 mute | off the mic and its requests; `forbiddenSpeaker` holds role → role (the client compares values) |
| 82500 / 82501 kick | the target gets 82483 result 0 and is blocked (`blackList`, role → role) until unblocked |
| 82504 / 82505 mute all | `isCloseMic`: every mic but the owner's |
| 82514 mute a mic | answered to the **target**: `MutePlayerInChannel` mutes the receiving client's own mic |
| 82513 GM | a GM (`GmCommands.Allowed`) sets Popularity and the special-room count |

Members ask for the mic with 82487 (Free mode answers 2567) and leave it with 82488 (the owner cannot, 2561).

## Chat and voice

Text chat is IM channel `10_<room>`, also with Tencent's `@TGS#_` prefix. `ChatService` admits only members
(`TavernService.InRoom`) and removes a player from the channel when they leave the room.

Voice joins the relay room named by the room id. `VoiceGateServer` accepts the join for members, and
`VoiceRooms.SweepMembership` drops non-members and forces the mic off when `TavernService.MayTalk` says no: the owner
always may talk; nobody while muted or while "mute all" is on; anyone in Free mode, else only those on the mic list
(`LC_BarRoomHelper.CheckMemberCanOpenMic`).

## Gifts

82491 pays the gift items (`ItemConfig` `_iType` 2 / `_iSubType` 213) and reads their `_iUseEffect`: 110 is the
room's Popularity, 111 the recipient's Star Power, per item.

| Item | Name | Popularity | Star Power |
|---|---|---|---|
| 1200801 | Hot Cocoa | 1 | 1 |
| 1200802 | Gingerbread | 10 | 5 |
| 1200803 | Craft Brew | 60 | 35 |
| 1200804 | Grape Wine | 400 | 300 |

The recipient must be another member who is online (2574 self, 2555 left, 2563 offline). Star Power goes to the
recipient's `popularity` and `weekPopularity` (reset on Monday 05:00, *inferred*). The answer goes to the gifter,
whose client posts the gift line in the room chat.

## Daily Tavern and achievements

Every 10 s the server adds each member's seconds in a room to `inBarRoomTime` (reset at 05:00). Reaching the target
of the Daily Tavern quest (`BarroomTaskID` 2000, `_kProgressTarget [[106010, 1200]]`, "Spend 20 minutes in any Tavern
room") sets `isToDayActComplete` and adds one to `actDays`. This also moves the Event Calendar's Daily Tavern row.

- Quest target 106010 reads today's seconds like the AFK seconds of 106901, so a quest accepted later in the day
  sees time already spent. The guild tasks 1602050-1603014 reuse 106010 as `[106010, 5]` with another meaning; only
  targets of a minute or more are read as Tavern time *(inferred)*.
- Achievements 102100500-502 (`[106007, 2000, n]`) read `actDays`; 102100600-602 (`[106008, n]`) read `popularity`.

## Not done

- Room heat tags (red packets, activities, sparks, `BarroomHeatTags*`): no source found *(inferred)*.
- Reports (`BarroomReportConfig`) and the GM panel's Tavern buttons.
