# Season refine (5023, 83220-83222)

Season clock and page behavior are documented in [season-page-evidence.md](season-page-evidence.md).

## Evidence used

- `MG_DB.proto:1779-1792` defines the persisted part/slot state; `MG_SeasonRefine.proto:5-31` defines the three requests and replies.
- `LC_EquipmentServer.lua:240-306` sends `equipType`, `refineId`, and the selected `groupId`. `LC_DAL_Equipment.lua:400-515` inserts `canSelectedAttr` map **keys** into its candidate list, ignoring values; `activeAttr` holds the selected group ID. Thus 5023 uses real group IDs as keys and 83221 validates the key. Zero-valued payloads are **(inferred)** because the original server's map values were not captured.
- `RefiningSeasonTable.json` has five rows per part (1-7), unlocked at refine levels 1/4/7/10/13. Each row supplies `_kAttrsId` weights and `_kDeplete` costs. `RefiningSeasonAttrConfig.json` supplies each group's `_kAttrs` effects. `LC_EquipmentHelper.lua:3285-3314` counts unlocked slots from the grid's refine level.
- `UI_Sys_StrengthenPropertyPanelCtrl.lua:27,735-775` displays three choices and has an "Again" path that sends another paid recast while an offer is pending.
- `LC_DAL_Equipment.lua:400-515` calls `pairs(slot.canSelectedAttr)` without a nil check. Login therefore sends a wire-only zero candidate for otherwise empty maps, then deletes it in the next 5023 increment; stored role state never contains this marker. `LC_PbHelper.lua:1538-1562,1926-1958` creates the map for an increment and preserves the empty table after deletion.

## Inferred server decisions

- **(inferred)** The following decisions follow the available client and config evidence but were not verified against original server packets.
- `_kAttrsId` numbers are treated as relative weights; each recast draws three distinct groups without replacement. No original-server RNG capture establishes that policy.
- A paid recast can replace a pending offer, following the client's "Again" sender. Error 6253 exists ("slot data is not empty") but its precise use is unproven.
- The active group stays effective during a pending recast and a giveup. If the grid's refine level drops below the slot threshold, its stored group remains but its effect is suppressed until re-unlocked.
- The playbook is captured in `session.SeasonState` at login. A GM change applies on the next login, so the current session retains its 101 refine access and active effects; a session logged into 102 remains ineligible after the global setting returns to 101.
- A request for an empty offer gets generic parameter error 82; the season refine error table has no direct "nothing pending" text. Item/currency shortages use the existing bag shortage codes. Login's two-packet empty-map seed is a client decoder workaround, not a captured original-server sequence.
- All three refine requests before role selection receive `FeatureUnavailable` rather than entering the character lock.

## Verification

```powershell
dotnet test tests/Ro3Emu.Tests/Ro3Emu.Tests.csproj --filter "FullyQualifiedName~SeasonRefineTests"
```

The tests use free ports and cover wire sync, refusals (including all three requests before role selection), configured payment, real group IDs as candidate keys, stat effects, a live 101 session after a GM switch, an ineligible 102 session after a global switch back, giveup, persistence of pending/active state, and the relog's initial entity bonus. They are wire tests: the panel's visual behavior in the client is not covered by them.
