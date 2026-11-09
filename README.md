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
| Areas | 2,271 | 2,640 | 19,577 | 17,317 |
| Connections | 11,446 | 14,117 | 91,538 | 79,161 |
| Height error, mean | 1.0 | 1.0 | 10.3 | **0.6** |
| Height error, median | 0.3 | **0.2** | 0.1 | **0.0** |
| Areas floating above the floor | 1 | **0** | 2,713 (13.9%) | **47 (0.3%)** |
| Areas over open air | 3 | **0** | 215 (1.1%) | **1** |

On gm_construct the two are level. On the large map they are not: the shipped mesh leaves 2,713 areas
above the floor and 215 over nothing.

### What it covers

`meshwright compare-areas` checks every area of the reference against the generated mesh, and
`-reachable <map.bsp>` leaves out the reference ground its own connections cannot reach from a spawn.
The spawn and reachability rows count player spawns with a generated area within 80 units under them,
and the areas the run's own reachability check cannot reach from any spawn.

| | gm_construct | rp_downtown_tits_v25 |
|---|---|---|
| Coverage of the reference's ground a player can reach | **98.4%** | 67.4% |
| Coverage of every reference area including stranded | 88.7% | 67.5% |
| Areas on ground the reference does not have | 2.8% | 9.6% |
| Player spawns with an area under them | **33 of 33** | 56 of 96 |
| Areas no player spawn can reach | 3 (0.1%) | 321 (1.9%) |

Score against reachable ground rather than against every area, because a reference mesh contains
ground nothing can path to: gm_construct's engine mesh strands 224 of its 2,271 areas, which is what
drops the second row to 88.7%.

67.4% on the large map is mostly the reference's doing. It looks like the output of an earlier
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
| pl_upward | Valve | 99.1% |
| cp_badlands | Valve | 99.8% |
| ctf_turbine | Valve | **100.0%** |
| Team Fortress 2, mean | | 99.7% |

ctf_2fort and cp_dustbowl found 43.6% and 45.3% until clip brushes counted as ground. TF2 lays a
clip ramp over its stairs and a clip slab over some sunken floors, and the mesh had treated those as
neither floor nor open air, so nothing was built on them. The same maps also need a toggle brush that
is switched on at the start - cp_dustbowl's stairs to the first stage's yard, pl_badwater's cover over
the final pit - to be a floor, and, for TF2 only, player clip to be solid, as it is to TF2's bots.
That last one also seals the map edges the flood used to walk out through: on koth_viaduct 83.5% of
the generated areas were on ground Valve's mesh does not have, and now 27.8% are, and on cp_badlands
69.2% now 25.2%. Coverage of the ground Valve's meshes can reach is 95.4% to 98.5% on every TF2 map.

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
the clearance lines. gm_flatgrass has no island left; across the twelve maps the areas with no link at
all fell from 830 to 513 and the crossings left unlinked away from the middle from 3,937 to 1,435.
Connections rise by 1% to 8%, routes and coverage hold or improve slightly, and the areas no spawn can
reach fall on every map but gm_construct, which stays at 3.

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
fewer, and refusing outright if the result would remove more than a third of the mesh.

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

**A drop off a ledge whose floor runs past its area can be refused.** A fall is checked down a
column a couple of units past the upper area's edge, and where the floor itself carries on a few units
further the column starts inside it. gm_flatgrass's ledges round its pit are like this: they link to
each other and to the ledge they lead off, but no drop into the pit is recorded from them.

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
