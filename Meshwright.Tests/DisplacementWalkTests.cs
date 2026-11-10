using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Threading.Tasks;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That <see cref="TriangleMesh.Blocks"/> - down the wide tree, asking first the leaves that
    /// blocked last - answers exactly what it answered before either existed.
    ///
    /// Each answer is held to two copies of old code kept here verbatim: the walk as it stood before
    /// the wide tree (master at beda4a9: children box-tested before they are pushed, nearer first, a
    /// leaf's triangles eight at a time), and the plain one-node-at-a-time scalar walk that one was
    /// itself held to. Both run over the same tree and the same triangles as the mesh under test, so
    /// they decide the same leaves on the same boxes.
    ///
    /// The geometry is what the walk is for and what is hardest on it: displacement grids, flat and
    /// steep, far from the origin and near it, with cells collapsed to slivers and points, sideways
    /// offsets, and neighbours that share edges. The segments are sight lines at eye height between
    /// sweeps of corner points, as the visibility pass asks them - in runs, because what the mesh
    /// remembers from one answer is what it asks first for the next - and lines that are hard on any
    /// triangle test: through a vertex exactly, along an edge, lying in the plane of flat terrain,
    /// ending exactly on the surface or starting on it, straight down a column, barely moving along
    /// an axis. Masks change within a run, so a leaf remembered under one mask is asked under another.
    /// </summary>
    public class DisplacementWalkTests
    {
        private const float SegmentEpsilon = 1e-6f;
        private const float SegmentEnd = 1f + SegmentEpsilon;
        private const int LanePadding = 7;

        // ---- master's walk, verbatim ----------------------------------------------------------

        private struct BvhNode
        {
            public BspFile.Vector3 Mins, Maxs;
            public int Right;        // right child index; left is always this node + 1
            public int First, Count; // triangle range when a leaf, Count 0 when interior
        }

        /// <summary>
        /// <see cref="TriangleMesh.Blocks"/> as it was at beda4a9, over the mesh's own tree: the walk,
        /// the box test and both leaf tests, copied unchanged, with the edge arrays rebuilt the way
        /// the mesh builds them.
        /// </summary>
        private sealed class MasterMesh
        {
            private readonly BvhNode[] bvh;
            private readonly BspFile.Vector3[] vertices;
            private readonly int[] contents;
            private readonly float[] v0x, v0y, v0z, e1x, e1y, e1z, e2x, e2y, e2z;

            public MasterMesh(TriangleMesh mesh)
            {
                var nodes = mesh.NodesForTests();
                bvh = new BvhNode[nodes.Length];
                for (int i = 0; i < nodes.Length; i++)
                    bvh[i] = new BvhNode
                    {
                        Mins = nodes[i].Mins, Maxs = nodes[i].Maxs, Right = nodes[i].Right,
                        First = nodes[i].First, Count = nodes[i].Count,
                    };

                vertices = mesh.VerticesForTests();
                int count = vertices.Length / 3;

                var c = mesh.ContentsForTests();
                contents = new int[count + LanePadding];
                Array.Copy(c, contents, count);

                int padded = count + LanePadding;
                v0x = new float[padded]; v0y = new float[padded]; v0z = new float[padded];
                e1x = new float[padded]; e1y = new float[padded]; e1z = new float[padded];
                e2x = new float[padded]; e2y = new float[padded]; e2z = new float[padded];

                for (int i = 0; i < count; i++)
                {
                    var a = vertices[i * 3];
                    var b = vertices[i * 3 + 1];
                    var d = vertices[i * 3 + 2];

                    v0x[i] = a.X; v0y[i] = a.Y; v0z[i] = a.Z;

                    e1x[i] = b.X - a.X; e1y[i] = b.Y - a.Y; e1z[i] = b.Z - a.Z;
                    e2x[i] = d.X - a.X; e2y[i] = d.Y - a.Y; e2z[i] = d.Z - a.Z;
                }
            }

            public bool Blocks(BspFile.Vector3 a, BspFile.Vector3 b, int mask)
            {
                if (bvh.Length == 0)
                    return false;

                var segment = new SegmentBoxes(a, b);

                if (!segment.Hits(in bvh[0], out _))
                    return false;

                bool wide = Vector256.IsHardwareAccelerated;

                Span<int> stack = stackalloc int[64];
                int top = 0;
                stack[top++] = 0;

                while (top > 0)
                {
                    int index = stack[--top];
                    ref readonly var node = ref bvh[index];

                    if (node.Count != 0)
                    {
                        if (wide
                                ? LeafBlocksWide(a, b, mask, node.First, node.Count)
                                : LeafBlocksScalar(a, b, mask, node.First, node.Count))
                        {
                            return true;
                        }

                        continue;
                    }

                    int left = index + 1;
                    int right = node.Right;

                    bool hitLeft = segment.Hits(in bvh[left], out float enterLeft);
                    bool hitRight = segment.Hits(in bvh[right], out float enterRight);

                    if (top + 2 > stack.Length)
                        continue;

                    // The nearer child goes on last, so it comes off first.
                    if (hitLeft && hitRight)
                    {
                        bool leftFirst = enterLeft <= enterRight;
                        stack[top++] = leftFirst ? right : left;
                        stack[top++] = leftFirst ? left : right;
                    }
                    else if (hitLeft)
                    {
                        stack[top++] = left;
                    }
                    else if (hitRight)
                    {
                        stack[top++] = right;
                    }
                }

                return false;
            }

            private bool LeafBlocksScalar(BspFile.Vector3 a, BspFile.Vector3 b, int mask, int first, int count)
            {
                for (int i = first; i < first + count; i++)
                {
                    if ((contents[i] & mask) == 0)
                        continue;

                    int t = i * 3;
                    if (TryHitTriangle(a, b, vertices[t], vertices[t + 1], vertices[t + 2], out _))
                        return true;
                }

                return false;
            }

            private static readonly Vector256<int> LaneIndex = Vector256.Create(0, 1, 2, 3, 4, 5, 6, 7);

            private bool LeafBlocksWide(BspFile.Vector3 a, BspFile.Vector3 b, int mask, int first, int count)
            {
                const float Epsilon = 1e-6f;
                int lanes = Vector256<float>.Count;

                var maskLanes = Vector256.Create(mask);
                var zero = Vector256<float>.Zero;
                var one = Vector256<float>.One;

                float sdx = b.X - a.X, sdy = b.Y - a.Y, sdz = b.Z - a.Z;
                var dx = Vector256.Create(sdx);
                var dy = Vector256.Create(sdy);
                var dz = Vector256.Create(sdz);

                var ax = Vector256.Create(a.X);
                var ay = Vector256.Create(a.Y);
                var az = Vector256.Create(a.Z);

                for (int i = first, end = first + count; i < end; i += lanes)
                {
                    var inLeaf = Vector256.LessThan(LaneIndex, Vector256.Create(end - i));
                    var wanted = Vector256.LoadUnsafe(ref contents[i]) & maskLanes;
                    var live = (inLeaf & ~Vector256.Equals(wanted, Vector256<int>.Zero)).AsSingle();

                    if (live.Equals(zero))
                        continue;

                    var v1x = Vector256.LoadUnsafe(ref e1x[i]);
                    var v1y = Vector256.LoadUnsafe(ref e1y[i]);
                    var v1z = Vector256.LoadUnsafe(ref e1z[i]);

                    var v2x = Vector256.LoadUnsafe(ref e2x[i]);
                    var v2y = Vector256.LoadUnsafe(ref e2y[i]);
                    var v2z = Vector256.LoadUnsafe(ref e2z[i]);

                    // p = d x e2
                    var px = dy * v2z - dz * v2y;
                    var py = dz * v2x - dx * v2z;
                    var pz = dx * v2y - dy * v2x;

                    var det = v1x * px + v1y * py + v1z * pz;

                    live &= ~Vector256.LessThan(Vector256.Abs(det), Vector256.Create(Epsilon)).AsSingle();
                    if (live.Equals(zero))
                        continue;

                    // A lane already dead may hold a zero determinant; whatever its quotient comes to, it
                    // is masked off and decides nothing. Live lanes divide exactly as the scalar test does.
                    var inverse = one / det;

                    var tx = ax - Vector256.LoadUnsafe(ref v0x[i]);
                    var ty = ay - Vector256.LoadUnsafe(ref v0y[i]);
                    var tz = az - Vector256.LoadUnsafe(ref v0z[i]);

                    var u = (tx * px + ty * py + tz * pz) * inverse;

                    live &= ~(Vector256.LessThan(u, zero) | Vector256.GreaterThan(u, one)).AsSingle();
                    if (live.Equals(zero))
                        continue;

                    // q = t x e1
                    var qx = ty * v1z - tz * v1y;
                    var qy = tz * v1x - tx * v1z;
                    var qz = tx * v1y - ty * v1x;

                    var v = (dx * qx + dy * qy + dz * qz) * inverse;

                    live &= ~(Vector256.LessThan(v, zero) | Vector256.GreaterThan(u + v, one)).AsSingle();
                    if (live.Equals(zero))
                        continue;

                    var at = (v2x * qx + v2y * qy + v2z * qz) * inverse;

                    // The segment's own half-open stretch; see SegmentEpsilon.
                    live &= ~(Vector256.LessThanOrEqual(at, Vector256.Create(SegmentEpsilon))
                              | Vector256.GreaterThan(at, Vector256.Create(SegmentEnd))).AsSingle();

                    if (!live.Equals(zero))
                        return true;
                }

                return false;
            }

            private readonly struct SegmentBoxes
            {
                private readonly Vector128<float> origin;
                private readonly Vector128<float> inverse;

                private readonly Vector128<float> slabs;

                private static readonly Vector128<float> IdleEntry =
                    Vector128.Create(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity, 0f);

                private static readonly Vector128<float> IdleExit =
                    Vector128.Create(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity, SegmentEnd);

                private readonly Vector128<float> flat;
                private readonly bool anyFlat;

                public SegmentBoxes(BspFile.Vector3 a, BspFile.Vector3 b)
                {
                    float dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;

                    bool flatX = MathF.Abs(dx) < 1e-6f;
                    bool flatY = MathF.Abs(dy) < 1e-6f;
                    bool flatZ = MathF.Abs(dz) < 1e-6f;

                    origin = Vector128.Create(a.X, a.Y, a.Z, 0f);

                    flat = Vector128.Create(Lane(flatX), Lane(flatY), Lane(flatZ), 0).AsSingle();
                    slabs = Vector128.Create(Lane(!flatX), Lane(!flatY), Lane(!flatZ), 0).AsSingle();
                    anyFlat = flatX || flatY || flatZ;

                    var quotient = Vector128<float>.One / Vector128.Create(dx, dy, dz, 1f);
                    inverse = Vector128.ConditionalSelect(slabs, quotient, Vector128<float>.Zero);

                }

                private static int Lane(bool on) => on ? -1 : 0;

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public static Vector128<float> MinsOf(in BvhNode node)
                    => Vector128.LoadUnsafe(ref Unsafe.As<BspFile.Vector3, float>(ref Unsafe.AsRef(in node.Mins)));

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public static Vector128<float> MaxsOf(in BvhNode node)
                    => Vector128.LoadUnsafe(ref Unsafe.As<BspFile.Vector3, float>(ref Unsafe.AsRef(in node.Maxs)));

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public bool Hits(in BvhNode node, out float enter) => Hits(MinsOf(in node), MaxsOf(in node), out enter);

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public bool Hits(Vector128<float> mins, Vector128<float> maxs, out float enter)
                {
                    if (anyFlat)
                    {
                        var inside = Vector128.GreaterThanOrEqual(origin, mins) & Vector128.LessThanOrEqual(origin, maxs);
                        if ((flat & ~inside).ExtractMostSignificantBits() != 0)
                        {
                            enter = 0f;
                            return false;
                        }
                    }

                    var t0 = (mins - origin) * inverse;
                    var t1 = (maxs - origin) * inverse;

                    var entry = Vector128.ConditionalSelect(slabs, Vector128.MinNative(t0, t1), IdleEntry);
                    var exit = Vector128.ConditionalSelect(slabs, Vector128.MaxNative(t0, t1), IdleExit);

                    entry = Vector128.MaxNative(entry, Vector128.Shuffle(entry, Vector128.Create(2, 3, 0, 1)));
                    entry = Vector128.MaxNative(entry, Vector128.Shuffle(entry, Vector128.Create(1, 0, 3, 2)));
                    exit = Vector128.MinNative(exit, Vector128.Shuffle(exit, Vector128.Create(2, 3, 0, 1)));
                    exit = Vector128.MinNative(exit, Vector128.Shuffle(exit, Vector128.Create(1, 0, 3, 2)));

                    enter = entry.ToScalar();
                    return enter <= exit.ToScalar();
                }
            }

            // ---- the walk master was held to: one node at a time, scalar, verbatim ----

            public bool BlocksScalar(BspFile.Vector3 a, BspFile.Vector3 b, int mask)
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

            /// <summary>
            /// Whether any triangle at all passes the triangle test, with no tree: the question
            /// <see cref="TriangleMesh.Blocks"/> means to answer, which the walk answers too unless a
            /// leaf's box turns the segment away from a triangle it crosses.
            /// </summary>
            public bool AnyTriangle(BspFile.Vector3 a, BspFile.Vector3 b, int mask)
            {
                for (int i = 0; i < vertices.Length / 3; i++)
                {
                    if ((contents[i] & mask) == 0)
                        continue;

                    int t = i * 3;
                    if (TryHitTriangle(a, b, vertices[t], vertices[t + 1], vertices[t + 2], out _))
                        return true;
                }

                return false;
            }
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
                return false; // parallel; a grazing hit is not worth chasing

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
        private static readonly int[] Masks = [0x1, 0x1, 0x1, 0x2, 0x1 | 0x2, 0x8, 0x4];

        private static float Coord(Random rng, float range) => (float)(rng.NextDouble() * 2 - 1) * range;

        /// <summary>
        /// One displacement as <see cref="BspDisplacements"/> lays it out: a grid of
        /// <c>2^power</c> cells a side over a quad, each corner pushed off it along the quad's normal
        /// and, sometimes, sideways too; two triangles a cell with the diagonal alternating in a
        /// checkerboard. The heights are flat, gently rolling, or steep enough to stand as a wall, on
        /// eighths of a unit like a compiled map's; some corners are dragged onto a neighbour, leaving
        /// slivers and triangles of no area.
        /// </summary>
        private static void AddDisplacement(List<BspFile.Vector3> v, List<int> c, Random rng,
            BspFile.Vector3 origin, BspFile.Vector3 across, BspFile.Vector3 along, BspFile.Vector3 normal)
        {
            int power = 2 + rng.Next(3);
            int size = 1 << power;
            int kind = rng.Next(4);   // 0 flat, 1 rolling, 2 steep, 3 terraced
            bool sideways = rng.Next(4) == 0;
            float amplitude = kind switch { 0 => 0f, 1 => 48f, 2 => 900f, _ => 96f };

            var grid = new BspFile.Vector3[size + 1, size + 1];
            for (int i = 0; i <= size; i++)
            {
                for (int j = 0; j <= size; j++)
                {
                    float s = (float)i / size, t = (float)j / size;
                    float height = kind switch
                    {
                        0 => 0f,
                        3 => MathF.Round((float)rng.NextDouble() * 3f) * 32f,
                        _ => MathF.Round((float)rng.NextDouble() * amplitude * 8f) / 8f,
                    };

                    float sx = sideways ? MathF.Round(Coord(rng, 40f) * 8f) / 8f : 0f;
                    float sy = sideways ? MathF.Round(Coord(rng, 40f) * 8f) / 8f : 0f;

                    grid[i, j] = new BspFile.Vector3(
                        origin.X + across.X * s + along.X * t + normal.X * height + sx,
                        origin.Y + across.Y * s + along.Y * t + normal.Y * height + sy,
                        origin.Z + across.Z * s + along.Z * t + normal.Z * height);
                }
            }

            // Corners dragged onto a neighbour: slivers, and cells folded to a line or a point.
            for (int k = rng.Next(4); k > 0; k--)
            {
                int i = rng.Next(size), j = rng.Next(size);
                grid[i, j] = grid[i + 1, rng.Next(2) == 0 ? j : j + 1];
            }

            int contents = ContentsChoices[rng.Next(ContentsChoices.Length)];

            for (int i = 0; i < size; i++)
            {
                for (int j = 0; j < size; j++)
                {
                    var p00 = grid[i, j];
                    var p10 = grid[i + 1, j];
                    var p11 = grid[i + 1, j + 1];
                    var p01 = grid[i, j + 1];

                    if (((i + j) & 1) == 0)
                    {
                        v.Add(p00); v.Add(p10); v.Add(p11);
                        v.Add(p00); v.Add(p11); v.Add(p01);
                    }
                    else
                    {
                        v.Add(p00); v.Add(p10); v.Add(p01);
                        v.Add(p10); v.Add(p11); v.Add(p01);
                    }

                    c.Add(contents);
                    c.Add(contents);
                }
            }
        }

        /// <summary>
        /// A patch of terrain: a few displacements side by side on a shared grid, so their edges meet
        /// exactly, laid flat at one height or on a slope, plus one standing as a wall, and a few loose
        /// triangles, some of no area. Placed near the origin or far out, where a float's last bit is a
        /// sixty-fourth of a unit or more.
        /// </summary>
        private static TriangleMesh Terrain(int seed, out List<BspFile.Vector3> vertices)
        {
            var rng = new Random(seed);
            var v = new List<BspFile.Vector3>();
            var c = new List<int>();

            float far = rng.Next(2) == 0 ? 0f : 12000f;
            float baseX = far + MathF.Round(Coord(rng, 300f)), baseY = -far + MathF.Round(Coord(rng, 300f));
            float baseZ = MathF.Round(Coord(rng, 200f));
            float side = rng.Next(2) == 0 ? 256f : 512f;
            float slope = rng.Next(3) == 0 ? 0.5f : 0f;

            int across = 2 + rng.Next(3), along = 2 + rng.Next(2);
            for (int i = 0; i < across; i++)
            {
                for (int j = 0; j < along; j++)
                {
                    var origin = new BspFile.Vector3(baseX + i * side, baseY + j * side, baseZ + i * side * slope);
                    AddDisplacement(v, c, rng, origin,
                        new BspFile.Vector3(side, 0f, side * slope), new BspFile.Vector3(0f, side, 0f),
                        new BspFile.Vector3(0f, 0f, 1f));
                }
            }

            // A wall of terrain standing on one edge of the patch.
            AddDisplacement(v, c, rng, new BspFile.Vector3(baseX, baseY, baseZ),
                new BspFile.Vector3(side, 0f, 0f), new BspFile.Vector3(0f, 0f, side), new BspFile.Vector3(0f, 1f, 0f));

            for (int i = rng.Next(20); i > 0; i--)
            {
                var a = new BspFile.Vector3(baseX + Coord(rng, side * across), baseY + Coord(rng, side * along),
                    baseZ + Coord(rng, 300f));
                var b = rng.Next(4) == 0 ? a : new BspFile.Vector3(a.X + Coord(rng, 80f), a.Y + Coord(rng, 80f), a.Z + Coord(rng, 80f));
                var d = rng.Next(4) == 0 ? b : new BspFile.Vector3(a.X + Coord(rng, 80f), a.Y + Coord(rng, 80f), a.Z + Coord(rng, 80f));
                v.Add(a); v.Add(b); v.Add(d);
                c.Add(ContentsChoices[rng.Next(ContentsChoices.Length)]);
            }

            vertices = v;
            var mesh = new TriangleMesh();
            mesh.Build(v.ToArray(), c.ToArray());
            return mesh;
        }

        /// <summary>
        /// A run of segments: one source of four corners and a centre at eye height over the terrain,
        /// one target the same, every corner to every corner, as the corner sweep traces them - or, one
        /// time in three, a run of segments built to be hard on the triangle test.
        /// </summary>
        private static IEnumerable<(BspFile.Vector3 A, BspFile.Vector3 B)> Run(Random rng, List<BspFile.Vector3> vertices)
        {
            BspFile.Vector3 Vertex() => vertices[rng.Next(vertices.Count)];

            if (rng.Next(3) != 0)
            {
                float eye = rng.Next(4) switch { 0 => 0f, 1 => 8f, _ => 64f };
                var from = Area(Vertex(), eye);
                var to = Area(Vertex(), eye);

                foreach (var s in from)
                    foreach (var t in to)
                        yield return (s, t);

                yield break;
            }

            for (int k = 0; k < 16; k++)
                yield return Awkward(rng, vertices);

            BspFile.Vector3[] Area(BspFile.Vector3 centre, float eye)
            {
                float half = rng.Next(3) switch { 0 => 12.5f, 1 => 50f, _ => 150f };
                return
                [
                    new(centre.X - half, centre.Y - half, centre.Z + eye),
                    new(centre.X + half, centre.Y - half, centre.Z + eye),
                    new(centre.X + half, centre.Y + half, centre.Z + eye + (rng.Next(3) == 0 ? 3f : 0f)),
                    new(centre.X - half, centre.Y + half, centre.Z + eye),
                    new(centre.X, centre.Y, centre.Z + eye),
                ];
            }
        }

        /// <summary>A segment built to sit on an edge of the triangle test or the box test.</summary>
        private static (BspFile.Vector3 A, BspFile.Vector3 B) Awkward(Random rng, List<BspFile.Vector3> vertices)
        {
            int tri = rng.Next(vertices.Count / 3) * 3;
            var (v0, v1, v2) = (vertices[tri], vertices[tri + 1], vertices[tri + 2]);
            BspFile.Vector3 Lerp(BspFile.Vector3 p, BspFile.Vector3 q, float s)
                => new(p.X + (q.X - p.X) * s, p.Y + (q.Y - p.Y) * s, p.Z + (q.Z - p.Z) * s);
            BspFile.Vector3 Off(BspFile.Vector3 p, float r) => new(p.X + Coord(rng, r), p.Y + Coord(rng, r), p.Z + Coord(rng, r));

            switch (rng.Next(11))
            {
                case 0: // through a vertex exactly, from either side
                {
                    var a = Off(v0, 200f);
                    return (a, Lerp(a, v0, 1f + (float)rng.NextDouble()));
                }
                case 1: // ending exactly on a vertex
                    return (Off(v1, 200f), v1);
                case 2: // starting exactly on a vertex
                    return (v2, Off(v2, 200f));
                case 3: // along an edge, in the triangle's own plane, overrunning both ends
                    return (Lerp(v0, v1, -0.5f), Lerp(v0, v1, 1.5f));
                case 4: // through the middle of an edge, crossing it
                {
                    var m = Lerp(v0, v2, 0.5f);
                    var a = Off(m, 100f);
                    return (a, Lerp(a, m, 2f));
                }
                case 5: // in the plane of the terrain, level at a vertex's height
                {
                    var a = Off(v0, 300f);
                    return (new BspFile.Vector3(a.X, a.Y, v0.Z), new BspFile.Vector3(v1.X + Coord(rng, 300f), v1.Y + Coord(rng, 300f), v0.Z));
                }
                case 6: // straight down a column through a vertex or an edge
                {
                    var p = rng.Next(2) == 0 ? v1 : Lerp(v1, v2, 0.5f);
                    return (new BspFile.Vector3(p.X, p.Y, p.Z + 100f), new BspFile.Vector3(p.X, p.Y, p.Z - (float)rng.NextDouble() * 200f));
                }
                case 7: // ending exactly on the surface, inside a triangle
                {
                    var p = Lerp(Lerp(v0, v1, 0.5f), v2, 0.25f);
                    return (Off(p, 150f), p);
                }
                case 8: // barely moving along an axis, either side of the millionth the box test divides by
                {
                    float tiny = rng.Next(4) switch
                    {
                        0 => 1e-6f,
                        1 => MathF.BitDecrement(1e-6f),
                        2 => -1e-6f,
                        _ => MathF.BitIncrement(1e-6f),
                    };
                    var a = Off(v0, 200f);
                    var b = Off(v1, 200f);
                    return rng.Next(2) == 0
                        ? (a, new BspFile.Vector3(b.X, b.Y, a.Z + tiny))
                        : (a, new BspFile.Vector3(a.X + tiny, b.Y, b.Z));
                }
                case 9: // of no length
                {
                    var a = Off(v0, 5f);
                    return (a, a);
                }
                default: // just touching the surface at the far end of a short drop
                {
                    var p = Lerp(v0, Lerp(v1, v2, 0.5f), 0.5f);
                    float drop = rng.Next(2) == 0 ? 1f : 64f;
                    float fraction = rng.Next(3) switch { 0 => SegmentEnd, 1 => MathF.BitIncrement(SegmentEnd), _ => 1f };
                    return (new BspFile.Vector3(p.X, p.Y, p.Z + drop * fraction), new BspFile.Vector3(p.X, p.Y, p.Z + drop * fraction - drop));
                }
            }
        }

        // ---- the tests ------------------------------------------------------------------------

        private static void CheckAgainstMaster(TriangleMesh mesh, List<BspFile.Vector3> vertices, int seed, int runs,
            ref int blocked, ref int clear)
        {
            var master = new MasterMesh(mesh);
            var rng = new Random(seed * 7919 + 17);
            int mask = 0x1;

            for (int r = 0; r < runs; r++)
            {
                int i = 0;
                foreach (var (a, b) in Run(rng, vertices))
                {
                    // Mostly one mask down a run, as the visibility pass asks; sometimes another.
                    if (rng.Next(12) == 0)
                        mask = Masks[rng.Next(Masks.Length)];

                    bool expected = master.Blocks(a, b, mask);
                    bool actual = mesh.Blocks(a, b, mask);

                    if (expected != actual)
                        Assert.Fail($"seed {seed} run {r} line {i}: ({a.X}, {a.Y}, {a.Z}) to ({b.X}, {b.Y}, {b.Z}) mask {mask}: expected {expected}");

                    Assert.Equal(expected, master.BlocksScalar(a, b, mask));

                    if (expected) blocked++; else clear++;
                    i++;
                }
            }
        }

        [Theory]
        [InlineData(1, true, true)]
        [InlineData(2, true, true)]
        [InlineData(3, true, true)]
        [InlineData(4, true, true)]
        [InlineData(5, true, true)]
        [InlineData(6, true, true)]
        [InlineData(7, true, false)]
        [InlineData(8, true, false)]
        [InlineData(9, false, true)]
        [InlineData(10, false, true)]
        [InlineData(11, false, false)]
        public void BlocksAnswersAsMastersWalkDid(int seed, bool wide, bool remember)
        {
            var mesh = Terrain(seed, out var vertices);
            mesh.UseForTests(wide, remember);

            if (wide && Vector256.IsHardwareAccelerated)
                Assert.NotEmpty(mesh.WideTreeForTests().Children);

            int blocked = 0, clear = 0;
            CheckAgainstMaster(mesh, vertices, seed, 600, ref blocked, ref clear);

            // Plenty of both, or the comparison proves nothing.
            Assert.True(blocked > 1500 && clear > 1500, $"{blocked} blocked, {clear} clear");
        }

        /// <summary>
        /// The same against the random triangles, terrain and boxes the older hot-path tests use, in
        /// runs - the leaves remembered from one answer asked first about the next.
        /// </summary>
        [Theory]
        [InlineData(31)]
        [InlineData(32)]
        [InlineData(33)]
        [InlineData(34)]
        public void BlocksAnswersAsMastersWalkDidOnLooseTriangles(int seed)
        {
            var rng = new Random(seed);
            var v = new List<BspFile.Vector3>();
            var c = new List<int>();

            for (int i = 0; i < 300 + rng.Next(300); i++)
            {
                var a = new BspFile.Vector3(Coord(rng, 400), Coord(rng, 400), Coord(rng, 400));
                float spread = rng.Next(4) switch { 0 => 1f, 1 => 0.003f, _ => 120f };
                var b = new BspFile.Vector3(a.X + Coord(rng, spread), a.Y + Coord(rng, spread), a.Z + Coord(rng, spread));
                var d = rng.Next(10) == 0 ? b : new BspFile.Vector3(a.X + Coord(rng, spread), a.Y + Coord(rng, spread), a.Z + Coord(rng, spread));
                v.Add(a); v.Add(b); v.Add(d);
                c.Add(ContentsChoices[rng.Next(ContentsChoices.Length)]);
            }

            var mesh = new TriangleMesh();
            mesh.Build(v.ToArray(), c.ToArray());

            int blocked = 0, clear = 0;
            CheckAgainstMaster(mesh, v, seed, 400, ref blocked, ref clear);
            Assert.True(blocked > 500 && clear > 500, $"{blocked} blocked, {clear} clear");
        }

        /// <summary>
        /// More meshes than a thread keeps blockers for, asked in turn on one thread, so they share
        /// places and push each other's out; and meshes too small to have a wide tree at all.
        /// </summary>
        [Fact]
        public void MeshesSharingAPlaceKeepTheirOwnAnswers()
        {
            var meshes = new List<(TriangleMesh Mesh, MasterMesh Master, List<BspFile.Vector3> Vertices)>();
            for (int s = 0; s < 21; s++)
            {
                TriangleMesh mesh;
                List<BspFile.Vector3> vertices;

                if (s % 7 == 6)
                {
                    // One leaf or a few: no wide tree, and the binary walk with memory.
                    var rng0 = new Random(s);
                    vertices = [];
                    var c = new List<int>();
                    for (int i = 0; i < 1 + s % 11; i++)
                    {
                        var a = new BspFile.Vector3(Coord(rng0, 100), Coord(rng0, 100), Coord(rng0, 20));
                        vertices.Add(a);
                        vertices.Add(new BspFile.Vector3(a.X + 60, a.Y, a.Z + Coord(rng0, 20)));
                        vertices.Add(new BspFile.Vector3(a.X, a.Y + 60, a.Z + Coord(rng0, 20)));
                        c.Add(0x1);
                    }

                    mesh = new TriangleMesh();
                    mesh.Build(vertices.ToArray(), c.ToArray());
                }
                else
                {
                    mesh = Terrain(100 + s, out vertices);
                }

                meshes.Add((mesh, new MasterMesh(mesh), vertices));
            }

            var rng = new Random(5);
            int blocked = 0, clear = 0;

            for (int r = 0; r < 3000; r++)
            {
                var (mesh, master, vertices) = meshes[rng.Next(meshes.Count)];
                int mask = Masks[rng.Next(Masks.Length)];

                foreach (var (a, b) in Run(rng, vertices))
                {
                    bool expected = master.Blocks(a, b, mask);
                    Assert.Equal(expected, mesh.Blocks(a, b, mask));
                    if (expected) blocked++; else clear++;
                }
            }

            Assert.True(blocked > 3000 && clear > 3000, $"{blocked} blocked, {clear} clear");
        }

        /// <summary>
        /// One mesh asked from many threads at once: what each thread remembers is its own, and no
        /// thread's answer depends on another's.
        /// </summary>
        [Fact]
        public void ThreadsKeepTheirOwnAnswers()
        {
            var mesh = Terrain(77, out var vertices);
            var master = new MasterMesh(mesh);

            var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

            Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 8 }, worker =>
            {
                var rng = new Random(worker);
                for (int r = 0; r < 60; r++)
                {
                    int mask = Masks[rng.Next(Masks.Length)];
                    foreach (var (a, b) in Run(rng, vertices))
                    {
                        if (master.Blocks(a, b, mask) != mesh.Blocks(a, b, mask))
                            failures.Add($"worker {worker} run {r}: ({a.X}, {a.Y}, {a.Z}) to ({b.X}, {b.Y}, {b.Z}) mask {mask}");
                    }
                }
            });

            Assert.Empty(failures);
        }

        /// <summary>
        /// The wide tree is the binary tree's own boxes and leaves, nothing more: every leaf listed
        /// exactly once, with its box copied to the bit, and every other box listed the union of the
        /// leaves under it - which is what the binary node it stands for holds, a box's bounds being
        /// the least and greatest of its triangles', with no rounding in either.
        /// </summary>
        [Theory]
        [InlineData(41)]
        [InlineData(42)]
        [InlineData(43)]
        public void TheWideTreeIsTheBinaryTreesOwnBoxes(int seed)
        {
            if (!Vector256.IsHardwareAccelerated)
                return;

            var mesh = Terrain(seed, out _);
            var nodes = mesh.NodesForTests();
            var (bounds, children, full) = mesh.WideTreeForTests();
            Assert.NotEmpty(children);

            var seenLeaf = new int[nodes.Length];
            var seenWide = new int[full.Length];

            float[] Union(int wide)
            {
                seenWide[wide]++;
                float[] all = [float.MaxValue, float.MaxValue, float.MaxValue, float.MinValue, float.MinValue, float.MinValue];
                int listed = 0;

                for (int slot = 0; slot < 8; slot++)
                {
                    if ((full[wide] & (1 << slot)) == 0)
                        continue;

                    listed++;
                    int child = children[wide * 8 + slot];
                    float[] box;

                    if (child < 0)
                    {
                        var n = nodes[~child];
                        Assert.NotEqual(0, n.Count);
                        seenLeaf[~child]++;
                        box = [n.Mins.X, n.Mins.Y, n.Mins.Z, n.Maxs.X, n.Maxs.Y, n.Maxs.Z];
                    }
                    else
                    {
                        Assert.True(child > wide, "a wide node lists one before it");
                        box = Union(child);
                    }

                    for (int k = 0; k < 6; k++)
                    {
                        Assert.Equal(BitConverter.SingleToInt32Bits(box[k]),
                            BitConverter.SingleToInt32Bits(bounds[wide * 48 + k * 8 + slot]));
                        all[k] = k < 3 ? MathF.Min(all[k], box[k]) : MathF.Max(all[k], box[k]);
                    }
                }

                Assert.InRange(listed, 2, 8);
                return all;
            }

            var root = Union(0);
            var r = nodes[0];
            float[] rootBox = [r.Mins.X, r.Mins.Y, r.Mins.Z, r.Maxs.X, r.Maxs.Y, r.Maxs.Z];
            for (int k = 0; k < 6; k++)
                Assert.Equal(BitConverter.SingleToInt32Bits(rootBox[k]), BitConverter.SingleToInt32Bits(root[k]));

            for (int i = 0; i < nodes.Length; i++)
                Assert.Equal(nodes[i].Count != 0 ? 1 : 0, seenLeaf[i]);
            for (int w = 0; w < full.Length; w++)
                Assert.Equal(1, seenWide[w]);
        }

        /// <summary>
        /// The box test still has its say. Where a leaf's box turns a segment away from a triangle the
        /// triangle test alone would pass, the old walk said no, and so must the new one - through the
        /// wide tree and through the leaves it remembers. Such segments are rare; this hunts for them
        /// among segments that end exactly on a leaf box's face, where the box and the triangle test
        /// round differently, and checks every one it finds.
        /// </summary>
        [Fact]
        public void ALeafsBoxStillTurnsASegmentAway()
        {
            int found = 0, tried = 0;

            for (int seed = 200; seed < 260 && found < 20; seed++)
            {
                var mesh = Terrain(seed, out _);
                var master = new MasterMesh(mesh);
                var nodes = mesh.NodesForTests();
                var sorted = mesh.VerticesForTests();
                var rng = new Random(seed);

                for (int k = 0; k < 4000; k++)
                {
                    tried++;
                    int leaf;
                    do leaf = rng.Next(nodes.Length); while (nodes[leaf].Count == 0);

                    var n = nodes[leaf];
                    int t = (n.First + rng.Next(n.Count)) * 3;
                    var (v0, v1, v2) = (sorted[t], sorted[t + 1], sorted[t + 2]);

                    // A point on the triangle's edge or corner that lies on the leaf box's face.
                    float s = rng.Next(3) switch { 0 => 0f, 1 => 1f, _ => (float)rng.NextDouble() };
                    var p = new BspFile.Vector3(v0.X + (v1.X - v0.X) * s, v0.Y + (v1.Y - v0.Y) * s, v0.Z + (v1.Z - v0.Z) * s);
                    if (rng.Next(2) == 0) p = v2;

                    var a = new BspFile.Vector3(p.X + Coord(rng, 300f), p.Y + Coord(rng, 300f), p.Z + Coord(rng, 300f));
                    float over = rng.Next(3) switch { 0 => 1f, 1 => 1f + 1e-6f, _ => 2f };
                    var b = new BspFile.Vector3(a.X + (p.X - a.X) * over, a.Y + (p.Y - a.Y) * over, a.Z + (p.Z - a.Z) * over);

                    int mask = 0x1 | 0x2 | 0x8;
                    bool expected = master.Blocks(a, b, mask);
                    if (expected || !master.AnyTriangle(a, b, mask))
                        continue;

                    // The triangle test passes somewhere, and the old walk still said no.
                    found++;

                    foreach (var (wide, remember) in new[] { (true, true), (true, false), (false, true) })
                    {
                        var fresh = Terrain(seed, out _);
                        fresh.UseForTests(wide, remember);

                        // Remembered first: put the leaf there by asking a segment it does block.
                        fresh.Blocks(new BspFile.Vector3(p.X, p.Y, p.Z + 50f), new BspFile.Vector3(p.X, p.Y, p.Z - 50f), mask);
                        Assert.False(fresh.Blocks(a, b, mask), $"seed {seed}: ({a.X}, {a.Y}, {a.Z}) to ({b.X}, {b.Y}, {b.Z})");
                    }
                }
            }

            // They exist, and this fixture finds them: without some, the box test's part in the answer
            // would go unchecked.
            Assert.True(found >= 10, $"only {found} segments in {tried} turned away by a leaf's box alone");
        }

        /// <summary>
        /// The same for a level line, the commonest sight line there is - two eyes at one height - for
        /// which the box test asks only whether the line's height lies within a box's. Run level across
        /// a wall of terrain a hair above or below the height of a row of its corners, such a line can
        /// pass the triangle test on a triangle whose leaf's box it lies just outside, and the old walk
        /// said no to it. This hunts for such lines and holds every walk to that answer.
        /// </summary>
        [Fact]
        public void ALevelLineJustOffALeafsBoxIsTurnedAwayAsBefore()
        {
            int found = 0, tried = 0;

            foreach (float far in new[] { 0f, 3000f, 12000f })
            {
                for (int seed = 0; seed < 12; seed++)
                {
                    var rng = new Random(seed);
                    var v = new List<BspFile.Vector3>();
                    var c = new List<int>();

                    // Walls standing on x, their corner rows on whole heights, bulging in y - and one
                    // taller than all of them well off to the side, so that it is not the root's box
                    // that a line over the top of the others lies outside, but a box further down.
                    float x0 = far + MathF.Round(Coord(rng, 200f)), y0 = -far + MathF.Round(Coord(rng, 200f));
                    for (int w = 0; w < 3; w++)
                        AddDisplacement(v, c, rng, new BspFile.Vector3(x0 + 256f * w, y0, 64f * seed),
                            new BspFile.Vector3(256f, 0f, 0f), new BspFile.Vector3(0f, 0f, 256f), new BspFile.Vector3(0f, 1f, 0f));
                    int shortWalls = v.Count;
                    AddDisplacement(v, c, rng, new BspFile.Vector3(x0 + 4000f, y0, -2000f),
                        new BspFile.Vector3(256f, 0f, 0f), new BspFile.Vector3(0f, 0f, 5000f), new BspFile.Vector3(0f, 1f, 0f));

                    var mesh = new TriangleMesh();
                    mesh.Build(v.ToArray(), c.ToArray());
                    var master = new MasterMesh(mesh);
                    int mask = 0x1 | 0x2 | 0x8;

                    for (int k = 0; k < 1500; k++)
                    {
                        tried++;
                        var corner = v[rng.Next(shortWalls)];
                        float z = rng.Next(3) switch
                        {
                            0 => MathF.BitIncrement(corner.Z),
                            1 => MathF.BitDecrement(corner.Z),
                            _ => corner.Z,
                        };

                        float x = corner.X + Coord(rng, 6f);
                        var a = new BspFile.Vector3(x, corner.Y - 900f - Coord(rng, 300f), z);
                        var b = new BspFile.Vector3(x + Coord(rng, 300f), corner.Y + 900f + Coord(rng, 300f), z);

                        if (master.Blocks(a, b, mask) || !master.AnyTriangle(a, b, mask))
                            continue;

                        found++;

                        foreach (var (wide, remember) in new[] { (true, true), (true, false), (false, true) })
                        {
                            var fresh = new TriangleMesh();
                            fresh.Build(v.ToArray(), c.ToArray());
                            fresh.UseForTests(wide, remember);

                            // A level line a unit lower first, so that the leaf it stops at is
                            // remembered when the line just off its box is asked.
                            fresh.Blocks(new BspFile.Vector3(a.X, a.Y, corner.Z - 1f), new BspFile.Vector3(b.X, b.Y, corner.Z - 1f), mask);
                            Assert.False(fresh.Blocks(a, b, mask), $"({a.X}, {a.Y}, {a.Z}) to ({b.X}, {b.Y}, {b.Z})");
                        }
                    }
                }
            }

            Assert.True(found >= 5, $"only {found} level lines in {tried} turned away by a leaf's box alone");
        }

        /// <summary>
        /// Segments through the origin, straight down a column and along each axis, through terrain
        /// around it. A wide node's empty slots hold a box of no size at the origin; the walk must
        /// never take one for a child, which only a segment through that very point would show.
        /// </summary>
        [Fact]
        public void SegmentsThroughTheOriginWalkAsBefore()
        {
            var v = new List<BspFile.Vector3>();
            var c = new List<int>();
            var rng = new Random(12);

            for (int i = -2; i < 2; i++)
                for (int j = -2; j < 2; j++)
                    AddDisplacement(v, c, rng, new BspFile.Vector3(256f * i, 256f * j, -40f),
                        new BspFile.Vector3(256f, 0f, 0f), new BspFile.Vector3(0f, 256f, 0f), new BspFile.Vector3(0f, 0f, 1f));

            var mesh = new TriangleMesh();
            mesh.Build(v.ToArray(), c.ToArray());
            var master = new MasterMesh(mesh);

            if (Vector256.IsHardwareAccelerated)
                Assert.Contains(mesh.WideTreeForTests().Full, full => full != 0xFF);

            foreach (bool remember in new[] { false, true })
            {
                mesh.UseForTests(true, remember);

                foreach (float reach in new[] { 1f, 64f, 1024f })
                {
                    (BspFile.Vector3, BspFile.Vector3)[] segments =
                    [
                        (new(0f, 0f, reach), new(0f, 0f, -reach)),
                        (new(-reach, 0f, 0f), new(reach, 0f, 0f)),
                        (new(0f, -reach, 0f), new(0f, reach, 0f)),
                        (new(-reach, -reach, -reach), new(reach, reach, reach)),
                    ];

                    foreach (var (a, b) in segments)
                        Assert.Equal(master.Blocks(a, b, 0x1 | 0x2 | 0x8), mesh.Blocks(a, b, 0x1 | 0x2 | 0x8));
                }
            }
        }

        /// <summary>
        /// And for a segment run along a triangle's edge, in its plane or a hair out of it, starting a
        /// little way past a point of the triangle and going on away from it. The triangle test can
        /// put such a triangle a millionth ahead of the start when it lies just behind it, and only the
        /// box test, which starts no box before the segment's own start, says no. This hunts for such
        /// segments and holds every walk to that answer.
        /// </summary>
        [Fact]
        public void ASegmentStartingJustPastATriangleIsTurnedAwayAsBefore()
        {
            int found = 0, tried = 0;
            const int Mask = 0x1 | 0x2 | 0x8;

            for (int seed = 0; seed < 400; seed++)
            {
                var mesh = Terrain(seed, out _);
                var master = new MasterMesh(mesh);
                var sorted = mesh.VerticesForTests();
                var rng = new Random(seed);
                var turnedAway = new List<(BspFile.Vector3 A, BspFile.Vector3 B)>();

                for (int k = 0; k < 600; k++)
                {
                    tried++;
                    int t = rng.Next(sorted.Length / 3) * 3;
                    var (v0, v1, v2) = (sorted[t], sorted[t + 1], sorted[t + 2]);

                    // A point of the triangle: inside it, or one of its corners.
                    float r1 = (float)rng.NextDouble(), r2 = (float)rng.NextDouble() * (1 - r1);
                    var p = new BspFile.Vector3(
                        v0.X + (v1.X - v0.X) * r1 + (v2.X - v0.X) * r2,
                        v0.Y + (v1.Y - v0.Y) * r1 + (v2.Y - v0.Y) * r2,
                        v0.Z + (v1.Z - v0.Z) * r1 + (v2.Z - v0.Z) * r2);
                    if (rng.Next(3) == 0)
                        p = rng.Next(2) == 0 ? v0 : v1;

                    // Along one of its edges, tilted a hair out of its plane or not at all.
                    var e = rng.Next(2) == 0
                        ? new BspFile.Vector3(v1.X - v0.X, v1.Y - v0.Y, v1.Z - v0.Z)
                        : new BspFile.Vector3(v2.X - v0.X, v2.Y - v0.Y, v2.Z - v0.Z);
                    float length = MathF.Sqrt(e.X * e.X + e.Y * e.Y + e.Z * e.Z);
                    if (length < 1e-3f)
                        continue;

                    float reach = rng.Next(3) switch { 0 => 64f, 1 => 1000f, _ => 4000f };
                    float tilt = rng.Next(4) switch { 0 => 1e-4f, 1 => -1e-4f, _ => 0f };
                    var d = new BspFile.Vector3(e.X / length * reach, e.Y / length * reach, e.Z / length * reach + tilt * reach);

                    float past = rng.Next(2) == 0 ? 1e-3f : (float)rng.NextDouble() * 0.01f;
                    var a = new BspFile.Vector3(p.X + d.X * past, p.Y + d.Y * past, p.Z + d.Z * past);
                    var b = new BspFile.Vector3(a.X + d.X, a.Y + d.Y, a.Z + d.Z);

                    if (!master.Blocks(a, b, Mask) && master.AnyTriangle(a, b, Mask))
                        turnedAway.Add((a, b));
                }

                found += turnedAway.Count;
                if (turnedAway.Count == 0)
                    continue;

                foreach (var (wide, remember) in new[] { (true, true), (true, false), (false, true) })
                {
                    var fresh = Terrain(seed, out _);
                    fresh.UseForTests(wide, remember);

                    foreach (var (a, b) in turnedAway)
                    {
                        // The segment backwards first: it starts where this one's triangle is, and so
                        // leaves that triangle's leaf remembered, if anything does.
                        fresh.Blocks(b, new BspFile.Vector3(2 * a.X - b.X, 2 * a.Y - b.Y, 2 * a.Z - b.Z), Mask);
                        Assert.False(fresh.Blocks(a, b, Mask), $"seed {seed}: ({a.X}, {a.Y}, {a.Z}) to ({b.X}, {b.Y}, {b.Z})");
                    }
                }
            }

            Assert.True(found >= 30, $"only {found} segments in {tried} turned away by a leaf's box alone");
        }

        /// <summary>
        /// A mesh built again over other triangles forgets the leaves it remembered: they were leaves
        /// of a tree it no longer has, and the indices may not even be in its new one.
        /// </summary>
        [Fact]
        public void ARebuiltMeshForgetsTheLeavesItRemembered()
        {
            var big = new List<BspFile.Vector3>();
            var bigContents = new List<int>();
            var rng = new Random(9);
            AddDisplacement(big, bigContents, rng, new BspFile.Vector3(0f, 0f, 0f), new BspFile.Vector3(512f, 0f, 0f),
                new BspFile.Vector3(0f, 512f, 0f), new BspFile.Vector3(0f, 0f, 1f));
            for (int i = 0; i < 6; i++)
                AddDisplacement(big, bigContents, rng, new BspFile.Vector3(512f * (i + 1), 0f, 0f), new BspFile.Vector3(512f, 0f, 0f),
                    new BspFile.Vector3(0f, 512f, 0f), new BspFile.Vector3(0f, 0f, 1f));

            var mesh = new TriangleMesh();
            mesh.Build(big.ToArray(), Fill(bigContents.Count, 0x1));

            // Remember leaves far down a large tree: a column through the far end of the patch.
            for (int x = 3000; x < 3500; x += 50)
                Assert.True(mesh.Blocks(new BspFile.Vector3(x, 100f, 5000f), new BspFile.Vector3(x, 100f, -5000f), 0x1));

            // Rebuilt small, and asked the same.
            var small = new List<BspFile.Vector3>();
            var smallContents = new List<int>();
            AddDisplacement(small, smallContents, new Random(3), new BspFile.Vector3(2900f, 0f, -20f),
                new BspFile.Vector3(256f, 0f, 0f), new BspFile.Vector3(0f, 256f, 0f), new BspFile.Vector3(0f, 0f, 1f));
            mesh.Build(small.ToArray(), Fill(smallContents.Count, 0x1));
            var master = new MasterMesh(mesh);

            for (int x = 2900; x < 3500; x += 25)
            {
                var a = new BspFile.Vector3(x, 100f, 5000f);
                var b = new BspFile.Vector3(x, 100f, -5000f);
                Assert.Equal(master.Blocks(a, b, 0x1), mesh.Blocks(a, b, 0x1));
            }

            static int[] Fill(int count, int value)
            {
                var c = new int[count];
                Array.Fill(c, value);
                return c;
            }
        }
    }
}
