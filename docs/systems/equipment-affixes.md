# Equipment entries, stunts and instance lifecycle

Evidence was checked in both `re/lua-src` and `re/clientB/lua-src`. Paths and line numbers below use
`re/lua-src` unless explicitly stated. `data/` and `re/` are read-only inputs.

## Proven configuration and wire contract

`ItemConfig._iEquipId` selects `EquipConfig`. `Lua_CFG_EquipConfig.GetExtraAttribute` (270-352) follows
`EquipConfig._iEntries` to `EquipAttriGroupConfig._iGroup`, then appends `_kFixedEntries` tuples.
`Lua_CFG_EquipAttriGroupConfig.lua:10-84` exposes each pool row as `[attribute ID, min, max]` from
`_iAttriID/_iMin/_iMax`. These are AttributeConfig IDs, **not** EquipEntry row IDs. Pool rows also export `_iWeight`.

`Lua_CFG_EquipConfig.GetExtraAttributeCount` (493-531) and `Lua_CFG_EquipEntryRandom.lua:10-97` select the count
range by **ItemConfig quality**. Current data has quality 1: 0; quality 2/3/4: 1; quality 5: 1 or 2 (weights 67/33);
quality 6: 2. The selected equipment pool already carries the rank/quality-specific value range; there is no extra
player-level multiplier. For example Cutter 2010250 is quality 5, rank 2, level requirement 20, `_iEntries=6202`,
with possible STR/AGI/VIT/INT/DEX/LUK values 1-2. Its base PATK/MATK ranges are 38-51. `_kSpecial` is empty, so
appraisal must give **1-2 affixes and no stunt**. Higher-rank pools such as 6204 use 3-6, and 6207 uses 7-12.

`Lua_CFG_EquipConfig.GetSpecialAttribute` (392-462) treats `_kSpecial` as a list of directives (also accepts one
flat directive); the first member selects `EquipSpecialGroupConfig._iGroup`. The group's `_iSpecialID` is a
**SpecialEffectConfig ID**, and its `_iWeight` is exported. `_iSpecialID=0` is a no-effect sentinel. Fixed effect
IDs come from `_kFixedSpecialAttribute` (464-491). `GetSpecialAttributeCount` (533-567) previews 0 through the
number of directives. Red rank-4 dagger 2010460 has `[[40101,10000],[40103,1000]]`; group 40101 contains
10010401/10010404, and group 40103 contains 10020402/10020405. Fixed armor 2550561 has STR+7, AGI+7 and
guaranteed group 50303 -> 10010505 (refine-step Hit bonus).

These named files do exist but their rows have **no fields**, both in the JSON export and in both decompiled Lua
versions: EquipEntry (102 rows), EquipEntryLevel (2,856), EquipEntryExclusion (35), EquipSpecialAttr (108),
EquipSpecialAttrLevel (9), EquipSpecialExclusion (8). They cannot establish a different pool, level-scaling or
cross-ID exclusion algorithm. The populated group tables above establish the live UI's actual pool contract.

`MG_Common.proto:1003-1028` defines `ItemBaseInfo.itemInfos` as a map of `ItemAttrValue`, whose oneof is `longVal`
or `strVal`. There is **no `ItemBaseInfo.strAttrs` field**. `InvItem.StringAttrs` is the emulator's persistence
map for string-valued itemInfos (e.g. creator metadata/cost history), not the affix transport. `LC_ItemProtoParse`
reads longVal first, then strVal (37-53), but these equipment numeric fields are sent as longVal:

| itemInfos keys | meaning |
|---|---|
| 2 | item rating -> `kEquipInfo.iScore` |
| 3 | exactly 1 means appraised -> `bIsAppraised` |
| 30-49 / 60-79 | base AttributeConfig IDs / rolled values |
| 100-119 / 140-159 | entry AttributeConfig IDs / rolled values |
| 80-89 | SpecialEffectConfig IDs; no companion value is consumed |

`LC_ItemProtoParse.ParseEquipInfo` (271-488) stops each list at the first missing key/value pair or special key.
The implementation therefore writes dense sequences and never writes a zero sentinel as a stunt slot.
`ItemBaseInfo.enchantment` and `playBookEnchantment` remain separate maps; entries are not enchantments.

Consumers use this same parsed instance:

- Tooltip `UI/Common/Controllers/UI_Com_ItemTipsWidgetCtrl_Equip.lua:781-906` displays the actual entry list and
  joins it to `GetExtraAttribute` for range text; `:1218-1354` displays special names/descriptions and their active
  state. The ordinary `UI_Com_EquipTipsWidgetCtrl` is only a shell.
- Build preview `UI/Equip/Controllers/UI_Sys_Equip_BuildInfoBaseCtrl.lua:447-503,586-690` shows the configured
  entry ranges/counts and special pool. Build result `UI_Sys_Equip_Build_ProcessWidgetCtrl.lua:970-1056` and
  appraisal success `UI_Sys_Equip_SuccessEquipWidgetCtrl.lua:855-946` use `kEntryList/kSpecialList`.
- `Logic/Asset/LC_DAL_Equipment.lua:859-898` handles 31401's updated `ItemBaseInfo` list. Bag 5009 and equipped
  5007 carry the same fields. No extra affix-specific packet, dataset or string serialization is required.
- Stunt comparison groups nonzero `_iGroupID` in `LC_SpecialEffectHelper.GetComparedSpecialEffect:529-633`.
  This proves comparison grouping, **not** the original server's generation-exclusion rule.
- Rating, tooltip arrows, build score and PowerUnit distinctions are covered in [equipment-rating-evidence.md](../research/equipment-rating-evidence.md).

## Server implementation and decisions marked inferred

1. **(inferred)** Use exported positive weights for quality-count and pool draws. Select entry IDs without
   replacement; roll their integer values uniformly over inclusive min/max. Preserve the pre-existing base-stat
   policy of a quality-weighted EquipAttriRandom ratio. The client exposes ranges/weights, not the server RNG.
2. **(inferred)** Each `_kSpecial=[group,chance]` directive independently succeeds with chance/10000, then selects
   one positive-weight group row. Fixed entries/effects precede random rolls. Reject duplicate attribute/effect
   IDs and duplicate nonzero special families, including conflicts with fixed selections. Cross-ID exclusions
   from the empty exclusion tables remain **unverified**; no guessed hidden schema is implemented.
3. **(inferred)** Base values are rolled at creation and retained. Already-appraised gear rolls entries/stunts
   before persistence; gear with `_iIfAppraise=1` gets them only on successful 31401. Appraisal validates the entire
   batch, rolls detached candidates, then commits every item's complete Attrs in one SQLite transaction before
   publishing 5009 and the response. A failed write leaves owned instances, flags and rolls unchanged.
4. **(inferred)** Login repairs old emulator saves that are appraised but have no required entries, preserving
   their base rolls, any already persisted stunts, GUID, lock, binding and enchantments. Completed rolls are not
   rerolled; a valid zero-stunt result stays zero. Existing stack migration still splits legacy stacks atomically.
5. **(inferred)** Intrinsic rating is computed on projection from saved base/entry values and special power
   metadata. It is not a recovered original-server formula; see the separate rating evidence document.
6. Equipment stunt ops 1/4/5/8/14 use the client description formulas in
   `LC_SpecialEffectHelper.lua:308-321,1928-2046,2099-2150,2612-2672`. Applying them to authoritative stats is
   **(inferred)**. Flat, equipped-stunt-count and slot-refine bonuses feed the usual attribute pipeline. Types 8/14
   use a single pre-conversion snapshot **(inferred evaluation order)**, preventing reciprocal PATK/MATK feedback.
   Equipped special count and main-hand-weapon-in-off-hand exclusion follow `LC_DAL_Equipment.RefreshSpecailEffectNum`
   and the tooltip. All operations of a special row are considered, not just its first tuple.
7. Opcode 13's localized description supplies chance and potion recovery bonus. **(inferred)** Roll independently
   per consumed instant HP/SP recovery item and active stunt, sum bonuses, round total recovery down once. Other
   item actions, buffs and healing skills are unaffected. This is not proof of original proc stacking or eligibility.
8. **(inferred)** Debug configurations with 11 fixed stunts are capped at the client's ten numeric special slots;
   no field outside 80-89 is repurposed. Ordinary generated gear does not hit that limit.

## Lifecycle and verification coverage

The common factory is used by `AddItemCore` and atomic daily grants. Thus drops/rewards, GM, 31409 craft,
82332/82340 shop and 31326 combine grant fresh independent instances through their existing transactions.
Craft's quality selection and `_iIfAppraise` remain config-driven; no input item's affixes are inherited by a new
crafted/combined item. The current production catalogue contains no equipment shop offers or combine recipes;
tests use per-host in-memory fixture rows for those routed requests, without modifying shared config files.

31404 strengthen and 31405 refine modify **grid levels**, leaving item rolls/rating intact; refine-dependent
stunt bonuses recompute from the current grid. 31400 equip/unequip and 31413 plans move existing GUIDs, with the
same saved rolls. 31415 enchant adds/replaces only its enchantment map. 31327 dismantle and automatic loot
dismantling count actual entries/stunts for `EquipReturnConfig`, retaining the client's ItemConfig fallback.
Dismantling prunes the removed GUID from saved plans. Protection/binding and negative/recovery paths remain intact.

Automatic entry-count filters are exact matches: `re/clientB/lua-src/Logic/Composition/LC_CompositionHelper.lua:328-350`
requires appraised gear and compares `#kEntryList` to the selected option. `DecomposeConditionConfig[4]` maps to
this type-3 filter; option 0 means zero entries, not any entry count. Purple weapon 2010240 is already appraised,
uses entry pool 4202 and always rolls one entry (`EquipEntryRandom`, quality 4), with no stunt, so a zero-entry
filter does not match it and no 31330 is sent. Changing the filter to match "any count" would destroy gear the
player's setting excludes. `MergeInventoryAwardTests` covers selections 0/1/2: only 1 decomposes the new award gear
and sends 31330. All three retain the SQLite failure/rollback checks, task/daily commit checks and old locked
instance; the wire reward also verifies the rolled entry and rating.

## Tests

`EquipmentAffixTests` exercises actual encrypted game requests and AUTO_SYNC payloads: Cutter appraisal, red
stunts, creation routes, duplicate/family/sentinel exclusions, character stat contributions, refine, plan changes,
dismantle returns, potion effects and relog. The legacy wire test also proves that entry repair preserves an
existing stunt, base rolls, protection, binding, string metadata and enchantments, and leaves the complete instance
unchanged on the next login. Synthetic fixtures force otherwise random boundaries without treating them as original
data; fixture changes exist only in each test host's memory. `EquipmentRatingTests` checks exact numeric examples
and unknown/unappraised behavior. Appraisal/enchant tests retain all-or-nothing failure injection and refusal
coverage; the enchant stat expectation includes a generated STR affix. Auto-dismantle tests select the configured
one-entry gear.

```text
dotnet test tests/Ro3Emu.Tests --filter "FullyQualifiedName~EquipmentAffixTests|FullyQualifiedName~EquipmentRatingTests|FullyQualifiedName~AppraiseEnchantTests|FullyQualifiedName~EquipmentPlansLockTests|FullyQualifiedName~MergeInventoryAwardTests"
```

Original-server RNG distribution, hidden exclusions, original rating construction, and visual acceptance in the
client remain unverified. No 1:1 original-server parity is claimed from these tests.
