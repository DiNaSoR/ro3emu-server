# Starter kit

A new character gets a starter kit on its first login: 20 Beginner Potions, a Novice Dagger, a Cotton Shirt and
1000 Zeny. Code: `src/Ro3Emu.Server/Game/CharacterService.Login.cs` (`FinishLoadAsync`, `WearStarterGear`), the kit
itself in `CharacterService.cs` (`StarterKit`).

The dagger and the shirt start worn (main hand and armor), because the client never tells a new player that gear is
waiting in the bag. To hand them out in the bag instead:

```json
"Ro3Emu": { "WearStarterGear": false }
```

Or on the command line: `--Ro3Emu:WearStarterGear=false`.

## What the client data says

The exported client tables have no starting-item table: JobConfig (Novice, 1000) has no item field and
PublicParamConfig has no such parameter, so the kit itself is **(inferred)**. Two hints shape it:

- GuideConfig 19 opens at main quest 10101230 (Adventure Essentials). Its mobile steps (160-163) open the bag and
  walk the player through equipping the Novice Dagger (2010121, "an essential weapon for every Novice"). Its PC
  steps (1000161-1000163) instead say "Open your Backpack and check the equipment you just put on" and show the worn
  armor's affix. On PC, starter gear is expected to be worn already.
- The first gear any quest awards is 10101240 (Request Reward): Novice Cotton Shirt, Sandals, Hooded Pauldrons,
  two Novice Rings and, for first jobs, a Novice Iron Shield.
