# Guild Group Mail

A guild officer writes one letter that lands in every member's Guild mailbox tab. Server: `GuildService.GroupMail`,
route in `SocialMessages`, test `GuildGroupMailTests`.

## Client sources

| Piece | Client |
|---|---|
| Request | 32241 `GuildBroadEmailReq {req: SendGuildMailReq {title = guild name, content}}` → `GuildBroadEmailResp {result, nextBroadEmailTime, broadEmailLimit}` (`LC_GuildServer` RequestGuildMailBroadCast, `UI_Sys_Guild_MailWidgetCtrl`) |
| Answer | success shows 25253 "Mail Sent" and closes the panel; the time and count in the answer are not read (`LC_DAL_Guild` GuildBoardEmail) |
| Button state | 32231 tp=6 `nextBroadEmailTime` (ms) and `broadEmailLimit` (count used), also in the 32213 full info; the Members tab shows the Mail button only with right 6, counts the cooldown down (170096) and stops at the daily cap (170095) (`UI_Sys_Guild_Member_InfoWidgetCtrl`) |
| Config | `GuildSettingConfig`: `GuildSendEmailPermission` 6, `GuildMassEmailNum` 10, `GuildMassEmailCD` 3600 s, `GuildEmailContentNumNew` [0, 80], `GuildMassEmailMaxContent` 200; `ErrorCodeConfig` 10025-10027, 369, 370 |

## Rules

Refused, in order: not in a guild 462; the guild still founding, or no right 6, 466; ten letters already today 10025;
within an hour of the last letter 161 *(inferred: the client never sends while it counts down)*; an empty text 369; over
80 characters or 200 UTF-8 bytes 10027; a blocked word 370.

On success the guild saves the count and the next allowed time, the sender gets the answer, and every member, the
sender too *(inferred)*, gets a Guild-category (3) letter titled with the guild name, signed with the sender's name and
kept 30 days (MailConfig 1019 "Guild Group Mail" keeps 30 days). Online members then get the 32231 tp=6 push. The count
starts over at 05:00 *(inferred: the guild's other resets use 05:00)*.

## Not done

- The letter is a plain letter, not MailConfig 1019 with its parameter, and carries no sender avatar
  (`MailPersonSenderInfo` is a guild-server to mail-server shape the client never fills).
