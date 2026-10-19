# Frozen Bonus Time

Field Bonus Time (the auto-battle "High-Rate Auto Mode" clock, 60 minutes a day, 180 at most) normally runs down
while it is on and you are killing field monsters. `Ro3Emu:FreezeBonusTime` stops it from running down. It is not
original behaviour and is off by default. Code: `src/Ro3Emu.Server/Game/WildAfkService.cs` (`Frozen`, `ConsumeUntil`,
`ClientView`); how Bonus Time works: [system notes](../systems/tower-pets-afk.md).

```json
"Ro3Emu": {
  "FreezeBonusTime": true
}
```

Or on the command line: `--Ro3Emu:FreezeBonusTime=true`. It takes effect when the server starts.

With it on:

- The remaining Bonus Time never goes down, on the server or in the game's panel. Kills still get the Bonus Time
  drops and EXP while it is switched on.
- Bonus Time still has to be switched on, and only with some time left; it still only counts on field maps.
- Time played under Bonus Time still counts for the daily "1 hour of Bonus Time" activity and the pause summary.
- The daily refill (60 minutes at 05:00, at most 180) still applies, so a character's bank still grows up to 180.
- The "High-Rate Auto Mode Resumed" tip no longer shows: the game is told Bonus Time is suspended so its own
  countdown stays still, and that tip only shows when it stops being suspended.

Turning it off again later lets the bank run down from wherever it is.
