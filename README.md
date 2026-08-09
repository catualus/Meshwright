# NavPal

**An offline navigation mesh generator for Source engine maps.**

NavPal builds `.nav` files from a compiled `.bsp` on your machine, without launching the game. It is
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

**It blocks the game while it works.** On a large map you sit and watch a frozen client. NavPal
runs as a normal program, uses every core you have, and can be cancelled.

**It has no way to tell you what it did.** When a mesh comes out wrong, `nav_generate` gives you a
mesh and nothing else. Working out *why* a staircase was missed means looking at the mesh in game and
guessing. NavPal ships a set of commands that answer that question directly — see
[Finding out what went wrong](#finding-out-what-went-wrong) below.

**It builds no ladders outside Left 4 Dead.** `CNavMesh::BuildLadders` is wrapped in `#ifdef TERROR`
in the public SDK, and even where it does run it only looks for `func_simpleladder` entities. Brush
ladders — the ordinary kind — are invisible to it. Bots simply cannot use them. NavPal finds ladder
brushes by material and wires them into the mesh.

**Some things it just does not mark.** Lifts and elevator platforms get no connections, so bots
treat a working lift as a dead end.

## What NavPal does

| | `nav_generate` | NavPal |
|---|---|---|
| Where it runs | Inside the game, on the loaded map | Standalone, from a `.bsp` file |
| Uses multiple cores | No | Yes |
| Blocks the game | Yes | N/A |
| Can be scripted / put in a build | Not really | Yes |
| Brush ladders | Not outside L4D | Yes |
| Lift and elevator stops | No | Yes |
| Explains its own output | No | Yes, several diagnostic commands |
| Visibility (`nav_analyze`) | Yes, in game | Yes, offline and parallel |
| Hiding / sniper / encounter spots | Yes | **Not yet** — see [Limitations](#limitations) |

That last row matters when comparing the two on time: `nav_generate` does strictly more work than
NavPal currently does, so any timing comparison has to say which phases it is counting.

## How it compares on real maps

**Benchmarks are not published yet.** NavPal is still being worked on, and the parts that are known
to be incomplete — see [Limitations](#limitations) — are exactly the parts a quality comparison would
be measuring. Numbers taken now would flatter or damn it for the wrong reasons, and would need
retracting a week later.

What will go here, once the outstanding work is done and the results have been checked in game rather
than only in a spreadsheet:

- **Wall-clock time**, measured like for like. That means comparing the same set of phases: the
  engine's `nav_generate` also computes hiding, encounter and sniper spots, which NavPal does not do
  at all, so a straight stopwatch on both is not a fair fight in either direction. Both the
  whole-pipeline number and a phase-by-phase breakdown belong here, with the machine and core count
  stated, from repeated runs rather than one.
- **Mesh quality**, against the engine's own mesh for the same map: area and connection counts,
  unreachable areas, crouch and stair marking, and the share of areas that float above the ground or
  hang over open air. That last one is the measurement that shows up instantly in game and is
  what `navpal fit` reports.

Two maps are used for development, both from Garry's Mod: `gm_construct`, small and hand-polished,
and `rp_downtown_meowy`, a large roleplay map of roughly 22,000 areas. They disagree with each other
often enough that quoting either alone would be misleading, which is the other reason for waiting.

If you want to run the comparison yourself in the meantime, everything needed is in the box:
generate a mesh with the commands below, then `navpal fit`, `navpal shape` and `navpal info` against
both your result and the map's shipped `.nav`.

## Getting started

```bash
navpal build-areas    map.bsp map.nav -o map.areas.nav
navpal build-movement map.bsp map.areas.nav -o map.movement.nav
navpal build-ladders  map.bsp map.movement.nav -o map.ladders.nav
navpal build-visibility map.bsp map.ladders.nav -o map.final.nav
```

Each pass writes a new file rather than editing in place, so you can inspect any stage and go back a
step. Run them in that order — later passes read what earlier ones wrote.

If you have no mesh at all, add `-scratch` to `build-areas` and it will generate one from the map's
player spawns.

Every command takes `-threads N` to cap how many cores it uses.

```bash
navpal info map.nav        # summarise a mesh
navpal verify map.nav      # read and rewrite it, and diff byte for byte
```

## Finding out what went wrong

This is the part `nav_generate` has no equivalent for. Each of these answers a specific question
about a mesh you are unhappy with:

| Command | Answers |
|---|---|
| `navpal fit <bsp> <nav>` | Which areas float above the ground or hang over air, and by how much |
| `navpal shape <bsp> <nav>` | Which areas have grown into walls |
| `navpal stairs <bsp> <nav> -at x y z` | Why one specific area was or was not marked as stairs |
| `navpal floors <bsp> x y` | Every surface found in one vertical column |
| `navpal reach <bsp> x y z` | Why the generator never reached a spot it should have |
| `navpal probe <bsp> x1 y1 z1 x2 y2 z2` | What blocks one straight line |
| `navpal compare-areas <ref.nav> <candidate.nav>` | How much of a known-good mesh a generated one covers |
| `navpal vis-compare <bsp> <analysed.nav>` | How computed visibility scores against an analysed mesh |

Run `navpal` with no arguments for the full list.

## Limitations

Worth knowing before you rely on it:

- **No hiding spots, sniper spots or encounter spots.** The engine's `nav_analyze` computes these and
  NavPal does not. Games whose bots use them will want a pass of `nav_analyze` afterwards. Area
  visibility *is* computed.
- **Stair marking is incomplete.** 11 of 19 on gm_construct. Areas that span a staircase are found
  correctly; some still pick up a neighbouring ramp or run off the end of a flight, which makes the
  stair test correctly reject them.
- **Some areas still overhang.** 3–5% depending on the map, against the engine's 0.1% on gm_construct.
- **The swept-box collision test does not see displacements.** It covers world brushes and brush
  entities only, so anywhere the ground is terrain the code falls back to thin line traces. This is
  the main known structural gap.
- **Movement constants are not switched per game.** They use the non-Counter-Strike branch of
  `nav.h` — a 64-unit crouch jump and a 400-unit survivable drop, which is right for Garry's Mod and
  TF2 and wrong for CS:S/CS:GO (58 and 200).

## Licence

GPL-3.0. See [LICENSE](LICENSE).

NavPal reimplements algorithms Valve documents in the public Source SDK 2013, but contains no Valve
source code. Please read [NOTICE.md](NOTICE.md) before contributing — in particular, do not paste
code from the SDK or from any leaked or decompiled source.
