using System;
using System.Collections.Generic;

namespace Meshwright
{
    /// <summary>
    /// Closed convex pieces of collision - the hulls static props are made of - for asking whether a
    /// point is *inside* one.
    ///
    /// The triangle tracer cannot answer that, and by design. A segment owns the half-open stretch
    /// <c>(epsilon, 1 + epsilon]</c> of itself (see <see cref="TriangleMesh"/>), so a line starting on a surface does
    /// not stop on it - which headroom lines from a floor point depend on - and a line starting
    /// *inside* a hull reports, at best, the face it leaves through. A line lying wholly inside one
    /// crosses nothing and reads as clear; a zero-length line, the point-in-solid test
    /// <see cref="BspVisibility.IsPointSolid"/> is built from, never crosses a triangle at all. Brushes
    /// do not have the problem - they are solids, and a segment starting in one is blocked - so this
    /// is the half of "started in solid" the props were missing.
    ///
    /// A point is inside a convex piece exactly when it is behind every one of its face planes. Each
    /// plane is oriented outwards against the piece's own vertex centroid rather than by winding, which
    /// the .phy format does not reliably give (see <see cref="PhyFile"/>). A point *on* a face, within
    /// <see cref="Margin"/>, is not inside: a floor sampled on top of a prop is on it, not in it.
    /// </summary>
    public sealed class ConvexSolids
    {
        /// <summary>How far behind every face a point must be to count as inside.</summary>
        public const float Margin = 0.01f;

        /// <summary>Side of the square grid cells the pieces are bucketed into, in units.</summary>
        private const float CellSize = 128f;

        private float[] nx = [], ny = [], nz = [], nd = [];
        private int[] firstPlane = [0];
        private BspFile.Vector3[] mins = [], maxs = [];

        /// <summary>
        /// Bounds of every piece together. Most points asked about are nowhere near a prop - the floor
        /// walk asks one every eight units down every column, through the solid outside the map - so
        /// rejecting those before the hash lookup is what keeps this off the profile.
        /// </summary>
        private BspFile.Vector3 allMins = new(float.MaxValue, float.MaxValue, float.MaxValue);
        private BspFile.Vector3 allMaxs = new(float.MinValue, float.MinValue, float.MinValue);

        private readonly Dictionary<long, (int First, int Count)> cells = [];
        private int[] cellItems = [];

        /// <summary>How many closed pieces were kept: flat or open ones have no inside and are dropped.</summary>
        public int Count => mins.Length;

        /// <summary>
        /// Indexes the pieces of a triangle soup. Each entry of <paramref name="pieces"/> is a run of
        /// whole triangles in <paramref name="triangles"/> (three vertices each) forming one convex hull.
        /// </summary>
        public static ConvexSolids Build(BspFile.Vector3[] triangles, IReadOnlyList<(int First, int Count)> pieces)
        {
            var result = new ConvexSolids();

            var planeX = new List<float>(); var planeY = new List<float>();
            var planeZ = new List<float>(); var planeD = new List<float>();
            var starts = new List<int> { 0 };
            var lows = new List<BspFile.Vector3>(); var highs = new List<BspFile.Vector3>();

            foreach (var (first, count) in pieces)
            {
                if (count < 4 || first < 0 || (first + count) * 3 > triangles.Length)
                    continue;

                double cx = 0, cy = 0, cz = 0;
                float x0 = float.MaxValue, y0 = float.MaxValue, z0 = float.MaxValue;
                float x1 = float.MinValue, y1 = float.MinValue, z1 = float.MinValue;

                for (int v = first * 3; v < (first + count) * 3; v++)
                {
                    var p = triangles[v];
                    cx += p.X; cy += p.Y; cz += p.Z;
                    x0 = MathF.Min(x0, p.X); y0 = MathF.Min(y0, p.Y); z0 = MathF.Min(z0, p.Z);
                    x1 = MathF.Max(x1, p.X); y1 = MathF.Max(y1, p.Y); z1 = MathF.Max(z1, p.Z);
                }

                int n = count * 3;
                var centre = new BspFile.Vector3((float)(cx / n), (float)(cy / n), (float)(cz / n));
                int planesBefore = planeX.Count;

                for (int t = first; t < first + count; t++)
                {
                    var a = triangles[t * 3];
                    var b = triangles[t * 3 + 1];
                    var c = triangles[t * 3 + 2];

                    float ex = b.X - a.X, ey = b.Y - a.Y, ez = b.Z - a.Z;
                    float fx = c.X - a.X, fy = c.Y - a.Y, fz = c.Z - a.Z;
                    float px = ey * fz - ez * fy, py = ez * fx - ex * fz, pz = ex * fy - ey * fx;

                    float length = MathF.Sqrt(px * px + py * py + pz * pz);
                    if (length < 1e-6f)
                        continue; // a sliver has no direction worth trusting

                    px /= length; py /= length; pz /= length;
                    float d = px * a.X + py * a.Y + pz * a.Z;

                    // Outwards is away from the centre. A face the centre lies on says nothing about
                    // which side is in, and only a flat piece has one.
                    float side = px * centre.X + py * centre.Y + pz * centre.Z - d;
                    if (MathF.Abs(side) < 1e-3f)
                        continue;

                    if (side > 0f) { px = -px; py = -py; pz = -pz; d = -d; }

                    planeX.Add(px); planeY.Add(py); planeZ.Add(pz); planeD.Add(d);
                }

                // Four planes is the fewest that close a volume.
                if (planeX.Count - planesBefore < 4)
                {
                    planeX.RemoveRange(planesBefore, planeX.Count - planesBefore);
                    planeY.RemoveRange(planesBefore, planeY.Count - planesBefore);
                    planeZ.RemoveRange(planesBefore, planeZ.Count - planesBefore);
                    planeD.RemoveRange(planesBefore, planeD.Count - planesBefore);
                    continue;
                }

                starts.Add(planeX.Count);
                lows.Add(new BspFile.Vector3(x0, y0, z0));
                highs.Add(new BspFile.Vector3(x1, y1, z1));
            }

            result.nx = planeX.ToArray(); result.ny = planeY.ToArray();
            result.nz = planeZ.ToArray(); result.nd = planeD.ToArray();
            result.firstPlane = starts.ToArray();
            result.mins = lows.ToArray();
            result.maxs = highs.ToArray();

            for (int i = 0; i < result.mins.Length; i++)
            {
                result.allMins = new BspFile.Vector3(MathF.Min(result.allMins.X, result.mins[i].X),
                    MathF.Min(result.allMins.Y, result.mins[i].Y), MathF.Min(result.allMins.Z, result.mins[i].Z));
                result.allMaxs = new BspFile.Vector3(MathF.Max(result.allMaxs.X, result.maxs[i].X),
                    MathF.Max(result.allMaxs.Y, result.maxs[i].Y), MathF.Max(result.allMaxs.Z, result.maxs[i].Z));
            }

            // Bucketed on the floor plan only. Pieces are tall as often as they are wide, and what a
            // point query wants is the handful standing over its own spot.
            var buckets = new Dictionary<long, List<int>>();

            for (int i = 0; i < result.mins.Length; i++)
            {
                int gx0 = Cell(result.mins[i].X), gx1 = Cell(result.maxs[i].X);
                int gy0 = Cell(result.mins[i].Y), gy1 = Cell(result.maxs[i].Y);

                for (int gx = gx0; gx <= gx1; gx++)
                {
                    for (int gy = gy0; gy <= gy1; gy++)
                    {
                        long key = Key(gx, gy);
                        if (!buckets.TryGetValue(key, out var list))
                            buckets[key] = list = [];

                        list.Add(i);
                    }
                }
            }

            var items = new List<int>();
            foreach (var (key, list) in buckets)
            {
                result.cells[key] = (items.Count, list.Count);
                items.AddRange(list);
            }

            result.cellItems = items.ToArray();
            return result;
        }

        /// <summary>Whether <paramref name="point"/> is strictly inside any piece.</summary>
        public bool Contains(BspFile.Vector3 point)
        {
            if (point.Z <= allMins.Z || point.Z >= allMaxs.Z || point.X <= allMins.X || point.X >= allMaxs.X ||
                point.Y <= allMins.Y || point.Y >= allMaxs.Y)
                return false;

            if (!cells.TryGetValue(Key(Cell(point.X), Cell(point.Y)), out var bucket))
                return false;

            for (int k = bucket.First; k < bucket.First + bucket.Count; k++)
            {
                int i = cellItems[k];
                var lo = mins[i];
                var hi = maxs[i];

                if (point.X <= lo.X || point.X >= hi.X || point.Y <= lo.Y || point.Y >= hi.Y ||
                    point.Z <= lo.Z || point.Z >= hi.Z)
                    continue;

                bool inside = true;
                for (int p = firstPlane[i]; p < firstPlane[i + 1]; p++)
                {
                    if (nx[p] * point.X + ny[p] * point.Y + nz[p] * point.Z - nd[p] > -Margin)
                    {
                        inside = false;
                        break;
                    }
                }

                if (inside)
                    return true;
            }

            return false;
        }

        private static int Cell(float v) => (int)MathF.Floor(v / CellSize);

        private static long Key(int gx, int gy) => ((long)gx << 32) | (uint)gy;
    }
}
