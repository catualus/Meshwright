using System;
using System.Collections.Generic;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That the floor walk's batched descent through solid stops exactly where stepping one point test
    /// at a time stopped.
    ///
    /// <see cref="BspVisibility.StepOutOfSolid"/> walks the BSP tree once for a run of heights rather
    /// than once per height, splitting the run at each plane by searching for where the heights cross
    /// it. That is only the same answer if the search finds, to the bit, the split a point descent
    /// makes - which rests on the plane test being monotonic in height. These hold it to the plain loop
    /// on random trees full of the planes that would break that: steep, near-vertical, vertical, and
    /// heights landing exactly on a plane.
    /// </summary>
    public class FloorWalkStepTests
    {
        private const int Solid = 0x1;

        /// <summary>The loop <see cref="BspVisibility.StepOutOfSolid"/> replaces, verbatim.</summary>
        private static float Stepped(BspVisibility vis, float x, float y, float cursor, float bottomZ)
        {
            while (cursor > bottomZ && vis.IsPointSolid(x, y, cursor, BspVisibility.GroundMask))
                cursor -= BspVisibility.SolidStep;

            return cursor;
        }

        /// <summary>
        /// <see cref="BspVisibility.EnumerateFloors"/> as it was before the descent was batched, built
        /// only from the public point test and surface trace.
        /// </summary>
        private static int SteppedFloors(BspVisibility vis, float x, float y, float topZ, float bottomZ,
            Span<BspVisibility.FloorSample> into)
        {
            int count = 0;
            float cursor = topZ;
            bool buried = vis.IsPointSolid(x, y, cursor, BspVisibility.GroundMask);

            while (count < into.Length && cursor > bottomZ)
            {
                if (!vis.TryTraceSurface(new BspFile.Vector3(x, y, cursor), new BspFile.Vector3(x, y, bottomZ),
                        BspVisibility.GroundMask, out var point, out var normal, out _, out int flags))
                    break;

                if (!buried)
                    into[count++] = new BspVisibility.FloorSample(point.Z, normal, flags);

                buried = false;
                cursor = Stepped(vis, x, y, point.Z - 0.25f, bottomZ);
            }

            return count;
        }

        private static void AssertSameFloat(float expected, float actual)
            => Assert.Equal(BitConverter.SingleToInt32Bits(expected), BitConverter.SingleToInt32Bits(actual));

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
        [InlineData(11)]
        [InlineData(12)]
        public void TheBatchedDescentStopsWhereSteppingDid(int seed)
        {
            var rng = new Random(seed);
            var vis = RandomWorld(rng, out var heights, out _);

            for (int i = 0; i < 300; i++)
            {
                var (x, y) = Column(rng);
                float cursor = Height(rng, heights);
                float bottom = cursor - (float)rng.NextDouble() * 24000f;

                AssertSameFloat(Stepped(vis, x, y, cursor, bottom), vis.StepOutOfSolid(x, y, cursor, bottom));
            }
        }

        /// <summary>
        /// Every suffix of a run of heights, so that every split the walk makes is the first point of
        /// some run - and the heights include each horizontal plane's own height, and the floats either
        /// side of where every other plane crosses the column, where a split found any other way than
        /// by asking the points themselves would come out a point off.
        /// </summary>
        [Theory]
        [InlineData(21)]
        [InlineData(22)]
        [InlineData(23)]
        [InlineData(24)]
        [InlineData(25)]
        [InlineData(26)]
        public void TheFirstOpenHeightIsTheOnePointTestsFind(int seed)
        {
            var rng = new Random(seed);
            var vis = RandomWorld(rng, out var heights, out var planes);

            for (int i = 0; i < 60; i++)
            {
                var (x, y) = Column(rng);

                var run = new List<float>();
                foreach (float h in heights)
                {
                    run.Add(h);
                    run.Add(MathF.BitIncrement(h));
                    run.Add(MathF.BitDecrement(h));
                }

                foreach (var plane in planes)
                {
                    if (MathF.Abs(plane.Normal.Z) < 1e-12f)
                        continue;

                    float z = (plane.Distance - plane.Normal.X * x - plane.Normal.Y * y) / plane.Normal.Z;
                    if (!float.IsFinite(z) || MathF.Abs(z) > 30000f)
                        continue;

                    run.Add(z);
                    run.Add(MathF.BitIncrement(z));
                    run.Add(MathF.BitDecrement(z));
                }

                for (int k = 0; k < 40; k++)
                    run.Add(Height(rng, heights));

                run.Sort((a, b) => b.CompareTo(a));
                var descending = run.ToArray();

                var solid = new bool[descending.Length];
                for (int k = 0; k < solid.Length; k++)
                    solid[k] = vis.IsPointSolid(x, y, descending[k], BspVisibility.GroundMask);

                for (int start = 0; start < descending.Length; start++)
                {
                    int expected = start;
                    while (expected < descending.Length && solid[expected])
                        expected++;

                    Assert.Equal(expected - start,
                        vis.FirstOpenStep(descending.AsSpan(start), x, y, BspVisibility.GroundMask));
                }
            }
        }

        [Theory]
        [InlineData(31)]
        [InlineData(32)]
        [InlineData(33)]
        [InlineData(34)]
        [InlineData(35)]
        [InlineData(36)]
        public void TheFloorsFoundAreTheSteppedWalksFloors(int seed)
        {
            var rng = new Random(seed);
            var vis = RandomWorld(rng, out _, out _);

            Span<BspVisibility.FloorSample> expected = stackalloc BspVisibility.FloorSample[16];
            Span<BspVisibility.FloorSample> actual = stackalloc BspVisibility.FloorSample[16];

            for (int i = 0; i < 150; i++)
            {
                var (x, y) = Column(rng);
                float top = 9000f - (float)rng.NextDouble() * 2000f;
                float bottom = -9000f + (float)rng.NextDouble() * 2000f;

                int n = SteppedFloors(vis, x, y, top, bottom, expected);
                Assert.Equal(n, vis.EnumerateFloors(x, y, top, bottom, actual));

                for (int k = 0; k < n; k++)
                    Assert.Equal(expected[k], actual[k]);
            }
        }

        /// <summary>
        /// Deep solid, past the largest batch more than once: the ground of a plain world walked from
        /// just under its face to thirty thousand units down.
        /// </summary>
        [Fact]
        public void ADescentLongerThanEveryBatchTogetherEndsAtTheBottom()
        {
            var vis = TestWorlds.Ground();

            foreach (float start in new[] { -0.25f, -1f, -3.3f })
            {
                float expected = Stepped(vis, 10, 10, start, -30000f);
                Assert.True(expected <= -30000f);
                AssertSameFloat(expected, vis.StepOutOfSolid(10, 10, start, -30000f));
            }

            // And one that comes out after more steps than every batch together: solid from z = 0 down
            // to z = -20000, open below.
            var slab = BspVisibility.FromGeometry(
                [TestWorlds.Plane(0, 0, 1, 0), TestWorlds.Plane(0, 0, 1, -20000)],
                [
                    new BspVisibility.Node { PlaneNum = 0, Child0 = -1, Child1 = 1 },
                    new BspVisibility.Node { PlaneNum = 1, Child0 = -2, Child1 = -3 },
                ],
                [
                    new BspVisibility.Leaf { Contents = 0 },
                    new BspVisibility.Leaf { Contents = Solid },
                    new BspVisibility.Leaf { Contents = 0 },
                ],
                [], [], [null, null, null]);

            float through = Stepped(slab, 0, 0, -0.25f, -30000f);
            Assert.True(through < -20000f && through > -20010f);
            AssertSameFloat(through, slab.StepOutOfSolid(0, 0, -0.25f, -30000f));
        }

        /// <summary>
        /// Why the walk is not simply moved to where the solid ends: a gap thinner than a step is
        /// stepped over, and the floor at the bottom of it is never seen. Jumping to the underside of
        /// the slab would find it, and change the mesh. This pins the behaviour the batching keeps.
        /// </summary>
        [Fact]
        public void AGapThinnerThanAStepIsSteppedOverAsItWas()
        {
            var vis = StartSolidTests.Boxes(
                (new BspFile.Vector3(-100, -100, 100), new BspFile.Vector3(100, 100, 120)),
                (new BspFile.Vector3(-100, -100, 90), new BspFile.Vector3(100, 100, 99.5f)));

            Span<BspVisibility.FloorSample> samples = stackalloc BspVisibility.FloorSample[16];
            int count = vis.EnumerateFloors(0, 0, 400, -400, samples);

            Assert.Equal(2, count);
            Assert.Equal(120f, samples[0].Z, 3);
            Assert.Equal(0f, samples[1].Z, 3);
        }

        /// <summary>Props and brush entities are asked point by point, and still give the same stops.</summary>
        [Fact]
        public void PropsAndBrushEntitiesStopTheDescentWhereTheyDid()
        {
            var block = ToggleFloorTests.WorldWithBlock("");
            for (float z = 70f; z > -10f; z -= 0.75f)
                AssertSameFloat(Stepped(block, 50, 50, z, -400), block.StepOutOfSolid(50, 50, z, -400));

            var props = StartSolidTests.Boxes((new BspFile.Vector3(-30, -30, 0), new BspFile.Vector3(30, 30, 40)));
            props.AttachStaticProps(StaticProps.FromHulls(
                LineTraceStartSolidTests.Box(new BspFile.Vector3(-20, -20, 30), new BspFile.Vector3(20, 20, 300)),
                LineTraceStartSolidTests.Box(new BspFile.Vector3(-20, -20, -600), new BspFile.Vector3(20, 20, -520))));

            for (float z = 320f; z > -700f; z -= 1.25f)
                AssertSameFloat(Stepped(props, 0, 0, z, -800), props.StepOutOfSolid(0, 0, z, -800));
        }

        /// <summary>A world with no tree is answered by its props alone, as the point test answers it.</summary>
        [Fact]
        public void AWorldWithNoTreeIsAnsweredByItsPropsAlone()
        {
            var vis = BspVisibility.FromGeometry([], [], [], [], [], []);
            vis.AttachStaticProps(StaticProps.FromHulls(
                LineTraceStartSolidTests.Box(new BspFile.Vector3(-20, -20, -100), new BspFile.Vector3(20, 20, 100))));

            AssertSameFloat(Stepped(vis, 0, 0, 90, -400), vis.StepOutOfSolid(0, 0, 90, -400));
            AssertSameFloat(Stepped(vis, 50, 0, 90, -400), vis.StepOutOfSolid(50, 0, 90, -400));
        }

        private static (float X, float Y) Column(Random rng)
        {
            // Whole units half the time, as the generator's grid mostly is; anywhere otherwise.
            float x = (float)(rng.NextDouble() * 4000 - 2000);
            float y = (float)(rng.NextDouble() * 4000 - 2000);
            return rng.Next(2) == 0 ? (MathF.Round(x), MathF.Round(y)) : (x, y);
        }

        /// <summary>
        /// A start height: often exactly a horizontal plane's height, or a whole number of steps above
        /// one, so that the steps land on it; otherwise anywhere.
        /// </summary>
        private static float Height(Random rng, List<float> heights)
        {
            switch (rng.Next(4))
            {
                case 0 when heights.Count > 0:
                    return heights[rng.Next(heights.Count)] + BspVisibility.SolidStep * rng.Next(0, 40);
                case 1 when heights.Count > 0:
                    return heights[rng.Next(heights.Count)] - 0.25f + BspVisibility.SolidStep * rng.Next(0, 40);
                case 2:
                    return MathF.Round((float)(rng.NextDouble() * 18000 - 9000));
                default:
                    return (float)(rng.NextDouble() * 18000 - 9000);
            }
        }

        /// <summary>
        /// A random tree, not a real map: it need not describe any consistent solid, only exercise the
        /// descent. Planes are horizontal, vertical, steep or anything; leaves are solid, open, or of
        /// contents the mask ignores, and the open ones list random brushes, some cut by a slanted side.
        /// A few nodes name a plane that does not exist and a few children a leaf that does not, which
        /// the point test answers in its own way. <paramref name="heights"/> receives the heights of the
        /// horizontal planes, where steps can land exactly on a plane, and <paramref name="allPlanes"/> every
        /// plane.
        /// </summary>
        private static BspVisibility RandomWorld(Random rng, out List<float> heights,
            out List<BspFile.Plane> allPlanes)
        {
            var planes = new List<BspFile.Plane>();
            allPlanes = planes;
            var nodes = new List<BspVisibility.Node>();
            var leafs = new List<BspVisibility.Leaf>();
            var leafBrushes = new List<int[]?>();
            var brushes = new List<BspFile.Brush>();
            var sides = new List<BspFile.BrushSide>();
            var levels = new List<float>();
            heights = levels;

            int AddPlane(float nx, float ny, float nz, float d)
            {
                planes.Add(new BspFile.Plane { Normal = new BspFile.Vector3(nx, ny, nz), Distance = d });
                return planes.Count - 1;
            }

            int RandomPlane()
            {
                float d = (float)(rng.NextDouble() * 6000 - 3000);
                switch (rng.Next(5))
                {
                    case 0:
                    {
                        float h = rng.Next(2) == 0 ? MathF.Round(d * 2f) : d * 2f;
                        levels.Add(h);
                        return rng.Next(2) == 0 ? AddPlane(0, 0, 1, h) : AddPlane(0, 0, -1, -h);
                    }
                    case 1:
                    {
                        double a = rng.NextDouble() * Math.PI * 2;
                        return AddPlane((float)Math.Cos(a), (float)Math.Sin(a), 0, d / 2f);
                    }
                    case 2:
                    {
                        // Steep: the plane crosses a column a long way from where it crosses its
                        // neighbour, and its height test changes slowly with height.
                        double a = rng.NextDouble() * Math.PI * 2;
                        float nz = (float)(rng.NextDouble() * 0.01) * (rng.Next(2) == 0 ? 1 : -1);
                        float r = MathF.Sqrt(1 - nz * nz);
                        return AddPlane(r * (float)Math.Cos(a), r * (float)Math.Sin(a), nz, d / 2f);
                    }
                    default:
                    {
                        float nx = (float)(rng.NextDouble() * 2 - 1);
                        float ny = (float)(rng.NextDouble() * 2 - 1);
                        float nz = (float)(rng.NextDouble() * 2 - 1);
                        float length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                        if (length < 1e-3f)
                            return AddPlane(0, 0, 1, d);

                        return AddPlane(nx / length, ny / length, nz / length, d);
                    }
                }
            }

            int RandomBrush()
            {
                float x0 = (float)(rng.NextDouble() * 4000 - 2000), y0 = (float)(rng.NextDouble() * 4000 - 2000);
                float z0 = rng.Next(2) == 0 && levels.Count > 0
                    ? levels[rng.Next(levels.Count)]
                    : MathF.Round((float)(rng.NextDouble() * 16000 - 8000));
                float x1 = x0 + (float)(rng.NextDouble() * 3000), y1 = y0 + (float)(rng.NextDouble() * 3000);
                float z1 = z0 + 0.5f + (float)(rng.NextDouble() * (rng.Next(2) == 0 ? 12 : 3000));

                int first = sides.Count;
                foreach (int p in new[]
                         {
                             AddPlane(1, 0, 0, x1), AddPlane(-1, 0, 0, -x0), AddPlane(0, 1, 0, y1),
                             AddPlane(0, -1, 0, -y0), AddPlane(0, 0, 1, z1), AddPlane(0, 0, -1, -z0),
                         })
                {
                    sides.Add(new BspFile.BrushSide { PlaneNum = (ushort)p });
                }

                if (rng.Next(3) == 0)
                    sides.Add(new BspFile.BrushSide { PlaneNum = (ushort)RandomPlane() });

                int[] contents = [Solid, Solid, Solid, 0x2, 0x10];
                brushes.Add(new BspFile.Brush
                {
                    FirstSide = first, NumSides = sides.Count - first, Contents = contents[rng.Next(contents.Length)],
                });
                return brushes.Count - 1;
            }

            int Leaf()
            {
                int[] contents = [0, 0, Solid, Solid, 0x10, 0x2];
                int c = contents[rng.Next(contents.Length)];
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

            Build(9);

            var vis = BspVisibility.FromGeometry(planes.ToArray(), nodes.ToArray(), leafs.ToArray(),
                brushes.ToArray(), sides.ToArray(), leafBrushes.ToArray());

            if (rng.Next(2) == 0)
            {
                var hulls = new List<BspFile.Vector3[]>();
                for (int i = 0; i < 6; i++)
                {
                    float x0 = (float)(rng.NextDouble() * 4000 - 2000), y0 = (float)(rng.NextDouble() * 4000 - 2000);
                    float z0 = (float)(rng.NextDouble() * 16000 - 8000);
                    hulls.Add(LineTraceStartSolidTests.Box(new BspFile.Vector3(x0, y0, z0),
                        new BspFile.Vector3(x0 + 50 + (float)rng.NextDouble() * 2000,
                            y0 + 50 + (float)rng.NextDouble() * 2000, z0 + 1 + (float)rng.NextDouble() * 400)));
                }

                vis.AttachStaticProps(StaticProps.FromHulls([.. hulls]));
            }

            return vis;
        }
    }
}
