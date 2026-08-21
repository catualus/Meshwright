using System.Collections.Generic;

namespace Meshwright
{
    /// <summary>
    /// Makes sure every id in a mesh points at something that exists, immediately before it is written.
    ///
    /// **Nothing on this side of the tooling could see that they did not.** A dangling reference
    /// survives a byte-for-byte round trip, reloads with identical area and connection counts, and
    /// passes every quality measure here - <c>fit</c>, <c>shape</c>, <c>compare-areas</c> - because a
    /// reader that resolves ids lazily never asks whether they resolve. The first thing that noticed was
    /// Garry's Mod, which resolves all of them at load and printed 9,631 copies of
    /// "CNavArea::PostLoad: Corrupt navigation data. Cannot connect Navigation Areas."
    ///
    /// Two passes were leaving them. <see cref="AreaMerger"/> deleted the absorbed area without moving
    /// references off it; that one is fixed at source, because repointing preserves the link where
    /// dropping it would lose a route. <see cref="AreaSquarer"/> splits an area into two pieces with
    /// fresh ids and discards the original id entirely, so there is no single successor to repoint to -
    /// the connections have to be rebuilt, and whatever is left over pruned.
    ///
    /// Hence a sweep at the end rather than only fixes at each site. Every pass that removes or replaces
    /// an area is a chance to leave a dangling id, this catches all of them including ones not written
    /// yet, and it reports what it removed rather than tidying up silently - a large count here means an
    /// earlier pass is losing connectivity, which is a real defect that pruning hides.
    /// </summary>
    public static class NavIntegrity
    {
        public readonly record struct Result(
            int Connections, int Ladders, int Visibility, int SelfConnections, int Duplicates,
            int Inherits)
        {
            public int Total =>
                Connections + Ladders + Visibility + SelfConnections + Duplicates + Inherits;
        }

        public static Result Prune(NavFile nav)
        {
            var areas = new HashSet<uint>(nav.Areas.Count);

            foreach (var area in nav.Areas) areas.Add(area.Id);

            var ladders = new HashSet<uint>(nav.Ladders.Count);

            foreach (var ladder in nav.Ladders) ladders.Add(ladder.Id);

            int droppedConnections = 0, droppedLadders = 0, droppedVisibility = 0;
            int self = 0, duplicates = 0, droppedInherits = 0;

            foreach (var area in nav.Areas)
            {
                foreach (var list in area.Connections)
                {
                    var seen = new HashSet<uint>();
                    int keep = 0, count = list.Count;

                    for (int i = 0; i < count; i++)
                    {
                        uint id = list[i];

                        // An area connected to itself is as corrupt to the engine as one connected to
                        // nothing, and merging produces them: absorb your own neighbour and any link
                        // between the two becomes a self-reference.
                        if (id == area.Id) { self++; continue; }
                        if (!areas.Contains(id)) { droppedConnections++; continue; }
                        if (!seen.Add(id)) { duplicates++; continue; }

                        list[keep++] = id;
                    }

                    list.RemoveRange(keep, list.Count - keep);
                }

                foreach (var list in area.Ladders)
                {
                    int keep = 0, count = list.Count;

                    for (int i = 0; i < count; i++)
                    {
                        if (!ladders.Contains(list[i])) { droppedLadders++; continue; }

                        list[keep++] = list[i];
                    }

                    list.RemoveRange(keep, list.Count - keep);
                }

                // Visibility is the one that runs to millions, because an area set that changed after
                // the mesh was last analysed leaves every stored pair naming the old ids.
                int keptVisible = 0;

                for (int i = 0; i < area.VisibleAreas.Count; i++)
                {
                    var entry = area.VisibleAreas[i];

                    if (!areas.Contains(entry.AreaId)) { droppedVisibility++; continue; }

                    area.VisibleAreas[keptVisible++] = entry;
                }

                area.VisibleAreas.RemoveRange(keptVisible, area.VisibleAreas.Count - keptVisible);

                // The other area id in the record, and the one this pass used to walk straight past
                // despite the promise above. It is the parent of Valve's visibility delta encoding: the
                // reader resolves it and takes that area's visible set as the base for this one's, so a
                // parent that no longer exists is a dangling reference exactly like a connection to a
                // deleted area, and fails at load the same way.
                //
                // Zero is not an id here, it is the encoding's "no parent" - VisibilityCompressor writes
                // it for every area that stores its own list outright - so it must never be treated as
                // a miss. Nor may an area inherit from itself, which resolves to a cycle rather than a
                // base set.
                if (area.InheritVisibilityFrom != 0 &&
                    (area.InheritVisibilityFrom == area.Id || !areas.Contains(area.InheritVisibilityFrom)))
                {
                    area.InheritVisibilityFrom = 0;
                    droppedInherits++;
                }
            }

            return new Result(droppedConnections, droppedLadders, droppedVisibility, self, duplicates,
                droppedInherits);
        }

        /// <summary>Prunes and prints what went, so a pass that is losing links cannot do it quietly.</summary>
        public static void PruneAndReport(NavFile nav)
        {
            var pruned = Prune(nav);

            if (pruned.Total == 0) return;

            System.Console.WriteLine(
                $"      pruned {pruned.Total:N0} references that pointed nowhere: " +
                $"{pruned.Connections:N0} connections, {pruned.SelfConnections:N0} self-links, " +
                $"{pruned.Duplicates:N0} duplicates, {pruned.Ladders:N0} ladder, " +
                $"{pruned.Visibility:N0} visibility, {pruned.Inherits:N0} inherit");
        }
    }
}
