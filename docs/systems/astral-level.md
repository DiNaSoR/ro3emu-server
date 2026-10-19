# Astral Level (Paragon)

The Astral Level is the level that keeps growing after the base level cap (function 71, opens at base level 99). The
client calls it "Patron", the protocol "Paragon". Astral Points earned on the way go into the stars of three
constellations, and the star-core stars take Star Core items. Server: `ParagonService`, routes in `ParagonMessages`,
tests in `ParagonTests`.

## Client sources

| Piece | Client |
|---|---|
| State | 5015 `DBParagonLevel` auto-sync (`LC_DAL_PatronLevel`); everything the panel shows comes from it |
| Requests | 83101 allocate {slot, points}, 83102 socket {slot, runeGuid}, 83103 remove {slotId}, 83104 reset all, 83105 reset one {slotId} (`MG_Paragon.proto`); each answers only {result} |
| Panels | `UI_Sys_Personal_LevelWidgetCtrl`, `UI_Sys_Personal_TalentistRunesWidgetCtrl` (mobile) / `InformationWidgetCtrl` (PC), `OverviewWidgetCtrl`, `RuneSlotWindowCtrl`; the tip on the character attributes page (`UI_Sys_RoleTipsPanelCtrl`) |
| Configs | `PatronLevelConfig` (100 levels: `_iPatronExp`, `_iStatusPoint`), `PatronTalentGroupConfig` (3 constellations), `PatronTalentConfig` (36 stars: `_iMaxLevel`, `_iPostTalentID`, `_iType`, `_kTalentAttrIds`, `_iSeasonPower`), `PatronTalentAttrConfig`, `PatronRuneConfig` (Star Cores 1201001-1201030), `PatronWordLvConfig`, `PatronExpAddConfig` |
| Errors | `ErrorCodeConfig` 5900-5909 (constants at the top of `ParagonService`) |

## Levels and points

- **EXP:** Base EXP earned at the level cap fills the Astral Level instead of being thrown away (`RoleProgress.CapExp`,
  credited after each EXP commit). The client's tip names quests and kills *(inferred: every Base EXP source)*. `exp`
  is the progress inside the current level, against `_iPatronExp`; level 100 is the top and keeps no EXP.
- **World bonus:** the world Paragon level (`PatronWordLvConfig` by open day, 100 on every day) against the role's gives
  `PatronExpAddConfig`'s bonus: +10% from 5 below, +20% from 10 below. It is sent as `extraAdd` (the client shows
  `extraAdd / 10000` %), so the unit is a millionth *(inferred)*.
- **Astral Points:** each reached level's `_iStatusPoint` (1), so level n holds n points *(inferred)*. `leftPoints` is
  earned minus allocated.

## Stars and Star Cores

- **Allocate** (83101): the star must be in an active constellation and every star leading to it (rows that list it in
  `_iPostTalentID`) must be at its max. Constellation 1 is always active; the next one opens once the previous one has
  every star at max *(inferred from the UI's rules)*.
- **Socket** (83102): only on a star-core star (`_iType` 920-923) that has its point, with a bag Star Core whose
  `_iSubType` equals that `_iType`. The core leaves the bag in the same commit that records it; a core already there
  goes back. No star takes subtype 924, so Cross Star Cores cannot be socketed.
- **Remove** (83103) returns the core to the bag. **Reset one** (83105) zeroes that star and every star after it,
  refunds their points and returns their cores; **reset all** (83104) does that for everything. Both are free, as the
  client's refund popup shows.
- **Attributes:** a counted star gives `PatronTalentAttrConfig[_kTalentAttrIds[points]]` (rows are running totals), a
  socketed core its `PatronRuneConfig._kAttrs`. Only active constellations count.
- **lvPower** on the level page is the counted stars' `_iSeasonPower` plus the cores' *(inferred)*. The power package
  has no slot for it, so it does not change the role's combat power.

Every star is sent in `runegroup` (the level page reads `runegroup[id].points` unguarded), `rune` is always present so a
removal reaches the client as item 0, and `activeGroup` is the run {1: 1, 2: 2, ...} the overview walks with `ipairs`.
Saved as the role blob `paragon` (JSON `ParagonService.State`).

## Limits

- The client says Astral Level data resets between seasons; the server does not wipe it yet.
- Star Cores have no drop or shop source in the export, so a server has to add one (or GM `!item`).
- `ParagonService.SetLevelAsync` sets the level for tests; there is no GM chat command for it yet.
