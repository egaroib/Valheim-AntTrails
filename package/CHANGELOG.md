# Changelog

## 1.2.2
- Changed the required Jotunn version to 2.30.2, matching servers on the latest Jotunn

## 1.2.1

### Arriving at a busy base no longer spawns a crowd of terrain compilers

Walking into someone else's base produced a burst of vanilla's "Found another terrain
compiler in this area, removing it" warnings -- a hundred of them in eight seconds on a
heavily built, heavily hoed base -- and lag to match.

Every zone is meant to hold exactly one terrain compiler. Vanilla's
`Heightmap.GetAndCreateTerrainCompiler` finds the existing one through
`TerrainComp.s_instances`, which a compiler joins in its own `Awake`. A zone's heightmap
is built the moment the zone spawns, but the compiler ZDO that goes with it is
instantiated by `ZNetScene` some frames later -- ten objects a frame outside a loading
screen. AntTrails resolved a compiler for every tile in an incoming paint batch through
that create-if-missing helper, so any tile resolved inside that window found nothing and
spawned a second, empty compiler: a replicated ZDO plus five arrays of `(m_width + 1)`
squared, including a 4225-entry colour mask. Vanilla then destroyed it when the real
compiler awoke, which is what the warning was announcing. Arriving somewhere with a large
backlog of pending tiles did this once per tile per heightmap.

The ownership check that was supposed to gate all of this ran nine lines too late -- the
object had already been created by the time the mod decided it had no business writing
there. Tiles are now resolved without creating anything, and a compiler is only made once
`ZNetScene` reports every object in the area already has an instance. Zones that have
genuinely never been terraformed -- where new trails form -- still get one made for them,
because there is no compiler ZDO for them to be waiting on. Tiles skipped in the meantime
are left unacked, stay the server's problem, and are offered again once the area settles.

This was most visible on bases with hand-laid hoe paths, which is a place with both a lot
of built objects streaming in and a large backlog of tiles the server wants painted. Those
tiles were then mostly no-ops at the paint stage, so the whole cost was being paid to
change nothing.

Resolution is also now cached per heightmap for the duration of a batch, instead of
rescanning every live compiler once per tile.

## 1.2.0

### Hoe paths are no longer erased by walking on them

Walking a path a player had laid with a hoe wiped it out, and the grass and foliage came
straight back — within a second or two, because the mod kicks the clutter system after
every paint.

Vanilla decides whether grass may grow on a tile with `Heightmap.IsCleared`, which reads
the paint mask's red channel as a hard threshold at `0.5` and ignores the vegetation alpha
entirely. A hoed path is red `1.0`. AntTrails tracked its own wear from zero and wrote the
result over that channel unconditionally, so a few crossings of a fresh route replaced the
player's `1.0` with something like `0.1` — under the threshold, so the tile stopped
counting as cleared and the grass system reclaimed it. Nothing about this was specific to
dedicated servers; it simply could not happen before 1.1.0, when servers painted nothing.

The server now tells each client what it last asked that tile for, so the client can tell
its own paint apart from anyone else's. Ground darker than that was darkened by someone
else — a hoe, or another mod — and is treated as a floor: AntTrails may darken a tile
further, never lighten one it did not darken. Decay is unaffected, because on the mod's
own trails the ground tracks what it last sent and no floor applies.

Player-laid paths now also take part in the simulation rather than fighting it. Crossings
were always counted on them; what changes is that the count now leads somewhere, so a hoed
road that genuinely gets used will eventually cobble itself to stone.

### Also

- A paint that would not change what is on the ground is skipped before it reaches
  `TerrainComp.Save`, so a tile held at a player's value no longer re-serializes the zone's
  terrain blob and resets the grass every time the wear model creeps past its epsilon.

### Upgrading

The paint message carries one extra value per tile, so server and clients must both be on
1.2.0. This is a minor bump rather than a patch on purpose: version strictness is set to
minor, which compares only major and minor, so a 1.1.x peer would have been let through the
check and then misread every paint batch.

Existing `.trails` files load unchanged — the save format is untouched, so wear counters,
formation progress and stone upgrades all carry over.

## 1.1.0

### Trails now form on dedicated servers

On a dedicated server trails were counted but never appeared. The server owns the wear
counters, and it was also trying to paint the terrain itself — which it cannot do. A
dedicated server has no local player, so `ZNet`'s reference position stays at the world
origin, and `ZoneSystem` only builds live zones around that point. Peers get ghost zones,
whose terrain objects are destroyed the moment they are generated. So the server had no
`Heightmap` and no `TerrainComp` anywhere near a player, found nothing to paint, and
deferred every tile silently, forever.

Painting has moved to the peer that actually has the zone loaded. The server still owns
every counter and decides what each tile should look like; it now offers that to the
clients near the tile, and each client paints the terrain it owns and reports back what it
applied. A tile is recorded as painted only when a client confirms it — previously the
server assumed a dispatch had succeeded, so a failed paint was indistinguishable from a
painted trail.

Host-and-play sessions are unaffected in behaviour; the host simply plays both roles.

### Decay now follows players, not the server

The decay sweep used `ZoneSystem.IsZoneLoaded`, which on a dedicated server describes the
zones at the world origin rather than the ones anyone is standing in. It now sweeps zones
near connected peers, which also catches a zone up after a player returns to it — work that
`TerrainComp.Awake` does for a host but never does on a dedicated server.

### Also

- The dirty-tile backlog is capped, so a server with nobody in range no longer accumulates
  tiles for the life of the session.
- Verbose logging now says when tiles are dirty but no peer can paint them, instead of
  going quiet — the exact condition that made the original bug invisible.

### Upgrading

The wire protocol changed, so server and clients must both be on 1.1.0. Version strictness
is set to minor, so a mismatched peer is refused at connect rather than misbehaving.
Existing `.trails` files load unchanged.

## 1.0.0

### Valheim 1.0 support

The 1.0 update renamed or reshaped three of the game APIs AntTrails depends on — the world
name it keys save data on, the zone identifier used to decide which tiles are loaded, and
the terrain rebuild call it makes after painting. Rebuilt against 1.0.7 (Unity 6). This
release does not run on 0.221.x, and 0.2.x does not run on 1.0.

**Existing trails survive the update.** The world name the game hands out is unchanged in
value despite the rename, so `BepInEx/config/AntTrails/<worldname>.trails` is found and
loaded exactly as before. Wear counters, formation progress and stone upgrades all carry
over.

### Dependencies

BepInExPack 5.4.2350 and Jotunn 2.30.0.

### Note for servers

The minor version is part of the mod's network compatibility check, so 0.2.x clients
cannot join a 1.0.0 server or vice versa. Everyone updates together.

## 0.2.0

### Retuned formation, so that paths actually form

The old defaults could not produce a path in ordinary play. Formation progress leaks at a
flat `StepsToPath / FormationWindowDays` per in-game day, which at 60 and 4 meant a tile
shed a crossing every two real minutes. Unless you re-crossed the same square metre more
often than that, its progress sat at zero forever, however many hours you spent on the
route — a five-minute round trip to a mine never stood a chance. Formation was a contest of
rate, and the rate was set past what walking anywhere can produce.

| Setting | Old | New |
|---|---|---|
| `StepsToPath` | `60` | `25` |
| `FormationWindowDays` | `4` | `12` |
| `StoneSteps` | `1200` | `400` |
| `StoneChance` | `0.004` | `0.01` |

Break-even is now one crossing every quarter hour of real time rather than every two
minutes, and a route you keep using wears through over a few hours of play. Stone needs
roughly 500 lifetime crossings on a tile instead of about 1450, which no tile was ever
going to reach — the busiest ground in any base sits inside a workbench radius the mod
deliberately skips.

**Updating will not change settings you already have.** BepInEx writes the config file the
first time the mod runs and never overwrites it afterwards, so an existing
`BepInEx/config/com.ragemedia.anttrails.cfg` keeps the old values. To take up the new
tuning, either edit those four keys to the values in the table or delete the file and let
it regenerate. On a dedicated server, edit the *server's* copy: these keys are admin-only
and pushed out to clients.

### Wear is now traced between position samples

The client sampled its position four times a second and credited only the tile underfoot.
At a run that is more than a metre of travel per sample, so consecutive credited tiles were
not even adjacent: crossings went missing and trails came out dotted rather than
continuous. Each sample now credits every tile on the line back to the previous sample, so
a route wears the same whether you stroll it or sprint it. Eligibility — farmland, lava,
build privilege — is checked at each tile's centre, where the wear actually lands.

### Fixed

- The fallback day length used when the game's own value is unavailable was 1200 seconds
  against an actual 1800, running decay 50% fast for as long as it applied.

### Note for servers

The minor version is part of the mod's network compatibility check, so 0.1.x clients cannot
join a 0.2.0 server or vice versa. Everyone updates together.

## 0.1.2

- Added an MIT license.
- Set `website_url` in the manifest so the Thunderstore listing links to the source repo.

No functional or behavioural changes; the plugin itself is identical to 0.1.1.

## 0.1.1

- Widened the allowed range on `StepsToPath`, `RevertDays`, and `StoneSteps`. The previous
  minimums (5, 1 day, and 50 respectively) were arbitrary and sat above the values needed to
  observe path formation, decay, and the stone upgrade inside a single play session.

## 0.1.0
- Initial release.
