using System;
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
            int Inherits, int LadderEndpoints = 0, int Encounters = 0, int EncounterSpots = 0,
            int DuplicateIds = 0)
        {
            public int Total =>
                Connections + Ladders + Visibility + SelfConnections + Duplicates + Inherits
                + LadderEndpoints + Encounters + EncounterSpots;

            /// <summary>
            /// Areas sharing an id with another area, which is reported and not repaired.
            ///
            /// Deliberately outside <see cref="Total"/>, because everything counted there was fixed and
            /// this was not. There is no repair that is not a guess: every reference to a duplicated id
            /// was meant for one of the two, and renumbering picks one without knowing which.
            /// </summary>
            public int DuplicateIds { get; init; } = DuplicateIds;
        }

        public static Result Prune(NavFile nav)
        {
            var areas = new HashSet<uint>(nav.Areas.Count);

            foreach (var area in nav.Areas) areas.Add(area.Id);

            var ladders = new HashSet<uint>(nav.Ladders.Count);

            foreach (var ladder in nav.Ladders) ladders.Add(ladder.Id);

            // Hiding spot ids are global across the mesh rather than per area, so an encounter in one
            // area legitimately names a spot belonging to another. Collected across all of them.
            var spots = new HashSet<uint>();

            foreach (var area in nav.Areas)
                foreach (var spot in area.HidingSpots)
                    spots.Add(spot.Id);

            // Two areas answering to one id makes every reference to it ambiguous. Counted with a
            // second pass over the ids rather than while building the set above, because "how many
            // areas were not the first to claim their id" is the number worth reporting.
            int duplicateIds = 0;
            var claimed = new HashSet<uint>(nav.Areas.Count);

            foreach (var area in nav.Areas)
                if (!claimed.Add(area.Id)) duplicateIds++;

            int droppedConnections = 0, droppedLadders = 0, droppedVisibility = 0;
            int self = 0, duplicates = 0, droppedInherits = 0;
            int droppedEncounters = 0, droppedEncounterSpots = 0;

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

                // An encounter is a path between two named areas with the covered spots seen along
                // it. Both halves are references the engine resolves at load, and neither was swept.
                //
                // A full generate rebuilds encounters from nothing, which is why this never showed up
                // there. Every other route to a written mesh keeps whatever was loaded: -noencounters,
                // -nospots, and each of the staged build-* commands. By then merging, squaring up or
                // pruning may have retired one of the ids.
                int keptEncounters = 0;

                for (int i = 0; i < area.Encounters.Count; i++)
                {
                    var encounter = area.Encounters[i];

                    // Either end missing and the encounter describes a route between somewhere and
                    // nothing, so the whole record goes rather than half of it.
                    if (!areas.Contains(encounter.FromAreaId) || !areas.Contains(encounter.ToAreaId))
                    {
                        droppedEncounters++;
                        continue;
                    }

                    int keptSpots = 0;

                    for (int s = 0; s < encounter.Spots.Count; s++)
                    {
                        if (!spots.Contains(encounter.Spots[s].SpotId))
                        {
                            droppedEncounterSpots++;
                            continue;
                        }

                        encounter.Spots[keptSpots++] = encounter.Spots[s];
                    }

                    encounter.Spots.RemoveRange(keptSpots, encounter.Spots.Count - keptSpots);
                    area.Encounters[keptEncounters++] = encounter;
                }

                area.Encounters.RemoveRange(keptEncounters, area.Encounters.Count - keptEncounters);

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

            // The other direction of the area/ladder relationship, and the one this pass walked past.
            // It prunes an area's list of ladders; a ladder's own four top ids and its bottom id are
            // references to areas in exactly the same sense and dangle in exactly the same way - a
            // ladder built against an area that a later pass then discarded as a sliver, or as
            // unreachable, keeps naming it. Zero is the format's "no area here", so clearing is the
            // repair rather than deleting the ladder.
            int droppedEndpoints = 0;

            foreach (var ladder in nav.Ladders)
            {
                droppedEndpoints += ClearIfMissing(areas, ladder, static l => l.BottomAreaId,
                    static (l, v) => l.BottomAreaId = v);
                droppedEndpoints += ClearIfMissing(areas, ladder, static l => l.TopForwardAreaId,
                    static (l, v) => l.TopForwardAreaId = v);
                droppedEndpoints += ClearIfMissing(areas, ladder, static l => l.TopLeftAreaId,
                    static (l, v) => l.TopLeftAreaId = v);
                droppedEndpoints += ClearIfMissing(areas, ladder, static l => l.TopRightAreaId,
                    static (l, v) => l.TopRightAreaId = v);
                droppedEndpoints += ClearIfMissing(areas, ladder, static l => l.TopBehindAreaId,
                    static (l, v) => l.TopBehindAreaId = v);
            }

            return new Result(droppedConnections, droppedLadders, droppedVisibility, self, duplicates,
                droppedInherits, droppedEndpoints, droppedEncounters, droppedEncounterSpots,
                duplicateIds);
        }

        private static int ClearIfMissing(HashSet<uint> areas, NavLadder ladder,
            Func<NavLadder, uint> read, Action<NavLadder, uint> write)
        {
            uint id = read(ladder);

            if (id == 0 || areas.Contains(id))
                return 0;

            write(ladder, 0);
            return 1;
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
