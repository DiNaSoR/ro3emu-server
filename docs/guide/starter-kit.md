# Starter kit

By default a new character starts with nothing: no items, no Zeny. The main story hands out the first supplies (see
below). Code: `src/Ro3Emu.Server/Game/CharacterService.Login.cs` (`FinishLoadAsync`), the default in
`CharacterService.cs` (`DefaultStarterKit`).

To give new characters something anyway, list ItemConfig ids (1 is Zeny) and counts:

```json
"Ro3Emu": {
  "StarterKit": [ { "Entry": 1000000, "Count": 20 }, { "Entry": 2010121, "Count": 1 }, { "Entry": 1, "Count": 1000 } ]
}
```

Unknown items are skipped with a warning in the log. The kit is given once per character, on its first login.

## What the client data says

The exported client tables have no starting-item table (JobConfig 1000 has no item field, PublicParamConfig no such
parameter), so the empty start is **(inferred)**, like classic RO's 0 Zeny start. The main story's rewards:

1. 10101060 Report the Outcome: 50 Beginner Potions and 100 Refreshing Breath Medicine.
2. 10101150-10101160: the first job change, still without a weapon (beta footage shows bare-handed attacks);
   10101160 also gives 200,000 Minted Coins (item 2).
3. 10101210 The First Request: a job weapon (drop pool 31121, group 121121: dagger, sword, two-handed sword, spear,
   axe, rod, bow or guard by job).
4. 10101230 Adventure Essentials, "Ask Rhine how to use the equipment you just obtained": GuideConfig 19 starts here.
   Its mobile steps (160-163) walk through equipping from the bag; its PC steps (1000161-1000163) show the equipment
   screen and affixes.
5. 10101240 Request Reward: the first armor (Novice Cotton Shirt, Sandals, Hooded Pauldrons, two Novice Rings and,
   for first jobs, a Novice Iron Shield).
