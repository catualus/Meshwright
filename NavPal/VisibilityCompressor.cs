using System;
using System.Collections.Generic;

namespace NavPal
{
    /// <summary>
    /// Shrinks computed visibility using the mesh format's own delta scheme.
    ///
    /// Written uncompressed, rp_downtown_meowy's visibility is 145 MB - 28.5 million links at five bytes
    /// each. Neighbouring areas see almost the same things, which is what
    /// <c>CNavArea::m_inheritVisibilityFrom</c> exists to exploit: an area can name a neighbour and store
    /// only where it differs.
    ///
    /// The semantics were read off Valve's analysed gm_construct rather than assumed. Its attribute byte
    /// takes values 0x00 (4,090 entries), 0x01, 0x02 and 0x03, and 709 of its 710 inheriting areas also
    /// carry their own entries - consistent with <c>IsPotentiallyVisible</c> checking the area's own list
    /// first, treating a zero attribute as an explicit "not visible", and only falling through to the
    /// inherited list when the area is absent from its own.
    ///
    /// Everything written here uses 0x01 (POTENTIALLY_VISIBLE) for present links. The 0x02/0x03 grades
    /// distinguish completely-visible areas, which needs more than a line-of-sight answer to establish,
    /// so claiming them would be asserting something that was never computed.
    /// </summary>
    public static class VisibilityCompressor
    {
        private const byte Visible = 0x01;
        private const byte NotVisible = 0x00;

        public sealed class Result
        {
            public int Compressed;
            public long EntriesBefore;
            public long EntriesAfter;

            public double Ratio => EntriesBefore == 0 ? 1.0 : (double)EntriesAfter / EntriesBefore;
        }

        /// <summary>
        /// Writes the visible sets into the mesh, picking an inheritance parent per area where that
        /// saves space. <paramref name="visible"/> must be indexed by area index and sorted ascending.
        /// </summary>
        public static Result Apply(NavFile nav, int[][] visible, NavProgress? progress = null)
        {
            int count = nav.Areas.Count;
            var result = new Result();

            var idToIndex = new Dictionary<uint, int>(count);
            for (int i = 0; i < count; i++)
                idToIndex[nav.Areas[i].Id] = i;

            var parent = new int[count];
            Array.Fill(parent, -1);

            // An area may only inherit from one that stores a full list, and a chosen parent may never
            // later become a delta itself. One level of indirection keeps resolution trivial and makes
            // cycles structurally impossible rather than something to detect.
            var isParent = new bool[count];

            for (int i = 0; i < count; i++)
            {
                progress?.Report(i / (double)Math.Max(1, count));

                var mine = visible[i];
                result.EntriesBefore += mine.Length;

                if (mine.Length == 0)
                    continue;

                // Somebody already inherits from this area, so its list has to stay complete. Without
                // this the guard below is not enough: it only rejects parents that are *already* deltas,
                // and an area picked as a parent early is still free to become one when its own turn
                // comes round, silently changing what its children resolve to.
                if (isParent[i])
                    continue;

                int best = -1;
                int bestCost = mine.Length; // storing the full list is what we have to beat

                foreach (uint neighbourId in EnumerateNeighbours(nav.Areas[i]))
                {
                    if (!idToIndex.TryGetValue(neighbourId, out int j) || j == i)
                        continue;

                    if (parent[j] != -1) // j is a delta; it cannot be a parent
                        continue;

                    int cost = SymmetricDifferenceSize(mine, visible[j]);
                    if (cost >= bestCost)
                        continue;

                    bestCost = cost;
                    best = j;
                }

                if (best < 0)
                    continue;

                parent[i] = best;
                isParent[best] = true;
            }

            for (int i = 0; i < count; i++)
            {
                var area = nav.Areas[i];
                area.VisibleAreas.Clear();

                var mine = visible[i];

                if (parent[i] < 0)
                {
                    area.InheritVisibilityFrom = 0;
                    foreach (int j in mine)
                        area.VisibleAreas.Add(new VisibleArea { AreaId = nav.Areas[j].Id, Attributes = Visible });

                    result.EntriesAfter += mine.Length;
                    continue;
                }

                var theirs = visible[parent[i]];
                area.InheritVisibilityFrom = nav.Areas[parent[i]].Id;

                foreach (var (index, present) in Difference(mine, theirs))
                {
                    area.VisibleAreas.Add(new VisibleArea
                    {
                        AreaId = nav.Areas[index].Id,
                        Attributes = present ? Visible : NotVisible,
                    });
                }

                result.EntriesAfter += area.VisibleAreas.Count;
                result.Compressed++;
            }

            return result;
        }

        /// <summary>Every area directly connected to this one, in any direction, plus ladder links.</summary>
        private static IEnumerable<uint> EnumerateNeighbours(NavArea area)
        {
            foreach (var direction in area.Connections)
                foreach (uint id in direction)
                    yield return id;
        }

        /// <summary>Size of the symmetric difference of two ascending, duplicate-free arrays.</summary>
        private static int SymmetricDifferenceSize(int[] a, int[] b)
        {
            int i = 0, j = 0, size = 0;

            while (i < a.Length && j < b.Length)
            {
                if (a[i] == b[j]) { i++; j++; }
                else if (a[i] < b[j]) { i++; size++; }
                else { j++; size++; }
            }

            return size + (a.Length - i) + (b.Length - j);
        }

        /// <summary>
        /// The delta from <paramref name="theirs"/> to <paramref name="mine"/>: entries only this area
        /// sees (present), and entries the parent sees that this area does not (an explicit override).
        /// </summary>
        private static IEnumerable<(int Index, bool Present)> Difference(int[] mine, int[] theirs)
        {
            int i = 0, j = 0;

            while (i < mine.Length && j < theirs.Length)
            {
                if (mine[i] == theirs[j]) { i++; j++; }
                else if (mine[i] < theirs[j]) yield return (mine[i++], true);
                else yield return (theirs[j++], false);
            }

            while (i < mine.Length) yield return (mine[i++], true);
            while (j < theirs.Length) yield return (theirs[j++], false);
        }

        /// <summary>
        /// Rebuilds each area's effective visible set from the stored deltas. Used to prove a compressed
        /// mesh still means exactly what the uncompressed one did.
        /// </summary>
        public static HashSet<uint>[] Resolve(NavFile nav)
        {
            int count = nav.Areas.Count;
            var byId = new Dictionary<uint, int>(count);
            for (int i = 0; i < count; i++)
                byId[nav.Areas[i].Id] = i;

            var sets = new HashSet<uint>[count];

            for (int i = 0; i < count; i++)
            {
                var area = nav.Areas[i];
                var set = new HashSet<uint>();

                if (area.InheritVisibilityFrom != 0 && byId.TryGetValue(area.InheritVisibilityFrom, out int p))
                {
                    foreach (var v in nav.Areas[p].VisibleAreas)
                        if (v.Attributes != 0) set.Add(v.AreaId);
                }

                // the area's own entries win over anything inherited
                foreach (var v in area.VisibleAreas)
                {
                    if (v.Attributes != 0) set.Add(v.AreaId);
                    else set.Remove(v.AreaId);
                }

                sets[i] = set;
            }

            return sets;
        }
    }
}
