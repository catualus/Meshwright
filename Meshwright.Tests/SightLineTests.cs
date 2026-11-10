using System;
using System.Collections.Generic;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That the visibility tracer answers exactly what it did before it learnt to skip work: the lines
    /// it no longer traces because the PVS or a sight point in solid already says they are blocked, and
    /// the order <see cref="BspVisibility.IsSightClear"/> asks the four kinds of geometry in.
    ///
    /// The tracer as it was is kept here verbatim and held against the new one on random worlds - a
    /// tree of random planes and leaves, terrain, props now and then and brush entities now and then -
    /// so that sight points land in solid, lines stop on every kind of geometry, and the carried
    /// answer is wrong as often as it is right. A small hand-built world pins what the PVS is asked:
    /// the cluster each line starts and ends in, at eye height rather than at the floor.
    /// </summary>
    public class SightLineTests
    {
        private const int Solid = 0x1;

        private static BspFile.Vector3 V(float x, float y, float z) => new(x, y, z);

        private static NavArea Quad(uint id, float x0, float y0, float x1, float y1, float z = 0f)
        {
            var area = new NavArea { Id = id };
            area.NwCorner[0] = x0; area.NwCorner[1] = y0; area.NwCorner[2] = z;
            area.SeCorner[0] = x1; area.SeCorner[1] = y1; area.SeCorner[2] = z;
            area.NeZ = z;
            area.SwZ = z;
            return area;
        }

        private static NavFile Mesh(params NavArea[] areas)
        {
            var nav = new NavFile();
            nav.Areas.AddRange(areas);
            return nav;
        }

        // ---- a hand-built world ----------------------------------------------------------------

        /// <summary>
        /// Solid ground under z = 0. Above it, everything below z = 30 is cluster 2, so the floor
        /// samples the pair filter looks at all land there. From z = 30 up the world is cluster 1 where
        /// x &lt; 0 and cluster 0 where x &gt;= 0, except that past x = 500 a solid slab comes down to
        /// z = 40, below the eye height of an area on the ground there.
        /// </summary>
        private static BspVisibility Rooms(byte[][]? pvs)
        {
            var planes = new[]
            {
                TestWorlds.Plane(0, 0, 1, 0),    // 0: ground
                TestWorlds.Plane(0, 0, 1, 30),   // 1: floor band / eye band
                TestWorlds.Plane(1, 0, 0, 0),    // 2: x = 0
                TestWorlds.Plane(1, 0, 0, 500),  // 3: x = 500
                TestWorlds.Plane(0, 0, 1, 40),   // 4: the slab's underside
            };

            var nodes = new[]
            {
                new BspVisibility.Node { PlaneNum = 0, Child0 = 1, Child1 = -1 },
                new BspVisibility.Node { PlaneNum = 1, Child0 = 2, Child1 = -2 },
                new BspVisibility.Node { PlaneNum = 2, Child0 = 3, Child1 = -3 },
                new BspVisibility.Node { PlaneNum = 3, Child0 = 4, Child1 = -4 },
                new BspVisibility.Node { PlaneNum = 4, Child0 = -5, Child1 = -6 },
            };

            var leafs = new[]
            {
                new BspVisibility.Leaf { Contents = Solid, Cluster = -1 }, // 0: ground
                new BspVisibility.Leaf { Contents = 0, Cluster = 2 },      // 1: floor band
                new BspVisibility.Leaf { Contents = 0, Cluster = 1 },      // 2: x < 0
                new BspVisibility.Leaf { Contents = 0, Cluster = 0 },      // 3: 0 <= x < 500
                new BspVisibility.Leaf { Contents = Solid, Cluster = -1 }, // 4: the slab
                new BspVisibility.Leaf { Contents = 0, Cluster = 0 },      // 5: under the slab
            };

            return BspVisibility.FromGeometry(planes, nodes, leafs, [], [], new int[]?[leafs.Length], pvs: pvs);
        }

        /// <summary>One byte a row: bit n set when that row's cluster may see cluster n.</summary>
        private static byte[][] Rows(params byte[] rows)
        {
            var result = new byte[rows.Length][];
            for (int i = 0; i < rows.Length; i++)
                result[i] = [rows[i]];
            return result;
        }

        /// <summary>Clusters 0 and 1 cannot see each other; the floor band sees and is seen by both.</summary>
        private static byte[][] Apart => Rows(0b101, 0b110, 0b111);

        private static byte[][] AllVisible => Rows(0b111, 0b111, 0b111);

        private static (VisibilityTracer Tracer, int[][] Visible) Trace(NavFile nav, BspVisibility vis)
        {
            var filter = new VisibilityFilter(nav, vis);
            var tracer = new VisibilityTracer(filter, vis, nav.Areas.Count);
            filter.Run(tracer);
            var visible = tracer.Symmetrise();
            foreach (var ids in visible)
                Array.Sort(ids);
            return (tracer, visible);
        }

        [Fact]
        public void SightClustersAreTakenAtEyeHeight()
        {
            var vis = Rooms(Apart);
            var filter = new VisibilityFilter(Mesh(Quad(1, 100, 0, 200, 100), Quad(2, -200, 0, -100, 100)), vis);

            // The floor samples are all in the band below z = 30; the eyes are above it.
            foreach (short cluster in filter.SightClusters(0))
                Assert.Equal(0, cluster);

            foreach (short cluster in filter.SightClusters(1))
                Assert.Equal(1, cluster);
        }

        [Fact]
        public void AnEyeInsideSolidIsMarkedAsSuch()
        {
            var vis = Rooms(Apart);
            var filter = new VisibilityFilter(Mesh(Quad(1, 600, 0, 700, 100)), vis);

            foreach (short cluster in filter.SightClusters(0))
                Assert.Equal(VisibilityFilter.SightInSolid, cluster);
        }

        [Fact]
        public void MaySeeReadsTheRowOfTheLinesStart()
        {
            // Cluster 1 may see 0, but 0 may not see 1.
            var vis = Rooms(Rows(0b101, 0b111, 0b111));
            var filter = new VisibilityFilter(Mesh(Quad(1, 100, 0, 200, 100)), vis);

            Assert.False(filter.MaySee(0, 1));
            Assert.True(filter.MaySee(1, 0));
            Assert.True(filter.MaySee(0, 2));
            Assert.True(filter.MaySee(2, 1));
        }

        [Fact]
        public void MaySeeOnlyRulesOutWhatItKnows()
        {
            var vis = Rooms(Apart);
            var filter = new VisibilityFilter(Mesh(Quad(1, 100, 0, 200, 100)), vis);

            Assert.True(filter.MaySee(-1, 1));
            Assert.True(filter.MaySee(0, -1));
            Assert.True(filter.MaySee(0, 200));    // past the end of the row
            Assert.False(filter.MaySee(VisibilityFilter.SightInSolid, 2));
            Assert.False(filter.MaySee(2, VisibilityFilter.SightInSolid));
            Assert.False(filter.MaySee(VisibilityFilter.SightInSolid, -1));

            var none = new VisibilityFilter(Mesh(Quad(1, 100, 0, 200, 100)), Rooms(null));
            Assert.True(none.MaySee(0, 1));
            Assert.False(none.MaySee(0, VisibilityFilter.SightInSolid));
        }

        /// <summary>
        /// Two areas whose floors share a cluster, so the pair filter passes them, and whose eyes are in
        /// clusters the PVS keeps apart. The line between them is clear, so only the PVS can say no -
        /// and every one of the 25 lines, at every stage, has to ask it.
        /// </summary>
        [Fact]
        public void EveryStageAsksThePvsAboutEachLine()
        {
            var nav = Mesh(Quad(1, 100, 0, 200, 100), Quad(2, -200, 0, -100, 100));
            var vis = Rooms(Apart);

            Assert.True(vis.IsLineClear(V(150, 50, 54), V(-150, 50, 54)));

            var (tracer, visible) = Trace(nav, vis);

            Assert.Empty(visible[0]);
            Assert.Empty(visible[1]);
            Assert.Equal(0, tracer.RaysCast);
            Assert.Equal(1, tracer.CulledCentre);
            Assert.Equal(8, tracer.CulledCross);
            Assert.Equal(16, tracer.CulledSweep);
        }

        [Fact]
        public void APvsThatAllowsTheLineLeavesItToTheTrace()
        {
            var nav = Mesh(Quad(1, 100, 0, 200, 100), Quad(2, -200, 0, -100, 100));

            var (tracer, visible) = Trace(nav, Rooms(AllVisible));

            Assert.Equal([1], visible[0]);
            Assert.Equal([0], visible[1]);
            Assert.Equal(1, tracer.RaysCast);
            Assert.Equal(0, tracer.CulledCentre + tracer.CulledCross + tracer.CulledSweep);
        }

        /// <summary>
        /// An area under the slab has its eyes in solid. Every line from them is blocked - the trace
        /// says so too - and none of them is traced to find that out.
        /// </summary>
        [Fact]
        public void LinesFromAnEyeInSolidAreNotTraced()
        {
            var nav = Mesh(Quad(1, 100, 0, 200, 100), Quad(2, 600, 0, 700, 100));
            var vis = Rooms(AllVisible);
            var filter = new VisibilityFilter(nav, vis);

            foreach (var a in filter.SightPoints(0))
                foreach (var b in filter.SightPoints(1))
                {
                    Assert.False(vis.IsLineClear(a, b));
                    Assert.False(vis.IsLineClear(b, a));
                }

            var (tracer, visible) = Trace(nav, vis);

            Assert.Empty(visible[0]);
            Assert.Empty(visible[1]);
            Assert.Equal(0, tracer.RaysCast);
            Assert.Equal(25, tracer.CulledCentre + tracer.CulledCross + tracer.CulledSweep);
        }

        // ---- the tracer as it was --------------------------------------------------------------

        private sealed class OldTracer(VisibilityFilter filter, BspVisibility vis, int count) : VisibilityFilter.ICandidateSink
        {
            private readonly List<int>[] forward = Lists(count), backward = Lists(count);

            private static List<int>[] Lists(int n)
            {
                var lists = new List<int>[n];
                for (int i = 0; i < n; i++) lists[i] = [];
                return lists;
            }

            public void Candidates(int areaIndex, ReadOnlySpan<int> others)
            {
                var from = filter.SightPoints(areaIndex);
                foreach (int other in others)
                {
                    Sees(from, filter.SightPoints(other), out bool f, out bool t);
                    if (f) forward[areaIndex].Add(other);
                    if (t) backward[areaIndex].Add(other);
                }
            }

            private void Sees(ReadOnlySpan<BspFile.Vector3> from, ReadOnlySpan<BspFile.Vector3> to,
                out bool fromSeesTo, out bool toSeesFrom)
            {
                const int Corners = VisibilityFilter.SightPointsPerArea - 1;

                if (vis.IsLineClear(from[Corners], to[Corners]))
                {
                    fromSeesTo = true;
                    toSeesFrom = true;
                    return;
                }

                fromSeesTo = false;
                toSeesFrom = false;

                for (int c = 0; c < Corners; c++)
                {
                    if (vis.IsLineClear(from[Corners], to[c]))
                    {
                        fromSeesTo = true;
                        break;
                    }
                }

                for (int c = 0; c < Corners; c++)
                {
                    if (vis.IsLineClear(to[Corners], from[c]))
                    {
                        toSeesFrom = true;
                        break;
                    }
                }

                if (fromSeesTo && toSeesFrom)
                    return;

                for (int s = 0; s < Corners; s++)
                {
                    for (int c = 0; c < Corners; c++)
                    {
                        if (vis.IsLineClear(from[s], to[c]))
                        {
                            fromSeesTo = true;
                            toSeesFrom = true;
                            return;
                        }
                    }
                }
            }

            public int[][] Visible()
            {
                var sets = new List<int>[forward.Length];
                for (int i = 0; i < sets.Length; i++) sets[i] = [];
                for (int i = 0; i < sets.Length; i++)
                {
                    sets[i].AddRange(forward[i]);
                    foreach (int j in backward[i]) sets[j].Add(i);
                }

                var result = new int[sets.Length][];
                for (int i = 0; i < sets.Length; i++)
                {
                    result[i] = sets[i].ToArray();
                    Array.Sort(result[i]);
                }

                return result;
            }
        }

        // ---- random worlds ---------------------------------------------------------------------

        /// <summary>
        /// Areas on the world's terrain heights and around its sites, from slivers to a few hundred
        /// units across, flat or sloping, so that some eyes sit in solid leaves and lines between them
        /// end on every kind of geometry.
        /// </summary>
        private static NavFile RandomAreas(Random rng, FloorColumnTests.World world)
        {
            var nav = new NavFile();
            int count = 12 + rng.Next(20);

            for (int i = 0; i < count; i++)
            {
                var (sx, sy) = world.Sites[rng.Next(world.Sites.Count)];
                float x0 = MathF.Round(sx + (float)(rng.NextDouble() * 400 - 200));
                float y0 = MathF.Round(sy + (float)(rng.NextDouble() * 400 - 200));
                float w = rng.Next(4) == 0 ? 1 + rng.Next(4) : 8 + rng.Next(300);
                float h = rng.Next(4) == 0 ? 1 + rng.Next(4) : 8 + rng.Next(300);
                float z = world.Heights[rng.Next(world.Heights.Count)] + (rng.Next(3) == 0 ? (float)(rng.NextDouble() * 40 - 20) : 0f);

                var area = Quad((uint)(i + 1), x0, y0, x0 + w, y0 + h, z);
                if (rng.Next(3) == 0)
                {
                    area.NeZ = z + (float)(rng.NextDouble() * 60 - 30);
                    area.SeCorner[2] = z + (float)(rng.NextDouble() * 60 - 30);
                }

                nav.Areas.Add(area);
            }

            return nav;
        }

        [Fact]
        public void TheTracerAnswersAsItDidBefore()
        {
            int differing = 0, culled = 0, carried = 0;

            for (int seed = 0; seed < 60; seed++)
            {
                var rng = new Random(seed);
                var world = FloorColumnTests.RandomWorld(rng);
                var nav = RandomAreas(rng, world);
                var filter = new VisibilityFilter(nav, world.Vis);

                var old = new OldTracer(filter, world.Vis, nav.Areas.Count);
                filter.Run(old);
                var expected = old.Visible();

                var (tracer, actual) = Trace(nav, world.Vis);

                for (int i = 0; i < expected.Length; i++)
                {
                    if (!expected[i].AsSpan().SequenceEqual(actual[i]))
                        differing++;
                }

                culled += (int)(tracer.CulledCentre + tracer.CulledCross + tracer.CulledSweep);
                carried += (int)tracer.RaysCast;
            }

            Assert.Equal(0, differing);

            // The test is only worth something if both kinds of skipping happened.
            Assert.True(culled > 100, $"only {culled} lines were ruled out");
            Assert.True(carried > 1000, $"only {carried} lines were traced");
        }

        /// <summary>
        /// <see cref="BspVisibility.IsSightClear"/> gives <see cref="BspVisibility.IsLineClear(BspFile.Vector3, BspFile.Vector3)"/>'s
        /// answer whichever kind of geometry it is told to ask first, says which kind stopped the line,
        /// and leaves the hint alone when nothing did.
        /// </summary>
        [Fact]
        public void TheOrderOfTheSightTestDoesNotChangeItsAnswer()
        {
            var seenBlocker = new HashSet<BspVisibility.SightBlocker>();

            for (int seed = 0; seed < 60; seed++)
            {
                var rng = new Random(1000 + seed);
                var world = FloorColumnTests.RandomWorld(rng);
                var vis = world.Vis;

                for (int n = 0; n < 400; n++)
                {
                    var a = Point(rng, world);
                    var b = rng.Next(10) == 0 ? a : Point(rng, world);
                    bool expected = vis.IsLineClear(a, b);

                    for (var first = BspVisibility.SightBlocker.World; first <= BspVisibility.SightBlocker.Props; first++)
                    {
                        var hint = first;
                        Assert.Equal(expected, vis.IsSightClear(a, b, ref hint));

                        if (expected)
                        {
                            Assert.Equal(first, hint);
                            continue;
                        }

                        seenBlocker.Add(hint);

                        if (hint != BspVisibility.SightBlocker.World)
                            Assert.True(vis.SightBlockedBy(hint, a, b));
                    }
                }
            }

            // Every kind of geometry stopped some line, so every branch was taken.
            Assert.Equal(4, seenBlocker.Count);
        }

        private static BspFile.Vector3 Point(Random rng, FloorColumnTests.World world)
        {
            var (x, y) = world.Column(rng);
            float z = world.Heights[rng.Next(world.Heights.Count)] + (float)(rng.NextDouble() * 120 - 20);
            return V(x, y, z);
        }
    }
}
