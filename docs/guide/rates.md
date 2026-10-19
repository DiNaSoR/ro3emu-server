# Rates

Server rates multiply what monsters give when they die, like the exp and drop rates of a classic Ragnarok server.
Code: `src/Ro3Emu.Server/Game/RewardService.cs` (`ApplyRates`, `ScaledChance`), options in
`src/Ro3Emu.Server/ServerOptions.cs` (`RatesOptions`).

```json
"Ro3Emu": {
  "Rates": { "BaseExp": 1.0, "JobExp": 1.0, "ItemDrop": 1.0, "CurrencyDrop": 1.0 }
}
```

Or on the command line: `--Ro3Emu:Rates:BaseExp=3 --Ro3Emu:Rates:JobExp=3`.

| Rate | What it scales |
| --- | --- |
| `BaseExp` | Base EXP of a kill, after the level-gap (LvExpCorrectionConfig) and party bonuses, and a quest's Base EXP |
| `JobExp` | Job EXP of a kill (see below) |
| `ItemDrop` | The chance of every drop roll of a kill, capped at 100%, on top of the party drop bonus |
| `CurrencyDrop` | The amount of every currency (Zeny and the like) a kill drops |

1 keeps the client's tables. A rate of 0 removes that reward; anything above 1000 is read as 1000. Results are
rounded, and a positive reward never rounds down to 0.

## Scope

Rates apply to ordinary monster kills: field, dungeon trash and AFK kills. `BaseExp` also scales a quest's Base EXP
(TaskConfig `_kEXP`), and its Job EXP stays 20% of the scaled amount. Quest items, mail, daily activity stages,
dungeon and MVP award chests and every other award keep their configured amounts.

- EXP rolls are already 100% in every drop pool, so `ItemDrop` never changes EXP.
- A currency that drops from a chance roll drops more often under `ItemDrop` and in larger amounts under
  `CurrencyDrop`.
- Monster Hunt's area drop keeps its own chance; a currency in it still follows `CurrencyDrop`.

## Party bonuses

A party gets bonuses for each member on the killer's map, as the party buff tooltip says:

| Members on the map | 1 | 2 | 3 | 4 | 5 |
| --- | --- | --- | --- | --- | --- |
| Base and Job EXP (`TeamExpBuffNum`) | +0% | +25% | +50% | +75% | +100% |
| Item drop chance (`TeamDropBuffWeights` per extra member) | +0% | +30% | +60% | +90% | +120% |

The drop bonus multiplies every drop roll's chance the same way `ItemDrop` does (so 2x `ItemDrop` in a 3 member party
is 3.2x), still capped at 100%.

## Job EXP

The client's drop tables give Base EXP (item 100) only; no DropGroupConfig row drops Job EXP (item 101). Job EXP is
`PublicParamConfig.BaseToJobConversionPercent` (2000, so 20%) of the Base EXP, rounded down, for kills and for quest
and other award EXP. Beta footage agrees: kills of 5, 9 and 23 Base EXP gave 1, 1 and 4 Job EXP, a 70,000 Base EXP quest
gave 14,000 Job EXP, and a base level 18 character (236,056 Base EXP earned) was at job level 25 (about 47,200 Job EXP
earned). An award that lists its own Job EXP keeps that amount. For kills, `JobExp` scales the result.

For scale: a first job needs 287,240 Job EXP from job level 15 to 40 (JobLvConfig rank 2), while base level 10 to 40
needs 3,853,500 Base EXP (BaseLvConfig). Kill EXP in the tables is small next to that: a level 20 monster gives
10 Base EXP (75 under Bonus Time) against 62,700 for base level 20 to 21.
