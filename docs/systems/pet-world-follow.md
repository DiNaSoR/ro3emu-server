# Deployed pets in the world: evidence and implementation boundary

The reported defect was a deployed Bobo overlapping its owner, including after a jump.
The earlier `PetFollowerActor.Follow` and `AtOwner` explicitly copied the owner's position on stops,
teleports and entry. Owner binding in the client does not supply a missing follow offset.
The missing 111/114 pendant rows described below are specific to the supplied client export, not a claim about every game build.

## What the supplied client actually proves

Paths below abbreviate `re/clientB/lua-src/` as `Lua/`. The corresponding files in `re/lua-src/`
were also checked: the pet helper, unit manager, player/pet units, pendant manager/follower,
pendant/public parameter configs and skill widget are byte-identical between the two trees.

| Mechanism | Evidence | Consequence for the server |
| --- | --- | --- |
| Server AOI actor | `Lua/Manager/LC_UnitManager.lua:1274-1350,1494-1507` creates a server unit, consumes its movement and emits `PetAOIEnter`. `Lua/Logic/Unit/LC_Unit_Pet.lua:67-84` finds `owner` and calls `AddBindPet`; `Lua/Logic/Unit/LC_Unit_Player.lua:2454-2469` only records the pet UUID in a table. | Send a separate type-7 base in 31037, after the owner exists. Supply its own position and 31081 movement; `owner` does not mean attachment to the player's transform or automatic following. |
| Cosmetic pendant | `Lua/Logic/Unit/LC_Unit_Player.lua:995-1007,1229-1241` passes a nonempty `EntityBase.pendantInfo` to the pendant manager. `Lua/Logic/Unit/Component/LC_Pendant_Manager.lua:1020-1040,1181-1224` resolves `pendantId` through `PendantConfig` and selects the implementation by the row's `_iType`. `Lua/Config/Define/Lua_GameDefine.lua:2903-2904` assigns **10** to `PendantType.PetFollow`. | A valid type-10 pendant row would drive the client's follower; its position and per-frame motion do not require a separate pet UUID or pet 31081 packets. Type 10 is a component type, not a proven wire map key. |
| Guardian-skill appearance | `Lua/Logic/Scene/LC_Scene_Sync.lua:3898-3913` handles `petConfigId` on a guardian skill; `:4064-4086` uses `PetSkillDistance` for its position; `:4265` creates a local model effect. `Lua/Logic/Unit/LC_Unit_ModelEffect.lua:585-586` uses `PetSkillDisapper` for fading. | A pet appearing during a battle skill is not, by itself, evidence that the server created a type-7 AOI pet. This is a third presentation path. |

The exported NPC data maps Bobo **13001** to `_iMonster=2300130`, whose `_iNPCType=7`,
`_iCamp=1`, `_iSpeed=700`. Choco **10001** maps to monster **2300440**.
Their separate `_iFollowPet` values are **114** and **111**, respectively
(`data/config/PetBaseConfig.json`, `data/config/NPCConfig.json`).

## Pendant wire contract, unlock and missing configuration

`src/Ro3Emu.Server/Protos/romsg/MG_Entity.proto:114-123` defines the owner's
`EntityBase.pendantInfo` at **field 14**, a `map<int32, PendantData>`.
`:330-339` defines `PendantData { pendantId=1, piles=2, isHide=3 }` and a separate
`PendantUpdate { entityID=1, pendantInfo=2 }`. Line 338 belongs to **PendantUpdate**, not EntityBase.
The dedicated update is **31015 PENDANT_UPDATE** (`data/msgtable.tsv:185`), received by
`Lua/Logic/Scene/LC_Scene_Sync.lua:4541-4555`.

- `pendantId` must identify an actual `PendantConfig` row. The map key is an opaque, stable
  instance identity in `UpdatePendantList` (`LC_Pendant_Manager.lua:1001-1011,1059-1085`);
  the original server's pet key convention is **unverified**. It is not proven to be 10, 111,
  13001 or the battle slot index.
- `piles` is copied into pendant data (`LC_Pendant_Object.lua:353-360`); no pile-dependent
  follow behavior was found in `LC_Pendant_PetFollow.lua`. Choosing one pile would be **(inferred)**.
  `isHide` controls server visibility (`LC_Pendant_Object.lua:394-398` and
  `LC_Pendant_Manager.lua:1093-1102`).
- The manager treats a supplied non-nil map as a full list: missing keys remove old pendants
  (`LC_Pendant_Manager.lua:986-1014`). Empty EntityBase maps are skipped by the player code
  cited above, so a sparse/empty 31049 must not be assumed to remove an existing pendant.
- The skill list includes Follow when `_iFollowPet != 0` (`Lua/Logic/Pet/LC_PetHelper.lua:1325-1341`).
  Follow is skill type **6** (`Lua_GameDefine.lua:8644-8645`), but its UI unlock is **6 stars**:
  `LC_PetHelper.lua:109` sets `FOLLOW_UNLOCK_STAR=6`; `UI/Pet/Controllers/UI_Sys_Pet_Skill2WidgetCtrl.lua:222-235`
  checks ownership and that threshold. The rising-star screen advertises it at the same threshold
  (`UI_Sys_Pet_RisingStarTipsPanelCtrl.lua:835-861`). `_iUnlockStar=5` in PetBaseConfig is not
  the threshold used by this UI. There is no separate learned-Follow field in `MG_Pet.proto:25-33`.
- Crucially, both supplied `Config/DataConfig/PendantConfig.lua` tables contain **11 rows**
  (`:60-273`), none with type 10 or IDs 111-166. The exported `data/config/PendantConfig.json`
  agrees. The manager logs a missing-config error and aborts (`:1027-1040`). Additionally,
  `LC_PetHelper.IsFollowPet` always returns false (`:1753-1759`). These inputs do not establish
  a usable pendant for any roster pet in this client build.

The pendant class itself proves client-side following: defaults are start distance 5, maximum-speed
distance 10 and speeds 4/7/15 (`LC_Pendant_PetFollow.lua:39-45`). **These are defaults, not proven
deployed-pet distances**: Init replaces them with the pendant row, including its offset (`:233-286`).
The config wrapper divides offset/distance/speed values by 100 (`Lua/Config/CFG/Lua_CFG_PendantConfig.lua:24-44,100-105,155-199`).
Initial placement uses the offset path endpoint (`LC_Pendant_PetFollow.lua:301-314`); the endpoint rotates
the offset through the owner's forward/right vectors (`:1311-1378`). It maintains paths and idle states,
can reset an out-of-range follower (`:837-851,2257-2277`), and hides during the owner's fight state
(`:865-895,481-499`). This supports the interpretation of a cosmetic outside-combat follower,
but does not recover its missing config or original server activation policy.

## Deployment decision and what remains inferred

**The supplied Lua and protos do not prove which original-server policy selects a permanent world pet.**
They prove the available presentation contracts, not an original 33311-to-spawn capture, pendant key,
AOI follow trajectory or 31081 cadence. Sending both would risk duplicates; sending pendant 111/114
with these tables is invalid. A 6-star UI label does not establish that the existing deployed AOI actor
should disappear below 6 stars.

The correction therefore retains the emulator's deployed AOI actor **(inferred projection policy)**:
an owned pet selected in the active setup plan appears; an owned-only/support-only pet does not;
undeploying or selecting an empty plan removes it; replacing the selected pet removes the old UUID
before creating the new one. Function 33 remains required. This does not implement guardian-skill
summons, pet combat AI or the unavailable cosmetic pendant. Tower instance visibility remains attached
to the owner and its existing lineup/combat rules remain separate.

`PublicParamConfig.lua:8896-8901` supplies `PetConfigDistance=2500` and `PetConfigFollow=300`;
`:9104-9105` supplies `PetNoneFightFollowDistance=1000`. No executable Lua consumers of these three
parameters were found. Using `PetConfigFollow / 100` as a **3 m** stand-off is **(inferred)** from
the parameter name and the client's distance-unit convention. Side/back orientation, transitions and
server update cadence must also be marked **(inferred)**. Neither the pendant's 5 m constructor
default nor the skill effect's 2/2.5 m offsets are evidence for a deployed AOI follow distance.

## Verification scope

The implementation and tests are described in [pet.md](pet.md). Wire tests can establish
separate positions, movement endpoints, packet ordering, visibility and cleanup. They cannot establish
pixel-level name-board separation from every camera angle, the original server's exact trajectory,
or native-client rendering, which has not been checked.
