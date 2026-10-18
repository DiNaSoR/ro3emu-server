# Custom content

Quests, NPCs, dialogue and text that the client does not ship, written as JSON in the server's `content/` folder.
The server reads them like client data, and the client hooks add the same rows to the client's own tables, so a
custom quest shows in the quest log, a custom NPC has a name and dialogue, and both work with the normal quest engine.

Code: `src/Ro3Emu.Server/Data/CustomContent.cs` (loading and the overlay on GameData),
`src/Ro3Emu.Server/Game/CustomContentService.cs` (sending to the client), `src/Ro3Emu.Server/Maps/WorldData.cs`
(NPC placement), `src/Ro3Emu.ClientHooks/CustomContent.cs` (the client side).
Example: [`content/prototype-scholar.json`](../../content/prototype-scholar.json).

## How it works

The client's Lua cannot be changed (the recovery manifest pins the hash of every chunk), but it reads config rows
live: `Lua_DBManager.m_kCFG_<Table>` looks rows up in the loaded table's `m_kValues` on every call, and
`Language.Trans` reads text from the global `LanguageKV`. So:

1. At startup the server overlays every `content/*.json` on the exported tables (`data/config`). Quests, NPCs and
   texts written there are ordinary rows for every server system.
2. At role login, before any quest or NPC data, the server sends the resolved rows and texts to the client as game
   message 77020 (JSON).
3. The client hooks (`Ro3Emu.ClientHooks.dll`) add them to the client's tables and keep a copy in
   `<client>/ro3emu/custom-content.json`, so the next start has them before login. If the client reloads a table,
   the hooks add the rows again.

Adding or changing content needs a server restart and a relog, never a new client.

## File format

Every `*.json` under `content/` (subfolders too) is read in name order; a later file replaces an earlier file's
row with the same id. `//` comments and trailing commas are allowed. A file with an error is skipped and logged.

```jsonc
{
  "texts": { "99000000001": "Elias" },
  "rows": {
    "NPCConfig": {
      "90000001": { "_base": 200014, "_kName": [99000000001], "_iUnitID": 100079 }
    }
  },
  "npcs": [ { "map": 10100, "npc": 90000001, "x": 150.4, "z": 193.05, "face": 180 } ]
}
```

- **texts**: text id → English text. They become `Localization_en` rows, so any `[textId]` field can use them.
  Placeholders work as in the client's own strings: `^{n}` and `@{n}` are replaced by the field's n-th value after
  the text id. For coloured text put the tags there: text `"Defeat ^{1}5 Porings^{2}"` with
  `"_iText": [99000000113, "<color=#CC762A>", "</color>"]`; for a number, `"_iTaskDescribe": [99000000202, "5"]`
  with `@{1}` in the text. A placeholder with no value shows as written.
- **Objective text** (`_iTargetDescribe`): the quest log splits it at `|` into one line per objective, and a line
  ending in `*` shows the progress counter (5/5). Without the `*` the kills still count, but the log shows no number.
- **rows**: table name (as in `data/config`) → id → row. `"_base": <id>` copies an existing row of the same table
  first; the fields you write replace the copy's top-level fields, and the copy's id fields (`_iID` or another
  `_i…ID` equal to the base id) take the new id. Without `_base` the row must be complete. A row may also be an
  array of row objects (DialogueConfig keeps one array per dialogue id).
- **npcs**: server NPCs to place. `map` is the MapConfig id, `npc` the NPCConfig id, `x`/`z` the position (stand
  there and type `!pos` in chat as a GM to read yours), `y` optional (the ground height by default), `face` the facing in degrees.

### Ids

Pick ids far from the client's own so they never collide. The prototype uses:

| What | Range | Note |
| --- | --- | --- |
| TaskConfig, NPCConfig, NPCInteractConfig | 90000001… | |
| DialogueConfig | 900000101… | must fit a 32-bit int (the dialog id travels in 31208) |
| Texts | 99000000001… | 11 digits, like the client's |

## A quest, step by step

The prototype is three Side quests (blue tag) given by Elias, a new NPC next to Alphonse in Prontera:

1. **Talk** (90000001): `_kProgressTarget [[104001, npc, map, dialog]]`. Auto-accepted for every character because
   it has no `_kPreTask` and an `_iCondition` (2000008 = base level 1). The dialogue's last row carries action
   `["17", dialog]`, which tells the server the talk is done.
2. **Kill** (90000002): `_kPreTask [90000001]`, target `[104014, 5000001, 5]` (5 Porings, party kills count).
   `_kTrack` points the quest tracker at the South Gate spawn.
3. **Report** (90000003): talk again; `_kEXP [1, 2000]` pays 2000 Base EXP (Job EXP follows at 20%).

Useful target types (the first number of a `_kProgressTarget` entry; all supported by `TaskService`):

| Target | Fields | Meaning |
| --- | --- | --- |
| 104001 | npc, map, dialog | finish a dialogue with an NPC |
| 104002 | monster…, count | kill monsters yourself |
| 104014 | monster…, count | kill monsters, party credit counts |
| 101002 | level | reach a base level |
| 104003 | map, area | enter a scene-editor area |

Copy a client quest with `_base` (10101130 is a talk quest, 10101050 a kill quest) and change what differs.
Rewards: `_kEXP [1, n]` for Base EXP, `_iAward` for an AwardConfig item package.

## NPCs

A custom NPC is an NPCConfig row (usually `_base` of an existing NPC, with a new `_kName`, `_kTitle` and a model
via `_iUnitID`) plus an `npcs` entry. Clicking it runs its `_iInteractID` rows in NPCInteractConfig; action
`["53", dialog]` opens a dialogue. Models, animations and portraits must already exist in the client.

The hooks also add each placed NPC to its map's scene-editor NPC list (`kNpcList`, keyed by the NPC id), where the
client looks up walk targets. So a quest's `_kTrack` of `["143", map, npc, "9", dialog]` auto-walks to a custom NPC
like it does to a client one, and the NPC shows on the minimap. Place a given NPC id once per map.

## Testing a quest again

As a GM, `!quest drop <taskId>` forgets a quest (running or done) and `!quest` lists the running ones. Drop a chain
from its last quest back to its first; a first quest with no `_kPreTask` is offered again at once. For the prototype:
`!quest drop 90000003`, `!quest drop 90000002`, `!quest drop 90000001`. `!quest finish <taskId>` completes the
current quest's objectives, `!quest done <taskId>` hands it in.

## Limits

- Only existing art: no new models, maps, music, voice or images.
- On the first login after a content change the rows arrive with the login itself. If a quest shows up before its
  text does, relog once: the hooks keep a copy, so every later start has the rows from the beginning.
