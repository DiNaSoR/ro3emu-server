# Season talents

`SeasonTalentService` owns the durable state (`DBSeasonTalent`, sync 5050) and the three client requests: 83550
upgrade, 83552 switch plan, 83553 rename plan. `SeasonTalentService.Effects` applies the chosen nodes' effects.
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
- takes the selected small node, or a large node that has no small node for the role's job
  (`SeasonTalentService.Profession.cs`), and refuses anything else (`TalentInvalid`): a node without a level-1 row
  for the role's job, or a large node whose small nodes the role can choose;
- sets the branch point, the large node and the small node to level 1 and clears every other node of that branch
  point's choice in this plan. Choosing the same node again changes nothing. One small node per choice;
- charges nothing: every exported job row's `_kCosts` is empty.

## Total talent level

Tree and node thresholds count the total the talent panel shows (`UI_Sys_Skill_SeasonTalentPanelCtrl._refreshGrade`):
every node of every tree except the branch points (type 5). That is the selected plan's ordinary and job node levels
plus the shared oath level. The tree gates (`SeasonTalentTreeConfig._iNeedLevel`) need the job nodes: each one equals
all ordinary levels of the trees before it plus 2 per branch point, one large and one small node per choice:

| Tree | Gate | Ordinary levels before it | Branch points before it |
| --- | --- | --- | --- |
| 10103 | 126 | 120 | 3 |
| 10104 | 228 | 220 | 4 |
| 10105 | 330 | 320 | 5 |
| 10106-10110 | 510, 690, 870, 1050, 1230 | each tree adds 180 | 5 |
| 10111 | 1590 | 1580 | 5 |

Without the job nodes no tree after the second could open. **(inferred)** that the server's gate uses the same number
the panel shows.

`_kProfessionID` on large node rows names the schools the select list shows as suited. It is not a restriction.

## Job node effects

A job row's `_kSpecialEffectList` names `SpecialEffectConfig` rows. Their `_kSpecialEffect` tuples apply while the
node is bought in the current plan (`SeasonTalentService.Effects`, described with the talent procs and charges in
[skill-protocol.md](skill-protocol.md#talent-effects)):

| Opcode | Tuples | Meaning | Applied by |
| --- | --- | --- | --- |
| 1 | 18 | `[1, attribute, value]` | stats |
| 2 | 160 | `[2, buff]`: the buff's `_kSkillAttributeList`, and its `_kLogicData` proc (`TalentProcs`) | skill rules, procs |
| 16 | 7 | `[16, weapon subtype, attribute, value]` **(inferred)** | stats |
| 19 | 18 | `[19, skill group, level, attribute, value]` **(inferred)** | stats |
| 21 | 36 | `[21, weapon subtype or 0, scope, selector, type, operation, value, index]` **(inferred)** | skill rules |
| 22 | 4 | `[22, weapon subtype, attribute, per level, skill group]` **(inferred)** | stats |
| 12, 20 | 7 | 12: another buff's parameters (BuffDictRef); 20: unknown | 12: buff values; 20: not applied |

The opcode readings come from each row's English description. For example, 16120004 "Bowling Bash's Cooldown is
changed to 8 seconds" is `[21,0,0,11104,0,2,8000]`, and 16520016 "When a dagger or katar is equipped, Physical
ATK +20" is `[16,1,103,20]` plus `[16,12,103,20]`. Opcode 21 after its weapon slot is a `BuffConfig._kSkillAttributeList`
tuple, so it joins `SkillParameterRules` through `GameSession.TalentSkillModifiers`, with the buffs' own tuples.

`SeasonTalentTests` covers the branch choice, switching branches, relog, plan switch, the job filter and an
attribute effect. Behavior in the client has not been verified.
