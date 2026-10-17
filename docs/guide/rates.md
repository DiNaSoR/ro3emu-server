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
| `BaseExp` | Base EXP of a kill, after the level-gap (LvExpCorrectionConfig) and party bonuses |
| `JobExp` | Job EXP of a kill (see below) |
| `ItemDrop` | The chance of every drop roll of a kill, capped at 100% |
| `CurrencyDrop` | The amount of every currency (Zeny and the like) a kill drops |

1 keeps the client's tables. A rate of 0 removes that reward; anything above 1000 is read as 1000. Results are
rounded, and a positive reward never rounds down to 0.

## Scope

Rates apply to ordinary monster kills: field, dungeon trash and AFK kills. Quest rewards, mail, dungeon and MVP
award chests and every other award keep their configured amounts.

- EXP rolls are already 100% in every drop pool, so `ItemDrop` never changes EXP.
- A currency that drops from a chance roll drops more often under `ItemDrop` and in larger amounts under
  `CurrencyDrop`.
- Monster Hunt's area drop keeps its own chance; a currency in it still follows `CurrencyDrop`.

## Job EXP

The client's drop tables give Base EXP (item 100) only; no DropGroupConfig row drops Job EXP (item 101). Job EXP is
`PublicParamConfig.BaseToJobConversionPercent` (2000, so 20%) of the Base EXP, rounded down, for kills and for quest
and other award EXP. Beta footage agrees: kills of 5, 9 and 23 Base EXP gave 1, 1 and 4 Job EXP, a 70,000 Base EXP quest
gave 14,000 Job EXP, and a base level 18 character (236,056 Base EXP earned) was at job level 25 (about 47,200 Job EXP
earned). An award that lists its own Job EXP keeps that amount. For kills, `JobExp` scales the result.

For scale: a first job needs 287,240 Job EXP from job level 15 to 40 (JobLvConfig rank 2), while base level 10 to 40
needs 3,853,500 Base EXP (BaseLvConfig). Kill EXP in the tables is small next to that: a level 20 monster gives
10 Base EXP (75 under Bonus Time) against 62,700 for base level 20 to 21.
