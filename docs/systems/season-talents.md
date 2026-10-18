# Season talents

`SeasonTalentService` owns the durable state (`DBSeasonTalent`, sync 5050) and the three client requests: 83550
upgrade, 83552 switch plan, 83553 rename plan. `SeasonTalentEffects` owns the job node buffs and skill changes.
The talent graph is `SeasonTalentConfig` (232 nodes), `SeasonTalentTreeConfig` (11 trees) and
`SeasonTalentEffectConfig` (level rows by group and job). Node types follow the client's `enum_SeasonTalentType`
in `Lua_GameDefine`: Small 1, Middle 2, Large 3, PVP 4, SkillPoint 5, Profession_L 6, Profession_M 7.

## Ordinary nodes and the oath

Types 1 and 2 (166 nodes) cost Talent Remnants (item 1250900) and Minted Coin (currency 2), and add their row's
`_kAttrs`. A node opens when its tree is unlocked, the plan's total level reaches its `_iNeedLevel`, and it is a
start point or one incoming parent is at max level. The PVP node 10100000 (Battle Oath) has one shared level across
plans, capped by the unlocked trees' `_iUpPvPLvLimit`. Five plans and the 3-second switch cooldown are
**(inferred)** from `PublicParamConfig.SchemeNum=5`.

## Job branches

Trees 10102-10104 carry job branches. A branch point (type 5) has no level group, and the client never sends it.
Its widget (`UI_Sys_Skill_SeasonTalent_JobSeasonWidget`) opens `UI_Sys_Skill_SeasonTalent_SelectPanel`. That panel
lists the branch point's large job nodes (type 6), each with its small job nodes (type 7). Its Finish button sends
83550 with the selected node. `LC_SeasonTalentHelper.FindSelectedChild` treats the large node at max level as the
chosen branch. The panel's auto-advance needs the branch point itself at max level before moving to its ordinary
child. So the server, **(inferred)**:

- opens a job node while its branch point would open (a large node's parent is the type-5 node, a small node's
  parent is the large node);
- refuses a node without a level-1 row for the role's job (`FeatureUnavailable`). Rows are per job: a third-rank
  job sees two or three large nodes per branch point, each with two small nodes;
- on a purchase, sets the node and its large node to level 1, sets the branch point to level 1 (opening the
  ordinary node after it) and clears a different large node already chosen at that branch point in this plan,
  with its small nodes. Each small node of the chosen branch can be taken;
- charges a job node's `_kCosts` (empty for every exported row).

## Total talent level

Tree and node thresholds count the total the talent panel shows (`UI_Sys_Skill_SeasonTalentPanelCtrl._refreshGrade`):
every node of every tree except the branch points (type 5). That is the selected plan's ordinary and job node levels
plus the shared oath level. Job nodes must count: the first two trees' ordinary nodes stop at 120 levels, and the third
tree needs 126. **(inferred)** that the server's gate uses the same number the panel shows.

`_kProfessionID` on large node rows names the schools the select list shows as suited. It is not a restriction.

## Job node effects

A job row's `_kSpecialEffectList` names `SpecialEffectConfig` rows. Their `_kSpecialEffect` tuples apply while the
node is chosen in the current plan:

| Opcode | Tuples | Meaning | Applied by |
| --- | --- | --- | --- |
| 1 | 18 | `[1, attribute, value]` | stats |
| 2 | 160 | `[2, buff]`: a zero-time `BuffConfig` row, registered as a passive owned by the node | buffs, skill rules |
| 16 | 7 | `[16, weapon subtype, attribute, value]` **(inferred)** | stats |
| 19 | 18 | `[19, skill group, level, attribute, value]` **(inferred)** | stats |
| 21 | 36 | `[21, weapon subtype or 0, scope, selector, type, operation, value, index]` **(inferred)** | skill rules |
| 22 | 4 | `[22, weapon subtype, attribute, per level, skill group]` **(inferred)** | stats |
| 12, 20 | 7 | another buff's parameters; unknown | not applied |

The opcode readings come from each row's English description. For example, 16120004 "Bowling Bash's Cooldown is
changed to 8 seconds" is `[21,0,0,11104,0,2,8000]`, and 16520016 "When a dagger or katar is equipped, Physical
ATK +20" is `[16,1,103,20]` plus `[16,12,103,20]`. The weapon is the main hand's `ItemConfig._iSubType`
(`SkillService.Weapon`). Opcode 21 after its weapon slot is a `BuffConfig._kSkillAttributeList` tuple, so it
joins `SkillParameterRules` through `GameSession.ExtraSkillModifiers`. Opcode 2 buffs reach the same rules through
their own `_kSkillAttributeList`. Buff procs in `_kLogicData` (65 of the 160 buffs) are not executed: the server has
no generic proc engine for them.

Talent passives are registered again on scene load (their `_iSceneClear`), revive, weapon change, job change, plan
switch and Build preset apply. `CardEffects` leaves them alone (`ActiveBuff.IsTalentOwned`).

`SeasonTalentTests` covers the branch choice, switching branches, relog, plan switch, the job filter and an
attribute effect. Behavior in the client has not been verified.
