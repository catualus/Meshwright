# Meshwright

**An offline navigation mesh generator for Source engine maps.**

Meshwright builds `.nav` files from a compiled `.bsp` on your machine, without launching the game. It is
a replacement for typing `nav_generate` at the console and waiting — and, on the maps it has been
measured against, it produces a mesh that sits on the ground more accurately than the one the engine
makes.

It can run as a step inside [Compile Pal](https://github.com/ruarai/CompilePal), or on its own from
the command line.

---

## Why not just use `nav_generate`?

The engine's generator works, and for a lot of maps it is fine. The problems people actually hit
with it are these:

**You have to load the map to use it.** The generator runs inside the game, on the map you are
standing in. That makes it awkward to put in a build script, and impossible to run on a machine that
is not also running the game.

**It blocks the game while it works.** On a large map you sit and watch a frozen client. Meshwright
runs as a normal program, uses every core you have, and can be cancelled.

**It has no way to tell you what it did.** When a mesh comes out wrong, `nav_generate` gives you a
mesh and nothing else. Working out *why* a staircase was missed means looking at the mesh in game and
guessing. Meshwright ships a set of commands that answer that question directly — see
[Finding out what went wrong](#finding-out-what-went-wrong) below.

**It builds no ladders outside Left 4 Dead.** `CNavMesh::BuildLadders` is wrapped in `#ifdef TERROR`
in the public SDK, and even where it does run it only looks for `func_simpleladder` entities. Brush
ladders — the ordinary kind — are invisible to it. Bots simply cannot use them. Meshwright finds ladder
brushes by material and wires them into the mesh.

**Some things it just does not mark.** Lifts and elevator platforms get no connections, so bots
treat a working lift as a dead end.

## What Meshwright does

| | `nav_generate` | Meshwright |
|---|---|---|
| Where it runs | Inside the game, on the loaded map | Standalone, from a `.bsp` file |
| Uses multiple cores | No | Yes |
| Blocks the game | Yes | N/A |
| Can be scripted / put in a build | Not really | Yes |
| Brush ladders | Not outside L4D | Yes |
| Lift and elevator stops | No | Yes |
| Explains its own output | No | Yes, several diagnostic commands |
| Visibility (`nav_analyze`) | Yes, in game | Yes, offline and parallel |
| Hiding spots | Yes | Yes — 267/267 against the engine's own gm_construct mesh |
| Sniper spot grading | Only with `nav_quicksave 0` | Yes, always — 98.5% flag agreement |
| Encounter spots | Only with `nav_quicksave 0` | Yes, always |

Those last two rows need a caveat that is easy to get backwards. `nav_quicksave` defaults to **1**,
and both `ComputeSniperSpots` and `ComputeSpotEncounters` return immediately when it is set — so a
stock `nav_generate` produces neither. Any timing comparison has to say which phases it is counting,
in both directions.

## How it compares on real maps

**Benchmarks are not published yet.** Meshwright is still being worked on, and the parts that are known
to be incomplete — see [Limitations](#limitations) — are exactly the parts a quality comparison would
be measuring. Numbers taken now would flatter or damn it for the wrong reasons, and would need
retracting a week later.

What will go here, once the outstanding work is done and the results have been checked in game rather
than only in a spreadsheet:

- **Wall-clock time**, measured like for like. That means comparing the same set of phases, and the
  phases do not line up by default in either direction: a stock `nav_generate` skips sniper and
  encounter spots entirely (`nav_quicksave` is 1), while Meshwright always computes them. Both the
  whole-pipeline number and a phase-by-phase breakdown belong here, with the machine and core count
  stated, from repeated runs rather than one.
- **Mesh quality**, against the engine's own mesh for the same map: area and connection counts,
  unreachable areas, crouch and stair marking, and the share of areas that float above the ground or
  hang over open air. That last one is the measurement that shows up instantly in game and is
  what `meshwright fit` reports.

Two maps are used for development, both from Garry's Mod: `gm_construct`, small and hand-polished,
and `rp_downtown_meowy`, a large roleplay map of roughly 22,000 areas. They disagree with each other
often enough that quoting either alone would be misleading, which is the other reason for waiting.

If you want to run the comparison yourself in the meantime, everything needed is in the box:
generate a mesh with the commands below, then `meshwright fit`, `meshwright shape` and `meshwright info` against
both your result and the map's shipped `.nav`.

### What has been measured

One part is settled, because it can be scored against the engine directly rather than judged by eye.
Running `build-spots` over the mesh Garry's Mod itself produced for `gm_construct` — so both are
describing the same 2,150 areas, and any difference is the rule rather than the mesh:

| | engine | Meshwright |
|---|---|---|
| Hiding spots | 267 | 267 — all matched within 4 units, 100% recall and precision |
| In cover / exposed | 124 / 143 | 125 / 142 |
| Sniper grades (ideal / good) | 255 / 10 | 252 / 10 |
| Flag agreement | — | 263 of 267 (98.5%) |
| Encounters | 63,628 over 2,149 areas | 63,628 over 2,149 areas — exact |
| Spot sightings within them | 12,721 | 10,730 (84%) |

The encounter *structure* matches exactly, which is the part decided by the connection graph. The
sightings inside them are decided by line-of-sight traces instead, and land 16% short — that gap is
tracer fidelity rather than the encounter rule, and it is the same effect recorded elsewhere in this
codebase as roughly 25 of 250 sampled rays disagreeing with the engine over displacement terrain.

The engine reference for the sniper grades needs `nav_quicksave 0` before `nav_analyze`; the default
skips that phase entirely. Beware any comparison against a `.nav` you did not watch being generated —
the one sitting in this repo's own test directory turned out to be Meshwright's own earlier output, which
would have flattered every one of these numbers.

## Getting started

```bash
meshwright build-areas    map.bsp map.nav -o map.areas.nav
meshwright build-movement map.bsp map.areas.nav -o map.movement.nav
meshwright build-ladders  map.bsp map.movement.nav -o map.ladders.nav
meshwright build-spots    map.bsp map.ladders.nav -o map.spots.nav
meshwright build-visibility map.bsp map.spots.nav -o map.final.nav
```

Each pass writes a new file rather than editing in place, so you can inspect any stage and go back a
step. Run them in that order — later passes read what earlier ones wrote. `build-spots` is optional
and only matters for games whose bots use cover positions; it needs `build-movement` to have run
first, because it decides a corner is sheltered by looking at what connects across it.

If you have no mesh at all, add `-scratch` to `build-areas` and it will generate one from the map's
player spawns.

Every command takes `-threads N` to cap how many cores it uses.

```bash
meshwright info map.nav        # summarise a mesh
meshwright verify map.nav      # read and rewrite it, and diff byte for byte
```

## Finding out what went wrong

This is the part `nav_generate` has no equivalent for. Each of these answers a specific question
about a mesh you are unhappy with:

| Command | Answers |
|---|---|
| `meshwright fit <bsp> <nav>` | Which areas float above the ground or hang over air, and by how much |
| `meshwright shape <bsp> <nav>` | Which areas have grown into walls |
| `meshwright stairs <bsp> <nav> -at x y z` | Why one specific area was or was not marked as stairs |
| `meshwright floors <bsp> x y` | Every surface found in one vertical column |
| `meshwright reach <bsp> x y z` | Why the generator never reached a spot it should have |
| `meshwright probe <bsp> x1 y1 z1 x2 y2 z2` | What blocks one straight line |
| `meshwright compare-areas <ref.nav> <candidate.nav>` | How much of a known-good mesh a generated one covers |
| `meshwright vis-compare <bsp> <analysed.nav>` | How computed visibility scores against an analysed mesh |

Run `meshwright` with no arguments for the full list.

## Limitations

Worth knowing before you rely on it:

- **Encounter sightings run about 16% short.** The encounters themselves match the engine exactly —
  63,628 over the same 2,149 areas — because which ones exist is decided by the connection graph. What
  lies *inside* them is decided by line-of-sight traces, and there Meshwright finds 10,730 sightings
  against the engine's 12,721. The encounter rule is not the suspect; the tracer is, and the shortfall
  is the same order as the displacement disagreement noted below.
- **Stair marking is incomplete.** 11 of 19 on gm_construct. Areas that span a staircase are found
  correctly; some still pick up a neighbouring ramp or run off the end of a flight, which makes the
  stair test correctly reject them.
- **Some areas still overhang.** 3–5% depending on the map, against the engine's 0.1% on gm_construct.
- **Some collision questions are still answered with a thin line rather than a swept box.** The box
  sweep itself now covers all three geometry classes — world brushes, brush entities and displacement
  terrain — so the structural gap that used to be here is closed, and movement between samples is
  tested with Valve's own `NavTraceMins`/`NavTraceMaxs` hull. What remains is narrower: "is there room
  standing right here" is a *point* question, and a zero-length sweep is degenerate for a brush
  clipper, so headroom is still a line trace. Substituting a sweep there measured clearly worse (it
  fails open), and handling the degenerate case properly is the outstanding work.
- **Static props are not modelled at all.** Nothing here reads the static prop lump, so a prop that
  blocks a doorway in game is invisible to every trace and the mesh will run straight through it.
- **Movement constants are not switched per game.** They use the non-Counter-Strike branch of
  `nav.h` — a 64-unit crouch jump and a 400-unit survivable drop, which is right for Garry's Mod and
  TF2 and wrong for CS:S/CS:GO (58 and 200).

## Licence

GPL-3.0. See [LICENSE](LICENSE).

Meshwright reimplements algorithms Valve documents in the public Source SDK 2013, but contains no Valve
source code. Please read [NOTICE.md](NOTICE.md) before contributing — in particular, do not paste
code from the SDK or from any leaked or decompiled source.
