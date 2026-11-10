using System;
using System.Collections.Generic;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That the triangle mesh's faster walks answer exactly what the walks they replaced answered.
    ///
    /// <see cref="TriangleMesh.Blocks"/> now tests children before pushing them and walks the nearer
    /// first, works the segment's half of the box test out once and does the three axes side by side,
    /// and tests a leaf's triangles eight at a time. <see cref="TriangleMesh.TryTraceSurface"/> and
    /// <see cref="TriangleMesh.TryTraceHull"/> share the new box test. None of that may change an
    /// answer, to the bit - the meshes are compared byte for byte - so these hold each to the old code,
    /// kept here verbatim, over the same tree: random triangles, terrain-like grids full of shared
    /// edges and coplanar neighbours, and boxes; segments of every direction, axis-aligned ones,
    /// ones too short along an axis to divide by, ones that start or end exactly on a surface, and
    /// ones of no length at all.
    /// </summary>
    public class TriangleTraceHotPathTests
    {
        private const float SegmentEpsilon = 1e-6f;
        private const float SegmentEnd = 1f + SegmentEpsilon;

        // ---- the walks as they were ------------------------------------------------------------

        private sealed class OldMesh(TriangleMesh mesh)
        {
            private readonly (BspFile.Vector3 Mins, BspFile.Vector3 Maxs, int Right, int First, int Count)[] bvh =
                mesh.NodesForTests();

            private readonly BspFile.Vector3[] vertices = mesh.VerticesForTests();
            private readonly int[] contents = mesh.ContentsForTests();

            public bool Blocks(BspFile.Vector3 a, BspFile.Vector3 b, int mask)
            {
                if (bvh.Length == 0)
                    return false;

                Span<int> stack = stackalloc int[64];
                int top = 0;
                stack[top++] = 0;

                while (top > 0)
                {
                    int index = stack[--top];
                    var node = bvh[index];

                    if (!SegmentHitsBox(a, b, node.Mins, node.Maxs))
                        continue;

                    if (node.Count == 0)
                    {
                        if (top + 2 <= stack.Length)
                        {
                            stack[top++] = index + 1;
                            stack[top++] = node.Right;
                        }
                        continue;
                    }

                    for (int i = node.First; i < node.First + node.Count; i++)
                    {
                        if ((contents[i] & mask) == 0)
                            continue;

                        int t = i * 3;
                        if (TryHitTriangle(a, b, vertices[t], vertices[t + 1], vertices[t + 2], out _))
                            return true;
                    }
                }

                return false;
            }

            public bool TryTraceSurface(BspFile.Vector3 a, BspFile.Vector3 b, int mask,
                out float fraction, out BspFile.Vector3 normal)
            {
                fraction = 1f;
                normal = default;

                if (bvh.Length == 0)
                    return false;

                bool found = false;

                Span<int> stack = stackalloc int[64];
                int top = 0;
                stack[top++] = 0;

                while (top > 0)
                {
                    int index = stack[--top];
                    var node = bvh[index];

                    if (!SegmentHitsBox(a, b, node.Mins, node.Maxs))
                        continue;

                    if (node.Count == 0)
                    {
                        if (top + 2 <= stack.Length)
                        {
                            stack[top++] = index + 1;
                            stack[top++] = node.Right;
                        }
                        continue;
                    }

                    int nearest = -1;
                    float hit = found ? fraction : float.PositiveInfinity;

                    for (int i = node.First; i < node.First + node.Count; i++)
                    {
                        if ((contents[i] & mask) == 0)
                            continue;

                        int t = i * 3;
                        if (!TryHitTriangle(a, b, vertices[t], vertices[t + 1], vertices[t + 2], out float at))
                            continue;

                        if (at >= hit)
                            continue;

                        hit = at;
                        nearest = i;
                    }

                    if (nearest >= 0)
                    {
                        fraction = hit;
                        normal = TriangleNormal(vertices[nearest * 3], vertices[nearest * 3 + 1],
                            vertices[nearest * 3 + 2]);
                        found = true;
                    }
                }

                if (found && fraction > 1f)
                    fraction = 1f;

                return found;
            }
        }

        private static BspFile.Vector3 TriangleNormal(BspFile.Vector3 v0, BspFile.Vector3 v1, BspFile.Vector3 v2)
        {
            float e1x = v1.X - v0.X, e1y = v1.Y - v0.Y, e1z = v1.Z - v0.Z;
            float e2x = v2.X - v0.X, e2y = v2.Y - v0.Y, e2z = v2.Z - v0.Z;

            float nx = e1y * e2z - e1z * e2y;
            float ny = e1z * e2x - e1x * e2z;
            float nz = e1x * e2y - e1y * e2x;

            float length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (length < 1e-6f)
                return new BspFile.Vector3(0, 0, 1);

            float sign = nz < 0 ? -1f : 1f;
            return new BspFile.Vector3(sign * nx / length, sign * ny / length, sign * nz / length);
        }

        private static bool TryHitTriangle(BspFile.Vector3 a, BspFile.Vector3 b,
            BspFile.Vector3 v0, BspFile.Vector3 v1, BspFile.Vector3 v2, out float fraction)
        {
            fraction = 1f;
            const float Epsilon = 1e-6f;

            float dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;

            float e1x = v1.X - v0.X, e1y = v1.Y - v0.Y, e1z = v1.Z - v0.Z;
            float e2x = v2.X - v0.X, e2y = v2.Y - v0.Y, e2z = v2.Z - v0.Z;

            float px = dy * e2z - dz * e2y;
            float py = dz * e2x - dx * e2z;
            float pz = dx * e2y - dy * e2x;

            float det = e1x * px + e1y * py + e1z * pz;
            if (MathF.Abs(det) < Epsilon)
                return false;

            float inverse = 1f / det;

            float tx = a.X - v0.X, ty = a.Y - v0.Y, tz = a.Z - v0.Z;
            float u = (tx * px + ty * py + tz * pz) * inverse;
            if (u < 0f || u > 1f) return false;

            float qx = ty * e1z - tz * e1y;
            float qy = tz * e1x - tx * e1z;
            float qz = tx * e1y - ty * e1x;

            float v = (dx * qx + dy * qy + dz * qz) * inverse;
            if (v < 0f || u + v > 1f) return false;

            float t = (e2x * qx + e2y * qy + e2z * qz) * inverse;
            if (t is <= SegmentEpsilon or > SegmentEnd)
                return false;

            fraction = t;
            return true;
        }

        private static bool SegmentHitsBox(BspFile.Vector3 a, BspFile.Vector3 b,
            BspFile.Vector3 mins, BspFile.Vector3 maxs)
        {
            float tMin = 0f, tMax = SegmentEnd;

            return Slab(a.X, b.X - a.X, mins.X, maxs.X, ref tMin, ref tMax)
                && Slab(a.Y, b.Y - a.Y, mins.Y, maxs.Y, ref tMin, ref tMax)
                && Slab(a.Z, b.Z - a.Z, mins.Z, maxs.Z, ref tMin, ref tMax);
        }

        private static bool Slab(float origin, float delta, float min, float max, ref float tMin, ref float tMax)
        {
            if (MathF.Abs(delta) < 1e-6f)
                return origin >= min && origin <= max;

            float inverse = 1f / delta;
            float t0 = (min - origin) * inverse;
            float t1 = (max - origin) * inverse;

            if (t0 > t1) (t0, t1) = (t1, t0);

            tMin = MathF.Max(tMin, t0);
            tMax = MathF.Min(tMax, t1);

            return tMin <= tMax;
        }

        // ---- geometry -------------------------------------------------------------------------

        private static readonly int[] ContentsChoices = [0x1, 0x1, 0x1, 0x2, 0x1 | 0x2, 0x8];
        private static readonly int[] Masks = [0x1, 0x2, 0x1 | 0x2, 0x8, 0x4];

        /// <summary>Triangles at assorted angles and sizes, some of them slivers or degenerate.</summary>
        private static void AddField(List<BspFile.Vector3> v, List<int> c, Random rng, int count)
        {
            for (int i = 0; i < count; i++)
            {
                var a = new BspFile.Vector3(Coord(rng, 400), Coord(rng, 400), Coord(rng, 400));
                float spread = rng.Next(4) switch { 0 => 1f, 1 => 0.003f, _ => 120f };
                var b = new BspFile.Vector3(a.X + Coord(rng, spread), a.Y + Coord(rng, spread), a.Z + Coord(rng, spread));
                var d = rng.Next(10) == 0
                    ? b // degenerate: two corners the same
                    : new BspFile.Vector3(a.X + Coord(rng, spread), a.Y + Coord(rng, spread), a.Z + Coord(rng, spread));

                v.Add(a); v.Add(b); v.Add(d);
                c.Add(ContentsChoices[rng.Next(ContentsChoices.Length)]);
            }
        }

        /// <summary>
        /// A displacement-like height field: a grid on whole units with heights on halves, two
        /// triangles a cell, so neighbours share edges, flat runs are coplanar, and a segment down a
        /// grid line or through a vertex is easy to hit exactly.
        /// </summary>
        private static void AddTerrain(List<BspFile.Vector3> v, List<int> c, Random rng, int size, float cell,
            float originX, float originY, float baseZ)
        {
            var heights = new float[size + 1, size + 1];
            for (int x = 0; x <= size; x++)
                for (int y = 0; y <= size; y++)
                    heights[x, y] = rng.Next(3) == 0 ? baseZ : baseZ + MathF.Round((float)rng.NextDouble() * 96f) * 0.5f;

            BspFile.Vector3 P(int x, int y) => new(originX + x * cell, originY + y * cell, heights[x, y]);

            int contents = ContentsChoices[rng.Next(ContentsChoices.Length)];
            for (int x = 0; x < size; x++)
            {
                for (int y = 0; y < size; y++)
                {
                    v.Add(P(x, y)); v.Add(P(x + 1, y)); v.Add(P(x + 1, y + 1));
                    v.Add(P(x, y)); v.Add(P(x + 1, y + 1)); v.Add(P(x, y + 1));
                    c.Add(contents);
                    c.Add(contents);
                }
            }
        }

        /// <summary>An axis-aligned box on whole units, twelve triangles, as a prop hull would be.</summary>
        private static void AddBox(List<BspFile.Vector3> v, List<int> c, Random rng)
        {
            float x0 = MathF.Round(Coord(rng, 300)), y0 = MathF.Round(Coord(rng, 300)), z0 = MathF.Round(Coord(rng, 300));
            float x1 = x0 + rng.Next(1, 80), y1 = y0 + rng.Next(1, 80), z1 = z0 + rng.Next(1, 80);
            var p = new BspFile.Vector3[8];
            for (int i = 0; i < 8; i++)
                p[i] = new BspFile.Vector3((i & 1) == 0 ? x0 : x1, (i & 2) == 0 ? y0 : y1, (i & 4) == 0 ? z0 : z1);

            int[] faces = [0, 1, 3, 2, 4, 5, 7, 6, 0, 1, 5, 4, 2, 3, 7, 6, 0, 2, 6, 4, 1, 3, 7, 5];
            int contents = ContentsChoices[rng.Next(ContentsChoices.Length)];
            for (int f = 0; f < 6; f++)
            {
                var (q0, q1, q2, q3) = (p[faces[f * 4]], p[faces[f * 4 + 1]], p[faces[f * 4 + 2]], p[faces[f * 4 + 3]]);
                v.Add(q0); v.Add(q1); v.Add(q2);
                v.Add(q0); v.Add(q2); v.Add(q3);
                c.Add(contents);
                c.Add(contents);
            }
        }

        private static float Coord(Random rng, float range) => (float)(rng.NextDouble() * 2 - 1) * range;

        private static TriangleMesh World(int seed, out List<BspFile.Vector3> vertices)
        {
            var rng = new Random(seed);
            var v = new List<BspFile.Vector3>();
            var c = new List<int>();

            // Sizes chosen so the leaves come out of every size from four to eight, and the triangle
            // count lands on every remainder, which is where a lane read past the end would show.
            AddField(v, c, rng, 50 + rng.Next(120));
            AddTerrain(v, c, rng, 6 + rng.Next(8), 32f, -200f, -200f, 0f);
            AddTerrain(v, c, rng, 3 + rng.Next(4), 64f, 50f, -100f, -40f);
            for (int i = 0; i < 6 + rng.Next(8); i++)
                AddBox(v, c, rng);

            vertices = v;
            var mesh = new TriangleMesh();
            mesh.Build(v.ToArray(), c.ToArray());
            return mesh;
        }

        /// <summary>
        /// A segment, often an awkward one: along an axis, too short along one to divide by, starting
        /// or ending on a vertex or on the plane of a flat run of terrain, or of no length at all.
        /// </summary>
        private static (BspFile.Vector3 A, BspFile.Vector3 B) Segment(Random rng, List<BspFile.Vector3> vertices)
        {
            BspFile.Vector3 Anywhere() => new(Coord(rng, 500), Coord(rng, 500), Coord(rng, 500));
            BspFile.Vector3 Vertex() => vertices[rng.Next(vertices.Count)];

            var a = rng.Next(3) == 0 ? Vertex() : Anywhere();
            var b = rng.Next(3) == 0 ? Vertex() : Anywhere();

            switch (rng.Next(10))
            {
                case 0: // straight down a column, as the floor finder traces
                    return (a, new BspFile.Vector3(a.X, a.Y, a.Z - (float)rng.NextDouble() * 600f));
                case 1: // along x only
                    return (a, new BspFile.Vector3(b.X, a.Y, a.Z));
                case 2: // level, as a sight line between two floors at one height is
                    return (a, new BspFile.Vector3(b.X, b.Y, a.Z));
                case 3: // barely moving along one axis: either side of the millionth the box test divides by
                {
                    float tiny = rng.Next(4) switch
                    {
                        0 => 1e-6f,
                        1 => MathF.BitDecrement(1e-6f),
                        2 => -1e-6f,
                        _ => MathF.BitIncrement(1e-6f),
                    };
                    return (a, new BspFile.Vector3(b.X, b.Y, a.Z + tiny));
                }
                case 4: // no length
                    return (a, a);
                case 5: // from a point a hair past a vertex, ending on it
                {
                    var v = Vertex();
                    return (new BspFile.Vector3(v.X + Coord(rng, 30), v.Y + Coord(rng, 30), v.Z + Coord(rng, 30)), v);
                }
                case 6: // between whole-unit points, as grid samples are
                    return (new BspFile.Vector3(MathF.Round(a.X), MathF.Round(a.Y), MathF.Round(a.Z)),
                        new BspFile.Vector3(MathF.Round(b.X), MathF.Round(b.Y), MathF.Round(b.Z)));
                case 7: // through the middle of a triangle, often a short way: what reaches the slivers,
                        // whose determinant is small enough for the epsilon on it to decide
                {
                    int t = rng.Next(vertices.Count / 3) * 3;
                    var (v0, v1, v2) = (vertices[t], vertices[t + 1], vertices[t + 2]);
                    var centre = new BspFile.Vector3((v0.X + v1.X + v2.X) / 3f, (v0.Y + v1.Y + v2.Y) / 3f,
                        (v0.Z + v1.Z + v2.Z) / 3f);
                    float reach = rng.Next(2) == 0 ? 0.05f : 40f;
                    var offset = new BspFile.Vector3(Coord(rng, reach), Coord(rng, reach), Coord(rng, reach));
                    float k = (float)rng.NextDouble();
                    return (new BspFile.Vector3(centre.X + offset.X, centre.Y + offset.Y, centre.Z + offset.Z),
                        new BspFile.Vector3(centre.X - offset.X * k, centre.Y - offset.Y * k, centre.Z - offset.Z * k));
                }
                default:
                    return (a, b);
            }
        }

        // ---- the tests ------------------------------------------------------------------------

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        public void BlocksAnswersAsTheOldWalkDid(int seed)
        {
            var mesh = World(seed, out var vertices);
            var old = new OldMesh(mesh);
            var rng = new Random(seed * 7919);

            int blocked = 0, clear = 0;
            for (int i = 0; i < 6000; i++)
            {
                var (a, b) = Segment(rng, vertices);
                int mask = Masks[rng.Next(Masks.Length)];

                bool expected = old.Blocks(a, b, mask);
                Assert.True(expected == mesh.Blocks(a, b, mask),
                    $"ray {i}: ({a.X}, {a.Y}, {a.Z}) to ({b.X}, {b.Y}, {b.Z}) mask {mask}: expected {expected}");

                if (expected) blocked++; else clear++;
            }

            // Both answers, plenty of each, or the comparison proves nothing.
            Assert.True(blocked > 600 && clear > 600, $"{blocked} blocked, {clear} clear");
        }

        [Theory]
        [InlineData(11)]
        [InlineData(12)]
        [InlineData(13)]
        [InlineData(14)]
        [InlineData(15)]
        [InlineData(16)]
        public void TheNearestSurfaceIsTheOldWalksToTheBit(int seed)
        {
            var mesh = World(seed, out var vertices);
            var old = new OldMesh(mesh);
            var rng = new Random(seed * 104729);

            int hits = 0;
            for (int i = 0; i < 6000; i++)
            {
                var (a, b) = Segment(rng, vertices);
                int mask = Masks[rng.Next(Masks.Length)];

                bool expected = old.TryTraceSurface(a, b, mask, out float wantFraction, out var wantNormal);
                bool actual = mesh.TryTraceSurface(a, b, mask, out float gotFraction, out var gotNormal);

                Assert.Equal(expected, actual);
                Assert.Equal(BitConverter.SingleToInt32Bits(wantFraction), BitConverter.SingleToInt32Bits(gotFraction));
                Assert.Equal(BitConverter.SingleToInt32Bits(wantNormal.X), BitConverter.SingleToInt32Bits(gotNormal.X));
                Assert.Equal(BitConverter.SingleToInt32Bits(wantNormal.Y), BitConverter.SingleToInt32Bits(gotNormal.Y));
                Assert.Equal(BitConverter.SingleToInt32Bits(wantNormal.Z), BitConverter.SingleToInt32Bits(gotNormal.Z));

                if (expected) hits++;
            }

            Assert.True(hits > 600, $"only {hits} hits");
        }

        /// <summary>
        /// The box test on its own, against boxes chosen to sit on the edges of it: faces through either
        /// end of the segment, boxes of no thickness, boxes the segment only grazes, segments barely
        /// moving along an axis. This is what every walk culls with - <see cref="TriangleMesh.TryTraceHull"/>
        /// included, whose grown boxes are the same sums as before - so an answer it changed would move
        /// a trace somewhere.
        /// </summary>
        [Theory]
        [InlineData(21)]
        [InlineData(22)]
        [InlineData(23)]
        [InlineData(24)]
        public void TheBoxTestAnswersAsTheOldOneDid(int seed)
        {
            var rng = new Random(seed);
            int hits = 0, misses = 0;

            float[] tiny = [0f, 1e-6f, -1e-6f, MathF.BitDecrement(1e-6f), MathF.BitIncrement(1e-6f), 5e-7f];

            for (int i = 0; i < 200_000; i++)
            {
                var a = new BspFile.Vector3(Coord(rng, 300), Coord(rng, 300), Coord(rng, 300));
                var b = new BspFile.Vector3(Coord(rng, 300), Coord(rng, 300), Coord(rng, 300));

                // Along some axes, barely move at all.
                for (int axis = 0; axis < 3; axis++)
                {
                    if (rng.Next(4) != 0)
                        continue;

                    float d = tiny[rng.Next(tiny.Length)];
                    b = axis switch
                    {
                        0 => new BspFile.Vector3(a.X + d, b.Y, b.Z),
                        1 => new BspFile.Vector3(b.X, a.Y + d, b.Z),
                        _ => new BspFile.Vector3(b.X, b.Y, a.Z + d),
                    };
                }

                float Bound(float from, float to)
                {
                    return rng.Next(6) switch
                    {
                        0 => from,                                  // through the start
                        1 => to,                                    // through the end
                        2 => from + (to - from) * (float)rng.NextDouble(), // through a point along it
                        3 => MathF.BitIncrement(to),
                        4 => MathF.BitDecrement(from),
                        _ => Coord(rng, 300),
                    };
                }

                float x0 = Bound(a.X, b.X), x1 = rng.Next(5) == 0 ? x0 : Bound(a.X, b.X);
                float y0 = Bound(a.Y, b.Y), y1 = rng.Next(5) == 0 ? y0 : Bound(a.Y, b.Y);
                float z0 = Bound(a.Z, b.Z), z1 = rng.Next(5) == 0 ? z0 : Bound(a.Z, b.Z);

                var mins = new BspFile.Vector3(MathF.Min(x0, x1), MathF.Min(y0, y1), MathF.Min(z0, z1));
                var maxs = new BspFile.Vector3(MathF.Max(x0, x1), MathF.Max(y0, y1), MathF.Max(z0, z1));

                bool expected = SegmentHitsBox(a, b, mins, maxs);
                Assert.True(expected == TriangleMesh.SegmentHitsBoxForTests(a, b, mins, maxs),
                    $"box {i}: ({a.X}, {a.Y}, {a.Z}) to ({b.X}, {b.Y}, {b.Z}) against ({mins.X}, {mins.Y}, {mins.Z}) - ({maxs.X}, {maxs.Y}, {maxs.Z})");

                if (expected) hits++; else misses++;
            }

            Assert.True(hits > 20_000 && misses > 20_000, $"{hits} hits, {misses} misses");
        }

        /// <summary>
        /// A mesh small enough to be one leaf, and one whose triangle count leaves the last leaf short:
        /// the lanes past the end of a leaf read padding, and must never be what blocks.
        /// </summary>
        [Fact]
        public void TheLanesPastTheLastTriangleNeverBlock()
        {
            for (int count = 1; count <= 17; count++)
            {
                var rng = new Random(count);
                var v = new List<BspFile.Vector3>();
                var c = new List<int>();
                AddField(v, c, rng, count);

                var mesh = new TriangleMesh();
                mesh.Build(v.ToArray(), c.ToArray());
                var old = new OldMesh(mesh);

                for (int i = 0; i < 3000; i++)
                {
                    var (a, b) = Segment(rng, v);
                    int mask = Masks[rng.Next(Masks.Length)];
                    Assert.Equal(old.Blocks(a, b, mask), mesh.Blocks(a, b, mask));
                }
            }
        }

        /// <summary>
        /// Segments that meet a surface exactly at either end of the stretch they own - a millionth of
        /// the way along, and a millionth past the end - and a float either side of each.
        ///
        /// Random segments essentially never land there, so a test of them alone cannot tell
        /// <c>&lt;=</c> from <c>&lt;</c> at either end. A flat floor on a power-of-two grid can: dropped
        /// straight down a power-of-two length onto it, Moller-Trumbore's fraction comes out as the
        /// start's height over that length with no rounding but the division's, so the heights below
        /// put the hit at exactly the boundary or exactly a float off it.
        /// </summary>
        [Fact]
        public void HitsAtTheEndsOfTheSegmentAreDecidedAsBefore()
        {
            var v = new List<BspFile.Vector3>();
            var c = new List<int>();
            const int Size = 8;

            for (int x = 0; x < Size; x++)
            {
                for (int y = 0; y < Size; y++)
                {
                    BspFile.Vector3 P(int i, int j) => new(-128f + 32f * i, -128f + 32f * j, 0f);
                    v.Add(P(x, y)); v.Add(P(x + 1, y)); v.Add(P(x + 1, y + 1));
                    v.Add(P(x, y)); v.Add(P(x + 1, y + 1)); v.Add(P(x, y + 1));
                    c.Add(0x1);
                    c.Add(0x1);
                }
            }

            var mesh = new TriangleMesh();
            mesh.Build(v.ToArray(), c.ToArray());
            var old = new OldMesh(mesh);

            float[] fractions =
            [
                SegmentEpsilon, MathF.BitIncrement(SegmentEpsilon), MathF.BitDecrement(SegmentEpsilon),
                SegmentEnd, MathF.BitIncrement(SegmentEnd), MathF.BitDecrement(SegmentEnd), 0.5f,
            ];

            int nearRefused = 0, farTaken = 0;
            var rng = new Random(3);

            foreach (float length in new[] { 1f, 2f, 0.5f, 64f })
            {
                foreach (float fraction in fractions)
                {
                    for (int k = 0; k < 40; k++)
                    {
                        // Inside a cell and off its diagonal, so one triangle and no edge is in the way.
                        int cx = rng.Next(Size), cy = rng.Next(Size);
                        float x = -128f + 32f * cx + (k % 2 == 0 ? 20.5f : 7.25f);
                        float y = -128f + 32f * cy + (k % 2 == 0 ? 7.25f : 20.5f);

                        var a = new BspFile.Vector3(x, y, fraction * length);
                        var b = new BspFile.Vector3(x, y, fraction * length - length);

                        bool expected = old.Blocks(a, b, 0x1);
                        Assert.Equal(expected, mesh.Blocks(a, b, 0x1));

                        bool wanted = old.TryTraceSurface(a, b, 0x1, out float wantFraction, out _);
                        Assert.Equal(wanted, mesh.TryTraceSurface(a, b, 0x1, out float gotFraction, out _));
                        Assert.Equal(BitConverter.SingleToInt32Bits(wantFraction), BitConverter.SingleToInt32Bits(gotFraction));

                        if (fraction == SegmentEpsilon && !expected) nearRefused++;
                        if (fraction == SegmentEnd && expected) farTaken++;
                    }
                }
            }

            // The fixture really does put hits exactly on both boundaries: refused at the near end,
            // taken at the far one.
            Assert.Equal(160, nearRefused);
            Assert.Equal(160, farTaken);
        }
    }
}
