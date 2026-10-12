# Pet implementation

Evidence: the referenced client Lua, protobufs, and exported JSON tables. World following of a deployed pet is investigated separately in [pet-world-follow.md](pet-world-follow.md), including the unavailable pendant configuration and the limits of inferring original-server behavior from Lua. The 111/114 pendant rows and the room-timer wire field are absent from the supplied client build.

Implemented:

- 5017 `DBPetInfo`: durable empty-first roster, selected lineup, room, eggs, history, count and UI preferences. Login defers 5056 until 5027 and 5017 exist, including function sync triggered by automatic quest settlement.
- 33308 fragment combine; 33302 one/ten levels; 33303 progress; 33304 stars; 33311 lineup and swaps; 33314 existing-plan selection; 33315 rename; 33324 collection activation and its configured award; 33312 fragment breakdown.
- 33318 normal egg generation using material 1250709; 33322 completion with acceleration items/token payment; 33319 ready-egg collection; 33317/33320 preferences. Start/acceleration values use Unix seconds, survive reconnect, and use the current room's configured hatch duration. Collection is explicit, not automatically granted at expiry.
- 33316 room upgrade completes immediately after its configured EXP/cost (see "Explicitly unavailable" below); 33325 fast hatch is gated on room level 2.
- 33310 rebirth records and refunds the exact book denominations spent on new level-ups; pets levelled before this ledger existed receive an inferred equivalent in whole 500-EXP books.
- Pet skills resolve from the current `PetStarConfig` row; the synchronized roster gives the client its configured protect/passive/active/stage skill choices. No synthetic learned-skill dataset is introduced.
- One type-7 follower with an independent actor UUID, NPC template, positive owner, NPC movement property and owner-bound AOI. Creation follows the owner; removal precedes the owner; accepted walks/stops/teleports send 31081. Concurrent owner entry and lineup changes serialize the ordering.
- Functions 33/3301/3302 gate requests. A persisted setup cannot spawn when 33 is closed. Inventory/currency and pet state commit in one SQLite transaction, with in-memory rollback on failure. The existing ordinary item-exchange path retains its behavior.

Inferred policies (not original-server parity claims):

1. Initial plan indices 0-4 with empty names, `currentPlan=0` (the client's Build 1), `inited=true`, empty ownership and room level 1. `PetService.Normalize` migrates a saved sole plan 1 to 0. Plan names are limited to 32 non-control characters. Five initial plans and their names are inferred from the current plan UI.
2. Client eligibility costs become actual server debits. Levels consume small books first, stop at the requested +1/+10 target and carry all surplus EXP. Star/progress charges use the target row. The level cap is 99 and the owner's level.
3. Slot enforcement intersects the public parameter levels with the setup/support condition rows. Destination occupants swap into an occupied source or return to the roster when the source is none. Displayed per-pet strength follows client arithmetic; plan power is the sum of fight/support strength.
4. Static fight/support attributes are scaled by the slot's configured addition; activated collection attributes sum permanently. Collection's preview award is granted once at activation. Fragment breakdown multiplies the per-item preview by count.
5. Only normal round 10001 is enabled; other rounds lack verified event/season availability. Physical slots allow one round at a time. Egg result selection follows the UI's `min(roomLv+1, draw-count)` index, then `DrawConfig._kSubBanner` -> weighted `DrawPoolConfig` row -> uniform choice of one `[type,id,count]` tuple. Preview lists are not weights. Room-quality weighting, first-draw behavior and pity are unqualified; this implementation does not combine those separate tables into an invented guarantee.
6. A chosen result persists at generation. One accelerator means 300 seconds; finish uses owned items first and buys each missing unit for 100 token-3 currency, matching the client confirmation path. All costs are atomic. An already-ready acceleration retry succeeds without spending. The wire has no nonce to distinguish an old retry after a new egg occupies the same round/slot.
7. Collection grants configured result EXP once per egg, increments count and records collection time. A duplicate pet becomes `PetBaseConfig._kDecompose` fragments in `brokenItems`; directly drawn items use `normalItems`. Acquisition uses 5017 without an additional 33309 append. Combine's append reply precedes its 5017 roster replacement.
8. The lowest deployed setup slot selects one follower; support slots do not create actors. Visibility follows the owner's visibility, and a missing friendly camp falls back to 1. The follower keeps a separate follow position next to the owner (see "World follow" below and [pet-world-follow.md](pet-world-follow.md) for the distance, presentation-policy evidence and unresolved original-server details). Server pet combat, target choice and skill damage are not inferred from the UI.

Explicitly unavailable:

- Timed room upgrades: the client reads `incubationLvUpTime`, absent from both supplied DBPetInfo protos. 33316 therefore completes immediately after its configured EXP/cost as an inferred client-compatible policy; no 33321 mapping or timer field is fabricated.
- 33323 pet breakthrough: empty break-stage table and disabled client eligibility.
- 33305 legacy GUID breakdown and 33307 legacy lottery return `FeatureUnavailable` rather than empty success. 33325 fast hatch is implemented for normal round 10001, gated on room level 2, with the UI's projected material and accelerator costs.
- Extra/event pools, original RNG/pity parity, camp synergies and combat skill execution. Tower lineup remains separate.

## World follow

[pet-world-follow.md](pet-world-follow.md) explains why a synthetic `pendantInfo` entry cannot replace the existing actor with the supplied client tables.

- Keep the separate type-7 actor in 31037 and owner-first / pet-first removal ordering. At entry,
  deployment and teleport, place it 3 m diagonally beside/behind the owner. The exported
  `PetConfigFollow=300` suggests the distance; its centimetre interpretation and the 135-degree
  placement angle are **(inferred)**.
- Retain that formation offset through ordinary walking, stops, reversals and turns. Compute each
  position directly from the accepted owner position plus the offset, avoiding cumulative drift.
  A 31081 walk starts at that distinct position and uses the same finite one-second projection as
  the owner's AOI movement. Both projected trajectories remain separated. This synchronized
  formation policy and cadence are **(inferred)**, not recovered original pet AI.
- Send start/stop state changes even when coordinates are identical, refresh moving keepalives,
  and suppress repeated unchanged idle stops. Use 31014 for pet turns so turning does not restart
  the pet's movement clock. `SceneService.TeleportInMapAsync` explicitly signals a pet teleport,
  including a short jump below the ordinary 10 m AOI teleport heuristic.
- Keep the existing active-plan selection, undeploy/replacement, function gate, range exit/reentry,
  reconnect, map cleanup and private tower instance boundaries. No new dataset, learned skill,
  stat formula, incubation rule or pendant configuration is introduced.

This keeps the pet from sharing the owner's position. Native-client visual acceptance, exact original
server cadence/offset policy, and pet navigation around obstacles remain unqualified. Packet geometry
cannot guarantee separate name boards from every possible camera angle.

## Tests

`PetTests` and `PetAoiTests` are server/bot wire tests covering timer boundaries, reconnect, duplicate requests, stale sources, closed gates, a concurrent owner-entry race and injected SQLite rollback. The collection activation test verifies the one-time Zeny grant (`AwardConfig[id]` contains an array of rows; the reader uses the sole configured object row). The Bobo wire case covers spawn, walking and projected endpoints, same-position stop, reversal, turns (including owner/peer 31014), short teleport, late viewer, range exit/reentry, map exit/return, plan switch and undeploy. Focused AOI tests preserve owner-entry ordering and private-instance isolation, and cover same-position walk start/stop plus repeated idle suppression. Empty-panel rendering and visual pet behavior in the native client are not covered.

```text
dotnet test tests/Ro3Emu.Tests --filter "FullyQualifiedName~PetAoiTests|FullyQualifiedName~PetTests"
```
