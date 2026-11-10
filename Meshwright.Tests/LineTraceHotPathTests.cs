using System;
using System.Collections.Generic;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That the world line trace answers exactly what it did before it was made faster.
    ///
    /// <see cref="BspVisibility.IsLineClear(BspFile.Vector3, BspFile.Vector3, int, int)"/> now reads
    /// nodes with their plane beside them, takes an axis-aligned plane's distance as one coordinate
    /// less the plane's distance, reads brush sides' planes from a table by side, and clips each brush
    /// at most once per ray however many leaves list it. Each of those is only safe if it is exact:
    /// the shortcut must agree with the dot product on every comparison the trace makes, the stamps
    /// must never skip a brush this ray has not asked about, and the table must hold the plane the
    /// side named. So this holds the trace to the old code, kept here verbatim, on random trees whose
    /// planes include both signs of every axis, planes a hair off an axis, and oblique ones; whose
    /// leaves list brushes from a small shared pool, so that rays meet the same brush in leaf after
    /// leaf; and whose rays start and end on brush faces, run along planes, sit on zero coordinates
    /// of either sign, or have no length.
    /// </summary>
    public class LineTraceHotPathTests
    {
        private const int Solid = 0x1;

        // ---- the trace as it was --------------------------------------------------------------

        private sealed class OldWorld(BspFile.Plane[] planes, BspVisibility.Node[] nodes, BspVisibility.Leaf[] leafs,
            BspFile.Brush[] brushes, BspFile.BrushSide[] brushSides, int[]?[] leafBrushes)
        {
            private readonly BspFile.Brush[] brushes =
                BspVisibility.WithFullClip(brushes, NavConstants.PlayerClipStopsEveryWalker);

            /// <summary>How many brush tests the last ray made, and how many of those repeated one.</summary>
            public int Tests, Repeats;
            private readonly HashSet<int> seen = [];

            public bool IsLineClear(BspFile.Vector3 a, BspFile.Vector3 b, int mask, int ignoreContents)
            {
                Tests = Repeats = 0;
                seen.Clear();

                if (nodes.Length == 0 || leafs.Length == 0)
                    return true;

                return !Blocked(0, a, b, mask, a, b, ignoreContents);
            }

            private bool Blocked(int num, BspFile.Vector3 p1, BspFile.Vector3 p2, int mask,
                BspFile.Vector3 rayStart, BspFile.Vector3 rayEnd, int ignoreContents)
            {
                while (num >= 0)
                {
                    ref readonly var node = ref nodes[num];

                    if ((uint)node.PlaneNum >= (uint)planes.Length)
                        return false;

                    ref readonly var plane = ref planes[node.PlaneNum];

                    float d1 = plane.Normal.X * p1.X + plane.Normal.Y * p1.Y + plane.Normal.Z * p1.Z - plane.Distance;
                    float d2 = plane.Normal.X * p2.X + plane.Normal.Y * p2.Y + plane.Normal.Z * p2.Z - plane.Distance;

                    if (d1 >= 0 && d2 >= 0) { num = node.Child0; continue; }
                    if (d1 < 0 && d2 < 0) { num = node.Child1; continue; }

                    float frac = d1 / (d1 - d2);
                    var mid = new BspFile.Vector3(
                        p1.X + frac * (p2.X - p1.X),
                        p1.Y + frac * (p2.Y - p1.Y),
                        p1.Z + frac * (p2.Z - p1.Z));

                    bool behind = d1 < 0;
                    if (Blocked(behind ? node.Child1 : node.Child0, p1, mid, mask, rayStart, rayEnd,
                            ignoreContents))
                        return true;

                    num = behind ? node.Child0 : node.Child1;
                    p1 = mid;
                }

                int leafIndex = -num - 1;
                if ((uint)leafIndex >= (uint)leafs.Length)
                    return false;

                if ((leafs[leafIndex].Contents & mask) != 0)
                    return true;

                return HitsLeafBrush(leafIndex, rayStart, rayEnd, mask, ignoreContents);
            }

            private bool HitsLeafBrush(int leafIndex, BspFile.Vector3 p1, BspFile.Vector3 p2, int mask,
                int ignoreContents)
            {
                if ((uint)leafIndex >= (uint)leafBrushes.Length)
                    return false;

                var list = leafBrushes[leafIndex];
                if (list is null)
                    return false;

                foreach (int index in list)
                {
                    if ((brushes[index].Contents & mask) == 0)
                        continue;

                    if (ignoreContents != 0 && (brushes[index].Contents & ignoreContents) != 0)
                        continue;

                    Tests++;
                    if (!seen.Add(index))
                        Repeats++;

                    if (SegmentHitsBrush(brushes[index], p1, p2))
                        return true;
                }

                return false;
            }

            private bool SegmentHitsBrush(BspFile.Brush brush, BspFile.Vector3 p1, BspFile.Vector3 p2)
            {
                const float Epsilon = 0.03125f;

                float enter = -1f;
                float exit = 1f;
                bool startsOutside = false;

                for (int i = 0; i < brush.NumSides; i++)
                {
                    int sideIndex = brush.FirstSide + i;
                    if ((uint)sideIndex >= (uint)brushSides.Length)
                        return false;

                    var side = brushSides[sideIndex];
                    if ((uint)side.PlaneNum >= (uint)planes.Length)
                        return false;

                    var plane = planes[side.PlaneNum];
                    float d1 = plane.Normal.X * p1.X + plane.Normal.Y * p1.Y + plane.Normal.Z * p1.Z - plane.Distance;
                    float d2 = plane.Normal.X * p2.X + plane.Normal.Y * p2.Y + plane.Normal.Z * p2.Z - plane.Distance;

                    if (d1 > 0) startsOutside = true;

                    if (d1 > 0 && d2 > 0) return false;
                    if (d1 <= 0 && d2 <= 0) continue;

                    float f = d1 / (d1 - d2);

                    if (d1 > d2)
                    {
                        if (f - Epsilon > enter) enter = f - Epsilon;
                    }
                    else
                    {
                        if (f + Epsilon < exit) exit = f + Epsilon;
                    }
                }

                if (!startsOutside)
                    return true;

                return enter < exit && enter < 1f && exit > 0f;
            }
        }

        // ---- a random world -------------------------------------------------------------------

        private sealed class World
        {
            public readonly List<BspFile.Plane> Planes = [];
            public readonly List<BspVisibility.Node> Nodes = [];
            public readonly List<BspVisibility.Leaf> Leafs = [];
            public readonly List<int[]?> LeafBrushes = [];
            public readonly List<BspFile.Brush> Brushes = [];
            public readonly List<BspFile.BrushSide> Sides = [];

            /// <summary>Values a ray's coordinates should often land on exactly: plane offsets, brush faces, zeros.</summary>
            public readonly List<float> Levels = [0f, -0f];

            public BspVisibility New() => BspVisibility.FromGeometry(Planes.ToArray(), Nodes.ToArray(),
                Leafs.ToArray(), Brushes.ToArray(), Sides.ToArray(), LeafBrushes.ToArray());

            public OldWorld Old() => new(Planes.ToArray(), Nodes.ToArray(), Leafs.ToArray(), Brushes.ToArray(),
                Sides.ToArray(), LeafBrushes.ToArray());
        }

        private static int AddPlane(World w, float nx, float ny, float nz, float d)
        {
            // Type left at zero - "axial along x" - for every plane, oblique ones included: the trace
            // must read which planes are axial off the normals, never off the type.
            w.Planes.Add(new BspFile.Plane { Normal = new BspFile.Vector3(nx, ny, nz), Distance = d });
            return w.Planes.Count - 1;
        }

        private static int RandomPlane(World w, Random rng)
        {
            float d = rng.Next(2) == 0 ? MathF.Round(Coord(rng, 2500)) : Coord(rng, 2500);
            w.Levels.Add(d);
            w.Levels.Add(-d);

            switch (rng.Next(8))
            {
                case 0: return AddPlane(w, 1, 0, 0, d);
                case 1: return AddPlane(w, 0, 1, 0, d);
                case 2: return AddPlane(w, 0, 0, 1, d);
                case 3: // the negative of an axis, which is not the shortcut's to take
                    return rng.Next(3) switch
                    {
                        0 => AddPlane(w, -1, 0, 0, d),
                        1 => AddPlane(w, 0, -1, 0, d),
                        _ => AddPlane(w, 0, 0, -1, d),
                    };
                case 4: // a hair off an axis, which must not be mistaken for one
                {
                    float e = rng.Next(2) == 0 ? 1e-7f : -3e-8f;
                    return rng.Next(3) switch
                    {
                        0 => AddPlane(w, 1, e, 0, d),
                        1 => AddPlane(w, 0, 1, e, d),
                        _ => AddPlane(w, e, 0, 1, d),
                    };
                }
                case 5: // negative zeros in an otherwise axial normal - still exactly the axis
                    return AddPlane(w, -0f, 0f, 1, d);
                default:
                {
                    float nx = Coord(rng, 1), ny = Coord(rng, 1), nz = Coord(rng, 1);
                    float length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                    return length < 1e-3f ? AddPlane(w, 0, 0, 1, d) : AddPlane(w, nx / length, ny / length, nz / length, d);
                }
            }
        }

        private static void AddBrush(World w, Random rng)
        {
            float x0 = MathF.Round(Coord(rng, 2000)), y0 = MathF.Round(Coord(rng, 2000)), z0 = MathF.Round(Coord(rng, 2000));
            float x1 = x0 + 1 + rng.Next(1500), y1 = y0 + 1 + rng.Next(1500), z1 = z0 + 1 + rng.Next(rng.Next(2) == 0 ? 16 : 1500);
            w.Levels.AddRange([x0, x1, y0, y1, z0, z1]);

            int first = w.Sides.Count;
            foreach (int p in new[]
                     {
                         AddPlane(w, 1, 0, 0, x1), AddPlane(w, -1, 0, 0, -x0), AddPlane(w, 0, 1, 0, y1),
                         AddPlane(w, 0, -1, 0, -y0), AddPlane(w, 0, 0, 1, z1), AddPlane(w, 0, 0, -1, -z0),
                     })
            {
                w.Sides.Add(new BspFile.BrushSide { PlaneNum = (ushort)p });
            }

            if (rng.Next(3) == 0)
                w.Sides.Add(new BspFile.BrushSide { PlaneNum = (ushort)RandomPlane(w, rng) });

            // Now and then a side naming a plane that does not exist, which gives up on the brush.
            if (rng.Next(25) == 0)
                w.Sides.Add(new BspFile.BrushSide { PlaneNum = ushort.MaxValue });

            int[] contents = [Solid, Solid, Solid, 0x2, 0x10, BspVisibility.ContentsLadder | Solid, 0x4000];
            w.Brushes.Add(new BspFile.Brush
            {
                // And now and then a brush whose sides run off the end of the array.
                FirstSide = rng.Next(40) == 0 ? 1 << 20 : first,
                NumSides = w.Sides.Count - first,
                Contents = contents[rng.Next(contents.Length)],
            });
        }

        private static int Leaf(World w, Random rng, int brushPool)
        {
            int[] contents = [0, 0, 0, 0, Solid, 0x2, 0x10];
            w.Leafs.Add(new BspVisibility.Leaf { Contents = contents[rng.Next(contents.Length)] });

            int[]? list = null;
            if (rng.Next(4) != 0)
            {
                // From a small shared pool, so neighbouring leaves list the same brushes.
                list = new int[rng.Next(2, 8)];
                for (int i = 0; i < list.Length; i++)
                    list[i] = rng.Next(brushPool);
            }

            w.LeafBrushes.Add(list);
            return -(w.Leafs.Count - 1) - 1;
        }

        private static int Node(World w, Random rng, int depth, int brushPool)
        {
            if (depth == 0 || (depth < 8 && rng.Next(8) == 0))
                return Leaf(w, rng, brushPool);

            int self = w.Nodes.Count;
            w.Nodes.Add(default);

            // Rarely, and only deep down, a node naming a plane that does not exist, or a child naming
            // a leaf that does not: the trace gives up on both in its own way.
            int plane = depth < 5 && rng.Next(40) == 0 ? 1 << 30 : RandomPlane(w, rng);
            int child0 = depth < 5 && rng.Next(40) == 0 ? -100000 : Node(w, rng, depth - 1, brushPool);
            int child1 = Node(w, rng, depth - 1, brushPool);

            w.Nodes[self] = new BspVisibility.Node { PlaneNum = plane, Child0 = child0, Child1 = child1 };
            return self;
        }

        private static World RandomWorld(Random rng)
        {
            var w = new World();
            int pool = 10 + rng.Next(12);
            for (int i = 0; i < pool; i++)
                AddBrush(w, rng);

            Node(w, rng, 9, pool);
            return w;
        }

        private static float Coord(Random rng, float range) => (float)(rng.NextDouble() * 2 - 1) * range;

        private static float Pick(Random rng, World w)
            => rng.Next(3) switch
            {
                0 => w.Levels[rng.Next(w.Levels.Count)],
                1 => MathF.Round(Coord(rng, 3000)),
                _ => Coord(rng, 3000),
            };

        private static (BspFile.Vector3 A, BspFile.Vector3 B) Ray(Random rng, World w)
        {
            var a = new BspFile.Vector3(Pick(rng, w), Pick(rng, w), Pick(rng, w));
            var b = new BspFile.Vector3(Pick(rng, w), Pick(rng, w), Pick(rng, w));

            return rng.Next(6) switch
            {
                0 => (a, a),                                         // a point, as IsPointSolid asks
                1 => (a, new BspFile.Vector3(a.X, a.Y, b.Z)),        // down a column
                2 => (a, new BspFile.Vector3(b.X, b.Y, a.Z)),        // level, along a horizontal plane
                3 => (a, new BspFile.Vector3(b.X, a.Y, a.Z)),        // along one axis
                _ => (a, b),
            };
        }

        private static readonly int[] Masks =
        [
            BspVisibility.MaskBlockLos, BspVisibility.GenerationMask, BspVisibility.GroundMask, Solid, 0x2, 0x10 | Solid,
        ];

        private static readonly int[] Ignores = [0, 0, 0, BspVisibility.ContentsLadder, 0x2];

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
        [InlineData(9)]
        [InlineData(10)]
        public void TheLineTraceAnswersAsTheOldOneDid(int seed)
        {
            var rng = new Random(seed);
            var world = RandomWorld(rng);
            var vis = world.New();
            var old = world.Old();

            int blocked = 0, clear = 0, byBrush = 0, repeated = 0;

            for (int i = 0; i < 8000; i++)
            {
                var (a, b) = Ray(rng, world);
                int mask = Masks[rng.Next(Masks.Length)];
                int ignore = Ignores[rng.Next(Ignores.Length)];

                bool expected = old.IsLineClear(a, b, mask, ignore);
                Assert.True(expected == vis.IsLineClear(a, b, mask, ignore),
                    $"ray {i}: ({a.X}, {a.Y}, {a.Z}) to ({b.X}, {b.Y}, {b.Z}) mask {mask:x} ignore {ignore:x}: expected clear={expected}");

                if (expected) clear++; else blocked++;
                if (old.Tests > 0 && !expected) byBrush++;
                if (old.Repeats > 0) repeated++;
            }

            // The comparison is only worth something if both answers are common, brushes decide a good
            // share of them, and plenty of rays meet the same brush twice - the case the stamps skip.
            Assert.True(blocked > 400 && clear > 400, $"{blocked} blocked, {clear} clear");
            Assert.True(byBrush > 200, $"only {byBrush} rays reached a brush and were blocked");
            Assert.True(repeated > 200, $"only {repeated} rays met a brush twice");
        }

        /// <summary>
        /// The stamps live on the thread and outlast the ray: a thread that traces one world and then
        /// another, then the first again, must not read the other world's stamps as its own, and
        /// neither must a long run of rays on one world, each with stamps left by the last.
        /// </summary>
        [Fact]
        public void StampsLeftByOtherRaysAndOtherWorldsSkipNothing()
        {
            var rng = new Random(77);
            var first = RandomWorld(rng);
            var second = RandomWorld(rng);
            var (visA, oldA) = (first.New(), first.Old());
            var (visB, oldB) = (second.New(), second.Old());

            for (int i = 0; i < 6000; i++)
            {
                bool onA = rng.Next(2) == 0;
                var w = onA ? first : second;
                var (a, b) = Ray(rng, w);
                int mask = Masks[rng.Next(Masks.Length)];

                // The same ray twice in a row, so the second runs on stamps the first just left.
                for (int k = 0; k < 2; k++)
                {
                    Assert.Equal(onA ? oldA.IsLineClear(a, b, mask, 0) : oldB.IsLineClear(a, b, mask, 0),
                        onA ? visA.IsLineClear(a, b, mask, 0) : visB.IsLineClear(a, b, mask, 0));
                }
            }
        }

        /// <summary>
        /// The axis is read off the normal: exactly one and exactly zeros, a negative zero counting as
        /// zero, and nothing else - not the negative axes, not a normal a hair off one.
        /// </summary>
        [Fact]
        public void OnlyAnExactPositiveUnitNormalIsAnAxis()
        {
            Assert.Equal(0, BspVisibility.AxisOf(new BspFile.Vector3(1, 0, 0)));
            Assert.Equal(1, BspVisibility.AxisOf(new BspFile.Vector3(0, 1, 0)));
            Assert.Equal(2, BspVisibility.AxisOf(new BspFile.Vector3(0, 0, 1)));
            Assert.Equal(2, BspVisibility.AxisOf(new BspFile.Vector3(-0f, -0f, 1)));

            Assert.Equal(3, BspVisibility.AxisOf(new BspFile.Vector3(-1, 0, 0)));
            Assert.Equal(3, BspVisibility.AxisOf(new BspFile.Vector3(0, 0, -1)));
            Assert.Equal(3, BspVisibility.AxisOf(new BspFile.Vector3(1, 1e-7f, 0)));
            Assert.Equal(3, BspVisibility.AxisOf(new BspFile.Vector3(MathF.BitDecrement(1f), 0, 0)));
            Assert.Equal(3, BspVisibility.AxisOf(new BspFile.Vector3(0.6f, 0.8f, 0)));
        }
    }
}
