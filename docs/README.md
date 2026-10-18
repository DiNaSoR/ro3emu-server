# Documentation

## Guides: running a server

- [GM access](guide/gm-access.md): who may use GM commands (`Ro3Emu:GmAccounts`)
- [Open content](guide/open-content.md): every feature the client can show is playable (`Ro3Emu:OpenContent`)
- [Rates](guide/rates.md): Base/Job EXP, item drop and currency drop multipliers for monster kills (`Ro3Emu:Rates`)
- [Starter kit](guide/starter-kit.md): what a new character starts with (nothing by default; `Ro3Emu:StarterKit`)
- [Custom content](guide/custom-content.md): your own quests, NPCs, dialogue and text (`content/*.json`)
- [Bug reports](guide/feedback.md): the in-game Feedback button and `/bug`, and how to read the reports
- [Launcher and patch repository](guide/launcher.md): hosting the client for your players

## Systems: how each part is implemented

| Area | Notes |
|---|---|
| Combat and skills | [skill protocol](systems/skill-protocol.md) · [buffs and resources](systems/skill-buffs.md) · [skill growth](systems/skill-growth.md) · [remaining skill messages](systems/skills-rest.md) · [totem effects](systems/totem-effects.md) |
| Character and items | [equipment affixes](systems/equipment-affixes.md) · [build presets](systems/genre.md) · [season talents](systems/season-talents.md) · [wardrobe](systems/wardrobe.md) · [mounts and totems](systems/mount-totem.md) |
| Pets | [pets](systems/pet.md) · [pets in the world](systems/pet-world-follow.md) · [tower companions and Bonus Time](systems/tower-pets-afk.md) |
| Life jobs | [life jobs and production](systems/lifejob.md) · [gathering](systems/gathering.md) |
| Economy | [cash shop and Top Up](systems/cash-shop.md) · [trade and stalls](systems/trade-stalls.md) · [auctions](systems/auction.md) · [mail](systems/mail.md) |
| Guilds | [guilds](systems/guild.md) · [territory wars](systems/territory-wars.md) · [escort and guild trading](systems/territory-escort.md) |
| Instances | [dungeons](systems/dungeon.md) · [story dungeon inventory](systems/dungeon-inventory.md) |
| Activities | [overview](systems/activities.md) · [timed activities](systems/activities-timed-events.md) · [adventures](systems/activities-adventures.md) · [battle pass and month card](systems/activities-battlepass-monthcard.md) · [guild campfire](systems/activities-guild-campfire.md) · [lottery](systems/activities-lottery.md) |

## Research: client evidence

- [World core](research/world-core-evidence.md) · [world effects](research/world-effects-evidence.md) · [NPC interaction](research/world-interaction-evidence.md) · [role operations](research/world-role-operations-evidence.md) · [social](research/world-social-evidence.md) · [MG_Common](research/world-common-evidence.md)
- [Activities census](research/activities-evidence.md) · [season page](research/season-page-evidence.md) · [season refine](research/season-refine-evidence.md) · [equipment rating](research/equipment-rating-evidence.md)

Citations such as `re/clientB/lua-src/Logic/...lua:123` point into your own decompiled client (see the main
[README](../README.md#1-export-the-game-data)); `MG_*.proto` files are in `src/Ro3Emu.Server/Protos/romsg/`.
Behaviour the client cannot prove is marked *(inferred)*.
