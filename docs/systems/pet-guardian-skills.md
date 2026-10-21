# Ordinary pet guardian skills

Ordinary guardians have separate Primary, Reactive and King Core paths. The known scripts cover all 56 pet
families and 1,176 star/stage mappings in the examined client export. This is implementation coverage, not proof
of original-server parity. Numerical configuration and wire formats come from the client; execution policies
that it does not establish remain explicitly inferred. See [client evidence](../research/pet-skills-evidence.md).

## Ownership and protocol

`PetService.Guardians` resolves owned pets in unlocked fight slots of the current plan. Slot 1 supplies the only
Primary skill, projected into both slot-11 wheels on 5025. Slots 2-4 supply Reactive effects. The first King in
utility-slot order supplies Core; moving that King invalidates captured pending Core work. Assist and tower
lineups remain separate. A directly requested internal child, unknown wrapper, locked slot, unsupported cost,
invalid target or cooldown is rejected.

A primary request uses 31102; EXTRA_USEINSKILL (31121) carries the type-41 child and `petConfigId` so the client shows the temporary
pet actor. The wrapper reserves cooldown before any awaited publication. Guardian work does not replace the
owner's ordinary cast. Every delayed pulse revalidates owner, attachment, scene generation, plan, slot and
star/stage. Pulses serialize within one cast and aggregate committed primary hit amounts before a final Core hit.

## Implemented effects

- Single and multiple hits, adaptive attack, element components, circles/fans/rectangles and target-centered
  areas; Owl random lightning, Sarah bursts, Eve's final rectangle, owner heal per hit and kill-triggered curses.
- Direct and periodic recovery, finite shields, Chimera control charges, immunity, dispel, knock-up, root,
  petrify and timed stat changes. Monster stats use live overlays without altering shared templates.
- Category-specific Reactive effects: duration, coefficient, target count, cast speed, healing, defence,
  damage, elemental resistance, conditional shields, area debuffs, additional hits and temporary owner-hit windows.
- Stateful effects: Cat consumes the next owner-damage charges and the next received-hit vulnerability;
  Scout stacks defence after damage; Mummy limits kill-triggered curses; Piamette heals once below its threshold;
  Shinobi moves/cleanses/hides and ends stealth on owner damage or its incoming-hit limit; Scout detects stealth.
- King Core effects: Zherlthsh's share of accumulated primary damage; Hope's ally defence and bounded HP floor;
  Wander Man's amplified defence debuff and conditional control/attack debuff; Fairy's shield-bound debuff immunity.

PvP reuses hostility, damage settlement and target limits. Healing excludes hostile players. Support attributes
also affect real damage, casting and healing. Blind publishes the client's dark-state/radius properties rather
than inventing an accuracy penalty. The same inferred radius restricts monster target acquisition and normal hits.
Sohee's normal-attack reduction runs at the shared player HP transition for both PvE and PvP.
Freeze, petrify and sleep break on damage; the first two use their configured
follow-up debuffs. Ordinary timed buffs can outlive unequipping; future windows and delayed attacks cannot.

## Explicit inference boundary

The client does not contain the original authoritative pet scripts. Damage formula, adaptive-stat choice,
rounding, target tie-breaking, proc order, stacking, exact pulse-to-animation mapping and some auxiliary links
are emulator policies. Shared combat cooldowns are session state, not newly persisted timers.

Blind's original radius was not recovered: 5 metres is an explicit approximation. Angeling's periodic interval
is inferred. Where descriptions and auxiliary rows conflict, the named implementation documents which source
it uses; known examples are Owl's reactive coefficient, Aliot's per-hit healing, Angeling's halo radius and
Incubus's aura radius. Butterfly's exact subdivision and Sarah's auxiliary burst timing remain inferred.

## Validation

Synthetic tests cover admission, shared cooldown reservation, wheel updates, utility-slot separation, lifecycle
cancellation, independent modifiers and directed AOI visibility. Client-data tests cover every star/stage mapping
and real combat cases for heals, shields, control, stat changes, multi-hit attacks, conditional windows, periodic
pulses and all four King Core mechanisms. Passing these tests establishes the declared server behavior; only
comparative captures from the official server could establish numeric and timing parity.
