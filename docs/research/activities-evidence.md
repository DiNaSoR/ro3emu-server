# Activities evidence census

This census records the read-only searches behind the activity systems ([activities.md](../systems/activities.md) and
the `activities-*.md` pages in `docs/systems/`). The searched roots are the game data exported from the client
(`data/config`, `data/sceneeditor`, `data/nav`, `data/loc`), your local decompiled client under `re/` (`re/lua-src`
with its Config, Logic and UI trees, and `re/decomp`), and the server's own protos. Every root was searched in full,
not only a DAL directory.

Each pattern below was run as `rg -l -a -i -g <glob> <pattern> <scope>` (ripgrep). `-a` includes binary assets in the
literal-byte search. Counts are matching **files**, not rows, and are not evidence of gameplay absence. JSON
configuration names often appear only in filenames, so all filenames were also enumerated and searched with
`rg --files <scope> | rg -i <pattern>`. Relevant named tables and editors were then parsed as JSON and the matching Lua
consumers read.

| Key | Exact pattern |
| --- | --- |
| C | `NewActivity\|BattlePass\|MonthCard\|SevenDay\|LevelAward\|FunctionPreview\|WorldExploration\|Training\|Piling\|RainbowKoi\|DrawConfig\|DrawPool` |
| G | `CampFireProbability\|CampFirePosition\|CampFireAffixPool\|GuildTinderLottery\|CampFireAffixNew` |
| T | `OnePiece\|HiddenTreasure\|EatChicken\|Chicken\|DragonIsland\|PronteraDefense` |
| A | `SeasonPre\|FragmentGather\|TreasureHunt\|Journey\|SeasonEve\|Racing\|TopGuild\|MiddleSeason` |

The pipes in the table are regular-expression alternation (the Markdown escaping is not part of the command).

| Scope | Glob | C content hits | G | T | A |
| --- | --- | ---: | ---: | ---: | ---: |
| `data/config` | `*.json` | 7 | 0 | 38 | 10 |
| `data/sceneeditor` | `*.json` | 0 | 0 | 1 | 0 |
| `data/nav` | `*` | 0 | 0 | 0 | 0 |
| `data/loc` | `*` | 1 | 0 | 1 | 1 |
| `re/lua-src` | `*` | 266 | 8 | 763 | 159 |
| `re/decomp` | `*` | 0 | 0 | 0 | 4 |
| `src/Ro3Emu.Server/Protos` | `*.proto` | 48 | 0 | 52 | 11 |

The scene-editor census included all **89** `*_monsteridmap.json` files (`rg --files data/sceneeditor -g '*_monsteridmap.json'`),
separately from the scene editors. The nav/loc filename enumeration covered **174** files. Literal-byte results do not
interpret binary map data; map availability and placement are established by the actual `WorldData` loader plus the
relevant editor/nav pairs.

Filename matches in `data/config` were C=14, G=4, T=21, A=17; whole Lua filename matches were C=162, G=5, T=537, A=61.
Concrete positive sources include:

- C: NewActivity, TimeLimit, BattlePass/Reward, MonthCard, SevenDaySignUp, LevelAward, FunctionPreview,
  ExplorePhase/Node, TrainingAimAward, Draw/DrawPool, RainbowKoiQuestion/Reward, Product, Award and MultiverseGroup
  configurations; the exact consumer paths are listed in the system pages
  ([activities-battlepass-monthcard.md](../systems/activities-battlepass-monthcard.md),
  [activities-lottery.md](../systems/activities-lottery.md), [activities-adventures.md](../systems/activities-adventures.md)).
- G: `CampFireProbability.json`, `CampFirePosition.json`, `CampFireAffixPool.json` and `CampFireAffixNewConfig.json`,
  plus the original Lua DataConfig and GuildTinder controllers. Parsed Probability has 100 records with zero nonempty
  objects; Position has 20 and AffixPool 3 analogous records (see
  [activities-guild-campfire.md](../systems/activities-guild-campfire.md)). These exported records do not encode
  weights. The implementation's uniform selection policy is explicitly **(inferred)**; zero content-search hits alone
  did not establish this conclusion.
- T: the OnePiece, EatChicken, DragonIsland and PronteraDefense tables and client consumers, with actual editor
  points and monster maps checked for each implemented encounter
  ([activities-timed-events.md](../systems/activities-timed-events.md)). Private-player matches do not substitute an
  invented boss for a player.
- A: SeasonPre, FragmentGather, TreasureHunt, Journey and SeasonEve tables and their DAL/CFG consumers.
  `NpcLevelPointRespawnConfig` resolves Journey scene point 26 to its actual encounter templates despite the editor's
  `npcId:0` placeholder.

The counts and commands above make the census repeatable against your own exported data and decompiled client.
Source-derived choices are distinguished from explicit inferred emulator policies in the linked system pages.
