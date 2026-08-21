# Meshwright — Compile Pal plugin

Builds the nav mesh as a compile step, offline, from the compiled BSP. No game launch, no
`nav_generate`, and it uses every core you have.

## Install

1. Copy this whole `Meshwright` folder into your Compile Pal `Plugins` directory.
2. Restart Compile Pal.
3. Press **+** on the process list and add **Meshwright**.

That's it — there is nothing to register and no build of Compile Pal involved. The step appears
because the folder is there, and disappears if you delete it.

The step runs at order 8.5, which is after **NAV** (8.0) and before packing, so it operates on the
BSP that has already been copied to your maps folder and writes the `.nav` beside it.

## What the options do

By default the step **finishes an existing mesh**: it adds ladders, movement connections, cover spots
and visibility to whatever `.nav` is already sitting beside the BSP. That mesh has to come from
somewhere — either the **NAV** step, or `nav_generate` in game.

Turn on **Generate the mesh** and it will also find walkable ground the mesh is missing, or build one
from nothing if there is no `.nav` at all. That part is still experimental; review the result before
shipping it.

**Counter-Strike movement limits** is the one option that is wrong to leave at its default on the
wrong game. Three constants in `nav.h` differ between Counter-Strike and everything else, and there
is no way to tell which is right from the BSP, so it has to be told. Off suits Garry's Mod, HL2 and
TF2.

The rest are all `Skip …` options and a thread cap. Visibility is around two thirds of a full run, so
that is the one to skip if you are iterating.

## Files

| | |
|---|---|
| `meshwright.exe` | The tool. Also usable on its own — run it with no arguments for the command list. |
| `meta.json` | Tells Compile Pal what the step is and how to invoke it. |
| `parameters.json` | The options shown in Compile Pal's UI. |

`meshwright.exe` is self-contained, so it does not need .NET installed.

## If something goes wrong

The step writes its reasoning to the compile log. Beyond that, the same executable has a set of
commands that answer *why* a mesh came out the way it did — `meshwright fit`, `shape`, `reachable`,
`stairs`, `props` and others. Run it from a terminal in this folder:

```bash
meshwright fit path\to\map.bsp path\to\map.nav
```

Full documentation: <https://github.com/catualus/MeshWright>
