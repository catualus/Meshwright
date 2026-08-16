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

            /// <summary>The area containing a point whose surface is nearest a reference height.</summary>
            public int FindAt(float x, float y, float referenceZ, float tolerance)
            {
                int best = -1;
                float bestDelta = float.MaxValue;

                foreach (int i in Overlapping(x, y, x, y))
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
