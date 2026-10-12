# Spirit Tower companions and field Bonus Time

This system is implemented against the clientB export (`re/clientB/lua-src/`).

## Protocol and persistence

- Tower: `32803` validates and saves four `DBClimTower.petIds` positions; `5065` and `32805` return the saved formation. `32809` carries per-pet deaths and clears them on a new floor. Starting pets are owner-bound type-7 actors in the private floor; combat uses `31100`, `31101`, `31048`, `31049` and ordinary tower kill settlement. Formation changes during an unsettled attempt are refused, preventing HP/cooldown resets.
- AFK: durable `5035 DBWildAFKInfo`, `43401`, `43403`, `43404`, `43406`, `43409`, `43410`, `43411`, and `43412`. `31054` supplies the existing client auto-battle switch. `39000` type 5 preserves wire boolean presence and syncs `5061`; an omitted sibling is not reset when another checkbox changes.
- Per-role AFK metadata lives in the `wild_afk` blob: allowance, fractional consumption, daily epoch/consumption, cumulative drop totals, snapshots and favorites. Disconnect and role switching cannot charge offline time or inherit another role's activity. Queued accounting, item/EXP grants, delayed attacks and kill callbacks verify the original role after asynchronous waits, preventing an old role's combat from rewarding a newly selected role.
- The daily row uses quest `500130 [106901,3600]` through `5027`, and normal quest delivery updates row 106 in `5004` and grants its configured award once. The last eligible second is settled before the daily reset. A failed award remains pending and is retried before reset, including reconnect.
- HTTP and both gates use the `ServerOptions.ListenAddress()` contract: loopback for a loopback `PublicHost`, otherwise every interface; nonempty `BindAddress` overrides it, including wildcard addresses. Tests bind loopback. The [README](../../README.md) documents this configuration.

## Client/config evidence

- `Logic/Asset/LC_DAL_ClimbTower.lua`, tower UI and dungeon Lua: formation shape, selected companion IDs, death notifications and starting/substitute presentation.
- `PublicParamConfig`: `PetFightFieldNum=4`, `PetFightFieldUnlockLv=[33,33,45,50]`; `PetSetLimitConfig` supplies the additional slot conditions. `PetBaseConfig`, `PetStarConfig`, `NPCConfig`, `AttriMonsterConfig` and `SkillConfig` supply actor templates, levels, attributes and active skills. Combat allegiance comes from `NPCConfig._iCamp`; `PetBaseConfig._iCamp` is a separate collection/destiny category.
- `Logic/Asset/LC_DAL_OnHook.lua`, `Logic/OnHook/LC_OnHookHelper.lua`, `UI/OnHook/Controllers/UI_Sys_OnHook_IncomeWidgetCtrl.lua`: remaining-time interpolation, suspension, favorites, cumulative snapshot subtraction and partial setting edits. Suspend reasons are the enum strings `NoKillTimeout` / `MvpHate`, not localized text.
- `PublicParamConfig`: `OnHookDailyFightTime=60`, `OnHookFightTimeLimit=180`, `OnHookDailyRefreshTime=5`, `OnHookConditionMonito=10`, collection limit 7, snapshot cooldown 5 seconds and history limit 8.
- `Localization_en[10960000077]`: Bonus Time grants larger field EXP and drops; outside it, reduced drops still grant small EXP, while fatigue drops have only small coin/material rewards. It also specifies ten-second inactivity suspension, no consumption while suspended, resume on kill/drop, and dungeon/MVP-aggro exclusions. `37014` supports next-day reactivation; `37118` confirms the 180-minute bank. The older midnight reminder conflicts with the explicit refresh parameter; the implementation uses 05:00 server-local time, consistent with daily activities.

## Inferred decisions and limits

- Tower request refusal returns the saved formation. Empty first positions are filled from later living selections, up to the floor's configured `_iPetOnBattle`; deaths substitute in saved order. Each floor starts with fresh pet HP. Mid-attempt manual lineup changes are refused. The passive follower projection is suppressed in the tower, then rebuilt from the unchanged ordinary setup plan on return; this prevents a duplicate of a battling pet.
- Pet battle attributes use the client's displayed pet-level/star scaling. Combat uses `NPCConfig._iAI` / `AIConfig._iSearchTime` for search cadence, chooses the nearest enemy, approaches it, prefers a later configured damaging active skill when its cooldown permits, otherwise uses earlier attacks, and schedules hits at 40% of action time. Walking explicitly stops before a cast. Enemies can switch aggro to the striking pet. Damage uses the existing inferred combat formula. Single-target and radial damage are implemented; non-damaging/reactive/protection effects, the separate `_iBattleAttributeVariated` scaling and original named `_iAISpecial` server scripts remain unqualified.
- AFK activity requires auto-battle, an alive player on a loaded shared field, and a recent ordinary field kill with granted drops. It starts suspended until such a kill; scene changes, death, manual auto-off and MVP aggro suspend consumption. Fractional elapsed time is retained across pause/reconnect. The initial bonus switch is enabled; the bank refills by calendar days with a 180-minute cap.
- The `NPCConfig._iDrop` environment mapping is **(inferred)** from the localization and configured tables: environment 0 supplies reduced rewards, environment 1 the larger qualified Bonus Time rewards. For Poring, their Base EXP rows are 5 and 18 for player levels 1-99; the separate `_iNormalDrop` EXP row applies only through player level 36. `_iNormalDrop` remains additive under its own map/level conditions. Environment 2 has no proven fatigue trigger, so it is unused. Non-field, non-ordinary and below-unlock kills retain environment 0; no numeric multiplier is invented. Exact original reduced/fatigue economy remains unverified.
- `todayKillCount` counts authoritative ordinary field kills; item totals contain granted rewards, including corrected EXP, rather than generic inventory gains. Team maps remain empty because the current reward owner does not grant team-shared loot. `sumHighMultiplierTime` is lifetime consumption, with a separate daily quest counter; the pause response reports the daily consumed seconds.
- Snapshots store cumulative totals and never re-grant items. Date keys use the daily reset epoch; eight dated records are retained. Power-saving `43403` is a session UI flag and does not stop server accounting.
- Legacy offline `43405` and dungeon `43408` return explicit refusals: current 5035 has no recoverable offline-reward contract. No offline loot or AFK dungeon simulation is invented.

## Verification

`TowerPetTests` and `WildAfkTests` use real HTTP/gate/protobuf exchanges on dynamically selected test ports. Fixtures control monster HP and long clock intervals, seed earned/failed database states and hold asynchronous operations to exercise logout races; client-supplied victories or arbitrary kill counters are not accepted. All fixtures use the shared `TestPorts` selector, which never returns an already-selected port within the test host, excludes the default game ports and bounds candidate retries.

Tower coverage includes four-slot persistence, ownership/duplicate/invalid-slot refusals, 33/45/50 unlock boundaries, reorder/removal requests, both starting pets casting their configured skills, actor allegiance and movement, pet-caused floor victory, both substitute entries, death reset on a fresh attempt and passive-follower restoration. Tower monsters and room lifetime use `InstanceService`; pet actors leave before the shared instance removes their owner, and the tests verify pet removal before scene handoff and closed/removed rooms with no remaining participants or monsters.

AFK coverage includes full login state, manual/reduced versus Bonus Time field EXP, field kill/drop accounting, inactivity suspension, pause, favorites, snapshots, daily progress/award, rollover settlement/retry, reconnect, settings boolean presence and queued accounting/reward grants across logout and role switching. A Yoyo wire regression measures 21 reduced EXP versus 207 Bonus Time EXP from the configured tables. Direct `LoginRoleSelect` reconciles the previous role's AFK allowance before loading the next role; its regression disables the periodic AFK ticker so only role-selection cleanup can persist the pending allowance and fraction.

```powershell
dotnet test tests/Ro3Emu.Tests --filter "FullyQualifiedName~TowerPetTests|FullyQualifiedName~WildAfkTests"
```

These tests do not cover visual behavior in the game client.
