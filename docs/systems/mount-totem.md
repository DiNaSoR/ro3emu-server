# Mount and Divine Totem implementation

`data/` and `re/` are read-only inputs. The season owner remains `SeasonService` and its
configured playbook 101.

## Wire and persistence

| Owner | Requests | Persistent client dataset | World/gameplay effects |
| --- | --- | --- | --- |
| `MountService` | 32743-32745, 32748-32750; action 201 via 31323; quest actions 412/54 | 5012 `DBMountsInfo`; a role blob also retains claims, paid etchant previews and temporary quest ride | 31049 `Rider`, initial `EntityBase.curRider`, ordinary attribute recomputation and movement speed |
| `TotemService` | 82362-82370 | 5032 `DBTotemInfo`; actual GUID instances and itemInfos in 5009 pack 4 | deployment markers, stats, selected skills and source-owned combat effects |
| `WardrobeService` | 31183-31188; action 200 via 31323 | full 5034 ownership, selections, plans and favorites | full appearance map for self, AOI and offline role information |
| `GenreService` | 82800-82828 bound operations | 5008 `DBGenre`, durable child plans and recommendation records | validated atomic application of child plans |

Wardrobe/color evidence is detailed in [wardrobe.md](wardrobe.md).
Genre and child-plan behavior is detailed in [genre.md](genre.md).
Combat interpretation and tests for totem skills are detailed in [totem-effects.md](totem-effects.md).

## Mount behavior and evidence

`MG_Mount.proto`, `MG_DB.proto:633-655`,
`Logic/Mount/LC_MountServer.lua`, `Logic/Asset/LC_DAL_Mount.lua` and `LC_MountHelper.lua`
separate ownership, selected slot and visible riding. In particular:

- 32743's Lua sender omits `tp`; zero means the system slot. `MountsConfig` selects system slot 1,
  `CartRemouldTable` battle slot 2. `MountsConfig._iType` is presentation type, not this slot enum.
- 32745's values are off-system 0, on-system 1, off-battle 2, on-battle 3. A selected mount does
  not contribute riding stats until actually ridden. At most one selected slot is ridden.
- Ownership, expiry, class, function 27, combat/death/scene-loading and map restrictions are checked.
  `Config/CFG/Lua_CFG_MapConfigBase.lua:497-549` maps `_kRiderForbidden` to battle/system/flying.
  This request changes riding; it does not itself start a separate flight action.
- An accepted skill (31102) takes the rider off a system mount (slot 1) before the cast reply. The client
  never asks to dismount for a skill: `LC_State_Unit_Skill` holds a skill while
  `m_kMountComp.m_bRideSystemMount` is set and drops it once its chant and action time have passed, so a
  later dismount left the first hit invisible. The riding mount's `_kFlySwitchSkills`, quest rides and
  battle mounts (slot 2) stay mounted *(inferred)*.
- Flying mounts (`MountsConfig._iType` 1: the Starshatter Dragon 60060010 from the Dragon Whistle 1501004, and
  60040033) fly by their `_kFlySwitchSkills` [take-off, landing] (`WorldCoreService.MountFlight`). No client Lua sends
  31083; `LC_Movement_AStarAgentNew` sends 31084 once the unit is `m_bFlying`, which `LC_UnitBase` takes from the
  status word's `eObjectStateFly` (43) bit. While the mount is ridden the 5025 wheel's `SkillWheelMount1` holds the
  take-off (landing in the air), which `JoyStickSkillNormalMixin` shows as the mount button; the ground buff is
  `_kBindBuff` (3112101) and the air buff is the row sharing the landing skill's id (3112201, `FlyBuffTag`). An
  accepted take-off sets the bit; the skill climbs by itself (EffectData 3112121: 20 m) while the client already sends
  31084, so 31084 may climb or dive anywhere in the 50 m band over the nearest nav layer of any kind (water and
  cliffs included; a cell off the grid is refused), its speed check is horizontal, and a refused move is put
  back without an error tip (each one showed a "parameter error" and the echo of its own moves fought the client, which
  froze the rider). The flyer gets no echo of its own 31084. A ridden flying mount counts as able to fly, so the 1 s
  tick no longer lands a rider in the sends between the take-off and its air buff. The landing skill is refused over a
  cell with no walkable layer; an accepted landing clears the bit and grounds the position. Getting off in the air
  lands at once, and over unwalkable ground it puts the rider back where it took off (else the born point) *(inferred)*.
  Leaving a cliff for a lower band may hold or lose height above that band, not climb. The skill
  condition `[2009, 1]` is `CheckSceneFlyable` (`MapConfig._iFly`). The Worldscorch Feather suit's `_iSkillID`
  3112101 only shows this skill's video in the wardrobe; `content/wardrobe-unlocks.json` lists the dragon's
  disabled look so the wardrobe Mount tab lights it.
- All attribute contributions use `MountsConfig`/`CartRemouldTable._kAttr`, including percentage
  movement attribute 1131. Self and viewers receive the same `curRider` and refreshed properties.
- Item action 201 validates the configured target and consumes the requested GUID together with
  ownership persistence in one transaction. 32747 is a tip, while 5012 is the ownership state.
- A mount and its `WardRobeConfig` type-6 row (`_iSystemID` = mount ID; all 22 rows map 1:1) are one collectible.
  The wardrobe Mount tab lights entries only from 5034 (`UI_Sys_WardrobePanelCtrl.lua:1848-1859`) and rides one by
  sending 31183 then 32743 with that ID (`:1070-1126`, `:1398-1425`); conditions, the luxury serial and the panel's
  `AllMountData` binding read 5012 for the same row (`LC_InteractCondition.lua:4448-4515`, panel `:162`, `:2314-2346`,
  `:4880-4905`). Action 201 therefore commits the item, 5012 and the lit appearance in one transaction, then sends
  5012, 5034 and 32747; no 31189, whose reward tip would duplicate 32747 (`LC_DAL_Fashion.lua:253-265`).
  Both `overdueTimeMs` values are absolute Unix ms, zero permanent (`LC_FashionShowFunc.lua:256-286`); the appearance
  keeps the longer of its existing and the mount's lifetime. Mount expiry removes expired appearances in the same
  commit, and wardrobe login lights any owned mount still missing (repair of roles saved before this link).
  Disabled (`_iDisable`) rows are not listed by the client and are not lit. See `WardrobeService.Mounts.cs`.
- `EtchantPlanMax=4`; the UI uses indices 0-3 and keeps default plan 0 immutable. Configured default
  parts are seeded in each plan. 32748 pays the configured currency cost and persists the server
  preview. 32749 accepts only that paid preview for the exact mount/plan/part. 32750 selects a plan.
- 32744 validates every unique threshold index and atomically grants each `MountsSpecialConfig`
  award once. Its claimed set persists across reconnects.
- `TaskConfig[103012]` executes 412 for mount 60040033; 103014 recycles it with action 54.
  Task reward, task completion and temporary ride persistence share `ApplyAwardAsync`'s SQLite
  transaction. Mount publication follows its commit. It does not create permanent collection ownership.
- Connected-session expiry runs every second. Scene restrictions are reconciled after 30005 but
  before ENTITYSELF/AOI. Class changes revalidate riding. Role selection clears previous-role state.

Mount implementation inferences, rather than captured original-server behavior:

1. Action-201 durations are seconds, based on exported 60/600 trial durations; zero is permanent.
   Repeated timed unlocks extend from the later of now/current expiry; permanent ownership rejects duplicates.
2. Owned `uniqueId` equals the configured mount ID, matching the DAL's config-ID indexing.
3. `_iWeight` determines independent etchant draws; paid previews survive reconnects. Private claim,
   preview and quest fields use a JSON envelope around the wire protobuf, without inventing wire fields.
4. Legacy score rewards use the permanent mount appearances' `WardRobeConfig._iFashionScore`.
5. Quest auto-ride persists until the matching recycle action or an explicit mount selection/dismount.
   The action names and arguments establish ride/recycle; this bounded lifetime is an implementation choice.
6. Exact refusal-code mapping and notification order use the matching `ErrorCodeConfig` texts and DAL expectations.

## Divine Totem behavior and evidence

`MG_Totem.proto`, `MG_DB.proto:786-798`,
`Logic/Asset/LC_DAL_Totem.lua`, `Logic/Totem/LC_TotemHelper.lua` and the `UI/totem` controllers
provide the client contract. `TotemConfig`, `TotemSkillConfig`, `TotemLvConfig`,
`TotemSkillNumRandom`, `TotemNewGridPerConfig`, `ItemConfig` and `PublicParamConfig` supply its rules.

- Five plans use indices 0-4 and explicit positions 1-3, unlocked at levels 40/50/60. Function 29
  opens at level 40. `Config/CFG/Lua_CFG_JobConfig.lua:463-482` returns `_iJobRank` directly;
  novice rank 1 can equip rank-1 totems. No rank subtraction is performed.
- Totems stay real inventory GUIDs in pack 4, grid 0. The current plan sets itemInfos 10001=1,
  10002=position, 10003=Unix milliseconds. An inactive plan still reserves its GUIDs against
  unrelated item consumption. Movement between positions clears the prior position.
- Attribute IDs occupy 220-229, values 230-239, skills 240-249, bounds 250-269 and used refinement
  count 270. Generated attributes are stored per instance, not rerolled at sync or reconnect.
- 82370 identifies eligible unappraised instances in one transaction. Repeated appraisal and duplicate
  input GUIDs fail without changes. 82362 requires appraisal before deployment.
- Refinement uses the exact configured material/count and action-205 range, clamped to global
  attribute bounds. The cap comes from `TotemRefineNumPerMultiverse`. A separate persisted epoch
  derived from season playbook/start resets count 270 when the season changes, including offline roles;
  the initial migration retains existing counts.
- Skill learning reads action 206 from the consumed book. `TotemSkillInitMax` and localization
  10960000087 prohibit blue-totem learning. Superior skills, existing skills, born skills, initial
  capacity and configured new-slot probabilities are respected. The book and changed attributes
  commit together.
- EXP is accumulated from `TotemQualityExp` and `TotemCurrencyExp`. `Lua_CFG_TotemLvConfig`
  establishes cumulative level thresholds and percentage base-attribute enhancement. Deployed,
  locked, referenced, duplicate or unowned input GUIDs are rejected.
- `_iIfCompose == 0` permits combination (`Lua_CFG_TotemConfig.lua:343-356`), the reverse of what
  the field name might suggest. Both inputs must be eligible, same quality and same class rank.
  Two source GUIDs and the attributed output commit together. Plan references cannot dangle.
- Selected skills suppress configured inferior/noncoexisting duplicates. Totem effects own their
  passive sources and reconcile them after every state change; see [totem-effects.md](totem-effects.md).

Totem implementation inferences:

1. All five plan slots are initially usable; explicit zero GUIDs keep the client maps safe.
2. Attribute rolls and weighted skill selection use independent server randomness within exported
   intervals/weights. Refining chooses an inclusive action-205 multiplier. Combination uses the
   mean of matching input attributes, then the configured minus/plus range and global bounds.
3. The left input supplies the combined template and born skills. The configured combined skill-count
   table is used where it has a matching row; otherwise the distinct input skill count is retained.
   The higher used-refinement count is retained. Permanent binding dominates temporary binding;
   temporary binding keeps the later expiry.
4. A learned superior replaces an inferior; otherwise configured new-slot chance decides expansion,
   then a non-born skill is replaced. Born skills remain protected.
5. EXP beyond maximum level is returned in descending configured EXP-item denominations via the
   transaction and `UpgradeTotemSystemResp.gainItems`. The right-panel controller stores this map
   at lines 1010-1018 and displays it at lines 1667-1719; exact original denomination choice is not captured.
6. Exact error mappings, season epoch storage, private transaction boundaries and cooldown enforcement
   are emulator choices supporting the demonstrated client contract. Combat-specific inferences are listed separately.

## Search record and evidence boundaries

A repository-wide discovery search for `mount|rider|etchant|totem|genre|scheme` covered every relevant scope,
including both Lua exports and both proto copies:

| Scope | Files | Matching files / lines |
| --- | ---: | ---: |
| `data/config/*.json` | 517 | 40 / 40 |
| `data/sceneeditor/*`, including monster ID maps | 381 | 0 / 0 |
| `data/nav` | 172 | 0 / 0 |
| `data/loc` | 2 | 1 / 404 |
| entire `re/lua-src` | 13,488 | 867 / 11,861 |
| entire `re/clientB/lua-src` | 13,488 | 867 / 11,907 |
| `re/decomp` | 2,277 | 13 / 32 |
| `src/Ro3Emu.Server/Protos` | 147 | 52 / 491 |
| `re/proto` | 144 | 52 / 491 |

This broad search is a discovery record, not proof that a particular feature lacks an implementation.
Specific color and combat boundaries include their own patterns/classification in the linked documents.
Static client evidence and emulator protocol tests are distinct from original-client visual or
packet-capture acceptance, which has not been done.

## Tests

`MountTests` covers 5012, unlock/refusal, selected versus ridden state, speed, viewer AOI, reconnect,
expiry, paid etchant previews, a storage-abort rollback and the real 412/54 quest action paths.
`TotemTests` covers 5032, all nine operations, actual inventory markers and attributes, reconnect,
quality/gate/slot/ownership/material refusals, reservation, refinement cap and EXP overflow conservation.
`WardrobeTests`, `TotemEffectsTests` and `GenreTests` cover the corresponding owners and integration.
