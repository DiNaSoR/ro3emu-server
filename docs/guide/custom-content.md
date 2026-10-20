# Custom content

Quests, NPCs, dialogue and text that the client does not ship, written as JSON in the server's `content/` folder.
The server reads them like client data, and the client hooks add the same rows to the client's own tables, so a
custom quest shows in the quest log, a custom NPC has a name and dialogue, and both work with the normal quest engine.

Code: `src/Ro3Emu.Server/Data/CustomContent.cs` (loading and the overlay on GameData),
`src/Ro3Emu.Server/Data/CustomContent.Quests.cs` (the quest format), `src/Ro3Emu.Server/Data/CustomContentCheck.cs`
(the checks),
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

## Writing quests

Most content is an NPC and a quest chain, and both can be written in plain text. The server turns them into the
client rows described under [Raw rows](#raw-rows) when it loads, so you never write row fields, text ids or
dialogue ids by hand. The prototype is written this way: [`content/prototype-scholar.json`](../../content/prototype-scholar.json).

```jsonc
{
  "npcs": [
    {
      "id": 90000001,                 // NPCConfig id, see "Ids" below
      "name": "Elias",
      "title": "Wandering Scholar",   // optional, shown under the name
      "model": 100079,                // optional, an existing model (UnitConfig id); !findnpc shows the models of existing NPCs
      "map": 10100, "x": 150.4, "z": 193.05, "face": 180,   // optional placement; !pos shows where you stand
      "greeting": "So many curious creatures around Prontera...",
      "goodbye": "Goodbye"            // optional, the button that closes the greeting
    }
  ],
  "quests": [
    {
      "id": 90000001,                 // the first step's quest id; step 2 is 90000002, and so on
      "after": 10101130,              // optional: only offered once this quest is handed in
      "steps": [
        {
          "name": "A Scholar's Request",
          "description": "A scholar named Elias is looking for help in Prontera.",
          "objective": "Talk to Elias in Prontera",
          "talk": 90000001,
          "dialog": [
            { "say": "Ah, a traveler! I'm Elias.", "reply": "How can I help?" },
            "Could you defeat <color=#CC762A>5 Porings</color> for me?"
          ]
        },
        {
          "name": "Jelly Samples",
          "description": "Defeat 5 Porings at the Prontera South Gate for Elias",
          "objective": "Defeat Porings at the South Gate",
          "kill": 5000001, "count": 5, "map": 10111
        },
        {
          "name": "Back to the Scholar",
          "description": "Bring the jelly samples back to Elias in Prontera.",
          "objective": "Report to Elias in Prontera",
          "talk": 90000001,
          "dialog": [ "This jelly is perfect. Thank you, adventurer." ],
          "exp": 2000,
          "items": [[1, 1000], [1200801, 3]]  // 1000 Zeny and 3 Hot Cocoa
        }
      ]
    }
  ]
}
```

**NPCs** (`npcs` entries with a `name`): `id`, `name` and `greeting` are needed. Clicking the NPC shows the
greeting with a goodbye button. The NPC copies Alphonse (200014) for everything you leave out; `copy` names another
NPC to copy instead. Without `model` it keeps that NPC's look. `icon` sets the dialogue portrait, which is
`temphead_npc_<model>.png` by default. Leave out `map` to define the NPC without placing it.

**Shops**: an NPC with a `shop` sells items. Its greeting gets a Shop button (`button` renames it) that opens the
client's NPC shop window, the one the Kafra Staff's Novice Shop uses, titled with the shop's `name`:

```jsonc
"shop": {
  "name": "Scholar's Supplies",
  "items": [
    [1200801, 50],                                            // Hot Cocoa for 50 Zeny
    { "item": 1002000, "price": 3, "currency": 2, "count": 5 } // 5 Butterfly Wings for 3 Minted Coins
  ]
}
```

Each item is `[item, price]` in Zeny, or an object with a `currency` (an item id: 1 Zeny, 2 Minted Coin, 3 Sacred
Relic Crystal) and a `count` sold per purchase. There is no stock or purchase limit. The shop is ShopConfig row
`<NPC id>` (its `_iShopId` and `_iClassID` are the NPC id too) and the goods are ShopCommodityConfig rows
950000000 + (NPC id % 1,000,000) × 100 + 1, 2, …; buying goes through the server's normal shop checks, and the player must be on the NPC's map within 12 m of it
(talk reach plus slack), or the buy is refused with 804 "too far". The window
needs the client hooks from the same release: older hooks add the rows after the client has indexed its shops, so the
window stays empty.

**Quests**: each step is one quest in the quest log, offered when the step before it is handed in. The first step is
offered to every character at once, or after the quest named in `after`. Every step needs a `name`, a `description`
(shown in the quest log) and an `objective` (the tracker line), and then one of:

- **talk**: the NPC's id and its `dialog`. Each page is the NPC's line, either as text or as
  `{ "say": ..., "reply": ... }`. Every page but the last gets a reply button (`Continue` unless you write one) that
  opens the next page, and closing the last page completes the step. A talk step to a client NPC (one that no custom
  file places) also needs the `map` it stands on. The tracker walks to the NPC.
- **kill**: a monster id, or a list of them, and the `count`. Party kills count, unless you add `"solo": true`. Give
  the `map` where they spawn and the tracker walks to the spawn. The objective gets the `*` that shows the counter.
- **level**: a base level to reach. A character already at that level finishes the step at once.
- **area**: a scene-editor area (region) id and its `map`: the step finishes when the player walks into it, and the
  tracker walks there. As a GM, `!pos` lists the areas nearest to where you stand.
- **done**: a quest id, the client's or a custom one: the step finishes once that quest is handed in. Unlike
  `after`, the step is in the quest log while the player works on the other quest.

A quest can say its `type`: `"main"`, `"side"` (the default) or `"guide"`. The type sets the quest log tag and the
bubble over the NPC a talk step sends you to, in the client's colours: orange `[Main Quest]`, blue `[Side Quest]` or
green `[Guide]`. Every step of the quest takes it.

Optional on any step: `exp` (Base EXP when handed in; Job EXP follows at 20%), `items` (items handed out with the
quest, see below), `award` (a MultiverseGroupConfig id, whose
`_iValue_default` names the AwardConfig item package; instead of `items`), `track` (a raw `_kTrack` entry, replacing the one the server picks), and `fields` (any TaskConfig fields,
written over the generated row).

**items**: a list of `[item, count]`, or `{ "item": id, "count": n, "bound": true }` for an item that cannot be traded.
Any ItemConfig id works, currencies too: `"items": [[1, 1000], [1200801, 3]]` pays 1000 Zeny and 3 Hot Cocoa. The
quest log shows them as the step's reward. `!finditem <name>` finds an id. The server makes an AwardConfig package and
a MultiverseGroupConfig row with the step's quest id, so keep quest ids in the 90000001… range, which those tables leave
free.

Text can carry colour tags like `<color=#CC762A>5 Porings</color>`. Write the whole text, numbers included: there are
no placeholders to fill in this format.

## Checking your content

The server checks every custom file when it starts and logs what it finds as warnings: a quest that names a
monster, NPC, map, model, reward or dialogue that does not exist, a kill step with no spawn on its map, or a file or
quest that was skipped and why. To check without starting the server, run the built server with `--check-content`
from the folder you start the server in (the repository root for `tools/run-server.ps1`):

```
src\Ro3Emu.Server\bin\Release\net10.0\Ro3Emu.Server.exe --check-content
```

It reads `content/` the way the server does, lists every problem and exits with code 1 if there are any.

## Finding ids

As a GM in game:

- `!findnpc <name>` lists NPCs and monsters whose name contains the text, with their id, model and level.
  Monsters are NPCConfig rows too, so `!findnpc poring` gives the id for a kill step.
- `!finditem <name>` finds items.
- `!pos` shows your map and position; `!where` names the map.

## Ids

Pick ids far from the client's own so they never collide. Only NPC and quest ids are yours to choose, and they must
stay the same once players have the quest, because saved characters keep them. The prototype uses 90000001 and up for
both. The server makes the text and dialogue ids from the NPC or quest id, using its last six digits. So two NPCs, or
two quest steps, whose ids end in the same six digits clash, and the second one is skipped with an error.

| What | Range | Note |
| --- | --- | --- |
| TaskConfig, NPCConfig, NPCInteractConfig | 90000001… | chosen by you |
| DialogueConfig | 900000000… (quests), 1000000000… (NPCs) | made by the server |
| Texts | 98000000000… (quests), 97000000000… (NPCs) | made by the server; hand-written texts can use 99000000001… |

## Raw rows

Anything the quest format does not cover can be written as client rows directly, in the same files.

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
- **npcs**: server NPCs to place (entries without a `name`). `map` is the MapConfig id, `npc` the NPCConfig id,
  `x`/`z` the position (stand there and type `!pos` in chat as a GM to read yours), `y` optional (the ground height
  by default), `face` the facing in degrees.

### What the quest format makes

Each step copies a client quest (10101130 for a talk step, 10101050 for the others) and takes the quest's `type`:
`_iTaskType`, the quest log tag `_iShowTag` and the NPC bubble `_kNPCProgressMark` (Side by default).

1. **Talk**: `_kProgressTarget [[104001, npc, map, dialog]]`, `_kTrack ["143", map, npc, "9", dialog]`. The
   dialogue's last row carries action `["17", dialog]`, which tells the server the talk is done.
2. **Kill**: target `[104014, monster…, count]` (104002 with `solo`). With a `map`, `_kTrack`
   `["142", map, region, "-1"]` points the tracker at a scene-editor region next to a spawn of the monster.
3. **Level**: target `[101002, level]`, no tracker.
4. **Area**: target `[104003, map, area]`, `_kTrack ["142", map, area, "-1"]`.
5. **Done**: target `[104004, quest]`, no tracker.
6. **Order**: step n gets `_kPreTask [step n - 1]`; the first step gets `_kPreTask [after]`, or with no `after`
   `_iCondition` 2000008 (base level 1), which auto-accepts it for every character.
7. **Rewards**: `_kEXP [1, n]` pays n Base EXP, `_iAward` an AwardConfig package (`items` makes AwardConfig and
   MultiverseGroupConfig rows with the step's id: `_kAward [[item, count, bound 0/1]]`, `_kPreview [[item, count]]`).

Useful target types (the first number of a `_kProgressTarget` entry; all supported by `TaskService`), for steps
written as raw rows or through `fields`:

| Target | Fields | Meaning |
| --- | --- | --- |
| 104001 | npc, map, dialog | finish a dialogue with an NPC |
| 104002 | monster…, count | kill monsters yourself |
| 104014 | monster…, count | kill monsters, party credit counts |
| 101002 | level | reach a base level |
| 104003 | map, area | enter a scene-editor area |
| 104004 | quest | hand in another quest |

A custom NPC clicked in game runs its `_iInteractID` rows in NPCInteractConfig; action `["53", dialog]` opens a
dialogue. Models, animations and portraits must already exist in the client. The hooks also add each placed NPC to
its map's scene-editor NPC list (`kNpcList`, keyed by the NPC id), where the client looks up walk targets, so a
`_kTrack` of `["143", map, npc, "9", dialog]` auto-walks to a custom NPC and the NPC shows on the minimap. Place a
given NPC id once per map.

## Testing a quest again

As a GM, `!quest drop <taskId>` forgets a quest and every later quest of its chain (running ones leave the quest log
as if given up), and `!quest` lists the running ones. Drop the first quest to play a chain again: a first quest with
no `_kPreTask` is offered again at once. For the prototype: `!quest drop 90000001`. Handing a quest in again pays
its reward again. `!quest finish <taskId>` completes the current quest's objectives, `!quest done <taskId>` hands it in.

## Limits

- Only existing art: no new models, maps, music, voice or images.
- On the first login after a content change the rows arrive with the login itself. If a quest shows up before its
  text does, relog once: the hooks keep a copy, so every later start has the rows from the beginning.
