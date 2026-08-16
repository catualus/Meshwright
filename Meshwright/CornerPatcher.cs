using System;
using System.Collections.Generic;

namespace Meshwright
{
    /// <summary>
    /// Bridges areas that only touch corner-to-corner - the area half of Valve's
    /// <c>FixUpGeneratedAreas</c>, <c>FixCornerOnCornerAreas</c>.
    ///
    /// Two areas meeting only at a shared point, with no edge connection anywhere along either bordering
    /// side, is a real movement gap: a step across the point is a diagonal move the grid's cardinal
    /// connections cannot express, so nothing bridges it unless something is built there deliberately.
    /// Valve's fix is a small patch area - a quarter of a sampling step on a side - dropped into the
    /// gap and wired cardinally to both neighbours, turning the point contact into two ordinary edges.
    /// </summary>
    public static class CornerPatcher
    {
        /// <summary>Half of Valve's <c>GenerationStepSize</c> - how far the patch reaches from the corner.</summary>
        private static readonly float HalfStep = AreaGenerator.StepSize * 0.5f;

        /// <summary>
        /// How far apart two areas' corners may sit in plan view and still count as touching at a point.
        ///
        /// This was 0.5 - floating-point slack, on the assumption that a genuine corner-to-corner touch
        /// means the two corners are the same corner. That held while areas were grid-aligned and stopped
        /// holding the moment <see cref="AreaClipper"/> started pulling edges back to real geometry,
        /// which leaves them at arbitrary positions: two areas that shared a corner before clipping can
        /// afterwards sit up to a full sampling step apart without any gap having opened between them in
        /// the world. The test went from describing the mesh to describing the grid the mesh used to be
        /// on, and stopped firing - 656 candidate corners on gm_construct produced exactly one match.
        ///
        /// Half a step, which is a geometric ceiling rather than a tuned number: the patch this creates
        /// is <see cref="HalfStep"/> on a side, so corners further apart than that cannot be bridged by
        /// one - the patch would not reach, and the connections either side of it would be claiming an
        /// adjacency that is not there. Swept against the alternatives on gm_construct, isolated areas
        /// fall 49 -> 45 -> 37 at tolerances 0.5 -> 8 -> 12.5; going on to 25 reaches 32 but only by
        /// patching gaps wider than the patch.
        /// </summary>
        private static readonly float CornerTolerance = HalfStep;

        /// <summary>Valve's <c>MaxDrop</c>: a patch this steep is not worth creating.</summary>
        private const float MaxDrop = NavConstants.StepHeight;

        public sealed class Result
        {
            public int PatchesAdded;

            // Diagnostic only - lets a caller tell "nothing to patch" from "the check never got far
            // enough to find out", which a bare zero cannot.
            public int IsolatedCorners;
            public int CandidateFound;
            public int CornerMatched;
            public int TraceCleared;
        }

        public static Result Patch(NavFile nav, BspVisibility vis)
        {
            var result = new Result();
            var index = new NavGeometry.Index(nav.Areas);

            // Iterated over a snapshot: patches are appended to nav.Areas as they are found, and a patch
            // is never itself a candidate for a further patch in the same pass.
            var candidates = new List<NavArea>(nav.Areas);
            uint nextId = 1;
            foreach (var area in nav.Areas)
                nextId = Math.Max(nextId, area.Id + 1);

            foreach (var area in candidates)
            {
                for (int corner = 0; corner < 4; corner++)
                    TryPatchCorner(nav, index, vis, area, corner, ref nextId, result);
            }

            return result;
        }

        // NavCornerType order: NW=0, NE=1, SE=2, SW=3. Casting a corner straight to NavDirType pairs it
        // with the cardinal edge that starts there going clockwise; direction (corner+3)%4 is the other
        // edge meeting at the same point. Matches Valve's own cast exactly.
        private static void TryPatchCorner(NavFile nav, NavGeometry.Index index, BspVisibility vis,
            NavArea area, int corner, ref uint nextId, Result result)
        {
            int dirToRight = corner;
            int dirToLeft = (corner + 3) % 4;

            // Either bordering edge already carries a connection: this corner is not isolated, nothing
            // to patch. Outgoing only - Valve also checks incoming, but an outgoing edge on a generated
            // mesh reliably implies the reverse exists too, since ConnectionBuilder tests both directions.
            if (area.Connections[dirToRight].Count > 0 || area.Connections[dirToLeft].Count > 0)
                return;

            result.IsolatedCorners++;

            var corners = CornerPositions(area);
            var cornerPos = corners[corner];

            int dirToRightTwice = (dirToRight + 1) % 4;
            int dirToLeftTwice = (dirToLeft + 3) % 4;

            Span<int> otherEdgeDirs = [dirToLeft, dirToRight];
            Span<int> ourEdgeDirs = [dirToLeftTwice, dirToRightTwice];

            for (int i = 0; i < 2; i++)
            {
                int otherDir = otherEdgeDirs[i];
                int ourDir = ourEdgeDirs[i];

                var deltaOther = Scale(DirectionDelta(otherDir), HalfStep);
                var otherEdgePos = Offset(cornerPos, deltaOther);

                int otherIndex = index.FindAt(otherEdgePos.x, otherEdgePos.y, cornerPos.z, MaxDrop * 2f);
                if (otherIndex < 0 || ReferenceEquals(nav.Areas[otherIndex], area))
                    continue;

                result.CandidateFound++;

                var other = nav.Areas[otherIndex];

                // The other area's opposite corner has to land on exactly the same point, or this is not
                // a corner-to-corner touch at all - just two areas that both happen to pass near here.
                int otherCorner = (corner + 2) % 4;
                var otherCorners = CornerPositions(other);
                if (Distance2D(otherCorners[otherCorner], cornerPos) > CornerTolerance)
                    continue;

                result.CornerMatched++;

                if (!Traversability.CanStep(vis,
                        new BspFile.Vector3(cornerPos.x, cornerPos.y, cornerPos.z),
                        new BspFile.Vector3(otherEdgePos.x, otherEdgePos.y, cornerPos.z)))
                {
                    continue;
                }

                var deltaOur = Scale(DirectionDelta(ourDir), HalfStep);
                var ourEdgePos = Offset(cornerPos, deltaOur);
                var farCorner = Offset(otherEdgePos, deltaOur);

                if (!Traversability.CanStep(vis,
                        new BspFile.Vector3(otherEdgePos.x, otherEdgePos.y, cornerPos.z),
                        new BspFile.Vector3(farCorner.x, farCorner.y, cornerPos.z)) ||
                    !Traversability.CanStep(vis,
                        new BspFile.Vector3(ourEdgePos.x, ourEdgePos.y, cornerPos.z),
                        new BspFile.Vector3(farCorner.x, farCorner.y, cornerPos.z)))
                {
                    continue;
                }

                result.TraceCleared++;

                // Unlike Valve's raw per-cell mesh, node-based tiling here already consumes every
                // sampled node into some area, so the diagonal spot a synthetic patch would fill is
                // essentially always already ground some third area owns - it just is not edge-connected
                // to either neighbour along that shared corner. Wiring the area that is actually there
                // is the equivalent fix; manufacturing a patch on top of existing ground would overlap
                // it. A genuine gap - nothing at all at the far corner - still gets a real patch.
                int existingIndex = index.FindAt(farCorner.x, farCorner.y, cornerPos.z, MaxDrop * 2f);

                if (existingIndex >= 0)
                {
                    var bridge = nav.Areas[existingIndex];
                    if (ReferenceEquals(bridge, area) || ReferenceEquals(bridge, other))
                        continue;

                    // Both edges to the bridge area were already proven clear above - the trace from
                    // area's edge point to farCorner, and from other's edge point to farCorner - so this
                    // connects rather than re-traces.
                    bool madeConnection = ConnectIfNew(area, bridge, otherDir);
                    madeConnection |= ConnectIfNew(other, bridge, ourDir);

                    if (madeConnection)
                        result.PatchesAdded++;

                    continue;
                }

                var patch = BuildPatch(area, cornerPos, otherEdgePos, ourEdgePos, farCorner, nextId++);
                nav.Areas.Add(patch);

                Connect(area, patch, otherDir);
                Connect(other, patch, ourDir);

                result.PatchesAdded++;
            }
        }

        private static NavArea BuildPatch(NavArea source, (float x, float y, float z) corner,
            (float x, float y, float z) otherEdge, (float x, float y, float z) ourEdge,
            (float x, float y, float z) far, uint id)
        {
            var patch = new NavArea { Id = id, AttributeFlags = source.AttributeFlags, PlaceIndex = source.PlaceIndex };

            // The four world points are placed into NW/NE/SE/SW purely by which quadrant they fall in
            // relative to the shared corner - the patch is tiny and axis-aligned, so min/max sorting is
            // exactly ClassifyCorners without needing a general convex-hull sort for four points.
            float minX = MathF.Min(MathF.Min(corner.x, otherEdge.x), MathF.Min(ourEdge.x, far.x));
            float maxX = MathF.Max(MathF.Max(corner.x, otherEdge.x), MathF.Max(ourEdge.x, far.x));
            float minY = MathF.Min(MathF.Min(corner.y, otherEdge.y), MathF.Min(ourEdge.y, far.y));
            float maxY = MathF.Max(MathF.Max(corner.y, otherEdge.y), MathF.Max(ourEdge.y, far.y));

            patch.NwCorner[0] = minX; patch.NwCorner[1] = minY; patch.NwCorner[2] = corner.z;
            patch.SeCorner[0] = maxX; patch.SeCorner[1] = maxY; patch.SeCorner[2] = corner.z;
            patch.NeZ = corner.z;
            patch.SwZ = corner.z;

            return patch;
        }

        private static void Connect(NavArea a, NavArea b, int direction)
        {
            if (!a.Connections[direction].Contains(b.Id))
                a.Connections[direction].Add(b.Id);

            if (!b.Connections[NavGeometry.Opposite(direction)].Contains(a.Id))
                b.Connections[NavGeometry.Opposite(direction)].Add(a.Id);
        }

        /// <summary>Connects two areas already known to be reachable, if they are not linked yet.</summary>
        private static bool ConnectIfNew(NavArea a, NavArea b, int direction)
        {
            if (a.Connections[direction].Contains(b.Id))
                return false;

            Connect(a, b, direction);
            return true;
        }

        private static (float x, float y, float z)[] CornerPositions(NavArea area) =>
        [
            (area.NwCorner[0], area.NwCorner[1], area.NwCorner[2]),
            (area.SeCorner[0], area.NwCorner[1], area.NeZ),
            (area.SeCorner[0], area.SeCorner[1], area.SeCorner[2]),
            (area.NwCorner[0], area.SeCorner[1], area.SwZ),
        ];

        private static (float x, float y) DirectionDelta(int direction) => direction switch
        {
            NavGeometry.North => (0, -1),
            NavGeometry.East => (1, 0),
            NavGeometry.South => (0, 1),
            _ => (-1, 0),
        };

        private static (float x, float y, float z) Offset((float x, float y, float z) p, (float x, float y) d) =>
            (p.x + d.x, p.y + d.y, p.z);

        private static (float x, float y) Scale((float x, float y) d, float s) => (d.x * s, d.y * s);

        private static float Distance2D((float x, float y, float z) a, (float x, float y, float z) b)
        {
            float dx = a.x - b.x, dy = a.y - b.y;
            return MathF.Sqrt(dx * dx + dy * dy);
        }
    }
}
