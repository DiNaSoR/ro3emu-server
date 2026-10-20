# Walk check

The server checks every walk a client sends against the map's nav grid (exported from the client) and the
character's Speed attribute. Code: `SceneService.AcceptWalkAsync` in `src/Ro3Emu.Server/Game/SceneService.cs`,
options in `src/Ro3Emu.Server/ServerOptions.cs`.

There are two checks:

| Check | Fails when |
| --- | --- |
| Speed | The walk covers more than `Speed/100 × seconds × 1.5 + 1.5 m` since the last accepted walk (time clamped to 0.05-5 s) |
| Path | The target cell is not walkable, or the straight line from the last position crosses unwalkable cells |

Each check has one of three modes:

| Mode | What happens on a failed walk |
| --- | --- |
| `off` | Nothing; the check does not run |
| `log` | A warning in the server log, and the walk is accepted (default) |
| `enforce` | A warning, the walk is refused and the player is snapped back to the last accepted position |

`WalkCheck` sets both checks. `WalkSpeedCheck` or `WalkPathCheck` sets one of them apart; leave it empty to follow
`WalkCheck`. Modes are read in any case; anything else counts as `log` and is warned about once.

```json
"Ro3Emu": {
  "WalkCheck": "log",
  "WalkSpeedCheck": "enforce"
}
```

Or on the command line: `--Ro3Emu:WalkCheck=enforce --Ro3Emu:WalkPathCheck=log`, or in a systemd unit:
`Environment=Ro3Emu__WalkSpeedCheck=enforce`.

## Choosing a mode

`log` never rubber-bands an honest player, but speed and teleport hacks go through. Before turning `enforce` on, run
with `log` for a while and read the warnings (`Walk check <name> scene <map> ...: <problem> (<n> so far)`). Only the
first and every hundredth failed walk per session is logged, so read the `so far` count rather than counting lines.

The path check is the one most likely to misfire for normal play (map edges, bridges, slopes), so a cautious setup
enforces only the speed check: `WalkSpeedCheck=enforce` with `WalkCheck=log`.
