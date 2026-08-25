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
| `-game NAME` | Movement limits: `cs`/`css`/`csgo`, or `gmod`/`hl2`/`tf2`/`source` (default) |
| `-content DIR` | Extra directory to resolve prop models from. May be repeated |

`-content` matters more than it looks. Meshwright works out where the game's content lives from the
map's own path: a `.bsp` sits in `<mod>/maps`, so the mod directory is two levels up. That covers the
normal case and nothing else: a BSP in a build server's output directory infers a mod directory that
does not exist, so no model resolves, no prop is collided against, and the mesh floats over every one
of them. The run still succeeds. Point `-content` at the mod directory and it resolves normally.

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
meshwright stamp map.bsp map.nav
```

A mesh records the size of the BSP it was built for, and the engine prints
`Warning! .nav file is out of date!` when that no longer matches the map it is loading. Packing
content into the BSP, repacking it, or moving its entity lump out all change that size, and all of
them run after the mesh is built, because a mesh has to be built while the entities are still there
to read. The mesh is not stale in that case, only the stamp is. This rewrites the one field and
nothing else.

Run `meshwright` with no arguments for the full list.

---

## Results

Two maps, both public, both scored against a mesh the game generated for the same map. Machine is an
8-core Ryzen 7 6800H, 16 threads.

`gm_construct` ships with Garry's Mod. `rp_downtown_tits_v2` is a large roleplay map, 19,275 areas in
the engine's own mesh, and is the harder case by some distance.

### Where the mesh sits

The measure that shows up first in game is whether an area is on the ground. `meshwright fit` traces
down from 25 points on every area and reports the gap.

| | gm_construct | | rp_downtown_tits_v2 | |
|---|---|---|---|---|
| | engine | Meshwright | engine | Meshwright |
| Areas | 2,271 | 2,753 | 19,275 | 22,963 |
| Connections | 11,446 | 14,659 | 85,083 | 104,216 |
| Height error, mean | 1.0 | 1.0 | 2.9 | **0.6** |
| Height error, median | 0.3 | **0.2** | 0.3 | **0.0** |
| Areas floating above the floor | 1 | **0** | 924 (4.8%) | **18 (0.1%)** |
| Areas over open air | 3 | **0** | 19 | **1** |

On gm_construct the two are level. On the large map they are not: the engine leaves 924 areas above
the floor and 19 over nothing.

### What it covers

| | gm_construct | rp_downtown_tits_v2 |
|---|---|---|
| Coverage of the engine's ground a player can reach | **98.0%** | **90.1%** |
| Coverage of every engine area including stranded | 88.4% | 88.6% |
| Areas on ground the engine's mesh does not have | 5.3% | 40.0% |

Score against reachable ground rather than against every area, because an engine mesh contains ground
nothing can path to. gm_construct's own mesh strands 224 of its 2,271 areas that its own connection
graph cannot reach. Counting those as misses is what drops the second row to 88.4%.
`compare-areas -reachable <map.bsp>` does the filtering.

90.1% on the large map is the weakest result here. Most of the missing ground is ground the sampling
flood never reached; the rest was judged unstandable, had no floor found in its column, or was pulled
back out of geometry by the clip. `build-areas -reference <known-good.nav>` breaks the misses down by
cause.

### Speed

`rp_downtown_tits_v2`, both tools running the same passes: areas, movement, ladders, hiding spots,
sniper grading, encounter spots and visibility.

| | time |
|---|---|
| `nav_generate`, one core, game blocked | about 80 minutes |
| Meshwright, 16 threads | **255 seconds** |

`gm_construct`, the same passes, takes 8.6 seconds.

Runs are not yet bit-for-bit reproducible across different thread counts. On
`rp_downtown_tits_v2` the same build produces 25,382 areas at some thread counts and 25,379 at
others, a difference of one sampled cell out of 562,905, and which counts disagree is not itself
stable. Repeat runs at a fixed thread count do agree. This is a
known defect rather than a property being claimed.

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
`meshwright props <bsp>` reports the split; check it if a mesh looks wrong, because nothing else will
tell you. If the content exists but the map is not inside the game directory, `-content` is the fix.

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
clipper, so headroom is still a line trace. It can drop through a grating or a gap slightly narrower
than itself.

**Stair marking is limited by area shape, not by the test.** Run over the engine's own areas the
classifier reproduces the engine's verdicts. Run over a mesh generated here it finds fewer, because an
area that runs off the end of a flight or picks up a neighbouring ramp is correctly rejected. The
shape is wrong, not the verdict.

**Generated areas are marked experimental.** `-generateareas` warns on every run. Review the result
before shipping it.

---

## Building from source

Requires the .NET 10 SDK.

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
