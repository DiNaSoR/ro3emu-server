# Combat skill protocol and parameters

This part of the server consists of `CombatService.Protocol.cs` and `SkillParameterRules.cs`, tested by `SkillProtocolTests.cs`. It reads the same `SkillConfig` row for server casting and the client parameter notification. Each role keeps its temporary wheel rows, parameter signature, selection deltas, and training pause flag in `GameSession.SkillProtocol`. Boss cooldowns live on the actual `MonsterUnit` and are filtered to the requesting viewer's scene and AOI.

| Route | Implemented source and boundary |
| --- | --- |
| 31027 | A learned wheel slot changes to a configured mode row when a live buff's type-15 modifier selects that mode, and reverts on buff removal. The update uses `Replace` and the active manual plan's selected slot level (bounded by learned level), so a lower slotted rank is preserved. The durable wheel remains 5025. |
| 31132 | Active `BuffConfig._kSkillAttributeList` scope-0 entries for a learned group yield full effective scalar/list values. `isAll=true` replaces prior state; a group-0 empty record ensures the Lua `affectParamDict` check does not return before clearing a now-empty map. Buff changes, talent changes, learned-plan changes, and scene load call `RefreshSkillParametersAsync`. |
| 31100/06/21/07 | `SendCastTimelineAsync` and `SendMonsterTimelineAsync` each select one route for one validated onset. High-priority/extra routing must come from a real cast/proc source; these are alternate packets, never duplicates of the same timeline. |
| 31119/09 and 31120/08 | Bounded send methods accept resolved effect/bullet records with a valid skill/source. No source-backed independent producer is wired, so these routes are **not qualified**. The ordinary client timeline already plays effect and projectile events. |
| 31122 | A per-viewer `SetSkillTargetSelectionAsync` emits only a changed tag for a visible target. No source-backed eligibility transition is wired, so this route is **not qualified**. |
| 31125/26/27 | Actual cooldown end times are stored on a live monster. The client requests 31127 for an NPC with `_iImportantSkill=1`; the server returns visible, unexpired entries or a target refusal. Scene transition clears client display via 31126. |
| 31129/33 | Actual resolved damage entries are sent without invented `FormulaInfo`. Numeric 1 pauses this viewer's statistics, numeric 2 resumes. Invalid op values get `ErrorCodes.Parameter`. |
| 31118 | Legacy scalar-only carrier. The inspected client routes 31132 to `LC_DynamicParamMgr.ProcessDynamicSkillParam`; emitting both would give two representations of one change. The server emits no 31118. |

`SkillParameterRules.Active` parses `[scope, selector, type, operation, value, optional index]`. Scope 0 is exact skill group. Arithmetic operation 1=add, 2=set, 5=basis-point multiply is **(inferred)** from exported buff tuples. Damage-vector index -1 applying to every component is **(inferred)** from rows such as BuffConfig 1246101 on SkillConfig 1220101 `[6000,8000]`; the focused test covers both components. The handled field types are cooldown 0, action time 21, chant 23/24, distance 25/26, target cap 27, damage arrays 29/30, mode 15 and queue eligibility 44. Category scope 1 and structured tuple types 33-38 need recovered source semantics before they can alter combat; they are intentionally not applied. Direct `_kAttri` on the highest learned mode-0 passive row contributes attributes once. The active talent plan adds its own tuples ahead of the buffs' (see below).

Mode rows are looked up by actual `(group, level, mode)` identities, never arithmetic ID guesses. A client-submitted nonzero mode row must match the active type-15 mode modifier. `LC_DynamicParamMgr.lua:196-214,366-386` consumes type 15 as the skill attack mode. A requested base row resolves to the configured mode row while it is active. The same resolved row feeds `_iCD`, chant, range, damage and cost validation in the cast owner. A mode without a configured row falls back to base. `Lua_CFG_SkillConfig.GetHighBuffInfo` at lines 2526-2674 parses `_kSkillHighParam` buff/group/energy terms into keys supplied by its final tuple member. Those conditions are not used as a cast eligibility gate: the export does not show such a use, and their effect-tier purpose remains **(inferred)**.

The client evidence is `re/clientB/lua-src/Logic/Asset/LC_DAL_Joystick.lua:48-95` for 31027, `Logic/Skill/LC_DynamicParamMgr.lua:20-320` and `Network/NW_GameServerHandler.lua:960-965,1728-1746` for 31132, `Logic/Scene/LC_Scene_Sync.lua:3730-3896,3898-4434,4521-4539,4742-4762,7766-7912` for route handling, `Logic/Asset/LC_DAL_BossShowSkill.lua:8-73` and `Logic/Selector/LC_SelectorManager.lua:2474-2494` for boss display, and `Logic/Damage/LC_DamageStatistics.lua:378-405,450-538` for training. Proto fields are in `MG_Skill.proto:27-101,182-204,306-423`.

An exact `rg -a -l` search for route and selector evidence over `data/config/*.json`, `data/sceneeditor` (including monster-id maps), `data/nav`, `data/loc`, the complete `re/lua-src` Config and Logic trees (counted separately), `re/decomp` and the protos found these matching file counts:

| Exact pattern | data/config | sceneeditor | nav | loc | Lua Config | Lua Logic | decomp | protos |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `SKILL_EFFECT_DISPLAY_NOTIFY\|SKILL_FIXTIME_BULLET_NOTIFY\|SKILL_WHITE_LIST_CHANGE_NOTIFY` | 0 | 0 | 0 | 0 | 0 | 1 (`LC_Scene_Sync.lua`) | 0 | 1 (`MG_Define.proto`) |
| `_kShows\|m_kShows\|EffectDisplay\|FixTimeBullet\|WhiteListChange` | 0 | 0 | 0 | 0 | 2219 (skill presentation config) | 6 | 0 | 1 (`MG_Skill.proto`) |
| `GetShowBossSkillList\|_iImportantSkill\|GetHighBuffInfo` | 2 (`NPCConfig`, `SkillConfig`) | 0 | 0 | 0 | 3 | 1 (`LC_SelectorManager.lua`) | 0 | 0 |

The second search's Lua Logic matches include `LC_Skill_RunTimeContext.lua:1485-1497,3044-3088`, which loads `m_kClientPoints` from the skill timeline and creates a timed bullet locally. `LC_Scene_Sync.lua:4521-4539,4742-4762,7766-7912` separately receives the optional effect, bullet and selection packets; a shared handler does not establish an identical server trigger. `data/skills/skill_12010.json` already has Fire Wall `m_kShows` effect 1201005/1201002/1201006, so adding a standalone 31119 at its ordinary onset would risk duplicate presentation **(inferred)**. The inspected material establishes no concrete independent producer for 31119/20/08/09 or 31122. Alternate high-level route selection and the arithmetic operation mapping remain **(inferred)**.

For the unqualified category-scope interpreter, exact `_kSkillAttributeList|SkillAttributeList` search found one `data/config` match (`BuffConfig.json`), zero in sceneeditor/nav/loc, one in Lua Config (`DataConfig/BuffConfig.lua`), and zero in Lua Logic/decomp/protos. The export contains 477 scope-1 tuples whose selectors include `-1`, numeric `_kSkillCategory` values, and compound values such as `3012or3015` and `1000and1001`. Category membership is plausible **(inferred)**, but the all-category sentinel, compound operators, and some negative vector indices require a confirmed interpreter before applying scope 1. An exact `_iCdModeGroup|CdModeGroup|CDModeGroup|cdModeGroup` search over the same roots found only `SkillConfig.json` and Lua `DataConfig/SkillConfig.lua`; no Lua Logic or decomp use was found. Cast integration uses a negative `_iCdModeGroup` for group-wide cooldown and otherwise retains the explicit mode key **(inferred)**; the positive field value is not interpreted as a separate sharing key.

Focused wire tests cover one buff's 31132 change and reset against the effective cast row, 31027 replacement at a manually selected level below the learned level, 31133 refusal/pause/resume with a real 31129 entry and no fake formula trace, and a 31127 refusal for a nonvisible boss id. A live boss special-cast, independent effect/projectile script, and target-eligibility producer need their own source-backed tests before their optional routes can be qualified.

An empty successful 31127 snapshot is safe for the inspected client: `LC_DAL_BossShowSkill.lua:36-65` forwards it to `LC_Skill_BossShow._onSyncSkill`, whose lines 301-350 clear the selected UUID and explicitly accept a nil/empty skill list. No dummy cooldown entry is inserted.

## Talent effects

`SeasonTalentService.Effects.cs` applies the `SpecialEffectConfig` ids in `SeasonTalentEffectConfig._kSpecialEffectList` of the active plan's bought nodes (a profession choice buys its branch and its option). The client only describes these effects. Its `enum_SpecialEffectType` stops at 15, so the layouts of the server-only ops 16-22 are read from the exported rows and their descriptions **(inferred)**:

| Op | Tuple | Example description |
|---|---|---|
| 1 | `[1, attr, value]` | "Physical ATK +15" |
| 16 | `[16, weapon, attr, value]` | "When a Sword is equipped, STR +10" |
| 19 | `[19, skill group, learned level, attr, value]` | "When Mace Mastery reaches Lv. 10, STR +5" |
| 21 | `[21, weapon or 0, scope, selector, type, operation, value, index]` | "Bowling Bash's Cooldown is changed to 8 seconds" |
| 22 | `[22, weapon, attr, per learned level, skill group]` | "PATK +2 for each Learn Lv. of Sword Quicken" |
| 2 | `[2, buff]` | the buff's `_kSkillAttributeList` |

A weapon is an `ItemConfig._iSubType` (1 Dagger, 2 Sword, 3 Two-Handed Sword, 4 Spear, 5 Two-Handed Spear, 12 Katar) in the main-hand or off-hand grid. Ops of one effect that differ only in their condition ("a dagger or katar") apply once. Op 21 and the buffs' tuples go through `SkillParameterRules` like a live buff's, so the cast row, 31132 and mode selection see them. `_kBuffAttributeList`, BuffDictRef(12) and op 20 are not executed. `ProfessionTalentEffectTests` covers each applied op on an invented graph.

### Talent changes to other buffs

A talent can change the buffs its owner applies: `BuffConfig._kBuffAttributeList` of a Buff(2) row, or op 12 (BuffDictRef) written inline, as `["0", buff group, EBuffAffectValueType, operation, value, index, ...]`. Type 2 (BuffAttr) changes the value of the group's `_kChangeAttri` pair at `index` when the owner applies such a buff, to itself or to an ally. Guard Enhance's two rows give Parry's PDEF and MDEF pairs +50 each, and Rousing Support gives Crazy Uproar's second pair +10. The operation arithmetic is `SkillParameterRules`' **(inferred)**. Type 4 (BuffLogicData: proc chances such as Double Attack's) and scope-1 tuples are not applied yet.

When a talent effect has `_iIsOtherDesc` 1, its description and values are those of its `_kOtherDesc` buff.

### Talent procs

A Buff(2) row's `_kLogicData` names what a proc applies: a SkillConfig row (Rapid Pierce 1112101), a skill group (Double Counter's 11127) or a BuffConfig row with a duration. The proc row's own damage, `_kShowDmg` and script carry the hit. The talent's `_kDescData` holds the chance, cooldown and area in the order its description uses them. Which cast triggers a proc is written only in the description, so `TalentProcs.Rules` keys each rule by the talent buff and quotes the description **(inferred)**. A proc is an extra cast through `ApplyEffectsAsync` on the monsters the rule chooses. It pays nothing, starts no cooldown and never triggers another proc. `TalentProcs.WhileActive` lists the talents whose `_kLogicData` buff applies only while another buff group lives ("While [Aura Blade] is active ..."). Their skill tuples and `_kChangeAttri` follow that buff.

Some skills list their talents' buffs unconditionally in `_kBuff1`: Crazy Uproar gives "Crazy Uproar - Mind and Body Renewal" and "- Self-Motivation", Maximize Power and Maximum Over Thrust likewise. `TalentProcs.TalentBuffs` gives each of these only with its talent; Weapon Polishing's to the caster, Weapon Enhance's to the allies **(inferred)** from the descriptions.

A description's skill links (`<link="skill/1411801">`) name the same triggers the rules list. A proc can also cast its trigger again (Fated Resurrection, Double Assault), put its `_kLogicData` buff on the target (Momentum-Breaking Hammer's Stun) or hit every monster the cast hit (Ice Spike). A field cast (Meteor Storm, Storm Gust, Gravitation Field) resolves later, so its procs centre on its target point.

The Thief and Merchant lines' procs have no proc row: the description gives the hit ("PATK * ${4}% + ${5} ... to up to ${3} enemies within a ${2}-meter radius"). It is dealt as the caster's effective triggering row (its talent and buff tuples applied, `CombatService.ExtraRow`) with that damage and element (`SkillConfig.AsDescribedHit`). The copy drops the row's group, tags, script, buffs and shown hit count, so nothing else of the original applies to it **(inferred)**. A runtime count ("@{1} times") is read as one. A proc "around the caster" (Blade Dance) needs no target of its trigger: it finds the nearest monster in its radius.

Rules cover every job line's damage, buff and recast procs. Not covered yet: counters ("after every N casts"), timed follow-ups ("for ${1} seconds after casting"), taking-damage, kill and Double Attack triggers, heals on allies, zones, Earth Spike's stun, Cross Thrust (a runtime target count) and Concussive Shield Bash (its `_kLogicData` names no SkillConfig row). `SkillCombatTests.TalentProcs` checks Rapid Pierce, Double Counter, Elemental Pursuit, Flame Burst and Opening and the talent-gated Crazy Uproar buffs on the client data.
