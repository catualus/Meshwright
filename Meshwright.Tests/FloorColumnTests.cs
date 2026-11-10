using System;
using System.Collections.Generic;
using System.IO;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That the floor search's column (<see cref="BspVisibility.OpenColumn"/>) answers every line down
    /// it exactly as the line trace does, and so that <see cref="StairMarker.TryFindFloor"/> finds the
    /// same floors, to the bit, as it did when every segment and every bisection ray was traced from the
    /// roots.
    ///
    /// The column starts each segment's walk of the world tree where the column's walk divides, gathers
    /// brush entities from the ones whose bounds hold the column, and tests triangles only in the
    /// triangle-tree leaves whose boxes hold it. Each is exact only by an argument - a plane test
    /// monotonic in height, a node's box holding its children's - so these hold it to the plain trace on
    /// random worlds built to break it: planes horizontal, vertical, steep and oblique, many of them at
    /// the heights the search's segments end at; brushes in the leaves, beside the columns and across
    /// them, with slanted sides, sides naming no plane and sides off the end of the lump; terrain lying
    /// exactly on those heights, flat and steep, and columns down its shared edges and corners; props;
    /// brush entities, shifted by an origin, and now and then one with no heights at all.
    /// </summary>
    public class FloorColumnTests
    {
        private const int Solid = 0x1;

        /// <summary>
        /// <see cref="StairMarker.TryFindFloor"/> as it was before the column, verbatim but for its
        /// comments: every line through the line trace.
        /// </summary>
        private static bool OldTryFindFloor(BspVisibility vis, float x, float y, float top, float depth, out float z)
        {
            const float CoarseStep = 4f;
            const int Refinements = 7;
            const float Overlap = CoarseStep / 128f;

            z = 0;

            for (float d = 0; d < depth; d += CoarseStep)
            {
                bool last = d + CoarseStep >= depth;

                var upper = new BspFile.Vector3(x, y, top - d);
                var lower = new BspFile.Vector3(x, y, top - d - CoarseStep - (last ? 0f : Overlap));

                if (vis.IsLineClear(upper, lower, BspVisibility.GroundMask))
                    continue;

                float hi = upper.Z;
                float lo = top - d - CoarseStep;

                if (vis.IsLineClear(new BspFile.Vector3(x, y, top), new BspFile.Vector3(x, y, lo),
                        BspVisibility.GroundMask))
                {
                    hi = lo;
                    lo = lower.Z;
                }

                for (int i = 0; i < Refinements; i++)
                {
                    float mid = (lo + hi) / 2f;
                    if (vis.IsLineClear(new BspFile.Vector3(x, y, top), new BspFile.Vector3(x, y, mid),
                            BspVisibility.GroundMask))
                        hi = mid;
                    else
                        lo = mid;
                }

                z = hi;
                return true;
            }

            return false;
        }

        public static TheoryData<int> Seeds()
        {
            var data = new TheoryData<int>();
            for (int seed = 1; seed <= 16; seed++)
                data.Add(seed);
            return data;
        }

        /// <summary>
        /// The search finds what it found, to the bit, from tops at and around the heights of the
        /// world's surfaces and whole segments above them, so that surfaces land exactly on the
        /// boundaries between segments, and on the bisection's heights, as well as anywhere.
        /// </summary>
        [Theory]
        [MemberData(nameof(Seeds))]
        public void TheFloorSearchFindsWhatItFound(int seed)
        {
            var rng = new Random(seed);
            var world = RandomWorld(rng);
            int found = 0;

            for (int i = 0; i < 400; i++)
            {
                var (x, y) = world.Column(rng);
                float top = world.Height(rng);
                float depth = Depth(rng);

                bool expected = OldTryFindFloor(world.Vis, x, y, top, depth, out float expectedZ);
                bool actual = StairMarker.TryFindFloor(world.Vis, x, y, top, depth, out float actualZ);

                Assert.True(expected == actual, $"({x:R}, {y:R}) from {top:R} for {depth:R}: found {actual}, was {expected}");
                AssertSameFloat(expectedZ, actualZ);

                if (expected)
                    found++;
            }

            // Not every search may come up empty, or the fixture is exercising nothing.
            Assert.True(found > 40, $"only {found} searches found a floor");
        }

        /// <summary>
        /// Any line down the column, ends anywhere in it or past it, gets the line trace's answer:
        /// short and long, up and down, of no length, and ends exactly on the column's own bounds.
        /// </summary>
        [Theory]
        [MemberData(nameof(Seeds))]
        public void EveryLineDownTheColumnIsAnsweredAsTheLineTraceAnswersIt(int seed)
        {
            var rng = new Random(1000 + seed);
            var world = RandomWorld(rng);

            for (int i = 0; i < 60; i++)
            {
                var (x, y) = world.Column(rng);
                float high = world.Height(rng);
                float low = high - (float)rng.NextDouble() * (rng.Next(2) == 0 ? 60f : 600f);

                // Now and then a bottom exactly on a surface below the top: a brush's top face, a
                // horizontal plane or terrain, where a line ending at the bottom just touches it.
                if (rng.Next(3) == 0)
                {
                    float h = world.Heights[rng.Next(world.Heights.Count)];
                    if (h < high)
                        low = h;
                }

                var column = world.Vis.OpenColumn(x, y, low, high, BspVisibility.GroundMask);

                for (int k = 0; k < 40; k++)
                {
                    float from = End(rng, world, low, high);
                    float to = End(rng, world, low, high);

                    bool expected = world.Vis.IsLineClear(new BspFile.Vector3(x, y, from), new BspFile.Vector3(x, y, to),
                        BspVisibility.GroundMask);

                    Assert.True(expected == column.IsClear(from, to),
                        $"({x:R}, {y:R}) from {from:R} to {to:R} in a column {low:R} to {high:R}");
                }
            }
        }

        /// <summary>
        /// The triangle tree's half on its own: the column's leaves answer every vertical segment in
        /// the column's stretch as the walk from the root does, over random triangles and terrain
        /// grids, for stretches ending on, just off and a little way off the terrain, so that the cut
        /// to the stretch's heights is held to its margin; and segments up and down, ending on its ends,
        /// of no length, and short enough to be flat along their own axis.
        /// </summary>
        [Theory]
        [MemberData(nameof(Seeds))]
        public void TheTriangleColumnBlocksWhatTheTreeBlocks(int seed)
        {
            var rng = new Random(2000 + seed);
            var sites = Sites(rng);
            var (vertices, contents, heights) = Triangles(rng, sites);

            var mesh = new TriangleMesh();
            mesh.Build(vertices, contents);

            int[] leaves = new int[1];
            int blocked = 0;

            for (int i = 0; i < 500; i++)
            {
                float x, y;
                switch (rng.Next(3))
                {
                    case 0:
                        var v = vertices[rng.Next(vertices.Length)];
                        (x, y) = (v.X, v.Y);
                        break;
                    case 1:
                        var (sx, sy) = sites[rng.Next(sites.Count)];
                        x = sx + (float)(rng.NextDouble() * 100 - 50);
                        y = sy + (float)(rng.NextDouble() * 100 - 50);
                        break;
                    default:
                        x = (float)(rng.NextDouble() * 1200 - 600);
                        y = (float)(rng.NextDouble() * 1200 - 600);
                        break;
                }

                // A stretch of column ending at, just off, or a little way off a terrain height, at
                // either end, so that triangles lie just outside it as well as inside.
                float h = heights[rng.Next(heights.Count)];
                float off = rng.Next(6) switch
                {
                    0 => 0f,
                    1 => MathF.BitIncrement(h) - h,
                    2 => 1e-4f,
                    3 => 0.5f,
                    4 => 1f + (float)rng.NextDouble(),
                    _ => (float)rng.NextDouble() * 8f,
                };
                float length = (float)rng.NextDouble() * (rng.Next(2) == 0 ? 60f : 600f);
                bool fromBelow = rng.Next(2) == 0;
                float low = fromBelow ? h + off : h - off - length;
                float high = fromBelow ? low + length : h - off;

                float margin = BspVisibility.ColumnMargin(low, high);
                int count = mesh.CollectColumn(x, y, low - margin, high + margin, ref leaves);

                float End() => rng.Next(5) switch
                {
                    0 => low,
                    1 => high,
                    _ => low + (float)rng.NextDouble() * (high - low),
                };

                for (int k = 0; k < 20; k++)
                {
                    float from = End();
                    float to = rng.Next(4) switch
                    {
                        0 => from,
                        1 => MathF.Max(low, from - 1e-7f * rng.Next(0, 3)),
                        _ => End(),
                    };

                    int mask = rng.Next(4) == 0 ? 0x2 : Solid;
                    var a = new BspFile.Vector3(x, y, from);
                    var b = new BspFile.Vector3(x, y, to);

                    bool expected = mesh.Blocks(a, b, mask);
                    Assert.Equal(expected, mesh.ColumnBlocks(leaves.AsSpan(0, count), a, b, mask));
                    Assert.Equal(mesh.Blocks(b, a, mask), mesh.ColumnBlocks(leaves.AsSpan(0, count), b, a, mask));

                    if (expected)
                        blocked++;
                }
            }

            Assert.True(blocked > 100, $"only {blocked} segments crossed a triangle");
        }

        /// <summary>
        /// Brush entities on their own: the column gathers, for every vertical segment, the models the
        /// full gather does - including none of those under a node with no heights, which a model with
        /// a NaN bound makes.
        /// </summary>
        [Theory]
        [MemberData(nameof(Seeds))]
        public void TheModelColumnGathersWhatTheFullGatherDoes(int seed)
        {
            var rng = new Random(3000 + seed);
            bool spoilt = seed % 4 == 0;
            var models = RandomModels(rng, spoilt, out var origins);
            Assert.True(models.BlockingModelCount > 3, $"{models.BlockingModelCount} models loaded");

            int[] candidates = new int[1];
            var heads = new int[64];
            var shifts = new BspFile.Vector3[64];
            var columnHeads = new int[64];
            var columnShifts = new BspFile.Vector3[64];
            int gathered = 0;

            for (int i = 0; i < 300; i++)
            {
                float x = rng.Next(3) == 0 ? MathF.Round((float)(rng.NextDouble() * 1200 - 600)) : (float)(rng.NextDouble() * 1200 - 600);
                float y = rng.Next(3) == 0 ? MathF.Round((float)(rng.NextDouble() * 1200 - 600)) : (float)(rng.NextDouble() * 1200 - 600);
                int count = models.GatherColumn(x, y, ref candidates);

                for (int k = 0; k < 20; k++)
                {
                    float from = MathF.Round((float)(rng.NextDouble() * 400 - 200)) + (rng.Next(2) == 0 ? 0 : (float)rng.NextDouble());
                    float to = rng.Next(5) == 0 ? from : from - (float)rng.NextDouble() * 200f;

                    var a = new BspFile.Vector3(x, y, from);
                    var b = new BspFile.Vector3(x, y, to);

                    int n = models.Gather(a, b, heads, shifts, out bool truncated);
                    Assert.False(truncated);
                    int m = models.GatherFromColumn(candidates.AsSpan(0, count), a, b, columnHeads, columnShifts);

                    var expected = new List<(int, BspFile.Vector3)>();
                    var actual = new List<(int, BspFile.Vector3)>();
                    for (int j = 0; j < n; j++) expected.Add((heads[j], shifts[j]));
                    for (int j = 0; j < m; j++) actual.Add((columnHeads[j], columnShifts[j]));

                    expected.Sort(Order);
                    actual.Sort(Order);
                    Assert.Equal(expected, actual);

                    gathered += n;
                }
            }

            // A NaN anywhere reaches the root's bounds, and then nothing is gathered at all - by either.
            Assert.True(spoilt || gathered > 200, $"only {gathered} models gathered");
            Assert.Contains(origins, o => o.X != 0 || o.Y != 0 || o.Z != 0);
        }

        /// <summary>
        /// Brushes the column touches: one open leaf listing every brush, so every line meets all of
        /// them, and columns down their sides, edges and corners and lines ending on their tops and
        /// bottoms - where a side the column lies in, rather than in front of, must not count as
        /// passing it by.
        /// </summary>
        [Theory]
        [InlineData(51)]
        [InlineData(52)]
        [InlineData(53)]
        [InlineData(54)]
        public void BrushesTheColumnTouchesAreStillMet(int seed)
        {
            var rng = new Random(seed);
            var planes = new List<BspFile.Plane> { TestWorlds.Plane(0, 0, 1, -10000f) };
            var sides = new List<BspFile.BrushSide>();
            var brushes = new List<BspFile.Brush>();
            var boxes = new List<(float X0, float Y0, float Z0, float X1, float Y1, float Z1)>();

            int AddPlane(float nx, float ny, float nz, float d)
            {
                planes.Add(TestWorlds.Plane(nx, ny, nz, d));
                return planes.Count - 1;
            }

            for (int i = 0; i < 30; i++)
            {
                float x0 = MathF.Round((float)(rng.NextDouble() * 400 - 200)) + 0.25f * rng.Next(4);
                float y0 = MathF.Round((float)(rng.NextDouble() * 400 - 200)) + 0.25f * rng.Next(4);
                float z0 = MathF.Round((float)(rng.NextDouble() * 100 - 50));
                float x1 = x0 + 1 + rng.Next(60), y1 = y0 + 1 + rng.Next(60), z1 = z0 + 0.5f * (1 + rng.Next(40));

                int first = sides.Count;
                foreach (int p in new[]
                         {
                             AddPlane(1, 0, 0, x1), AddPlane(-1, 0, 0, -x0), AddPlane(0, 1, 0, y1),
                             AddPlane(0, -1, 0, -y0), AddPlane(0, 0, 1, z1), AddPlane(0, 0, -1, -z0),
                         })
                {
                    sides.Add(new BspFile.BrushSide { PlaneNum = (ushort)p });
                }

                brushes.Add(new BspFile.Brush { FirstSide = first, NumSides = 6, Contents = Solid });
                boxes.Add((x0, y0, z0, x1, y1, z1));
            }

            var all = new int[brushes.Count];
            for (int i = 0; i < all.Length; i++)
                all[i] = i;

            var vis = BspVisibility.FromGeometry(planes.ToArray(),
                [new BspVisibility.Node { PlaneNum = 0, Child0 = -1, Child1 = -2 }],
                [new BspVisibility.Leaf { Contents = 0 }, new BspVisibility.Leaf { Contents = Solid }],
                brushes.ToArray(), sides.ToArray(), [all, null]);

            int blocked = 0;
            for (int i = 0; i < 400; i++)
            {
                var (x0, y0, z0, x1, y1, z1) = boxes[rng.Next(boxes.Count)];
                float x = rng.Next(3) switch { 0 => x0, 1 => x1, _ => x0 + (float)rng.NextDouble() * (x1 - x0) };
                float y = rng.Next(3) switch { 0 => y0, 1 => y1, _ => y0 + (float)rng.NextDouble() * (y1 - y0) };

                float low = rng.Next(2) == 0 ? z1 : z0 - (float)rng.NextDouble() * 20f;
                float high = rng.Next(2) == 0 ? z0 : z1 + (float)rng.NextDouble() * 20f;
                if (high <= low)
                    high = low + 8f;

                var column = vis.OpenColumn(x, y, low, high, BspVisibility.GroundMask);

                for (int k = 0; k < 10; k++)
                {
                    float from = rng.Next(3) == 0 ? high : low + (float)rng.NextDouble() * (high - low);
                    float to = rng.Next(2) == 0 ? low : low + (float)rng.NextDouble() * (high - low);

                    bool expected = vis.IsLineClear(new BspFile.Vector3(x, y, from), new BspFile.Vector3(x, y, to),
                        BspVisibility.GroundMask);
                    Assert.True(expected == column.IsClear(from, to),
                        $"({x:R}, {y:R}) from {from:R} to {to:R} in a column {low:R} to {high:R}");

                    if (!expected)
                        blocked++;
                }

                float top = high + 4f * rng.Next(0, 4);
                float depth = 4f * rng.Next(1, 12);
                bool was = OldTryFindFloor(vis, x, y, top, depth, out float expectedZ);
                Assert.Equal(was, StairMarker.TryFindFloor(vis, x, y, top, depth, out float actualZ));
                AssertSameFloat(expectedZ, actualZ);
            }

            Assert.True(blocked > 200, $"only {blocked} lines were blocked");
        }

        /// <summary>
        /// Two columns open on one thread at once share its buffers, so the first to open hands its
        /// lines to the line trace once the second has taken them over - and both still answer truly.
        /// </summary>
        [Theory]
        [InlineData(41)]
        [InlineData(42)]
        [InlineData(43)]
        [InlineData(44)]
        public void AColumnOpenedOverAnotherLeavesBothAnsweringTruly(int seed)
        {
            var rng = new Random(seed);
            var world = RandomWorld(rng);

            for (int i = 0; i < 30; i++)
            {
                var (x1, y1) = world.Column(rng);
                var (x2, y2) = world.Column(rng);
                float high1 = world.Height(rng), high2 = world.Height(rng);
                float low1 = high1 - 80f, low2 = high2 - 80f;

                var first = world.Vis.OpenColumn(x1, y1, low1, high1, BspVisibility.GroundMask);
                var second = world.Vis.OpenColumn(x2, y2, low2, high2, BspVisibility.GroundMask);

                for (int k = 0; k < 20; k++)
                {
                    float from1 = End(rng, world, low1, high1), to1 = End(rng, world, low1, high1);
                    float from2 = End(rng, world, low2, high2), to2 = End(rng, world, low2, high2);

                    Assert.Equal(world.Vis.IsLineClear(new(x1, y1, from1), new(x1, y1, to1), BspVisibility.GroundMask),
                        first.IsClear(from1, to1));
                    Assert.Equal(world.Vis.IsLineClear(new(x2, y2, from2), new(x2, y2, to2), BspVisibility.GroundMask),
                        second.IsClear(from2, to2));
                }
            }
        }

        /// <summary>
        /// A column through a world with no tree answers as the line trace does there: everything clear.
        /// </summary>
        [Fact]
        public void AWorldWithNoTreeIsClearDownEveryColumn()
        {
            var vis = BspVisibility.FromGeometry([], [], [], [], [], []);
            vis.AttachStaticProps(StaticProps.FromHulls(
                LineTraceStartSolidTests.Box(new BspFile.Vector3(-20, -20, -100), new BspFile.Vector3(20, 20, 100))));

            var column = vis.OpenColumn(0, 0, -200, 200, BspVisibility.GroundMask);
            Assert.True(column.IsClear(150, -150));
            Assert.Equal(OldTryFindFloor(vis, 0, 0, 150, 300, out float expected),
                StairMarker.TryFindFloor(vis, 0, 0, 150, 300, out float actual));
            AssertSameFloat(expected, actual);
        }

        private static int Order((int Head, BspFile.Vector3 Origin) a, (int Head, BspFile.Vector3 Origin) b)
        {
            int c = a.Head.CompareTo(b.Head);
            if (c != 0) return c;
            c = a.Origin.X.CompareTo(b.Origin.X);
            if (c != 0) return c;
            c = a.Origin.Y.CompareTo(b.Origin.Y);
            return c != 0 ? c : a.Origin.Z.CompareTo(b.Origin.Z);
        }

        private static void AssertSameFloat(float expected, float actual)
            => Assert.Equal(BitConverter.SingleToInt32Bits(expected), BitConverter.SingleToInt32Bits(actual));

        /// <summary>How deep to search: the depths the callers use, and anything.</summary>
        private static float Depth(Random rng) => rng.Next(8) switch
        {
            0 => 18f,
            1 => 36f,
            2 => 54f,
            3 => 13.5f,
            4 => 64f,
            5 => 300f + (float)rng.NextDouble() * 400f,
            6 => rng.Next(2) == 0 ? 0f : -4f,
            _ => (float)rng.NextDouble() * 120f,
        };

        /// <summary>An end for a line: often exactly on the column's bounds or a surface, often past them.</summary>
        private static float End(Random rng, World world, float low, float high) => rng.Next(6) switch
        {
            0 => low,
            1 => high,
            2 => world.Height(rng),
            3 => low - (float)rng.NextDouble() * 20f,
            _ => low + (float)rng.NextDouble() * (high - low),
        };

        internal sealed class World
        {
            public required BspVisibility Vis { get; init; }
            public required List<float> Heights { get; init; }
            public required List<(float X, float Y)> Sites { get; init; }
            public required BspFile.Vector3[] Vertices { get; init; }
            public required List<(float X0, float Y0, float X1, float Y1)> Brushes { get; init; }

            /// <summary>
            /// A column: on a triangle's corner, so down its edges; in the plane of a brush's side or
            /// down its edge; near a site; or anywhere.
            /// </summary>
            public (float X, float Y) Column(Random rng)
            {
                switch (rng.Next(5))
                {
                    case 4:
                    {
                        var (x0, y0, x1, y1) = Brushes[rng.Next(Brushes.Count)];
                        float x = rng.Next(3) switch { 0 => x0, 1 => x1, _ => x0 + (float)rng.NextDouble() * (x1 - x0) };
                        float y = rng.Next(3) switch { 0 => y0, 1 => y1, _ => y0 + (float)rng.NextDouble() * (y1 - y0) };
                        return (x, y);
                    }
                    case 0:
                    {
                        var v = Vertices[rng.Next(Vertices.Length)];
                        return (v.X, v.Y);
                    }
                    case 1:
                    case 2:
                    {
                        var (sx, sy) = Sites[rng.Next(Sites.Count)];
                        float x = sx + (float)(rng.NextDouble() * 160 - 80);
                        float y = sy + (float)(rng.NextDouble() * 160 - 80);
                        return rng.Next(2) == 0 ? (MathF.Round(x), MathF.Round(y)) : (x, y);
                    }
                    default:
                        return ((float)(rng.NextDouble() * 1200 - 600), (float)(rng.NextDouble() * 1200 - 600));
                }
            }

            /// <summary>
            /// A top for a search: a surface's height, or a whole number of segments above one, so the
            /// surface lands on a boundary; a float either side of that; or anywhere in the band.
            /// </summary>
            public float Height(Random rng)
            {
                float h = Heights[rng.Next(Heights.Count)] + 4f * rng.Next(0, 16);
                return rng.Next(5) switch
                {
                    0 => MathF.BitIncrement(h),
                    1 => MathF.BitDecrement(h),
                    2 => h + 4f / 128f,
                    3 => (float)(rng.NextDouble() * 400 - 200),
                    _ => h,
                };
            }
        }

        private static List<(float X, float Y)> Sites(Random rng)
        {
            var sites = new List<(float, float)>();
            for (int i = 0; i < 6; i++)
                sites.Add((MathF.Round((float)(rng.NextDouble() * 1000 - 500)), MathF.Round((float)(rng.NextDouble() * 1000 - 500))));
            return sites;
        }

        /// <summary>
        /// Terrain around the sites: grids of quads, flat at a height of their own or sloping steeply,
        /// on whole-unit corners so columns run down their shared edges; and loose triangles anywhere.
        /// Contents are mostly solid, now and then of a kind the ground mask ignores.
        /// </summary>
        private static (BspFile.Vector3[] Vertices, int[] Contents, List<float> Heights) Triangles(Random rng,
            List<(float X, float Y)> sites)
        {
            var vertices = new List<BspFile.Vector3>();
            var contents = new List<int>();
            var heights = new List<float>();

            foreach (var (sx, sy) in sites)
            {
                float z0 = MathF.Round((float)(rng.NextDouble() * 200 - 100)) + (rng.Next(2) == 0 ? 0f : 0.5f);
                heights.Add(z0);

                bool flat = rng.Next(2) == 0;
                float gx = flat ? 0 : (float)(rng.NextDouble() * 3 - 1.5);
                float gy = flat ? 0 : (float)(rng.NextDouble() * 3 - 1.5);
                int quads = 1 + rng.Next(6);
                float size = 16f * (1 + rng.Next(4));

                BspFile.Vector3 V(int i, int j)
                {
                    float x = sx + (i - quads / 2f) * size, y = sy + (j - quads / 2f) * size;
                    return new BspFile.Vector3(x, y, z0 + gx * (x - sx) + gy * (y - sy));
                }

                for (int i = 0; i < quads; i++)
                {
                    for (int j = 0; j < quads; j++)
                    {
                        int c = rng.Next(10) == 0 ? 0x2 : Solid;
                        vertices.AddRange([V(i, j), V(i + 1, j), V(i + 1, j + 1)]);
                        vertices.AddRange([V(i, j), V(i + 1, j + 1), V(i, j + 1)]);
                        contents.Add(c);
                        contents.Add(c);
                    }
                }
            }

            for (int i = 0; i < 40; i++)
            {
                var c = new BspFile.Vector3((float)(rng.NextDouble() * 1000 - 500), (float)(rng.NextDouble() * 1000 - 500),
                    (float)(rng.NextDouble() * 300 - 150));
                for (int k = 0; k < 3; k++)
                {
                    vertices.Add(new BspFile.Vector3(c.X + (float)(rng.NextDouble() * 200 - 100),
                        c.Y + (float)(rng.NextDouble() * 200 - 100), c.Z + (float)(rng.NextDouble() * 120 - 60)));
                }

                contents.Add(rng.Next(6) == 0 ? 0x2 : Solid);
            }

            return (vertices.ToArray(), contents.ToArray(), heights);
        }

        /// <summary>
        /// A random world around the band the searches run in: a tree of planes and brushes not
        /// describing any consistent solid, only exercising the walk; terrain; props now and then; and
        /// brush entities now and then.
        /// </summary>
        internal static World RandomWorld(Random rng)
        {
            var sites = Sites(rng);
            var (vertices, triangleContents, heights) = Triangles(rng, sites);

            var planes = new List<BspFile.Plane>();
            var nodes = new List<BspVisibility.Node>();
            var leafs = new List<BspVisibility.Leaf>();
            var leafBrushes = new List<int[]?>();
            var brushes = new List<BspFile.Brush>();
            var faces = new List<(float, float, float, float)>();
            var sides = new List<BspFile.BrushSide>();

            int AddPlane(float nx, float ny, float nz, float d)
            {
                planes.Add(new BspFile.Plane { Normal = new BspFile.Vector3(nx, ny, nz), Distance = d });
                return planes.Count - 1;
            }

            int RandomPlane()
            {
                switch (rng.Next(6))
                {
                    case 0:
                    case 1:
                    {
                        // Horizontal, at a height of its own or at a terrain height, where the search's
                        // segments end.
                        float h = rng.Next(2) == 0
                            ? heights[rng.Next(heights.Count)] + 4f * rng.Next(-3, 4)
                            : MathF.Round((float)(rng.NextDouble() * 300 - 150)) + 0.25f * rng.Next(4);
                        heights.Add(h);
                        return rng.Next(2) == 0 ? AddPlane(0, 0, 1, h) : AddPlane(0, 0, -1, -h);
                    }
                    case 2:
                    {
                        double a = rng.NextDouble() * Math.PI * 2;
                        return AddPlane((float)Math.Cos(a), (float)Math.Sin(a), 0, (float)(rng.NextDouble() * 1000 - 500));
                    }
                    case 3:
                    {
                        double a = rng.NextDouble() * Math.PI * 2;
                        float nz = (float)(rng.NextDouble() * 0.01) * (rng.Next(2) == 0 ? 1 : -1);
                        float r = MathF.Sqrt(1 - nz * nz);
                        return AddPlane(r * (float)Math.Cos(a), r * (float)Math.Sin(a), nz, (float)(rng.NextDouble() * 1000 - 500));
                    }
                    default:
                    {
                        float nx = (float)(rng.NextDouble() * 2 - 1);
                        float ny = (float)(rng.NextDouble() * 2 - 1);
                        float nz = (float)(rng.NextDouble() * 2 - 1);
                        float length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                        if (length < 1e-3f)
                            return AddPlane(0, 0, 1, 0);

                        return AddPlane(nx / length, ny / length, nz / length, (float)(rng.NextDouble() * 400 - 200));
                    }
                }
            }

            int RandomBrush()
            {
                float x0 = (float)(rng.NextDouble() * 1200 - 600), y0 = (float)(rng.NextDouble() * 1200 - 600);
                float z0 = rng.Next(2) == 0
                    ? heights[rng.Next(heights.Count)] + 4f * rng.Next(-4, 4)
                    : MathF.Round((float)(rng.NextDouble() * 300 - 150));
                float x1 = x0 + 8f + (float)(rng.NextDouble() * 600), y1 = y0 + 8f + (float)(rng.NextDouble() * 600);
                float z1 = z0 + 0.5f + (float)(rng.NextDouble() * (rng.Next(2) == 0 ? 12 : 200));

                int first = sides.Count;
                foreach (int p in new[]
                         {
                             AddPlane(1, 0, 0, x1), AddPlane(-1, 0, 0, -x0), AddPlane(0, 1, 0, y1),
                             AddPlane(0, -1, 0, -y0), AddPlane(0, 0, 1, z1), AddPlane(0, 0, -1, -z0),
                         })
                {
                    sides.Add(new BspFile.BrushSide { PlaneNum = (ushort)p });
                }

                heights.Add(z1);
                heights.Add(z0);
                faces.Add((x0, y0, x1, y1));

                if (rng.Next(3) == 0)
                    sides.Add(new BspFile.BrushSide { PlaneNum = (ushort)RandomPlane() });

                // Now and then a side naming a plane that is not there, or a brush whose sides run off
                // the end of the lump, which the brush test answers in its own way.
                if (rng.Next(25) == 0)
                    sides.Add(new BspFile.BrushSide { PlaneNum = 60000 });

                int[] kinds = [Solid, Solid, Solid, 0x2, 0x10];
                brushes.Add(new BspFile.Brush
                {
                    FirstSide = rng.Next(40) == 0 ? 1_000_000 : first, NumSides = sides.Count - first,
                    Contents = kinds[rng.Next(kinds.Length)],
                });
                return brushes.Count - 1;
            }

            int Leaf()
            {
                int[] kinds = [0, 0, 0, Solid, 0x10, 0x2];
                int c = kinds[rng.Next(kinds.Length)];
                leafs.Add(new BspVisibility.Leaf { Contents = c });

                int[]? list = null;
                if (c == 0 || c == 0x10)
                {
                    int n = rng.Next(4);
                    if (n > 0)
                    {
                        list = new int[n];
                        for (int i = 0; i < n; i++)
                            list[i] = RandomBrush();
                    }
                }

                leafBrushes.Add(list);

                // Now and then a child naming a leaf past the end of the lump.
                return rng.Next(60) == 0 ? -(1000 + rng.Next(100)) - 1 : -leafs.Count;
            }

            int Build(int depth)
            {
                if (depth == 0 || (nodes.Count > 0 && rng.Next(8) == 0))
                    return Leaf();

                int index = nodes.Count;
                nodes.Add(default);

                // Now and then a node naming a plane that is not there.
                int plane = rng.Next(80) == 0 ? 60000 : RandomPlane();
                int child0 = Build(depth - 1);
                int child1 = Build(depth - 1);
                nodes[index] = new BspVisibility.Node { PlaneNum = plane, Child0 = child0, Child1 = child1 };
                return index;
            }

            Build(10);

            var vis = BspVisibility.FromGeometry(planes.ToArray(), nodes.ToArray(), leafs.ToArray(),
                brushes.ToArray(), sides.ToArray(), leafBrushes.ToArray());

            vis.AttachDisplacements(BspDisplacements.FromTriangles(vertices, triangleContents));

            if (rng.Next(3) != 0)
            {
                var hulls = new List<BspFile.Vector3[]>();
                for (int i = 0; i < 8; i++)
                {
                    var (sx, sy) = sites[rng.Next(sites.Count)];
                    float x0 = sx + (float)(rng.NextDouble() * 200 - 100), y0 = sy + (float)(rng.NextDouble() * 200 - 100);
                    float z0 = heights[rng.Next(heights.Count)] + 4f * rng.Next(-2, 3);
                    hulls.Add(LineTraceStartSolidTests.Box(new BspFile.Vector3(x0, y0, z0),
                        new BspFile.Vector3(x0 + 4 + (float)rng.NextDouble() * 100,
                            y0 + 4 + (float)rng.NextDouble() * 100, z0 + 1 + (float)rng.NextDouble() * 60)));
                }

                vis.AttachStaticProps(StaticProps.FromHulls([.. hulls]));
            }

            if (rng.Next(3) != 0)
            {
                var path = Path.Combine(Path.GetTempPath(), $"mw_test_{Guid.NewGuid():N}.bsp");
                try
                {
                    WriteModels(rng, path, nodes.Count, rng.Next(6) == 0, out _);
                    var bsp = BspFile.Load(path);
                    vis.AttachModels(
                        BspModels.Load(path, bsp),
                        BspModels.Load(path, bsp, forMovement: true),
                        BspModels.Load(path, bsp, forMovement: true, forGround: true));
                }
                finally
                {
                    File.Delete(path);
                }
            }

            return new World { Vis = vis, Heights = heights, Sites = sites, Vertices = vertices, Brushes = faces };
        }

        /// <summary>
        /// Random brush entities, written to a map file and loaded as the generator loads them, each
        /// heading a subtree of the world's (<paramref name="nodeCount"/> nodes) or a leaf, and placed by
        /// an origin now and then. With <paramref name="spoilt"/>, one of them has a NaN for a height.
        /// </summary>
        private static void WriteModels(Random rng, string path, int nodeCount, bool spoilt,
            out List<BspFile.Vector3> origins)
        {
            int count = 4 + rng.Next(30);
            var models = new (BspFile.Vector3 Mins, BspFile.Vector3 Maxs)[count + 1];
            var heads = new int[count + 1];
            var entities = new System.Text.StringBuilder("{ \"classname\" \"worldspawn\" }\n");
            origins = [];

            models[0] = (new BspFile.Vector3(-5000, -5000, -5000), new BspFile.Vector3(5000, 5000, 5000));

            for (int i = 1; i <= count; i++)
            {
                float x0 = MathF.Round((float)(rng.NextDouble() * 1200 - 600)), y0 = MathF.Round((float)(rng.NextDouble() * 1200 - 600));
                float z0 = MathF.Round((float)(rng.NextDouble() * 300 - 150));
                var mins = new BspFile.Vector3(x0, y0, z0);
                var maxs = new BspFile.Vector3(x0 + 1 + (float)rng.Next(400), y0 + 1 + (float)rng.Next(400), z0 + 1 + (float)rng.Next(100));

                if (spoilt && i == 1)
                    maxs = new BspFile.Vector3(maxs.X, maxs.Y, float.NaN);

                models[i] = (mins, maxs);
                heads[i] = rng.Next(3) == 0 ? -1 - rng.Next(4) : rng.Next(Math.Min(nodeCount, 40));

                var origin = rng.Next(2) == 0
                    ? default
                    : new BspFile.Vector3(MathF.Round((float)(rng.NextDouble() * 200 - 100)), MathF.Round((float)(rng.NextDouble() * 200 - 100)),
                        MathF.Round((float)(rng.NextDouble() * 60 - 30)));
                origins.Add(origin);

                entities.Append($"{{ \"classname\" \"func_brush\" \"model\" \"*{i}\" \"origin\" \"{origin.X} {origin.Y} {origin.Z}\" }}\n");
            }

            TestWorlds.WriteBsp(path, entities.ToString(), models, [], heads);
        }

        /// <summary>Brush entities on their own, for the gather test.</summary>
        private static BspModels RandomModels(Random rng, bool spoilt, out List<BspFile.Vector3> origins)
        {
            var path = Path.Combine(Path.GetTempPath(), $"mw_test_{Guid.NewGuid():N}.bsp");
            try
            {
                WriteModels(rng, path, 1, spoilt, out origins);
                return BspModels.Load(path, BspFile.Load(path));
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
