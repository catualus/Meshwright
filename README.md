# Meshwright

**Offline navigation mesh generation for Source engine maps.**

[![CI](https://github.com/catualus/NavPal/actions/workflows/ci.yml/badge.svg)](https://github.com/catualus/NavPal/actions/workflows/ci.yml)

Meshwright builds `.nav` files from a compiled `.bsp` without launching the game. It replaces typing
`nav_generate` at the console: it runs as an ordinary program, uses every core on the machine, fits
into a build script, and tells you what it did.

It works as a [Compile Pal](https://github.com/ruarai/CompilePal) plugin or as a standalone
command-line tool.

---

## Contents

- [Why not `nav_generate`?](#why-not-nav_generate)
- [Installation](#installation)
- [Usage](#usage)
- [Options](#options)
- [Diagnostics](#diagnostics)
- [Results](#results)
- [Limitations](#limitations)
- [Building from source](#building-from-source)
- [Licence](#licence)

---

## Why not `nav_generate`?

The engine's generator works, and for many maps it is enough. These are the problems it does not
solve.

**It only runs inside the game.** You have to load the map to generate a mesh for it, which makes it
awkward to script and impossible on a build machine with no game installed.

**It is single-threaded and blocks the client.** On a large map you watch a frozen game and cannot
cancel.

**It builds no ladders outside Left 4 Dead.** `CNavMesh::BuildLadders` is compiled out elsewhere, and
even where it runs it only looks for `func_simpleladder` entities. Ordinary brush ladders are
invisible to it, so bots cannot use them.

**It ignores lifts.** Elevator platforms get no connections, so a working lift is a dead end.

**It cannot explain itself.** When a mesh comes out wrong you get a mesh and nothing else. Working out
why a staircase was missed means looking around in game and guessing.

Meshwright addresses all five.

| | `nav_generate` | Meshwright |
|---|---|---|
| Runs from a `.bsp` alone | No | Yes |
| Uses multiple cores | No | Yes |
| Scriptable / CI-friendly | No | Yes |
| Brush ladders | Left 4 Dead only | Yes |
| Lift and elevator stops | No | Yes |
| Static prop collision | Yes | Yes |
| Visibility (`nav_analyze`) | Yes, in game | Yes, offline |
| Hiding spots | Yes | Yes |
| Sniper spots and encounters | Only with `nav_quicksave 0` | Always |
| Diagnostics | No | Yes |

---

## Installation

### As a Compile Pal step

1. Download `Meshwright-plugin.zip` from the releases page, or build it with `./build-plugin.ps1 -Zip`.
2. Extract the `Meshwright` folder into Compile Pal's `Plugins` directory.
3. Restart Compile Pal, press **+** on the process list, and add **Meshwright**.

Nothing is compiled into Compile Pal and there is no fork to run. The step exists because the folder
exists, and disappears if you delete it. It runs at order 8.5, after Compile Pal's own **NAV** step
and after the BSP has been copied to your maps folder, so it operates on the map the game will load.
Every option below has a corresponding checkbox.

### Standalone

Download `meshwright.exe` from the releases page, or build it yourself. See
[Building from source](#building-from-source).

### On Linux

The release is a Windows executable and runs under Wine, alongside Compile Pal and the Source
compile tools. Tested with Wine 9.0: `generate` builds, writes and reloads a mesh, both standalone
and as a Compile Pal step.

---

## Usage

### The whole build

```bash
meshwright generate map.bsp
```

This finishes the `.nav` beside the BSP and writes it back in place: ladders, movement connections,
cover spots and visibility.

To also find walkable ground the mesh is missing, or to build a mesh where there is none:

```bash
meshwright generate map.bsp -generateareas
```

Run again over its own output, `generate` keeps what the last run made instead of rebuilding it,
and does not check it against the map again. If the map's geometry has changed since, add `-scratch`
to rebuild the mesh. So that this is hard to miss, Meshwright remembers each mesh it writes, as a
hash of the file in `%LOCALAPPDATA%\Meshwright\written`, and warns when a run starts from one nobody
has touched since. A mesh edited in game is not warned about.

### A whole map pack

```bash
meshwright batch maps/ -generateareas
```

Runs the same build over every `.bsp` in a directory, or over the files and patterns you name,
finishing each `.nav` in place. Every option below applies. One map failing does not stop the rest:
failures are reported as they happen, listed again at the end, and set a non-zero exit code.

### One pass at a time

Each staged command writes a new file rather than editing in place, so you can inspect any stage and
step back. Run them in this order, because later passes read what earlier ones wrote.

```bash
meshwright build-areas      map.bsp map.nav          -o map.areas.nav
meshwright build-movement   map.bsp map.areas.nav    -o map.movement.nav
meshwright build-ladders    map.bsp map.movement.nav -o map.ladders.nav
meshwright build-spots      map.bsp map.ladders.nav  -o map.spots.nav
meshwright build-visibility map.bsp map.spots.nav    -o map.final.nav
```

`build-spots` only matters for games whose bots use cover positions, and needs `build-movement` to
have run first. If you have no mesh at all, add `-scratch` to `build-areas` to generate one from the
map's player spawns.

`generate` and the staged commands drive the same pipeline, so both produce the same mesh.

### Checking a mesh

```bash
meshwright info map.nav
```

```bash
meshwright verify map.nav
```

`info` summarises a mesh and confirms every id in it resolves. `verify` reads and rewrites it, then
diffs byte for byte.

---

## Options

Available on `generate`; each has a Compile Pal checkbox.

| Option | Effect |
|---|---|
| `-generateareas` | Find walkable ground the mesh is missing and add it |
| `-scratch` | Discard the existing mesh first. Requires `-generateareas` |
| `-noladders` | Skip nav ladders for the map's ladder brushes |
| `-nomovement` | Skip stair marking and step, jump, drop and lift connections |
| `-nospots` | Skip hiding spots, sniper grading and encounter spots |
| `-nosnipers` | Find cover spots but do not grade sniper positions |
| `-noencounters` | Find cover spots but do not build encounter spots |
| `-novisibility` | Skip area-to-area visibility, by far the slowest stage |
| `-nocompress` | Store full visibility instead of Valve's delta encoding |
| `-maxviewdistance N` | How far two areas can see each other. Default 6000 |
| `-pruneunreachable` | Delete small groups of areas no player spawn can reach |
| `-resume` | Cache the mesh between runs and reuse it when only later settings changed |
| `-o <path>` | Write somewhere other than in place |

These apply to every command, not just `generate`:

| Option | Effect |
|---|---|
| `-threads N` | Cap parallel work. Default is every core |
| `-game NAME` | Movement limits: `cs`/`css`/`csgo`, or `gmod`/`hl2`/`tf2`/`source` (default). `tf2`, or a Compile Pal game called Team Fortress 2, also makes player clip solid, as it is to TF2's bots, and floods only from `info_player_teamspawn`, the one class TF2 spawns players at |
| `-content DIR` | Extra directory to resolve prop models from. May be repeated |

`-content` matters more than it looks. Meshwright works out where the game's content lives from the
map's own path: a `.bsp` sits in `<mod>/maps`, so the mod directory is two levels up. That covers the
normal case and nothing else: a BSP in a build server's output directory infers a mod directory that
does not exist, so no model resolves, no prop is collided against, and the mesh floats over every one
of them. The run still succeeds, with a warning naming the models it could not find. Point `-content`
at the mod directory and it resolves normally.

You only need to name the mod directory, not each game it inherits from. Meshwright reads the mod's
`gameinfo.txt` and mounts what its `SearchPaths` block declares, the same list, in the same order,
that the engine uses, so a mod inheriting from a base game gets that game's content too.

```bash
meshwright generate out/map.bsp -content "C:/steamapps/common/GarrysMod/garrysmod"
```

`-resume` keeps the mesh as it stands after the movement passes in `<map>.bsp.mwresume`, and reuses it
next time. That covers about a third of a run: area generation, ladders, connections, clipping,
stairs and lifts. Visibility is the other two thirds and is always recomputed, so this makes tuning
`-maxviewdistance` or adding visibility to a `-novisibility` run cheaper; it does not make a repeat
build instant.

It rebuilds whenever anything upstream could have changed the mesh: the map, the `.nav` it started
from, the options above that feed those passes, `-game`, `-content`, or Meshwright itself. Every doubt
rebuilds, and the run says which input moved. The staged `build-*` commands do the same job explicitly
if you would rather control it yourself.

`-game` is not cosmetic. Counter-Strike uses a 58-unit crouch jump, a 200-unit survivable drop and a
58-unit climb; everything else uses 64, 400 and 200. The same map generates a different mesh under
each, because a ledge 62 units up stops being climbable. Nothing in the BSP says which is right, so
you have to tell it.

---

## Diagnostics

`nav_generate` has no equivalent for these. Each answers one specific question about a mesh you are
unhappy with.

| Command | Answers |
|---|---|
| `meshwright fit <bsp> <nav>` | Which areas float above the ground or hang over air, and by how much |
| `meshwright shape <bsp> <nav>` | Which areas have grown into walls |
| `meshwright reachable <bsp> <nav>` | What a bot can walk to from a spawn, and what is stranded where |
| `meshwright compare-areas <ref.nav> <nav>` | How much of a known-good mesh this one covers |
| `meshwright compare-areas ... -reachable <bsp>` | The same, counting only reference ground a player can actually walk to |
| `meshwright stairs <bsp> <nav> -at x y z` | Why one area was or was not marked as stairs |
| `meshwright reach <bsp> x y z` | Why the generator never reached a spot it should have |
| `meshwright floors <bsp> x y` | Every surface found in one vertical column |
| `meshwright probe <bsp> x1 y1 z1 x2 y2 z2` | What blocks one straight line |
| `meshwright props <bsp>` | Every static prop, which models resolved, how much collision was found |
| `meshwright disp <bsp>` | What every displacement reconstructed to |
| `meshwright vis-compare <bsp> <analysed.nav>` | How computed visibility scores against an analysed mesh |

If anything rewrites the BSP after the mesh is built, re-stamp it as the last step of the build:

```bash
meshwright stamp map.bsp
```

A mesh records the size of the BSP it was built for, and the engine prints
`Warning! .nav file is out of date!` when that no longer matches the map it is loading. Packing
content into the BSP, repacking it, or moving its entity lump out all change that size, and all of
them run after the mesh is built, because a mesh has to be built while the entities are still there
to read. The mesh is not stale in that case, only the stamp is. This rewrites the one field and
nothing else.

Compile Pal does this for you: it knows which of its own steps rewrote the BSP, so it re-stamps any
mesh beside the map once the compile has finished. The command is here for every other pipeline.

Run `meshwright` with no arguments for the full list.

---

## Results

Measured in November 2026. Every mesh here was generated from scratch (`-generateareas -scratch`)
with the passes Compile Pal runs by default - areas, ladders, movement, hiding spots, sniper grading,
encounter spots and visibility - at `-threads 8` on an 8-core Ryzen 7 6800H (16 threads), and scored
against a mesh made some other way for the same map.

`gm_construct` ships with Garry's Mod, and its reference is the mesh the game's own `nav_generate`
makes. `rp_downtown_tits_v25` is a large roleplay map; its reference is the `.nav` that ships with it,
19,577 areas, which is not a clean engine mesh - 13.9% of its areas float above the floor. The eight
Team Fortress 2 maps further down are scored against the meshes Valve ships with them.

### Where the mesh sits

The measure that shows up first in game is whether an area is on the ground. `meshwright fit` traces
down from 25 points on every area and reports the gap.

| | gm_construct | | rp_downtown_tits_v25 | |
|---|---|---|---|---|
| | engine | Meshwright | shipped | Meshwright |
| Areas | 2,271 | 2,631 | 19,577 | 17,331 |
| Connections | 11,446 | 14,146 | 91,538 | 81,660 |
| Height error, mean | 1.0 | 1.0 | 10.3 | **0.6** |
| Height error, median | 0.3 | **0.2** | 0.1 | **0.0** |
| Areas floating above the floor | 1 | **0** | 2,690 (13.7%) | **0** |
| Areas over open air | 3 | **0** | 210 (1.1%) | **0** |

On gm_construct the two are level. On the large map they are not: the shipped mesh leaves 2,690 areas
above the floor and 210 over nothing.

### What it covers

`meshwright compare-areas` checks every area of the reference against the generated mesh, and
`-reachable <map.bsp>` leaves out the reference ground its own connections cannot reach from a spawn.
The spawn and reachability rows count player spawns with a generated area within 80 units under them,
and the areas the run's own reachability check cannot reach from any spawn.

| | gm_construct | rp_downtown_tits_v25 |
|---|---|---|
| Coverage of the reference's ground a player can reach | **98.9%** | 67.5% |
| Coverage of every reference area including stranded | 89.2% | 67.6% |
| Areas on ground the reference does not have | 2.4% | 9.4% |
| Player spawns with an area under them | **33 of 33** | 56 of 96 |
| Areas no player spawn can reach | 3 (0.1%) | 40 (0.2%) |

Score against reachable ground rather than against every area, because a reference mesh contains
ground nothing can path to: gm_construct's engine mesh strands 224 of its 2,271 areas, which is what
drops the second row to 89.2%.

67.5% on the large map is mostly the reference's doing. It looks like the output of an earlier
Meshwright, whose flood walked through thin walls and terrain. Of the 6,135 reachable shipped areas
the generated mesh does not cover, 5,211 are on real ground, and 4,301 of those are under water -
most of them the sea and lake bed under the displacement islands in the south-east of the map.
Most of the rest lie under terrain and props in the
park and hills in the middle of the map. Walked from the spawns in Garry's Mod with the game's own
player collision - a crouched hull, 18 unit steps, 64 unit jumps, no ladders - 26 of the 5,211 can be
reached, against 88% of the ground both meshes have; another 252 are within half a sampling step of a
generated area and miss only on where the score samples them. `build-areas -reference
<known-good.nav>` breaks misses down by the sampler's own reasons.

The large map's 40 spawns without an area stand 105 units above the floor with nothing under them. The
ground below them is meshed, and the shipped mesh has an area under the same 56. gm_construct's 3
unreachable areas are the pit under its ladder: the ladder has its foot, but the passage at its top is
50 units high, lower than the 55 Meshwright needs to call it crouching room, so the top has no area.

How often a bot gets where it is going is the other half. 2,000 pairs of points are picked at random,
weighted by area, on the reference's ground that a spawn can reach and that lies on a real floor, and
a pair counts when the generated mesh finds a path between the areas under the two points, out of the
pairs the reference itself can route.

| | Reference | Routes found |
|---|---|---|
| gm_construct | engine | **99.8%** |
| ctf_2fort | Valve | **100.0%** |
| cp_dustbowl | Valve | 99.2% |
| pl_badwater | Valve | 99.8% |
| cp_granary | Valve | **100.0%** |
| koth_viaduct | Valve | **100.0%** |
| pl_upward | Valve | 99.7% |
| cp_badlands | Valve | 99.8% |
| ctf_turbine | Valve | **100.0%** |
| Team Fortress 2, mean | | 99.8% |

ctf_2fort and cp_dustbowl found 43.6% and 45.3% until clip brushes counted as ground. TF2 lays a
clip ramp over its stairs and a clip slab over some sunken floors, and the mesh had treated those as
neither floor nor open air, so nothing was built on them. The same maps also need a toggle brush that
is switched on at the start - cp_dustbowl's stairs to the first stage's yard, pl_badwater's cover over
the final pit - to be a floor, and, for TF2 only, player clip to be solid, as it is to TF2's bots.
That last one also seals the map edges the flood used to walk out through: on koth_viaduct 83.5% of
the generated areas were on ground Valve's mesh does not have, and now 27.8% are, and on cp_badlands
69.2% now 25.2%. Coverage of the ground Valve's meshes can reach is 95.9% to 98.9% on every TF2 map.

Crouch areas follow the engine's own heights: room under 71 units is crouching room, under 55 is
none, on TF2 as everywhere else. TF2's player is taller - 82 standing, 62 crouched - but Valve's
meshes do not use those figures: of the 68 reachable areas in them whose lowest headroom is 71 to 82,
63 are not marked crouch, and two thirds of the 33 that are marked have somewhere under 71. What the
generated TF2 meshes used to have was mostly noise. Of 239 crouch areas on the eight maps, 229 were off
Valve's ground: 148 filled the sealed space under cp_granary's terrain, flooded from
`info_player_start`s left on its floor, which TF2 never spawns anyone at, and 26 were too narrow for
a crouching body anywhere in them, since headroom was a single line. With teamspawns as the only TF2
seeds and a 32 unit wide body swept for crouch-only ground, 51 remain, 45 of them off Valve's ground,
most of them small pockets a crouching body does fit, several under props. Routes are unchanged and
coverage moves by a tenth of a point at most.

A shared edge narrower than a body used to be crossed only at its middle. Crossings away from the
middle were tried with the whole body over the shared span, a body's half-width in from each end, so
a span under 32 units left nothing else to try - and that is the usual case. Measured with hull
sweeps along every unlinked shared edge of the twelve benchmark meshes, 3,937 crossings could be made
only away from the middle, and 93% of those edges were narrower than a body. A ledge 25 units deep under an
overhang is one: a crouching body fits on it only at the lip, half of it over the drop. gm_flatgrass
has such ledges round the walls of its pit, and 22 of their areas were islands. The body's centre may
now be anywhere on the span, and where half of it hangs past the end a body is swept across as well as
the clearance lines. A body that starts that sweep inside a wall is refused, though the sweep itself
reports no hit when it only leads out of the wall: taken as clear, that linked 1,089 crossings out of
slivers against walls and in corners, nearly all one-way and most from areas no body fits in at all.
gm_flatgrass has no island left; across the twelve maps the areas with no link at all fell from 830
to 700 and the crossings left unlinked away from the middle from 3,937 to 1,582. Connections rise by
1% to 7%, routes and coverage hold or improve slightly, and the areas no spawn can reach fall on every
map but gm_construct, which stays at 3.

A crossing over ground sloping across it was refused at its middle. The clearance lines are level,
the lowest eight units above the floor under the middle of the crossing, but a walker on a slope
rests on its uphill side, and on terrain sloping 30 to 45 degrees across the crossing the outer line
on that side runs into the ground. Of the shared edges a hull sweep crosses at the middle that the
builder refused on the finished rp_downtown_tits_v25 mesh, that was 2,698 of 3,585 - nearly all over
displacement terrain, which RP maps are built on. Each outer line is now measured from the floor under
it when that floor is higher by no more than a step. Drops off a ledge whose floor runs on past its
area were refused as well, another 330 there: the fall was checked from two units past the area,
which put the column inside the ledge, and connections are made before areas are clipped back out of
walls, so the area below can still reach under the lip and its landing point be inside the ledge too.
The fall is now checked from where the floor ends, landing past that if the area below reaches up to
16 units further. gm_flatgrass's pit rim now drops into the pit. Across the twelve maps the areas with
no link at all fell from 700 to 476, and on the two RP maps the refused middle crossings from 3,585 to
456 and from 3,561 to 486, and the areas no spawn can reach from 321 to 153 and from 584 to 413.
Connections rise by 0.5% to 4.4%, routes and coverage hold, and unreachable areas fall on every map
but gm_construct and gm_flatgrass, which do not change.

The same "starts inside something, so reports no hit" flaw sat in the other body sweeps. A step
between two samples, and the diagonal links and patch areas added at corners, could start inside a
post and pass through it: a patch area was built inside a post on both RP maps and inside a wall on
koth_viaduct, and links ran through pillars on cp_granary and cp_badlands. A crouching body could fit by starting half inside a
crate. Those are refused now. A sample lying exactly on a wall face, which the sweep box also counts
as inside, still steps away from the wall: across the twelve maps that was 235 of the 287 start-solid
steps in the flood. Seven of the twelve meshes come out byte-identical; on the other five a few links
and one or two areas go, a handful of areas lose their last link, and routes and coverage do not move.

Connections are made before areas are clipped back out of walls, so a pair the clip reshaped kept the
answer it got on its old shapes. The pairs it reshaped are now tested again straight after it, adding
links and never removing any, with a body swept through the middle of a span narrower than one: a strip
pulled back to a wall can otherwise pass on clearance lines that graze the wall face. Of the adjacent
pairs the builder would link in the finished meshes, 28,941 of 29,684 are shortcuts the fixup removes on
purpose; the pass adds 105 links across the twelve maps, 82 after the fixup, flagged by a hull checker
about as often as the build's own. Routes and coverage do not move, and the pass takes under a second.

The links the build made on the old shapes are now tested again too. Failing the builder's test on the
new shapes is not enough to go: 10,559 of the 85,484 on a reshaped area fail it, but a crouching body
tried along the span at several depths crosses about 8,000, mostly gaps the clip opened over floor that
carries on, and about 2,000 lead to a spot no body stands in, such as a crate top used as a step. The
505 left are removed: drops through the floor over the landing, crossings through a wall the clip
exposed. A hull checker flags 84% of them, against 4% of the links kept, and where Valve's meshes cover
both ends they leave 28 of 58 unjoined, against 22 of 4,990. Routes and coverage do not move.

The clip also pulled areas back off floor that carries on. Its floor test takes the shortest of five
probe lines, and the outermost line runs a tenth of the area's width in from the edge beside it, so where
that edge overhung a ledge the line ran over the drop and the whole edge came back a step - usually
before the edge that did overhang had been clipped, and once a pass where it overhung by more than a
step. A line with no floor where it starts did the same, though pulling the edge back cannot help it.
Judged by a crouching body fitting at the area's own height and, on the ten maps that have one, the
reference covering it there, 859 passes removed a strip that was mostly such floor, and one of those
lines decided 700 of them. Another 71 were terrain the floor search missed: it walks down in four-unit
segments and a displacement counts only strictly inside one, so terrain lying on a boundary read as no
floor at all, and on cp_badlands that took an area back 70 units over a slope Valve's mesh covers, a
step a pass. Those lines now wait until the clip has stopped without them, and the search no longer
misses such terrain (see below). Edges of kept areas pulled back 16 units or more over such floor fall
from 1,211 to 303 across the twelve maps, and from 106 to 17 on the ten with a reference. Coverage rises
by 0.4 to 0.7 points on five maps, pl_upward's routes from 99.1% to 99.7%, and the old links on reshaped
areas that the builder's test now refuses fall from 10,491 to 9,187, the ones removed from 505 to 497.

On the TF2 maps it set eight areas floating and stopped two, none of them because the clip left too
much. Three were corner patches - the small squares built where two areas touch only at a corner -
built without asking whether there was ground under them: two hung off a platform on koth_viaduct and
one sat flat on a ramp on pl_upward. A patch is now built only over floor. Three were slivers kept as
the only route between two areas. The clip cannot pull a sliver in closer than four units to its
middle, so one eight or nine units wide keeps whatever overhang it has, and the spare test only looked
for floor at the quarters. A spared sliver is now pulled back to its floor first, and not kept when half
of it has none; refusing every sliver with any floor missing stranded 42 areas on rp_downtown_tits.
Floating areas on the TF2 maps fall from 32 to 7 (26 before the clip fix), and on the roleplay maps
from 48 and 49 to 4 each. Routes and coverage do not move. The one island added is a koth_viaduct area
no body fits in, joined before only through the two hanging patches. What still floated sat over cracks
a few units wide, which a body walks across; the clip no longer stops at those (see below).

Terrain on a segment boundary was missed everywhere the floor search runs, not only in the clip, which
had the only workaround. A displacement or prop triangle counted as hit only strictly inside a segment,
so terrain exactly where one four-unit segment ended and the next began was inside neither, and the
column read as having no floor. A segment now owns its far end, and the search's segments overlap by a
thirty-second of a unit, because on steep terrain far from the origin a hit on a boundary is only placed
to a few millionths of a segment and can still fall just outside both: a sample on rp_downtown_tits read
as over open air that way. The clip's workaround is gone. The meshes barely move: no area or island is
added or lost on any of the twelve maps, four come out byte-identical, the rest gain or lose at most two
links net, and routes and coverage do not change. What changes is `meshwright fit`, whose floor search
this is: it no longer reports terrain under an area as missing, and its counts fall to those of a check
that tries every column from four heights. Floating areas on the TF2 maps go from 29 to 6 - 13 to 3 on
cp_badlands, 12 to 1 on pl_upward - and the one area over open air on each of pl_upward and
rp_downtown_tits goes.

The clip read a crack as a ledge as well. Its floor test looks at four points along each of five probe
lines, and a crack between two brushes a few units wide often lies under one: running along the line it
took the line's whole reach, crossing it the reach beyond, and the edge came back to the crack. Two
areas on cp_badlands, 169 units by 100 of level floor, came out 9 units deep, and pl_upward's bridge
planks, a few units apart over the valley, cut areas short at every gap. A point with no floor is now
over a crack when floor within a step of the area's surface resumes on both sides of it, along either
axis, with the gap between no wider than 16 units - half a body, which a body centred over it still
straddles - and that floor carries on for two units past the gap's edge, so a rail down a trench is not
its far side. Floor behind a point and none in front of it is still a ledge however near, and a slot
between a floor and a wall is not a crack. `meshwright fit` asks the same of a sample it would otherwise
call floating or over open air, and says how many it let off.

On the twelve maps the clip discards 68 fewer slivers, coverage rises on seven maps by up to 0.7 points,
pl_upward's from 95.9% to 96.6%, and routes rise by 0.1 points on cp_dustbowl and pl_upward. Nothing
falls, and no area a spawn reached before is cut off. Sampled every two units over every area, the
footprint over a crack rises from 10,500 to 72,300 square units, and the footprint with no floor that is
not a crack from 23,200 to 23,700, 2% - mostly where two cracks meet, which neither axis sees across. By
the old fit floating areas rise from 36 to 268, and over open air from 2 to 5, nearly all areas that now
run across a crack one of their samples lands in; by the new one no area floats or hangs over air on any
of the twelve maps, before or after. Four new islands are pockets against a diagonal wall, two each on
cp_badlands and pl_upward, that no crouching body fits in anywhere and the build could not link: a
hairline seam under a probe line used to pare them down to slivers, which were discarded. cp_badlands
has two more areas no spawn reaches for it, 49; pl_upward and cp_dustbowl have three and one fewer.

A crossing between two areas was never tried with the body centred on the very end of their shared
span, half of it past the end over the floor of whatever runs alongside both - the way a body gets round
a corner. Every crossing stopped at least a unit short of the end, and at a corner that unit is the
whole difference: two strips the clip pulls back against a wall, each narrower than a body, are crossed
by a body standing at their open side, and a unit further along its far side is in the wall. Of the
39,889 adjacent pairs the builder refused on the twelve finished meshes, a hull checker gets a body
across 956 only with it centred on an end of the span, 737 of them level. The two ends are now tried
last, from six units and from a unit inside each area. A level crossing is linked there where every
probe under the overhanging half finds floor within a step, a crouching body stands at both ends, and a
body swept across is clear from start to finish. The stale-link pass tries them too, and removes a link
the build made only at a span end if the clip leaves no body standing at both ends of any crossing.

The two corner crossings on ctf_2fort an earlier audit found that pass removing were not this. Each is
a respawn-room doorway with a thin `func_brush` panel between the two areas, and a body gets round it
only with its middle 12 units past the span's end. The hull sweep said otherwise because it gathered
brush entities by the line through the box's origin, so a panel the box ran into beside that line was
never tested; it gathers them by the swept box now. On its own that leaves nine of the twelve meshes
byte-identical, takes four links on cp_granary that the build made through a `func_brush`, and on the
two RP maps re-partitions one room beside a wall button.

Connections rise by up to 0.7% on eleven maps; gm_flatgrass comes out byte-identical. Routes rise
from 99.85% to 99.95% on cp_badlands and hold elsewhere, coverage, floating and over-air areas do not
move, islands fall on seven maps (cp_badlands 46 to 41) and areas no spawn reaches on seven (cp_badlands
49 to 44), and no area a spawn reached before is cut off. A hull checker that also tries a span's exact
end flags 2 of the 905 new level links, against 1.2% of the links kept.

Drops and climbs are now tried at a span's end as well. Tried with the level guards alone, they added
84 drops and 3 climbs, and a hull checker flagged the drops at the rate it flags the drops kept, among
them a shape no other test saw: on pl_upward a slope ends in a lip that overhangs the floor 33 units
below by 12, and the drop was linked at the corner, where a body landing beside it is in the lip. A drop
or a climb at a corner now needs floor under the overhanging half beside its higher end only, a
crouching body standing at its foot beside the corner as well as at the top - its foot also tried 17
units into the lower area, clear of the face rising from the shared edge - and a body swept straight
up at the foot from where it rests to the height it crosses at, the column it falls down or jumps up.
The climb has to be in reach on the floor as well as on the areas, and the fall and its lip are tested
as for any other drop. The stale-link pass keeps a link a body gets across this way, and still removes
a link the build made at a span end when the clip leaves no room; a body standing at both ends of a
drop or climb there does not count as standing, as counted it removed 21 links on four maps that a hull
checker gets a body across nearly all of. The finished meshes gain 47 links, 30 drops and 16 climbs the
checker can test, and lose none; pl_upward's drop off the slope is not among them, and the one beside it
that lands clear of the lip is. The checker flags 4 of the drops and 5 of the climbs, against 30% and
32% of those kept, and 4 of the 9 are one 40 unit drop and its climb back, the same on both RP maps,
whose landing clears a wall's face by a unit the checker's insets step over. Areas, routes, coverage,
floating and over-air areas, islands and areas no spawn reaches do not move on any map, and ctf_2fort,
ctf_turbine, gm_construct and gm_flatgrass come out byte-identical. Building and re-testing links takes
about half a second longer on pl_upward, 3% of its run.

The swept-box gather also brought back 24 drops and climbs on the two RP maps, every one through a pane
of glass. Before the clip, a window sill's area ran past the glass, so the build linked it to the street
outside across a crossing that started beyond the pane; the clip pulls the sill back behind the glass,
and the stale-link pass then tests the link again. It leaves a link alone when no body stands at both
ends of any crossing, as the ends being too tight is not the crossing's fault. A sill 10 to 18 units
deep is too tight, and once the sweep saw the glass beside a body no body stood there, so the links
stayed. They now go when the crossing itself is shut to a walker of no width wherever it is tried: a
line through its middle runs into something, or it drops onto ground under the floor it leaves. Across
the twelve maps the pass removes 180 more links, and the finished meshes lose 175, 148 of them on the RP
maps, and gain 5 that the shortcut fixup pruned before; ctf_2fort, ctf_turbine and gm_flatgrass come out
byte-identical. A hull checker flags 159 of the 169 it can test, against 31% of the drops and climbs
kept, and Valve's TF2 meshes have ground at both ends of none of the 23 removed there. Routes, coverage,
floating and over-air areas do not move. Ten areas lose their last way in, every one through a removed
link: a sliver or slot no body fits in on each RP map and pl_badwater, and seven areas on cp_badlands in
two pockets under the bridge deck that Valve's mesh does not cover, reached before only by a 256 unit
drop through the deck and a link through a wall.

Areas cut off from everything that no body fits in are removed. The flood gives a cell to any floor a
column holds, so a slot between props, a pocket against a diagonal wall or a crevice between rocks
becomes an area, and once the movement passes stopped linking such areas through solid they were left
with no link at all, counted as islands and as ground no spawn reaches. After the corner patcher and
the shortcut fixup, a generated area that has no connection or ladder to anything else, alone or in a
group of up to four joined only to each other, goes when a crouching body stands nowhere on it - the
connection builder's own test, on a four-unit grid a unit inside its edges. 13 areas on cp_badlands and
pl_upward had room only with the body's centre exactly on their boundary, and none a unit in. An area a
body fits in is kept and reported, as ground the movement pass failed to link: 83 across the twelve
maps, 32 and 31 of them on the RP maps. So is anything near a player spawn, a ladder's top or foot or a
lift stop, the area a spawn resolves to, and any area a ladder names: 13 more. 380 areas go, from none
on ctf_2fort, gm_construct and gm_flatgrass to 93 on rp_downtown_tits_v25, and with them 6 links, each
between two of them. Islands fall from 468 to 91 and areas no spawn reaches from 1,925 to 1,545, on
every map but those three; no area a spawn reached is lost, and spawn coverage, ladders, floating and
over-air areas do not move. Routes rise from 99.95% to 100% on cp_badlands, where one end of a pair
was on a removed pocket, and hold elsewhere. Coverage falls by one reference area each on koth_viaduct
and rp_downtown_tits_v25, 98.4% to 98.3% and 67.6% to 67.5%: the reference area's centre lay on a
removed pocket, and no body fits at that centre in the reference either. The pass takes under a second
on the largest map. Still in place are pockets with a link out and none in, like a 32 by 10 area on
pl_badwater, and two groups under cp_badlands' bridge deck that a body does fit in.

Each cut-off group kept for having room was then traced to a cause. Of the 62 on the twelve maps, 12
were one floor sampled twice. The flood files a cell under its column and its height in eight-unit
bands, and the sewers of both RP maps are terrain laid 1.3 units over its brush face, at z=-195.7 over
z=-197, either side of a band edge: both became cells, and areas lay one over the other - 365 and 381
pairs on the RP maps, up to 12 on the TF2 maps - with a copy nothing linked to kept as a group a body
fits in, inside an area the spawns reach. Two floors of a column within four units are now one cell
wherever the band edges fall, and 0 to 2 such pairs are left on any map. One more, on ctf_2fort, had
room only for a body standing on a small prop over the floor the area lies on: the floor search starts
a step over the area, inside the prop, and reports its underside. Room is no longer found on ground
with something solid a unit above it, and that area goes as a pocket. The RP maps lose 271 and 284
areas and the TF2 maps up to 9, islands fall from 28 to 21 and 29 to 22 on the RP maps and from 1 to 0
on ctf_2fort, and areas no spawn reaches from 44 to 33, 309 to 285 and 2 to 1. No area left with the
same shape loses its way in, routes, coverage, floating and over-air areas do not move, and none of
the 34 links gained or 24 lost between areas of the same shape is one a hull checker flags.
gm_construct, gm_flatgrass and ctf_turbine come out byte-identical. The other 49 are kept: 12 that a
body walks out of across the shared edge, refused by the builder's test (its body is swept at the
height of the floor under its middle on a slope, positions near the middle of a narrow span are never
tried, and slivers are tried only six units in; loosening each linked links the checker flags more
often than the links kept); 11 left on foot only across floor no area covers or just past a span's end;
10 behind 46 to 48 unit fences and railings, which a jump clears; 10 inside open-topped props, first
taken for floor beyond bars 10 to 19 units high (see below); and 6 no
player reaches - a strip behind a row of containers on cp_granary and a recess under a 32 unit lintel,
a gap behind a displacement rock face on cp_badlands, a terrain shell on each RP map, and a room on
rp_downtown_meowy with no way out within 256 units.

A walker steps over anything up to a step high, and the builder did not. Its lowest clearance line runs
eight units over the higher floor, so a bar, a raised threshold or a prop's edge 9 to 18 units high read
as a wall and the floors either side were never linked. Where the lines refuse a crossing between floors
within a step of each other, a crouching body is now raised onto whatever it passes over and swept
across. That top has to be within a step of both floors, so a wall a step over a pavement and 30 units
over the road beside it stays the drop and climb tests' business. It has to stand in the crossing, with
its near and far faces no more than a body's width apart halfway up, so a raised floor is a floor and is
linked by steps up onto it and down off it. And there has to be a crouching body's headroom over it.
Of the pairs the builder refused and a step move crosses, 9 have ground at both ends in Valve's TF2
meshes or the engine's gm_construct mesh, and those meshes join all 9: 2 by a direct link, the rest
through an area spanning the obstacle or standing on it. None of the 10 cut-off groups above put down
to a bar is behind one. They are three stacked open-topped props at
(-844, -970) and two pieces of a diagonal planter at (5290, 2141) on each RP map, whose rims stand 26 to
30 units over the room floor; the floor search inside the rim found the prop's underside, 10 to 19 units
lower, and that was taken for the obstacle's height. They need a jump and stay cut off, as the engine's
own mesh leaves them. Five sites gain links on each RP map, one on ctf_2fort and four on pl_upward: 2, 8
and 10 links, all level, none flagged by a hull checker, against 0.9% to 2.0% of the level links kept.
The checker rests its body up to 24 units over the floor, so it steps over these too; each site was also
checked in cross-section. Four links each on the RP maps go to the connection fixer, which drops A to C
when A to B to C now exists, and a 14 unit strip on top of a 13 unit block on pl_upward goes with its 6
links, as it was kept only for being the one way between its neighbours. Routes, coverage, floating and
over-air areas, islands and areas no spawn reaches do not move, and eight maps come out byte-identical.
The 46 to 48 unit fences are not modelled: 86 and 87 refused pairs on the RP maps, 2 of them into a
cut-off group, and 0 to 5 on the other maps.

A line traced from inside a static prop is not told so. The line traces have no start-solid flag: one
starting inside a brush is blocked because it starts inside, but a prop is triangles, and a segment
starting inside one crosses nothing until it leaves - nothing at all if it never does. The point test
the generator guards its traces with is a zero-length line, which never crosses a triangle, so it
answered "open air" anywhere inside a prop. The floor walk stepped a quarter unit into a prop it had
landed on, was told it was in the open, and sampled the floor the prop stands on as ground inside it,
with headroom wherever the prop was taller than a player; the clipper's buried-edge and floor checks,
the connection builder's room, overhang and side-floor guards, its clearance lines and fall columns
and the lip search all read the inside of a prop the same way. The point test now also asks whether
the point is inside one of the props' convex hulls, and the line callers that ask whether a body fits
check the start of the line against them. Two start-in-solid misreadings of brushes went too: every
column the floor walk sampled began inside the brush sealing the top of the map and recorded a "floor"
there, taking one of its sixteen slots, and the lift finder took a probe starting in solid for a floor
or a landing. 143 areas and 720 links go across the twelve maps, 46 areas on rp_downtown_tits_v25;
gm_construct and gm_flatgrass come out byte-identical. Routes, coverage and over-air areas do not move,
floating areas fall from 266 to 249, islands from 91 to 82 and areas no spawn reaches from 1,561 to
1,549, and no area a spawn reached is lost but a slot on pl_badwater no crouching body fits. A hull
checker flags 140 of the 194 removed links it can test, against 1.3% to 31% of the links kept.

The body sweep was blind to props in three ways of its own. A box wholly inside a prop touches none of
its triangles, so it swept clear: the start of every sweep is now tested against the props' convex
hulls as well. A box dragged out through a prop face it starts across was a hit, where a brush reports
it leaving, so the step away from a wall face its sample lies on was allowed off a brush and refused
off a prop: the sweep now knows which side of a prop's face is out, and 118 of 222 such steps in the
flood, the node linker and the corner patcher are grazes it lets through. And a flat plate, the shape
the crouching and resting body tests sweep up and down, could pass straight through a level prop face
on rounding alone; plates are now given a hundredth of a unit of depth. Starts inside a prop were rare
by now, because the line-trace fixes above already keep floors out of props - 601 sweeps across the
twelve maps, all but 32 of them crouching bodies - and none of them changes a mesh. The graze steps
reshape 1 to 13 areas on seven maps, and the plate fix removes one cut-off 25 by 18 unit pocket on
pl_upward that no body fits in. Islands fall from 8 to 7 and areas no spawn reaches from 16 to 15 on
pl_upward; routes, coverage, floating and over-air areas do not move on any map, and cp_granary,
cp_badlands, gm_construct and gm_flatgrass come out byte-identical.

Ground inside a `trigger_hurt` that kills on contact gets no mesh. pl_upward's out-of-bounds valley
sits under one, and Meshwright meshed 1,510 areas there that Valve's mesh does not have and that
nothing could leave once it had fallen in. Without them pl_upward has 2,965 areas instead of 4,488,
27.5% of them on ground Valve's mesh lacks instead of 52.0%, and 90% of the mesh is one strongly
connected piece instead of 66%, with the same routes and coverage. Hurts that start disabled, are
filtered to one team, ride a moving entity or deal under 100 damage a second are left alone, as
Valve's meshes leave them.

Finishing an existing mesh (`-generateareas` without `-scratch`) lines the new areas up with the old
ones, so the seams connect. Run over Valve's own meshes, which then only need their gaps filled, the
generated areas overlapping an old one too far to link went from 431 to none on ctf_2fort and from
1,833 to 6 on cp_dustbowl, and the islands left cut off from the rest of the finished mesh from 71 to
5 and from 264 to 44.

### Speed

`rp_downtown_tits_v25` with the passes above, against the engine on `rp_downtown_tits_v2`, the
previous version of the map, with the same passes:

| | time |
|---|---|
| `nav_generate`, one core, game blocked (`rp_downtown_tits_v2`) | about 80 minutes |
| Meshwright, 8 threads | **161 seconds** |

`gm_construct`, the same passes, takes 12 seconds.

Runs are reproducible. Repeating a run writes a byte-identical file, checked on ctf_2fort,
gm_construct and rp_downtown_tits_v25, and so does running it at 4 threads instead of 8, checked on
the same three.

A timing comparison is worthless unless it says what `nav_quicksave` was set to. It defaults to 1, and
both `ComputeSniperSpots` and `ComputeSpotEncounters` return immediately when it is, so a stock
`nav_generate` skips both phases and finishes far sooner than the figure above. The run measured here
used `nav_quicksave 0`, so both tools did the same work. To compare against the default instead, run
Meshwright with `-nosnipers -noencounters`.

---

## Limitations

**A prop whose model you do not have is invisible.** Collision comes from each model's `.phy`, found
through the map's embedded pakfile, the loose game and addon directories, `.gma` workshop archives and
then VPKs, the order the engine mounts them, taken from the mod's own `gameinfo.txt` where there is
one. Anything not found contributes nothing, and the mesh will float where those props are.
`generate` warns when a model a solid prop uses could not be found, naming the first few, and
`meshwright props <bsp>` lists them all. If the content exists but the map is not inside the game
directory, `-content` is the fix.

A missing hull is left missing rather than replaced with the model's bounding box. Substituting a box
measured worse: the models that lack a `.phy` are typically bushes, tree cards and skybox props, and a
box around those deletes the ground beside them.

**Generated meshes are more fragmented than the engine's.** Props are solid here, so an area cannot
grow across one and ends at it instead, and slivers too narrow to walk are discarded. The mesh is
more truthful and a bot will not walk into scenery, but it is larger and more broken up.

**An obstacle in the middle of an area is left there.** Clipping pulls the four edges back out of
geometry, which is the only shape of correction it can make. A pillar or a post that ends up inside
an area, rather than at its edge, stays inside it and nothing splits the area around it. It is a
small residue: on `rp_downtown_meowy` 11 of 14,268 areas contain any solid at all, and 0.01% of the
sampled interior sits inside geometry. `meshwright shape <bsp> <nav>` reports it.

**Some areas cannot be reached from a player spawn.** Every run reports the count;
`meshwright reachable` shows where. `-pruneunreachable` deletes them, capped to groups of eight or
fewer, and refusing outright if the result would remove more than a third of the mesh. Areas cut off
from everything that no crouching body fits in are removed on every run, as they are not ground.

It is off by default because deleting it measures worse. Most unreachable ground is real ground the
movement pass failed to link, and the engine walks it perfectly well: on `rp_downtown_tits_v2` pruning
removes 665 areas and takes coverage of the engine's reachable ground from 90.1% to 88.2%. A stranded
area costs nothing at runtime, since nothing can path into it. Deleting real ground does cost
something.

The engine strands areas too: 1,032 of the 19,275 in its own mesh for that map, 5.4%. Meshwright
strands 7.0%, which is the worse of the two figures, though 276 of its 430 stranded groups are a
single area.

**Some collision questions use a line rather than a swept box.** Movement between samples is tested
with a proper hull sweep against world brushes, brush entities and displacement terrain. "Is there
room standing right here" is a point question, and a zero-length sweep is degenerate for a brush
clipper, so standing headroom is still a line trace. It can drop through a grating or a gap slightly
narrower than itself. Crouch-only ground, where that matters most, also sweeps a body's footprint
upward, which is a sweep with length and so not degenerate.

**A drop off floor that runs well past its area is not linked.** A fall is checked from where the
floor being stepped off ends, and the walker lands past that, in the area below - but only up to 16
units beyond where the crossing would otherwise land. Floor that carries on further than that with no
area on it is taken for the floor of an upper storey, and no drop is recorded off its far edge.

**Stair marking is limited by area shape, not by the test.** Run over the engine's own areas the
classifier reproduces the engine's verdicts. Run over a mesh generated here it finds fewer, because an
area that runs off the end of a flight or picks up a neighbouring ramp is correctly rejected. The
shape is wrong, not the verdict.

**Only `trigger_hurt` counts as a death pit.** A `trigger_multiple` that kills through an output
(`!activator,SetHealth,-1000`) is not recognised, nor is a hurt that map logic switches on later in
the round, so ground under either is meshed. Valve's meshes cover the second kind as well.

**Generated areas are marked experimental.** `-generateareas` warns on every run. Review the result
before shipping it.

---

## Building from source

Requires the .NET 10 SDK. The BSP and game-content readers come from
[SourceKit](https://github.com/catualus/SourceKit), a submodule in `external/SourceKit`, so clone
with submodules (or run `git submodule update --init` in an existing clone).

```bash
git clone --recurse-submodules https://github.com/catualus/NavPal.git
```

```bash
dotnet build -c Release
```

```bash
dotnet test
```

```bash
./build-plugin.ps1 -Zip
```

---

## Licence

GPL-3.0. See [LICENSE](LICENSE).

Meshwright reimplements algorithms Valve documents in the public Source SDK 2013 and contains no Valve
source code. Please read [NOTICE.md](NOTICE.md) before contributing. In particular, do not paste code
from the SDK or from any leaked or decompiled source.
