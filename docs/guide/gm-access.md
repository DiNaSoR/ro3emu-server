# Who has GM (`Ro3Emu:GmAccounts`)

GM commands are the `!` lines in chat (`!help`), the client's GM panel (30300) and the GM transmit/inspect messages
(1031/1034). One check decides all of them: `GmCommands.Allowed`.

| `GmAccounts` | `GmOpenWhenEmpty` | in game |
|---|---|---|
| listed accounts | any | only those accounts, spelled as stored (an account name is one account in any letter case; the list matches its stored spelling) |
| empty | unset, host not started as Production | everyone (tests, `tools/run-server.ps1`, local play) |
| empty | unset, host started as **Production** | **nobody** |
| empty | `true` / `false` | everyone / nobody (an explicit value wins over the environment; `server.sh` sets `false`, `--dev` too) |

"Started as Production" means the environment was set explicitly: `ASPNETCORE_ENVIRONMENT=Production`,
`DOTNET_ENVIRONMENT=Production` or `--environment Production`. ASP.NET's own default when nothing is set is also
called Production, but it does not count, so tests and local runs keep "empty = everyone".

At startup the server logs who has GM. With an empty list it warns loudly in both cases that matter:

- Production and nobody has GM: `!!! Production: Ro3Emu:GmAccounts is empty, so NO account has GM commands in game.`
- GM open to everyone on a public address (`PublicHost` not loopback): `!!! Ro3Emu:GmAccounts is empty and GM is open: EVERY account can use GM commands in game ...`

## Configuration

Like every `Ro3Emu:` option, the list can be set in `appsettings.json`:

```json
{
  "Ro3Emu": {
    "GmAccounts": [ "your-admin-login" ]
  }
}
```

or through environment variables, one per account: `Ro3Emu__GmAccounts__0=your-admin-login`,
`Ro3Emu__GmAccounts__1=another-login`, and so on. The value is the game login (account name), not a character name.

## The loopback tool

`POST http://127.0.0.1:5380/ro3emu/gm?account=<account>` (body `!warp 10111 150 190`) answers only on loopback and
keeps its own rule: with an empty list it runs for any online account, with a list only for listed accounts.
Production does not close it; it is the operator's tool on the server machine.

## A public server

On a server that other people can reach, start the host as Production and list the GM accounts. With systemd, a
drop-in such as `/etc/systemd/system/ro3emu.service.d/gm.conf` (adjust the unit name to yours) needs both lines:

```ini
[Service]
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=Ro3Emu__GmAccounts__0=your-admin-login
```

More GMs: `Ro3Emu__GmAccounts__1=...`. Passwords never go in the unit, the repository or these docs. After
`systemctl daemon-reload` and a restart, the log shows `GM accounts: your-admin-login`.

## The client's GM panel

The large draggable GM button opens the client's own GM panel (`UI_Sys_GMPanel`, rows from `GMConfig` and the
one-click presets from `GMCombinationConfig`). Each button sends 30300 with its `GmOpEnum` and the fields joined by
commas; the emulator answers with a text line. A target ID of 0 or an empty field means yourself; monster IDs come
from Spawn NPC's answer or the unit list.

Working buttons (40 of 66): add or remove item, clear bag, set level, add EXP, strengthen and refine a part (free, to
the given level), add stat points, reset stats, change HP, drop pool, send personal mail, send custom or template
mail to everyone online, quests (accept, give up, hand in, complete), job change, spawn NPC, show and change
attributes (current HP and SP only), no cooldowns, damage fluctuation, add, remove and list buffs, kill target, full
HP and SP, invincible, warp, fly to target, show position, create guild, enter and leave a dungeon, set a monster's
camp, learn all skills (with the role's own skill points) or reset them, and the server level. No cooldowns and
invincible last for the session; damage fluctuation is server-wide until a restart.

Not implemented: server time and open day, bulk character creation, revoking mail, the panel's Tavern tools (the
rooms themselves work, see [the Tavern](../systems/tavern.md)), casting a skill, reloading skill blueprints, combat logs, and the GvG reset. Ten buttons have no
op the server could receive. Presets that use season talents, emblems, equipping gear or skill wheels run only
their other steps.
