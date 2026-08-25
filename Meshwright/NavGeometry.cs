using System;
using System.Collections.Generic;

namespace Meshwright
{
    /// <summary>
    /// Geometry shared by the passes that reason about where areas are and how they meet.
    ///
    /// Nav areas are axis-aligned quads with four independent corner heights, which makes almost every
    /// question about them - what is the ground height here, do these two touch, which way does this one
    /// face - a small piece of arithmetic that is easy to get subtly wrong in more than one place.
    /// </summary>
    public static class NavGeometry
    {
        /// <summary>NavDirType. North is -Y and west is -X, matching the corner names.</summary>
        public const int North = 0;
        public const int East = 1;
        public const int South = 2;
        public const int West = 3;
        public const int DirectionCount = 4;

        public static int Opposite(int direction) => (direction + 2) % DirectionCount;

        public readonly record struct Bounds(float MinX, float MinY, float MaxX, float MaxY)
        {
            public float Width => MaxX - MinX;
            public float Depth => MaxY - MinY;
        }

        /// <summary>
        /// The area's footprint. NwCorner is the minimum in both axes and SeCorner the maximum, but the
        /// pair is normalised here rather than assumed, because a hand-edited mesh can carry either.
        /// </summary>
        public static Bounds GetBounds(NavArea area) => new(
            MathF.Min(area.NwCorner[0], area.SeCorner[0]),
            MathF.Min(area.NwCorner[1], area.SeCorner[1]),
            MathF.Max(area.NwCorner[0], area.SeCorner[0]),
            MathF.Max(area.NwCorner[1], area.SeCorner[1]));

        /// <summary>
        /// Ground height at a point on the area, bilinearly interpolated. Areas have four independent
        /// corner heights, so treating one as flat misplaces its surface by the whole of its slope.
        /// </summary>
        public static float SurfaceZ(NavArea area, float x, float y)
        {
            float x0 = area.NwCorner[0], y0 = area.NwCorner[1];
            float x1 = area.SeCorner[0], y1 = area.SeCorner[1];

            float u = MathF.Abs(x1 - x0) < 0.01f ? 0f : Math.Clamp((x - x0) / (x1 - x0), 0f, 1f);
            float v = MathF.Abs(y1 - y0) < 0.01f ? 0f : Math.Clamp((y - y0) / (y1 - y0), 0f, 1f);

            float nw = area.NwCorner[2], ne = area.NeZ, se = area.SeCorner[2], sw = area.SwZ;

            float north = nw + (ne - nw) * u;
            float south = sw + (se - sw) * u;
            return north + (south - north) * v;
        }

        /// <summary>
        /// The area's unit surface normal, from <c>CNavArea::ComputeNormal</c>.
        ///
        /// An area is a quad with four independent corner heights, so it need not be planar at all;
        /// this takes the plane through three of the four. <paramref name="alternate"/> selects which
        /// three - the primary normal is built from the NW corner's two edges, the alternate from the
        /// SE corner's - and Valve computes both wherever it matters, because the two disagree exactly
        /// when the quad is not flat.
        /// </summary>
        public static BspFile.Vector3 ComputeNormal(NavArea area, bool alternate = false)
        {
            float x0 = area.NwCorner[0], y0 = area.NwCorner[1];
            float x1 = area.SeCorner[0], y1 = area.SeCorner[1];
            float nw = area.NwCorner[2], ne = area.NeZ, se = area.SeCorner[2], sw = area.SwZ;

            var (ux, uy, uz, vx, vy, vz) = alternate
                ? (x0 - x1, 0f, sw - se, 0f, y0 - y1, ne - se)
                : (x1 - x0, 0f, ne - nw, 0f, y1 - y0, sw - nw);

            var n = new BspFile.Vector3(uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx);

            float length = MathF.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
            return length < 1e-6f
                ? new BspFile.Vector3(0, 0, 0)
                : new BspFile.Vector3(n.X / length, n.Y / length, n.Z / length);
        }

        /// <summary>
        /// Whether two areas describe the same plane - Valve's <c>CNavArea::IsCoplanar</c>, and the
        /// condition their <c>MergeGeneratedAreas</c> gates on that this codebase was missing.
        ///
        /// Agreeing about height along the shared seam is not the same claim and does not imply this.
        /// A flight of stairs climbing to a landing meets that landing at exactly the landing's height,
        /// so a seam-height test passes it, and the merged quad is then a single ramp interpolated from
        /// the foot of the flight to the far side of the landing - which reads tens of units below the
        /// real floor in the middle and hangs in the air over the lower treads. Measured on a 96-unit
        /// flight abutting a flat landing, the merged surface sat 48 units under the seam.
        ///
        /// Either pairing of normals agreeing is enough, as in Valve's: a quad that is not planar has a
        /// primary and an alternate normal that differ, and insisting on both would refuse merges
        /// between two areas that are genuinely describing one surface between them.
        /// </summary>
        public static bool AreCoplanar(NavArea a, NavArea b, float tolerance = CoplanarTolerance)
        {
            if (Agree(ComputeNormal(a), ComputeNormal(b), tolerance))
                return true;

            return Agree(ComputeNormal(a, true), ComputeNormal(b, true), tolerance);
        }

        /// <summary>Valve's own <c>IsCoplanar</c> tolerance, and the comment there keeps it at 0.99.</summary>
        public const float CoplanarTolerance = 0.99f;

        private static bool Agree(BspFile.Vector3 a, BspFile.Vector3 b, float tolerance)
        {
            // A degenerate quad has no plane to compare. Answering "yes" leaves it to the tests that
            // were already there rather than making this a new reason to refuse a merge.
            if (a.Z == 0 && a.X == 0 && a.Y == 0) return true;
            if (b.Z == 0 && b.X == 0 && b.Y == 0) return true;

            return a.X * b.X + a.Y * b.Y + a.Z * b.Z > tolerance;
        }

        public static bool Contains(NavArea area, float x, float y)
        {
            var b = GetBounds(area);
            return x >= b.MinX && x <= b.MaxX && y >= b.MinY && y <= b.MaxY;
        }

        /// <summary>
        /// Uniform grid over area footprints. Every pass that asks "what is near here" wants this, and
        /// at mesh sizes in the tens of thousands a linear scan is not viable.
        /// </summary>
        public sealed class Index
        {
            private const float CellSize = 256f;

            private readonly Dictionary<(int, int), List<int>> cells = [];
            private readonly IReadOnlyList<NavArea> areas;

            public Index(IReadOnlyList<NavArea> areas)
            {
                this.areas = areas;

                for (int i = 0; i < areas.Count; i++)
                {
                    var b = GetBounds(areas[i]);

                    for (int cx = Cell(b.MinX); cx <= Cell(b.MaxX); cx++)
                    for (int cy = Cell(b.MinY); cy <= Cell(b.MaxY); cy++)
                    {
                        if (!cells.TryGetValue((cx, cy), out var list))
                            cells[(cx, cy)] = list = [];

                        list.Add(i);
                    }
                }
            }

            private static int Cell(float v) => (int)MathF.Floor(v / CellSize);

            /// <summary>Indices of every area whose footprint could overlap the given rectangle.</summary>
            public IEnumerable<int> Overlapping(float minX, float minY, float maxX, float maxY)
            {
                var seen = new HashSet<int>();

                for (int cx = Cell(minX); cx <= Cell(maxX); cx++)
                for (int cy = Cell(minY); cy <= Cell(maxY); cy++)
                {
                    if (!cells.TryGetValue((cx, cy), out var list))
                        continue;

                    foreach (int i in list)
                    {
                        if (!seen.Add(i))
                            continue;

                        var b = GetBounds(areas[i]);
                        if (b.MinX <= maxX && b.MaxX >= minX && b.MinY <= maxY && b.MaxY >= minY)
                            yield return i;
                    }
                }
            }

            /// <summary>
            /// The area containing a point whose surface is nearest a reference height.
            ///
            /// Reads one cell's list directly instead of going through <see cref="Overlapping"/>, which
            /// allocates a HashSet per call to de-duplicate areas that straddle a cell boundary. A point
            /// falls in exactly one cell and an area appears in a cell's list once, so there is nothing
            /// to de-duplicate and nothing to allocate.
            ///
            /// That matters because of where this is called from: the sampling flood asks it for every
            /// cell it accepts, to decide whether the mesh already covers that ground, on every core at
            /// once.
            /// </summary>
            public int FindAt(float x, float y, float referenceZ, float tolerance)
            {
                if (!cells.TryGetValue((Cell(x), Cell(y)), out var list))
                    return -1;

                int best = -1;
                float bestDelta = float.MaxValue;

                foreach (int i in list)
                {
                    if (!Contains(areas[i], x, y))
                        continue;

                    float delta = MathF.Abs(SurfaceZ(areas[i], x, y) - referenceZ);
                    if (delta >= bestDelta || delta > tolerance)
                        continue;

                    bestDelta = delta;
                    best = i;
                }

                return best;
            }
        }
    }
}
