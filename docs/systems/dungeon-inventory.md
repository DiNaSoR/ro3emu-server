# Story dungeon inventory

Generated from the client data (DungeonConfig, scene editors, NpcLevelPointRespawnConfig, DungeonQuestConfig,
Localization_en). It lists the scenes the server's DungeonCatalog would accept (_iDungeonType 1|2,
_iGamePlayType 6|24, not a TenGameplayBoss dungeon). Point labels are the scene editors' original (Chinese) names.
"Spawnable today" means the exported tables alone resolve a combat spawn source for the scene; the reconstructed
encounters described in [dungeon.md](dungeon.md) are not counted here.

| Category | Scenes | Spawnable today |
|---|---:|---:|
| story | 28 | 1 |
| tutorial | 7 | 7 |
| minigame | 4 | 0 |
| territory-war | 29 | 0 |
| developer-test | 4 | 0 |

## 1000101 Job Advance Scene (story)

Editor `sc_indor_zzcs_001_pl01_sceneeditor`; 0 server points, 0 client-only; 0 point-respawn rows, 0 region rows; limit 60 min, 1 players; spawnable today: no.

Base map `sc_indor_zzcs_001_sceneeditor`: no monsters

## 1000201 Unknown Area (story)

Editor `sc_plot_yhcx_001_dg01_sceneeditor`; 2 server points, 5 client-only; 0 point-respawn rows, 0 region rows; limit 600 min, 1 players; spawnable today: no.

| Point | Label | Group | NPC | Source |
|---:|---|---:|---|---|
| 21 | 刷怪点 | 0 | 0 | none |
| 27 | 掉落的最终位置 | 0 | 0 | none |

Stages:
- 1. Explore the Ant Queen's Nest: Explore ahead (goals: [5,0,1] Head deeper into Maya's lair)
- 2. Rescue Bobo: Bobo was sucked into the vortex (goals: [5,0,1] Jump into the vortex)
- 3. Rescue Bobo: The Passage Beneath the Vortex (goals: [5,0,1] Avoid the ant eggs and reach the end of the passage)

## 1000301 Unknown Area (story)

Editor `sc_plot_yhcx_002_dg01_sceneeditor`; 4 server points, 0 client-only; 0 point-respawn rows, 0 region rows; limit 600 min, 1 players; spawnable today: no.

| Point | Label | Group | NPC | Source |
|---:|---|---:|---|---|
| 22 | 刷怪点 | 0 | 0 | none |
| 23 | 波波位置 | 0 | 0 | none |
| 27 | 荧光物质位置 | 0 | 0 | none |
| 29 | 兵蚁位置 | 0 | 0 | none |

Stages:
- 1. Find Bobo: Bobo lies Weak on the ground (goals: [5,0,1] Help Bobo up)
- 2. Try to Ride the Deniro: Ride the Deniro (goals: [5,0,1] Ride the Deniro)
- 3. Find a way to earn Deniro's Faith: Collect the fluorescent substance (goals: [5,0,1] Collect the fluorescent substance)
- 4. Feed the Deniro: Feed the Deniro (goals: [5,0,1] Feed the Deniro)
- 5. Reach the deepest part of the ant nest: Ride the Deniro to the depths of the Ant Hell (goals: [5,0,1] Ride the Deniro to the depths of the Ant Hell)

## 1000401 Unknown Area (story)

Editor `sc_plot_yhcx_003_dg01_sceneeditor`; 4 server points, 0 client-only; 0 point-respawn rows, 0 region rows; limit 600 min, 1 players; spawnable today: no.

| Point | Label | Group | NPC | Source |
|---:|---|---:|---|---|
| 21 | 蚁后刷怪点 | 0 | 0 | none |
| 26 | 教授刷新点 | 0 | 0 | none |
| 32 | 教授卵刷新点 | 0 | 0 | none |
| 34 | 宝箱刷怪点 | 0 | 0 | none |

Stages:
- 1. Encounter Maya: Reach Maya's hidden nest (goals: [5,0,1] Defeat Maya)
- 2. Find the Professor: Venture deeper into the cave to find the Professor (goals: [5,0,1] Venture deeper into the cave to find the Professor)
- 3. Free the Ant Egg: Rescue the Professor from the ant egg (goals: [5,0,1] Rescue the Professor from the ant egg)
- 4. Help the Professor Up: Help the Weak Professor walk (goals: [5,0,1] Help the Weak Professor walk)
- 5. Maya's Secret Treasure: Open the treasured chest (goals: [5,0,1] Open the treasured chest)

## 1000501 Unknown Area (story)

Editor `sc_plot_airship_001_pl01_sceneeditor`; 24 server points, 0 client-only; 0 point-respawn rows, 0 region rows; limit 600 min, 1 players; spawnable today: no.

| Point | Label | Group | NPC | Source |
|---:|---|---:|---|---|
| 22 | 刷怪点 | 0 | 0 | none |
| 24 | 敌方飞空艇1 | 0 | 0 | none |
| 26 | 刷怪点 | 0 | 0 | none |
| 27 | 刷怪点 | 0 | 0 | none |
| 28 | 刷怪点 | 0 | 0 | none |
| 40 | 火炮1 | 29 | 0 | none |
| 42 | 火炮2 | 29 | 0 | none |
| 43 | 火炮3 | 29 | 0 | none |
| 44 | 冲锋怪-1 | 0 | 0 | none |
| 45 | 冲锋怪-2 | 0 | 0 | none |
| 46 | 冲锋怪-3 | 0 | 0 | none |
| 47 | 冲锋怪-4 | 0 | 0 | none |
| 48 | 弓箭-1 | 0 | 0 | none |
| 49 | 弓箭-2 | 0 | 0 | none |
| 50 | 弓箭-3 | 0 | 0 | none |
| 51 | 弓箭-4 | 0 | 0 | none |
| 52 | 弓箭-5 | 0 | 0 | none |
| 54 | 波波NPC | 53 | 0 | none |
| 55 | 船长NPC | 53 | 0 | none |
| 56 | 晤勒 | 53 | 0 | none |
| 57 | 保亚​ | 53 | 0 | none |
| 58 | 团长 | 53 | 0 | none |
| 61 | 敌方飞空艇2 | 0 | 0 | none |
| 102 | 刷怪点 | 0 | 0 | none |

Stages:
- 1. Aboard the Airship: Airship Chitchat (goals: [1,undefined,undefined] Have a Chitchat with them.)
- 2. Encounter the Evil Boss: Quick, attack the enemy ship! (goals: [5,0,1] Use the ship's cannons to attack the enemy vessels.)
- 3. Encounter the Evil Boss: The enemy has boarded the ship! (goals: [5,0,1] Eliminate all enemies.)
- 4. Encounter the Evil Boss: Encountered an evil Boss aboard the airship (goals: [5,0,1] Defeat the Evil Boss)
- 5. The Battle temporarily subsides: Take a look around (goals: [1,undefined,undefined] Take a look around)
- 6. Check the Captain's injuries: Check on the captain (goals: [1,undefined,undefined] Talk to the captain)

## 1010101 Izlude (story)

Editor `sc_izlude_001_dg01_sceneeditor`; 0 server points, 0 client-only; 0 point-respawn rows, 0 region rows; limit 60 min, 1 players; spawnable today: no.

Base map `sc_izlude_001_sceneeditor`: no monsters

## 1011301 Prontera North Gate (story)

Editor `sc_prt_fild01_001_pl01_sceneeditor`; 4 server points, 0 client-only; 0 point-respawn rows, 0 region rows; limit 60 min, 1 players; spawnable today: no.

| Point | Label | Group | NPC | Source |
|---:|---|---:|---|---|
| 20 | 刷怪点 | 0 | 0 | none |
| 21 | 刷怪点 | 0 | 0 | none |
| 22 | 刷怪点_副本 | 0 | 0 | none |
| 23 | 刷怪点_副本 | 0 | 0 | none |

Stages:
- 1. Investigate: Go to the fork and see what happened (goals: [5,0,1] Head to the fork in the road)
- 2. Encounter Baphomet: Encountered Baphomet at Prontera's North Gate (goals: [5,0,1] Defeat Baphomet)

Base map `sc_prt_fild01_001_sceneeditor`: 5000021 Yoyo Lv30, 5000016 Creamy Lv23, 5000002 Lunatic Lv3, 5000003 Fabre Lv8, 5000001 Poring Lv5

## 1015051 Culvert Floor @{1} (story)

Editor `sc_prt_sewb1_001_pl01_sceneeditor`; 12 server points, 0 client-only; 12 point-respawn rows, 0 region rows; limit 60 min, 1 players; spawnable today: no.

Server-only NPC ids (not in NPCConfig): 20001002, 20001004, 20001005, 20001006, 20001007, 20001008, 20001009, 20001010, 20001011

| Point | Label | Group | NPC | Source |
|---:|---|---:|---|---|
| 22 | 苍蝇1-1 | 0 | 20001007 (unknown) | editor |
| 23 | 苍蝇1-2 | 0 | 20001007 (unknown) | editor |
| 24 | 盗虫2-1 | 0 | 20001008 (unknown) | editor |
| 25 | 盗虫2-2 | 0 | 20001008 (unknown) | editor |
| 26 | 盗虫2-3 | 0 | 20001008 (unknown) | editor |
| 27 | 友方战士1 | 0 | 20001005 (unknown) | editor |
| 28 | 友方牧师1 | 0 | 20001004 (unknown) | editor |
| 29 | 友方战士2 | 0 | 20001006 (unknown) | editor |
| 30 | 友方弓箭手1 | 0 | 20001009 (unknown) | editor |
| 31 | 友方魔法师1 | 0 | 20001010 (unknown) | editor |
| 32 | 友方弓箭手2 | 0 | 20001011 (unknown) | editor |
| 33 | MVP黄金虫 | 0 | 20001002 (unknown) | editor |

Stages:
- 1. Find the Source of the Smell in the Culvert: Investigate the Area Around the Culvert (goals: [5,0,1] Clear out the nearby Chonchons.)
- 2. Continue Investigating Deeper…: Continue Investigating Along the Guided Route
- 3. Help the Adventurers Defeat the Golden Thief Bug: Defeat Golden Thief Bug (goals: [5,0,1] Eliminate the source of the strange odor in the Culvert: Golden Thief Bug.)

Base map `sc_prt_sewb1_001_sceneeditor`: 5000015 Thief Bug Lv21, 5000018 Tarou Lv25, 5000034 Thief Bug Egg Lv20, 5000019 Familiar Lv22

## 1015052 Culvert Floor @{1} (story)

Editor `sc_prt_sewb1_001_pl52_sceneeditor`; 0 server points, 0 client-only; 0 point-respawn rows, 0 region rows; limit 60 min, 1 players; spawnable today: no.

Stages:
- 1. Encounter Golden Thief Bug: Encountered Golden Thief Bug on Culvert Floor @{1} (goals: [5,0,1] Defeat Golden Thief Bug)

Base map `sc_prt_sewb1_001_sceneeditor`: 5000015 Thief Bug Lv21, 5000018 Tarou Lv25, 5000034 Thief Bug Egg Lv20, 5000019 Familiar Lv22

## 1020051 Payon (story)

Editor `sc_payon_001_pl01_sceneeditor`; 1 server points, 0 client-only; 1 point-respawn rows, 0 region rows; limit 60 min, 1 players; spawnable today: no.

Server-only NPC ids (not in NPCConfig): 20001015

| Point | Label | Group | NPC | Source |
|---:|---|---:|---|---|
| 17 | 刷怪点 | 0 | 20001015 (unknown) | editor |

Stages:
- 1. Complete the Instance Objectives: Eliminate the Threat Around Payon (goals: [5,0,1] Eliminate the elite Squirrel.)

Base map `sc_payon_001_sceneeditor`: no monsters

## 1021301 Southern Payon (story)

Editor `sc_pay_fild03_001_01_sceneeditor`; 9 server points, 0 client-only; 0 point-respawn rows, 0 region rows; limit 60 min, 1 players; spawnable today: no.

| Point | Label | Group | NPC | Source |
|---:|---|---:|---|---|
| 25 | 传送门1 | 0 | 0 | none |
| 26 | 传送门2 | 0 | 0 | none |
| 27 | 传送门3 | 0 | 0 | none |
| 28 | 箭头 | 0 | 0 | none |
| 29 | 箭头 | 0 | 0 | none |
| 30 | 箭头_副本 | 0 | 0 | none |
| 38 | 传送门1_副本 | 0 | 0 | none |
| 39 | 传送门2_副本 | 0 | 0 | none |
| 40 | 传送门3_副本 | 0 | 0 | none |

Stages:
- 1. Monsters Attack: - (goals: [5,0,1] Defeat three waves of attacking monsters.)

## 1021851 Eastern Payon (story)

Editor `sc_pay_fild08_001_pl01_sceneeditor`; 1 server points, 0 client-only; 1 point-respawn rows, 0 region rows; limit 60 min, 1 players; spawnable today: no.

Server-only NPC ids (not in NPCConfig): 20001014

| Point | Label | Group | NPC | Source |
|---:|---|---:|---|---|
| 17 | 刷怪点 | 0 | 20001014 (unknown) | editor |

Stages:
- 1. Complete the Instance Objectives: Eliminate the Threat in Eastern Payon (goals: [5,0,1] Eliminate the elite Mandragora.)

Base map `sc_pay_fild08_001_sceneeditor`: 5000014 Spore Lv18, 5000028 Boa Lv18, 5000030 Horn Lv16, 5000032 Coco Lv30, 5000012 Worm Tail Lv16

## 1022001 Payon Forest (story)

Editor `sc_pay_fild10_001_pl01_sceneeditor`; 16 server points, 0 client-only; 1 point-respawn rows, 0 region rows; limit 600 min, 1 players; spawnable today: yes.

| Point | Label | Group | NPC | Source |
|---:|---|---:|---|---|
| 20 | boss位置 | 0 | 5300001 Eddga | point-respawn |
| 21 | 唔勒 | 0 | 0 | none |
| 22 | 对峙点 | 0 | 0 | none |
| 23 | 保亚 | 0 | 0 | none |
| 34 | 战 | 24 | 0 | none |
| 35 | 猎 | 24 | 0 | none |
| 36 | 奶 | 24 | 0 | none |
| 37 | 商 | 24 | 0 | none |
| 38 | 刺 | 24 | 0 | none |
| 39 | 法 | 24 | 0 | none |
| 41 | 战 | 40 | 0 | none |
| 42 | 猎 | 40 | 0 | none |
| 43 | 奶 | 40 | 0 | none |
| 44 | 商 | 40 | 0 | none |
| 45 | 刺 | 40 | 0 | none |
| 46 | 法 | 40 | 0 | none |

Stages:
- 1. Encounter Eddga: Cover Boya and Wule (goals: [5,0,1] Cover Boya and Wule)
- 2. Work Together: Join the Adventurers and defeat Eddga (goals: [5,0,1] Join the Adventurers and defeat Eddga)

Base map `sc_pay_fild10_001_sceneeditor`: 5000070 Greatest General Lv55, 5000030 Horn Lv16, 5000110 Beetle King Lv55, 5000087 Elder Willow Lv34, 5000028 Boa Lv18, 5000077 Savage Lv59

## 1023001 Payon Instance (story)

Editor `sc_payon_001_pl02_sceneeditor` (missing); 0 server points, 0 client-only; 0 point-respawn rows, 0 region rows; limit 60 min, 50 players; spawnable today: no.

Base map `sc_payon_001_sceneeditor`: no monsters

## 1030051 Geffen (story)

Editor `sc_geffen_001_pl01_sceneeditor`; 1 server points, 0 client-only; 1 point-respawn rows, 0 region rows; limit 60 min, 1 players; spawnable today: no.

Server-only NPC ids (not in NPCConfig): 20001016

| Point | Label | Group | NPC | Source |
|---:|---|---:|---|---|
| 17 | 刷怪点 | 0 | 20001016 (unknown) | editor |

Stages:
- 1. Complete the Instance Objectives: Eliminate the Threat Around Geffen (goals: [5,0,1] Eliminate the elite Savage.)

Base map `sc_geffen_001_sceneeditor`: no monsters

## 1032101 Goblin Forest (story)

Editor `sc_gef_fild11_001_pl01_sceneeditor`; 4 server points, 0 client-only; 0 point-respawn rows, 0 region rows; limit 60 min, 1 players; spawnable today: no.

| Point | Label | Group | NPC | Source |
|---:|---|---:|---|---|
| 20 | 刷怪点 | 0 | 0 | none |
| 21 | 刷怪点 | 0 | 0 | none |
| 22 | 刷怪点_副本 | 0 | 0 | none |
| 23 | 刷怪点_副本_副本 | 0 | 0 | none |

Stages:
- 1. Investigate: Go to the fork and see what happened (goals: [5,0,1] Head to the fork in the road)
- 2. Mysterious Space: Pulled into a mysterious space by a giant hand that suddenly appeared (goals: [5,0,1] Defeat Pharaoh)

Base map `sc_gef_fild11_001_sceneeditor`: 5000046 Rotar Zairo Lv45, 5000111 Goblin Archer Lv55, 5000130 Goblin Steamrider Lv66, 5000092 Goblin (Axe) Lv44, 5000093 Goblin (Flail) Lv44, 5000105 Goblin (Dagger) Lv48, 5000117 Goblin (Hammer) Lv49, 5000114 Goblin (Mace) Lv56

## 1033051 Geffen East Gate (story)

Editor `sc_gef_fild00_001_pl01_sceneeditor`; 11 server points, 0 client-only; 0 point-respawn rows, 0 region rows; limit 60 min, 1 players; spawnable today: no.

Server-only NPC ids (not in NPCConfig): 20001012, 20001018, 20001019, 20001020, 20001021

| Point | Label | Group | NPC | Source |
|---:|---|---:|---|---|
| 18 | 小野猪 | 0 | 20001012 (unknown) | editor |
| 19 | 传送门 | 0 | 20001018 (unknown) | editor |
| 20 | 绿棉虫1 | 0 | 20001019 (unknown) | editor |
| 21 | 绿棉虫2 | 0 | 20001019 (unknown) | editor |
| 22 | 绿棉虫3 | 0 | 20001019 (unknown) | editor |
| 23 | 蝗虫1 | 0 | 20001020 (unknown) | editor |
| 24 | 蝗虫2 | 0 | 20001020 (unknown) | editor |
| 25 | 蝗虫3 | 0 | 20001020 (unknown) | editor |
| 26 | 罗达蛙1 | 0 | 20001021 (unknown) | editor |
| 27 | 罗达蛙2 | 0 | 20001021 (unknown) | editor |
| 28 | 罗达蛙3 | 0 | 20001021 (unknown) | editor |

Stages:
- 1. Complete the Instance Objectives: Eliminate the Threat East of Geffen (goals: [5,0,1] Eliminate the monsters emerging from the portal.)

Base map `sc_gef_fild00_001_sceneeditor`: 5000001 Poring Lv5, 5000003 Fabre Lv8, 5000013 Ambernite Lv19, 5000011 Roda Frog Lv13, 5000005 Chonchon Lv21

## 1033052 Geffen East Gate (story)

Editor `sc_gef_fild00_001_pl02_sceneeditor`; 16 server points, 0 client-only; 16 point-respawn rows, 0 region rows; limit 60 min, 1 players; spawnable today: no.

Server-only NPC ids (not in NPCConfig): 20001016, 20001018, 20001019, 20001020, 20001021, 20001022

| Point | Label | Group | NPC | Source |
|---:|---|---:|---|---|
| 17 | 传送门1 | 0 | 20001018 (unknown) | editor |
| 18 | 传送门2 | 0 | 20001018 (unknown) | editor |
| 19 | 传送门3 | 0 | 20001018 (unknown) | editor |
| 20 | 绿棉虫1 | 0 | 20001019 (unknown) | editor |
| 21 | 绿棉虫2 | 0 | 20001019 (unknown) | editor |
| 22 | 绿棉虫3 | 0 | 20001019 (unknown) | editor |
| 23 | 毒蛇1 | 0 | 20001022 (unknown) | editor |
| 24 | 毒蛇2 | 0 | 20001022 (unknown) | editor |
| 25 | 毒蛇3 | 0 | 20001022 (unknown) | editor |
| 26 | 蝗虫1 | 0 | 20001020 (unknown) | editor |
| 27 | 蝗虫2 | 0 | 20001020 (unknown) | editor |
| 28 | 蝗虫3 | 0 | 20001020 (unknown) | editor |
| 29 | 罗达蛙1 | 0 | 20001021 (unknown) | editor |
| 30 | 罗达蛙2 | 0 | 20001021 (unknown) | editor |
| 31 | 罗达蛙3 | 0 | 20001021 (unknown) | editor |
| 32 | 小野猪 | 0 | 20001016 (unknown) | editor |

Stages:
- 1. Complete the Instance Objectives: Eliminate the Threat East of Geffen (goals: [5,0,1] Eliminate the monsters emerging from the portal.)

Base map `sc_gef_fild00_001_sceneeditor`: 5000001 Poring Lv5, 5000003 Fabre Lv8, 5000013 Ambernite Lv19, 5000011 Roda Frog Lv13, 5000005 Chonchon Lv21

## 1051001 Desert Crossroad (story)

Editor `sc_moc_fild01_pl02_sceneeditor`; 8 server points, 0 client-only; 0 point-respawn rows, 0 region rows; limit 60 min, 1 players; spawnable today: no.

| Point | Label | Group | NPC | Source |
|---:|---|---:|---|---|
| 26 | 刷怪点 | 0 | 0 | none |
| 27 | 刷怪点 | 0 | 0 | none |
| 32 | 刷怪点 | 0 | 0 | none |
| 33 | 刷怪点 | 0 | 0 | none |
| 34 | 刷怪点 | 0 | 0 | none |
| 35 | 刷怪点 | 0 | 0 | none |
| 36 | 刷怪点 | 0 | 0 | none |
| 37 | 刷怪点 | 0 | 0 | none |

Stages:
- 1. Peco Peco Ancient Nest: - (goals: [1,undefined,undefined] Defeat the Peco Peco and rescue Bragis.)

## 1051051 Desert Crossroad (story)

Editor `sc_moc_fild01_001_pl01_sceneeditor`; 1 server points, 0 client-only; 1 point-respawn rows, 0 region rows; limit 60 min, 1 players; spawnable today: no.

Server-only NPC ids (not in NPCConfig): 20001013

| Point | Label | Group | NPC | Source |
|---:|---|---:|---|---|
| 17 | 刷怪点 | 0 | 20001013 (unknown) | editor |

Stages:
- 1. Complete the Instance Objectives: Eliminate the Threat at Desert Crossroad (goals: [5,0,1] Eliminate the elite Scorpion.)

Base map `sc_moc_fild01_001_sceneeditor`: 5000050 Muka Lv23, 5000083 Peco Peco Lv25, 5000033 Peco Peco Egg Lv7, 5000069 Magnolia Lv53

## 1054001 Ant Hell (story)

Editor `sc_anthell02_001_pl01_sceneeditor`; 0 server points, 0 client-only; 0 point-respawn rows, 0 region rows; limit 60 min, 1 players; spawnable today: no.

Base map `sc_anthell02_001_sceneeditor`: 5000057 Andre Lv28, 5000059 Vitata Lv28, 5000085 Ant Egg Lv28, 5000055 Deniro Lv28, 5000056 Piere Lv28, 5000019 Familiar Lv22, 5000061 Giearth Lv42

## 3120402 Culvert (story)

Editor `sc_pm_pve5sewb_001_dg02_sceneeditor`; 6 server points, 0 client-only; 0 point-respawn rows, 0 region rows; limit 600 min, 10 players; spawnable today: no.

| Point | Label | Group | NPC | Source |
|---:|---|---:|---|---|
| 25 | 刷怪点 | 24 | 0 | none |
| 26 | 刷怪点 | 24 | 0 | none |
| 27 | 刷怪点 | 24 | 0 | none |
| 29 | 刷怪点 | 28 | 0 | none |
| 30 | 刷怪点 | 28 | 0 | none |
| 31 | 刷怪点 | 28 | 0 | none |

Stages:
- 1. Complete Louie's game: - (goals: [5,0,1] Find Kitty-Katie)
- 2. Retrieve Instrument: - (goals: [1,undefined,undefined] Retrieve the Professor's instrument.)

## 3120702 Pharaoh (story)

Editor `sc_pm_mainstory_sphinx_dg01_sceneeditor`; 2 server points, 0 client-only; 0 point-respawn rows, 0 region rows; limit 600 min, 10 players; spawnable today: no.

| Point | Label | Group | NPC | Source |
|---:|---|---:|---|---|
| 20 | 刷怪点 | 0 | 0 | none |
| 24 | 刷怪点 | 0 | 0 | none |

Stages:
- 1. Mysterious Space: Pulled into a mysterious space by a giant hand that suddenly appeared (goals: [5,0,1] Defeat Pharaoh)

## 3120703 Pharaoh (story)

Editor `sc_pm_branchstory_sphinx_001_sceneeditor`; 6 server points, 0 client-only; 0 point-respawn rows, 0 region rows; limit 600 min, 10 players; spawnable today: no.

| Point | Label | Group | NPC | Source |
|---:|---|---:|---|---|
| 25 | 刷怪点 | 24 | 0 | none |
| 26 | 刷怪点 | 24 | 0 | none |
| 27 | 刷怪点 | 24 | 0 | none |
| 29 | 刷怪点 | 28 | 0 | none |
| 30 | 刷怪点 | 28 | 0 | none |
| 31 | 刷怪点 | 28 | 0 | none |

Stages:
- 1. Complete Louie's game: - (goals: [5,0,1] Find Kitty-Katie)

## 3120802 Moonlight Flower (story)

Editor `sc_pm_branchstory_paydun_001_sceneeditor`; 44 server points, 0 client-only; 0 point-respawn rows, 0 region rows; limit 600 min, 10 players; spawnable today: no.

| Point | Label | Group | NPC | Source |
|---:|---|---:|---|---|
| 25 | 刷怪点 | 24 | 0 | none |
| 26 | 刷怪点 | 24 | 0 | none |
| 67 | 刷怪点 | 27 | 0 | none |
| 68 | 刷怪点 | 27 | 0 | none |
| 69 | 刷怪点 | 27 | 0 | none |
| 70 | 刷怪点 | 27 | 0 | none |
| 71 | 刷怪点 | 27 | 0 | none |
| 72 | 刷怪点 | 27 | 0 | none |
| 73 | 刷怪点 | 27 | 0 | none |
| 74 | 刷怪点 | 27 | 0 | none |
| 75 | 刷怪点 | 27 | 0 | none |
| 76 | 刷怪点 | 27 | 0 | none |
| 77 | 刷怪点 | 27 | 0 | none |
| 78 | 刷怪点 | 27 | 0 | none |
| 79 | 刷怪点 | 27 | 0 | none |
| 80 | 刷怪点 | 27 | 0 | none |
| 81 | 刷怪点 | 27 | 0 | none |
| 82 | 刷怪点 | 27 | 0 | none |
| 83 | 刷怪点 | 27 | 0 | none |
| 84 | 刷怪点 | 27 | 0 | none |
| 85 | 刷怪点 | 27 | 0 | none |
| 86 | 刷怪点 | 27 | 0 | none |
| 87 | 刷怪点 | 27 | 0 | none |
| 88 | 刷怪点 | 27 | 0 | none |
| 89 | 刷怪点 | 27 | 0 | none |
| 90 | 刷怪点 | 27 | 0 | none |
| 91 | 刷怪点 | 27 | 0 | none |
| 92 | 刷怪点 | 27 | 0 | none |
| 93 | 刷怪点 | 27 | 0 | none |
| 94 | 刷怪点 | 27 | 0 | none |
| 95 | 刷怪点 | 27 | 0 | none |
| 96 | 刷怪点 | 27 | 0 | none |
| 97 | 刷怪点 | 27 | 0 | none |
| 98 | 刷怪点 | 27 | 0 | none |
| 99 | 刷怪点 | 27 | 0 | none |
| 100 | 刷怪点 | 27 | 0 | none |
| 101 | 刷怪点 | 27 | 0 | none |
| 102 | 刷怪点 | 27 | 0 | none |
| 103 | 刷怪点 | 27 | 0 | none |
| 104 | 刷怪点 | 27 | 0 | none |
| 105 | 刷怪点 | 27 | 0 | none |
| 106 | 刷怪点 | 27 | 0 | none |
| 107 | 刷怪点 | 27 | 0 | none |
| 108 | 刷怪点 | 27 | 0 | none |

Stages:
- 1. Complete Moonlight Flower's game: - (goals: [5,0,1] Hide in the darkness. Don't let them Discover you.)

## 3120902 Baphomet (story)

Editor `sc_pm_mainstory_maze_dg01_sceneeditor`; 3 server points, 0 client-only; 0 point-respawn rows, 0 region rows; limit 600 min, 10 players; spawnable today: no.

| Point | Label | Group | NPC | Source |
|---:|---|---:|---|---|
| 20 | 刷怪点 | 0 | 0 | none |
| 25 | 刷怪点 | 0 | 0 | none |
| 26 | 刷怪点 | 0 | 0 | none |

Stages:
- 1. Encounter Baphomet: Defeat Baphomet (goals: [5,0,1] Defeat Baphomet)

## 4400001 Ring Test (story)

Editor `sc_pm_pve5pryd_001_dg01_sceneeditor`; 20 server points, 0 client-only; 0 point-respawn rows, 1 region rows; limit 60 min, 1 players; spawnable today: no.

| Point | Label | Group | NPC | Source |
|---:|---|---:|---|---|
| 19 | 刷怪点 | 0 | 0 | none |
| 47 | 刷怪点 | 0 | 0 | none |
| 48 | 刷怪点 | 0 | 0 | none |
| 49 | 刷怪点 | 0 | 0 | none |
| 50 | 刷怪点 | 0 | 0 | none |
| 58 | 刷怪点 | 0 | 0 | none |
| 59 | 刷怪点 | 0 | 0 | none |
| 60 | 刷怪点 | 0 | 0 | none |
| 61 | 刷怪点 | 0 | 0 | none |
| 62 | 刷怪点 | 0 | 0 | none |
| 63 | 刷怪点 | 0 | 0 | none |
| 65 | 刷怪点 | 0 | 0 | none |
| 66 | 刷怪点 | 0 | 0 | none |
| 67 | 刷怪点 | 0 | 0 | none |
| 68 | 刷怪点 | 0 | 0 | none |
| 69 | 刷怪点 | 0 | 0 | none |
| 70 | 刷怪点 | 0 | 0 | none |
| 71 | 刷怪点 | 0 | 0 | none |
| 72 | 刷怪点 | 0 | 0 | none |
| 73 | 刷怪点 | 0 | 0 | none |

## 5000101 MVP Role-Playing Party (story)

Editor `sc_gef_fild00_001_pl01_sceneeditor`; 11 server points, 0 client-only; 11 point-respawn rows, 0 region rows; limit 60 min, 2000 players; spawnable today: no.

Server-only NPC ids (not in NPCConfig): 20001012, 20001018, 20001019, 20001020, 20001021

| Point | Label | Group | NPC | Source |
|---:|---|---:|---|---|
| 18 | 小野猪 | 0 | 20001012 (unknown) | editor |
| 19 | 传送门 | 0 | 20001018 (unknown) | editor |
| 20 | 绿棉虫1 | 0 | 20001019 (unknown) | editor |
| 21 | 绿棉虫2 | 0 | 20001019 (unknown) | editor |
| 22 | 绿棉虫3 | 0 | 20001019 (unknown) | editor |
| 23 | 蝗虫1 | 0 | 20001020 (unknown) | editor |
| 24 | 蝗虫2 | 0 | 20001020 (unknown) | editor |
| 25 | 蝗虫3 | 0 | 20001020 (unknown) | editor |
| 26 | 罗达蛙1 | 0 | 20001021 (unknown) | editor |
| 27 | 罗达蛙2 | 0 | 20001021 (unknown) | editor |
| 28 | 罗达蛙3 | 0 | 20001021 (unknown) | editor |

Base map `sc_gef_fild00_001_sceneeditor`: 5000001 Poring Lv5, 5000003 Fabre Lv8, 5000013 Ambernite Lv19, 5000011 Roda Frog Lv13, 5000005 Chonchon Lv21

## 8100001 Boss Challenge (tutorial)

Editor `sc_pm_newbee_001_dg01_sceneeditor`; 1 server points, 0 client-only; 1 point-respawn rows, 0 region rows; limit 600 min, 1 players; spawnable today: yes.

| Point | Label | Group | NPC | Source |
|---:|---|---:|---|---|
| 26 | 刷怪点 | 0 | 81000001 Angeling | point-respawn |

Stages:
- 1. ^{1}Novice Challenge^{2}: Challenge Angeling (goals: [5,1,2] ^{1}Defeat Angeling^{2}; [4,0,undefined] Time Left)

## 8100002 Boss Challenge (tutorial)

Editor `sc_pm_newbee_001_dg01_sceneeditor`; 1 server points, 0 client-only; 1 point-respawn rows, 0 region rows; limit 600 min, 1 players; spawnable today: yes.

| Point | Label | Group | NPC | Source |
|---:|---|---:|---|---|
| 26 | 刷怪点 | 0 | 81000002 Eddga | point-respawn |

Stages:
- 1. ^{1}Novice Challenge^{2}: Challenge Eddga (goals: [5,1,2] ^{1}Defeat Eddga^{2}; [4,0,undefined] Time Left)

## 8100003 Boss Challenge (tutorial)

Editor `sc_pm_newbee_001_dg01_sceneeditor`; 1 server points, 0 client-only; 1 point-respawn rows, 0 region rows; limit 600 min, 1 players; spawnable today: yes.

| Point | Label | Group | NPC | Source |
|---:|---|---:|---|---|
| 26 | 刷怪点 | 0 | 81000003 Dracula | point-respawn |

Stages:
- 1. ^{1}Novice Challenge^{2}: Challenge Dracula (goals: [5,1,2] ^{1}Defeat Dracula^{2}; [4,0,undefined] Time Left)

## 8100004 Boss Challenge (tutorial)

Editor `sc_pm_newbee_001_dg01_sceneeditor`; 1 server points, 0 client-only; 1 point-respawn rows, 0 region rows; limit 600 min, 1 players; spawnable today: yes.

| Point | Label | Group | NPC | Source |
|---:|---|---:|---|---|
| 26 | 刷怪点 | 0 | 81000004 Mistress | point-respawn |

Stages:
- 1. ^{1}Novice Challenge^{2}: Challenge Mistress (goals: [5,1,2] ^{1}Defeat Mistress^{2}; [4,0,undefined] Time Left)

## 8100005 Boss Challenge (tutorial)

Editor `sc_pm_newbee_001_dg01_sceneeditor`; 1 server points, 0 client-only; 1 point-respawn rows, 0 region rows; limit 600 min, 1 players; spawnable today: yes.

| Point | Label | Group | NPC | Source |
|---:|---|---:|---|---|
| 26 | 刷怪点 | 0 | 81000005 Osiris | point-respawn |

Stages:
- 1. ^{1}Novice Challenge^{2}: Challenge Osiris (goals: [5,1,2] ^{1}Defeat Osiris^{2}; [4,0,undefined] Time Left)

## 8100006 Boss Challenge (tutorial)

Editor `sc_pm_newbee_001_dg01_sceneeditor`; 1 server points, 0 client-only; 1 point-respawn rows, 0 region rows; limit 600 min, 1 players; spawnable today: yes.

| Point | Label | Group | NPC | Source |
|---:|---|---:|---|---|
| 26 | 刷怪点 | 0 | 81000006 Moonlight Flower | point-respawn |

Stages:
- 1. ^{1}Novice Challenge^{2}: Challenge Moonlight Flower (goals: [5,1,2] ^{1}Defeat Moonlight Flower^{2}; [4,0,undefined] Time Left)

## 8100007 Boss Challenge (tutorial)

Editor `sc_pm_newbee_001_dg01_sceneeditor`; 1 server points, 0 client-only; 1 point-respawn rows, 0 region rows; limit 600 min, 1 players; spawnable today: yes.

| Point | Label | Group | NPC | Source |
|---:|---|---:|---|---|
| 26 | 刷怪点 | 0 | 81000007 Doppelganger | point-respawn |

Stages:
- 1. ^{1}Novice Challenge^{2}: Challenge Doppelganger (goals: [5,1,2] ^{1}Defeat Doppelganger^{2}; [4,0,undefined] Time Left)

