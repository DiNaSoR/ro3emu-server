# Equipment instance rating: evidence and bounded server rule

Citations under `re/` point into your local decompiled client (`re/clientB/lua-src/` for the current build,
`re/lua-src/` for the older tree); `data/config/` is the config exported from the client.

## Proven client contract

- `PBItemAttrTypeEnum.ItemAttrTypeScore=2` and `ItemAttrTypeAppraise=3` (`re/clientB/lua-src/PBTypeDefine/PBEnumDefine.lua:7476-7479`); base attribute keys/values occupy 30-49/60-79, special IDs 80-89, and entry attribute keys/values 100-119/140-159 (`:7486-7513`). The older `re/lua-src` parser also reads the same score and entry ranges (`Logic/Knapsack/LC_ItemProtoParse.lua:279,394`).
- The client reads `ItemBaseInfo.itemInfos[2]` through `LC_ItemProtoParse.ParseEquipInfo` into `kEquipInfo.iScore` and tests `itemInfos[3] == 1` for `bIsAppraised` (`re/clientB/lua-src/Logic/Knapsack/LC_ItemProtoParse.lua:271-293,471-487`). It reads consecutive complete base and entry key/value pairs and consecutive special IDs (`:356-450`).
- `LC_EquipmentHelper.GetEquipScore` returns `kEquipInfo.iScore` or zero; it performs no formula (`re/clientB/lua-src/Logic/Equipment/LC_EquipmentHelper.lua:965-975`). The equipment tooltip returns that score when appraised, otherwise the unknown-score text (`re/clientB/lua-src/UI/Common/Controllers/UI_Com_ItemTips_Equip_HeadWidgetCtrl.lua:216-250`). Its comparison arrow compares item scores, with two-hand slot handling (`:335-403`); the build view also consumes `iScore` (`re/clientB/lua-src/UI/Equip/Controllers/UI_Sys_Equip_Build_ProcessWidgetCtrl.lua:1865-1888`). The score is an item-instance field in 5009/5007, distinct from 5007 `DBEquipPlan.power` and the character's `PowerUnit`.
- Appraisal begins from the build or build-process controller with the item's GUID (`re/clientB/lua-src/UI/Equip/Controllers/UI_Sys_Equip_BuildWidgetCtrl.lua:1528-1551`, `UI_Sys_Equip_Build_ProcessWidgetCtrl.lua:1470-1501`). `31401 EquipAppraiseResp.appraiseResult` returns updated `ItemBaseInfo` instances, and the equipment DAL dispatches them (`re/clientB/lua-src/Logic/Asset/LC_DAL_Equipment.lua:859-898`). The score and appraisal flag therefore travel together on the same instance.
- The config helper exposes `EquipConfig._iPower` or zero when absent (`re/clientB/lua-src/Config/CFG/Lua_CFG_EquipConfig.lua:569-604`), but every currently exported EquipConfig row lacks this field. `AttributeConfig.GetPower(id)` returns `_iPower` (`re/clientB/lua-src/Config/CFG/Lua_CFG_AttributeConfig.lua:1102-1116`). For example Cutter 2010250 links ItemConfig/EquipConfig, with PATK/MATK roll ranges 38-51; weights 103/104 are 5 and STR(1) is 50 (`data/config/{ItemConfig,EquipConfig,AttributeConfig}.json`). `SpecialEffectConfig[1]._iPower` is 100.

## Inferred implementation boundary

`EquipmentRating.Calculate(item, data)` is an **(inferred)** deterministic proxy for appraised ItemConfig type-5 instances. It computes `round_half_away_from_zero(max(0, Σ rolled_base_value × AttributeConfig[attr]._iPower + Σ entry_value × AttributeConfig[attr]._iPower + Σ SpecialEffectConfig[special]._iPower))`. It reads only complete consecutive instance pairs and special IDs, then rounds once; missing config rows contribute zero. The result is a nonnegative `long`, saturated at `long.MaxValue`. An unappraised or non-equipment instance returns zero. The calculation does not read its own output slot 2.

The client explicitly implements `value * AttributeConfig._iPower` in `Logic/Score/LC_ScoreHelper.lua:16-27`
(both Lua trees), and `CalcSpecialScore` returns the special-power lookup at lines 29-39, so the multiplication itself
is proven client behavior. Direct `CalcAttrScore` caller searches in both `Logic/` trees return the score helper,
Totem and Wardrobe; equipment's reader returns `itemInfos[2]`. Applying those primitives to the complete equipment
instance, treatment of negative values, clamp, rounding, missing config behavior, and inclusion of specials remain
inferred. A search of the full equipment corpus and the parsed EquipConfig census find no exported base `_iPower`
(all 1,304 rows omit it), so this rule does not recover a base-equipment rating. Grid strengthen/refine levels, cards,
enchantments, quality, plan power and player power are excluded from the chosen intrinsic rule. The inspected
equipment readers consume the instance score and do not establish those components' original inclusion.

## Item projection

`CharacterService.ItemInfo` derives slot 2 from the saved instance after copying `Attrs`, assigning `EquipmentRating.Calculate(item, data)` for equipment. This overwrites any stale locally stored score; there is no original-server score capture to compare against. Unappraised items receive zero with `itemInfos[3]=0`. Emblems and aggregate `PowerUnit` values use their existing paths. If an original-server score is ever captured, its provenance and preservation policy need an explicit separate decision.

To establish 1:1 parity, compare original-client captures of `itemInfos[2]` against fixed instance state while changing exactly one base roll, one entry affix, one special, refine/strengthen, card, and enchantment separately; establish rounding and whether these changes alter the item score. Static analysis and the focused tests do not provide that runtime evidence.
