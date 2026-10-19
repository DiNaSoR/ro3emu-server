# Guild merging

One guild invites another to merge into it. The invited guild agrees, chooses which of its members move, and at 05:00
the next morning those members join the inviting guild while their old guild is disbanded. Server:
`GuildService.Merge`, routes in `SocialMessages`, test `GuildMergeTests`.

## Client sources

| Piece | Client |
|---|---|
| Lists | 32314 → `PullMergeGuildDataRes {recvInvite, sendInvite}` and 32315 → `PullRecommendMergeGuildDataRes {data}` (`GuildSnapInfo` rows); `UI_Sys_Guild_Manage_MergeWidgetCtrl`, `..._MergeInviteListWidgetCtrl`, `..._MergeRecommendPanelCtrl` (sorted by level, activity, id) |
| Comparison | 32316 `{guildid, isInvite}` → `MergeGuildDetailDataRes {targetGuildData, invitedGuildData, newGuildData, canMergeCount, announce, memberids, lastMergeTime}`: `(0, -)` asks for the agreed pair, `(inviter, false)` a received invite, `(target, true)` a sent invite or a recommended guild (`..._MergeInvitePopPanelCtrl`, `LC_GuildHelper` comparison rows) |
| Actions | 32319 `{guildid = target, isAgree = send or withdraw, announce}`; 32318 `{guildid = inviter, isAgree}`; 32317 `{guildid = inviter, memberids}` from `..._PeopleAdjustmentWidgetCtrl` |
| State | `pendingMergeGuildId` in the 32213 full info and in 32231 tp=1; non-zero switches the tab to the merge view, where only the merging guild sees Member Adjustment |
| Texts | 170288-170290, 170303, 170309, 170349; `ErrorCodeConfig` 10050-10078 |
| Config | `GuildSettingConfig`: `GuildMergeListNumLimit` 5, `GuildMergeLapseTimeLimit` 24 h, `GuildMergeTimeCD` 604800 s, `GuildMergeNoticeLimitNew` [0, 50]; mail 1121 (`GuildMail21`); right 21 |

## Rules

- Every request needs right 21 "Merge Guild" in an established guild (466).
- **Invite (32319):** refused for the guild's own impeachment 10060 or the other guild's 10075. It is also refused during
  the week after either guild's last merge (10063 / 10078), while either guild has an agreed merge (466, *inferred*),
  for a repeat invite 10055, and within 30 minutes of a refusal (10058, whose text carries 30). Further refusals: five
  live sent invites 10052, five live received invites on the target 10051, an announcement over 50 characters or with
  a blocked word 10056, and no free place in the inviting guild 453 (*inferred*). Withdrawing a missing invite answers
  10057. Invites lapse after 24 hours (10050).
- **Answer (32318):** a refusal removes the invite and starts the 30 minutes. Agreeing needs a live invite (10061 /
  10050) and the same checks. It clears the guild's other received invites and sets `pendingMergeGuildId` on both
  guilds, with both rosters pushed. The merge is set for the next 05:00. The members who move are preselected:
  officers first, then by join time, up to the inviting guild's free places (*inferred*).
- **Cancel:** the client has the text "Cancel Merge" (170311) but sends no request for it. A 32318 refusal naming the
  agreed guild cancels the merge (*inferred*).
- **Member Adjustment (32317):** only the merging guild, only its own members (454), and at most the inviting
  guild's free places (10053).
- **The merge (guild heartbeat, at 05:00):**
  - If the chosen members no longer fit, or none are left, the agreement ends and nothing moves (*inferred* from "the
    merge fails").
  - Otherwise, in one commit, the chosen members join the inviting guild as regular members, and the merging guild is
    disbanded as any guild is: its funds and market stock go to recovery (*inferred*).
  - Members who were not chosen are left without a guild (*inferred*).
  - The inviting guild's merge cooldown starts, and every member of the resulting guild gets mail 1121 naming the
    merged guild.
- **Comparison:** both guild columns and the result column list the same buildings in the same order, because the
  client compares them by index. The result keeps the inviting guild's level and buildings and adds the members who
  move (*inferred*). `activity` is last week's guild activity.

## Not done

- The "new guild" (10064 / 10076) and "low activity" (10065 / 10077) refusals: the client names no threshold, and
  `GuildMergeRecommendListLimit` (13999) has no known meaning.
- No notice reaches the invited guild when an invite arrives. Its officers see it the next time they open the tab.
