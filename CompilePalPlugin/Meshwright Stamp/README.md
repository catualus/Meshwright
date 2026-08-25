# Meshwright Stamp

Runs after everything else has finished with the BSP and writes the map's final size into the nav
mesh beside it.

A nav mesh records the size of the BSP it was built for. The engine compares that against the map it
is loading and prints `Warning! .nav file is out of date!` when they disagree. Packing content into
the BSP, repacking it, and moving its entity lump out all change that size, and all of them run after
the mesh is built, because a mesh has to be built while the entities are still in the BSP to read.
The mesh is not stale in that case, only the stamp is.

There is nothing to configure. It uses the executable from the Meshwright plugin folder, so both
folders need to be present, and it runs at order 12, after the entity lump step at 11.5. If you add
a step of your own that rewrites the BSP, order it before this one.

It changes four bytes and leaves the rest of the mesh untouched. Running it when the stamp is already
correct reports that there is nothing to do.
