# AntTrails

Walk somewhere often enough and the ground remembers.

AntTrails watches where players actually walk and slowly wears the terrain down into dirt
paths — no hoe, no stamina, no materials. Routes that see heavy traffic over a long time
have a small chance of cobbling over into stone. Routes that fall out of use fade back
toward bare ground.

The result is a world that records how it has been lived in: desire lines between a base and
the nearest copper vein, a worn ring around the smelter, a road to the boat that got wider
the summer everyone was raiding.

## Who needs to install this

**Every player and the server, and this is enforced.** This writes to the shared terrain
paint mask, which is persisted in the world save and replicated to every peer. The server
keeps every wear counter and decides what each tile should look like; clients report where
they walked and paint the terrain they own, because on a dedicated server the server itself
has no terrain loaded near a player. Both halves of that loop need the mod, so a peer
without it is refused at connect rather than silently desyncing.

Config values that change behaviour are admin-only and pushed from the server, so everyone
plays by the same numbers.

## How it works

Terrain in Valheim carries a per-square-metre paint mask with separate dirt, cultivated and
paved channels. The hoe slams the dirt channel to full in a 2 m circle. AntTrails instead
nudges it upward a little at a time, so a path fades in rather than appearing:

| Traffic on one 1×1 m tile | What you see |
|---|---|
| A few crossings | Faint scuffing, grass still there |
| Halfway to the threshold | Grass clears; visible bare track |
| Threshold reached (`StepsToPath`, default 25) | Full dirt path |
| Very heavy lifetime use (`StoneSteps`, default 400) | Small per-crossing chance of stone |

Crossings from **all players pool together** — four people walking a route wear it four
times as fast.

Your position is sampled a few times a second and every tile on the line *between* two
samples is credited too, so a route sprinted at full tilt wears exactly as evenly as one
strolled. The trace follows where you actually put your feet, which means a route walked
loosely spreads its wear over a corridor a couple of metres wide rather than concentrating
it into one line.

### Losing a path

Progress on an *unfinished* tile leaks away continuously, at a flat
`StepsToPath ÷ FormationWindowDays` per in-game day — about two crossings a day at the
defaults, one every quarter hour of real time.

That makes formation a contest of rate rather than of total. A tile crossed less often than
it leaks never gains ground at all, however long you persist; a tile crossed twice that
often wears through in eight or nine in-game days. So the route between your base and the
copper vein you have been working all evening settles in, and the one-off detour you took
to a swamp last week leaves nothing behind.

A *finished* path fades far more slowly, taking `RevertDays` (default 30) of neglect to sink
to `ResidualTrace` (default 0.3) — a faint permanent scar, below the threshold where grass
regrows. Old routes stay legible forever unless you set `ResidualTrace` to 0.

Stone is terminal. It never decays. Remove it with a hoe or pickaxe like any other terrain.

### What it will not touch

- **Cultivated soil** — walking through a turnip field never paves it.
- **Existing stone**, whether a player laid it or AntTrails did.
- **Inside workbench and ward range**, while `RespectBuildPrivilege` is on (the default).
  Turn it off if you want trails to form through your settlement too.
- Anything you are swimming through, standing on a built floor over, or Ashlands lava.

Standing still costs nothing: a tile only counts when you *enter* it, so idling at a
workbench does not bore a hole in the ground.

## Configuration

`BepInEx/config/com.ragemedia.anttrails.cfg`. Gameplay values are admin-only and
server-synced; sampling rate and logging are local to each client.

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Master switch. Existing terrain is left as-is when off. |
| `StepsToPath` | `25` | Crossings to fully wear one tile. |
| `FormationWindowDays` | `12` | Days of neglect to lose all unfinished progress. |
| `RevertDays` | `30` | Days of neglect for a finished path to fade to the floor. |
| `ResidualTrace` | `0.3` | Permanent floor a faded path keeps. `0` lets paths vanish. |
| `StoneSteps` | `400` | Lifetime crossings before stone becomes possible. |
| `StoneChance` | `0.01` | Per-crossing chance of stone once eligible. |
| `RespectBuildPrivilege` | `true` | Skip tiles inside workbench/ward range. |
| `FlushIntervalSeconds` | `5` | How often paint changes are pushed to terrain. |
| `DecaySweepSeconds` | `60` | How often loaded tiles are re-checked for decay. |
| `SampleIntervalSeconds` | `0.25` | How often this client samples its own position. Local. |

### A note on the network

Valheim serialises a zone's *entire* terrain blob every time any part of it is painted, and
replicates that to all peers. Painting once per footstep would be ruinous, so AntTrails
accumulates changes and flushes them in batches, one message per zone per flush. Raising
`FlushIntervalSeconds` trades responsiveness for bandwidth; lowering it does the reverse.

## Wear data

Counters live in `BepInEx/config/AntTrails/<worldname>.trails` on the host, saved alongside
the world. Deleting it resets progress but does not touch terrain — paths already painted
stay painted.

## Building from source

Requires the .NET SDK and a Valheim install. The mod compiles against the game's own
assemblies plus the BepInEx and Jotunn DLLs from a mod-manager profile, so that what it is
built against is exactly what loads at runtime.

```bash
cp Environment.props.example Environment.props   # then edit the paths
dotnet build -c Release
```

The DLL lands in `bin/Release/AntTrails.dll`. Copy it into your profile's
`BepInEx/plugins/AntTrails/` to test.

`Environment.props` is machine-specific and deliberately untracked.

## Requirements

- [BepInExPack Valheim](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/)
- [Jotunn](https://valheim.thunderstore.io/package/ValheimModding/Jotunn/)

## License

[MIT](LICENSE).

## Source

<https://github.com/egaroib/Valheim-AntTrails>
