# Reopened content (`content/reopened-content.json`)

The client comes from a private server that switches content off with impossible values: a base level of 999 or
9999, or a pre-quest that does not exist (999999, 9999999). This file opens some of it again with
[custom content raw rows](custom-content.md#raw-rows), so no code or client change is needed. Delete the file to keep
them closed. It works with or without [OpenContent](open-content.md).

| What | Was | Now |
|---|---|---|
| Season menu (FunctionConfig 69, condition 2000062) | base level 9999 since the 2026-10-18 client | base level 50, as before that update |
| "Dejected Young Man" blue side chain (TaskConfig 201010 to 205200, 75 quests) | pre-quest 9999999 and condition 88000010 (base level 999) | offered from base level 20 (condition 2000056) |
| 201090 "Allen's Request" | no reward; nothing in the client gives the Pearl (1253001) or Four-leaf Clover (1253002) that 201100 "Gather Materials" asks for | gives one of each (MultiverseGroupConfig/AwardConfig 99201090) |
| 201120 "Bunny Hair Band" | item turn-in mode 0, which the server leaves pending | mode 1, the NPC turn-in the main story uses; both items are taken at the Headgear Merchant |
| Base EXP for the whole chain | 100 a step and 100,000 for each arc's last step, about 507,000 in all | 10,000 a step and 150,000, 200,000, 250,000, 250,000, 300,000 for the five finales, about 1,850,000 in all (level 20 to about 34) |
| "Learn About Kafra: Survivor Battle" (TaskConfig 10200060), intro side quest in Prontera | pre-quest 9999999 | offered at base level 49, its own condition (its siblings 10200010 to 10200090 were already open) |
| Totem goods (ShopCommodityConfig): Random Divine Arts Totem (Zeny Shop 12107, Competition Shop 25103), Low-Tier Totem Skill Book (12108), Surprise Chest (Crystal Shop 13101), Candle of Contemplation - Life, P. ATK, M. ATK (13120 to 13122) | show and buy conditions at base level 999 | base level 40, the Divine Totem's own level (condition 2000026) |

The level-20 start for the side chain is a choice: the client data names no level for it. Its other steps are the
client's own rows. Base level 40 for the totem goods is also a choice, taken from the Divine Totem menu (function 29). The chain's dungeon steps (201040 Royal Palace dance, 205110 Moonlight Flower, 205170 Pharaoh) have
not been played through.

Tests: `tests/Ro3Emu.Tests/ReopenedContentTests.cs`. The full list of locked content, with what each item needs, was
written up in the project's locked-content audit (2026-10-19).
