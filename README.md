# Pedigree

Pedigree is an in-game planning engine for a tactical roguelike with a persistent, evolving roster. Between runs, the arrangement of units across the base determines how the roster's stats develop over generations. Pedigree reads the live game state, solves that arrangement as an optimization problem, picks the next expedition squad and flags which units to recruit or retire, then applies the plan in one click from the game's mod menu

## The problem

Every in-game day the player makes the same coupled decisions:

- **Room assignment.** Who shares a room decides who can pair. With *N* units and *R* rooms there are *R^N* layouts.
- **Inheritance.** Each stat is inherited from one parent: the better value with probability (100 + s) / (200 + s),
  where *s* is the room's stimulation. A good pair is two units whose strengths complement each other, placed in a
  high-stimulation room.
- **Inbreeding.** Offspring of related units risk birth defects, rising steeply with kinship (75% for siblings), so
  relatives, including parents and their offspring, must never share a room.
- **Social constraints.** Rivals fight, aggressive units fight more in uncomfortable rooms, crowding past four units
  costs comfort, and bonded pairs pair far more reliably than strangers.
- **Roster turnover.** Units retire after one expedition, so every squad sent out is breeding stock not kept, and every
  new recruit or retirement changes the gene pool.

## How it works

The engine (`src/Core`) is plain C# with no game dependencies: deterministic, side-effect free, and tested on its own.
The game adapter (`src/Mod`) is the only layer that touches game memory.

**Room assignment: local search over a scored objective.** Starting from the current layout, the search applies
single-unit moves and pairwise swaps until no change improves the score, then repeats from seeded random restarts and
keeps the best result. A room's score is:

- the value of its **best set of disjoint pairs**, solved exactly with bitmask dynamic programming,
- minus penalties: rivals or related breedable pairs sharing a room (large enough to act as hard constraints),
  crowding, aggressive units together, split bonded pairs, weaker partners that compete for a strong unit, and a small
  cost per move so the plan changes as little as possible.

Plans are deterministic (same input, same plan) and stable (applying a plan and planning again yields no further moves).

**Pair value.** The expected offspring stat total under the inheritance model at the room's stimulation, plus bonuses
for 7s and for the roster's best values so rare genes stay in the pool, plus a bonded-pair bonus. Kinship comes from
the game's own pedigree data; defect risk follows the game's curves as mapped by the mod framework, and any pair above the configurable
risk ceiling (5% by default, which only allows unrelated units) is excluded.

**Infeasible constraints.** When a unit has a relative or rival in every room, no layout can satisfy it. The planner
reports the conflict and routes that unit to the next expedition or to retirement, instead of returning an unsafe layout.

**Roster decisions.**
- *Squad:* units that cannot be housed safely first, then fresh units with no breeding role, strongest first; breeders
  fill remaining slots in order of how little their room loses without them.
- *Recruits:* accepted when they add a stat value the roster lacks or form a top pair with an existing unit.
- *Retirements* (suggestions only): units past breeding use, weak offspring, and surplus units the squad does not need.
  Never a unit in a planned pair, in the squad, or the sole holder of the roster's best value for a stat.

## In-game integration

Everything lives in the mod menu (**F10**):

| Control | What it does |
|---|---|
| **Show plan** (or **F9**) | The full plan with reasons: layout, pairs, squad, notable offspring, recruits, retirements. |
| **Auto-arrange** | Applies the layout. Backs up the save first, once per in-game day. |
| **Stage squad** | Places the suggested squad in the expedition staging area, verifies each placement, and rolls back any that miss. |
| **Undo today's arranging** | Restores the layout from before the day's first auto-arrange, however many arranges followed. |
| Morning summary | After each day, a short note only when there is something to decide. |
| Risk ceiling | Highest birth-defect risk allowed in a pair. |

**Safety.** The mod checks the game build and stays idle on any build it was not written for. It never deletes or
retires units itself. Every action is reversible and logged. All game calls are marshalled onto the game thread.

## Testing

`tests/` runs the engine on fixtures exported from real saves and checks its invariants:

- no rivals or above-ceiling pairs share a room, unless the conflict is reported and the unit is given a way out
- no room exceeds capacity when the roster fits
- plans are deterministic and stable
- squads contain only eligible units, and retirement suggestions never touch pairs or the squad

CI runs them on every push. They need no game files:

```
dotnet run --project tests/Pedigree.Tests -- tests/fixtures
```

## Building the mod

On Windows, with the current .NET SDK, Visual Studio Build Tools ("Desktop development with C++", for the NativeAOT
linker) and the mod framework's SDK DLL in `sdk/`:

```powershell
.\build.ps1 -Install -GameDir "<game install folder>"
```

This runs the tests, compiles `out\Pedigree.dll` with NativeAOT and copies it into the game's `Mods` folder. The game
needs the framework's loader next to its executable.

## Layout

```
src/Core   engine: models, inheritance and risk model, room search, reports
src/Mod    game adapter: live state, menu, auto-arrange, staging, backups, build guard
tests      invariant tests on real-save fixtures
```

## License

MIT
