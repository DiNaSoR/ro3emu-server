# Guild leader impeachment

Members can take a guild away from a leader who stopped playing. Server: `GuildService.Impeach`, routes in
`SocialMessages`, test `GuildImpeachTests`. The client calls it "Peach".

## Client sources

| Piece | Client |
|---|---|
| Requests | 32235 `GuildPeachReq {isCancle}` → `GuildPeachResp {result, isCancle}` (start: 25094 "Impeachment initiated", cancel: 25102); 32236 `GuildPeachApplyReq {}` → `{result}` (25099 "Responded") (`LC_GuildServer`, `LC_DAL_Guild`) |
| State | `GuildPeachInfo {isPeaching, nextPeachTime ms, canPeach, peachOwnerID, needCount, peachEndTime ms, peachPlayerIDs}` in the 32213 full info and 32231 tp=4 pushes (`LC_DAL_GuildNew` DecodeGuildPeachInfoData, `LC_GuildHelper`) |
| Panels | the Members tab shows the Impeach button only when `canPeach` (`UI_Sys_Guild_Member_InfoWidgetCtrl`); `UI_Sys_Guild_ImpeachPresidentWidgetCtrl` shows the conditions, cost, counter, countdown and the Start, Cancel or Respond button |
| Config | `GuildSettingConfig` `ActiveImpeach*`; mails 1001-1006 (`GuildMail01`-`06`); journal event 7; `ErrorCodeConfig` 472-479 |

## Rules

- **Grounds:** the leader has been offline `ActiveImpeachChairmanOffineTimeLimit` (7) days. The client lists a second
  ground, under `ActiveImpeachWeekContribution` (3000) guild contribution a week for `ActiveImpeachChairmanWeekNum`
  (2) weeks. The server keeps no weekly contribution, so only the offline ground applies *(inferred)*. `canPeach` is
  true while there are grounds and while an impeachment runs, so the other members can reach the Respond button
  *(inferred)*.
- **Start (32235 isCancle=false):** refused when one runs 478, from the leader 475, within the cooldown 472, from a
  member of under `ActiveImpeachJoinDay` (10080 minutes) 474, without grounds 473, and without
  `ActiveImpeachMoney` (30 × item 3) 154. The payment and the impeachment commit together. The initiator counts as
  the first response.
- **Responses needed:** `ActiveImpeachOnlineScale` (50) % of the members online or seen within
  `ActiveImpeachOnlineDayNum` (1) day, rounded up, at least one *(inferred rounding)*.
- **Respond (32236):** refused when none runs 479, a second time 477, and from the leader 474 *(inferred)*.
- **Success:** as soon as enough members responded *(inferred: not at the end of the hour)*, the initiator becomes
  leader and the old leader a regular member (20). Journal event 7, mail 1001 to every member.
- **Ends without success:** the initiator cancels (476 from anyone else, mail 1002), the initiator leaves or is
  kicked (mail 1003, client confirm 25103), or `ActiveImpeachTime` (60 minutes) passes (mail 1004, on the guild
  heartbeat). A responder who leaves takes their response back *(inferred)*.
- **Cooldown:** `ActiveImpeachCD` (24) hours after any end. The client prints it after a cancel; after a success or a
  failure it is *inferred*.
- On start, mail 1005 goes to the initiator and 1006 to everyone else.
- While one runs, the leader cannot hand over the guild (478, *inferred*).

## Not done

- The contribution ground (see above) and mail 1122 "Low Guild Activity Warning", which belongs to it.
- Function 1369's level 21 is checked only by the client.
