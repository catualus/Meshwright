using System;
using System.Collections.Generic;
using System.Linq;

namespace NavPal
{
    /// <summary>
    /// Removes jump areas, reconnecting what they joined - Valve's <c>StichAndRemoveJumpAreas</c> and
    /// the <c>JumpConnector</c> functor it runs.
    ///
    /// Ground too steep to stand on still matters, because it is usually the *route* between two pieces
    /// of ground that are not. A slope, a bank, the face of a step up onto a crate: none of them are
    /// somewhere to stand, all of them are somewhere to cross. Marking that ground as a jump area keeps
    /// it in the mesh long enough to know what it joins; this pass then turns it into a direct
    /// connection between its neighbours and deletes it.
    ///
    /// Discarding steep ground outright, which is what happened before this existed, loses the
    /// connection as well as the area - the two walkable pieces end up with nothing between them.
    /// </summary>
    public static class JumpAreaStitcher
    {
        public sealed class Result
        {
            public int JumpAreas;
            public int ConnectionsAdded;
            public int Removed;
        }

        public static Result Stitch(NavFile nav)
        {
            var result = new Result();

            var jumpIds = new HashSet<uint>();
            foreach (var area in nav.Areas)
            {
                if (((NavAttributes)area.AttributeFlags & NavAttributes.Jump) != 0)
                    jumpIds.Add(area.Id);
            }

            result.JumpAreas = jumpIds.Count;
            if (jumpIds.Count == 0)
                return result;

            var byId = nav.Areas.ToDictionary(a => a.Id);

            // Who connects *into* each area, and from which direction. The mesh only stores outgoing
            // links, so bridging across an area needs this built up front.
            var incoming = new Dictionary<uint, List<(uint From, int Direction)>>();
            foreach (var area in nav.Areas)
            {
                for (int direction = 0; direction < NavGeometry.DirectionCount; direction++)
                {
                    foreach (uint target in area.Connections[direction])
                    {
                        if (!incoming.TryGetValue(target, out var list))
                            incoming[target] = list = [];

                        list.Add((area.Id, direction));
                    }
                }
            }

            foreach (uint jumpId in jumpIds)
            {
                if (!byId.TryGetValue(jumpId, out var jump))
                    continue;

                for (int direction = 0; direction < NavGeometry.DirectionCount; direction++)
                {
                    // Sources arriving on this heading, destinations leaving on it. Bridging within a
                    // single direction preserves the sense of the crossing: an area entered from the
                    // west continues west, rather than being wired to everything the jump area touched.
                    var sources = incoming.TryGetValue(jumpId, out var list)
                        ? list.Where(l => l.Direction == direction).Select(l => l.From)
                        : [];

                    var destinations = ResolveThrough(byId, jumpIds, jumpId, direction, []);

                    foreach (uint source in sources)
                    {
                        if (jumpIds.Contains(source) || !byId.TryGetValue(source, out var from))
                            continue;

                        foreach (uint destination in destinations)
                        {
                            if (destination == source)
                                continue;

                            if (from.Connections[direction].Contains(destination))
                                continue;

                            from.Connections[direction].Add(destination);
                            result.ConnectionsAdded++;
                        }
                    }
                }
            }

            // Drop the jump areas and every reference to them.
            nav.Areas.RemoveAll(a => jumpIds.Contains(a.Id));
            result.Removed = jumpIds.Count;

            foreach (var area in nav.Areas)
            {
                foreach (var list in area.Connections)
                    list.RemoveAll(jumpIds.Contains);
            }

            return result;
        }

        /// <summary>
        /// Where a jump area leads in one direction, following on through further jump areas.
        ///
        /// Steep ground arrives in runs - a bank two or three samples deep is several jump areas in a
        /// line - so stopping at the first neighbour would bridge one jump area to the next and then
        /// delete both, leaving the gap it was meant to span. The visited set guards against a loop of
        /// jump areas chaining into each other forever.
        /// </summary>
        private static List<uint> ResolveThrough(Dictionary<uint, NavArea> byId, HashSet<uint> jumpIds,
            uint areaId, int direction, HashSet<uint> visited)
        {
            var found = new List<uint>();

            if (!visited.Add(areaId) || !byId.TryGetValue(areaId, out var area))
                return found;

            foreach (uint target in area.Connections[direction])
            {
                if (!jumpIds.Contains(target))
                {
                    if (!found.Contains(target))
                        found.Add(target);

                    continue;
                }

                foreach (uint beyond in ResolveThrough(byId, jumpIds, target, direction, visited))
                {
                    if (!found.Contains(beyond))
                        found.Add(beyond);
                }
            }

            return found;
        }
    }
}
