using System.Collections.Generic;
using System.Linq;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That two areas a body crosses between only at the very end of their shared span - centred on the
    /// end, half of it past it over the floor of whatever lies alongside both - are linked, and that
    /// without floor there, or with a wall in the way, they are not.
    ///
    /// Every other crossing is tried a unit or more in from the span's ends, and at a corner that unit
    /// is the difference: two strips the clip leaves against a wall, each narrower than a body, are
    /// crossed by a body standing at their open side, and a unit further along its far side is in the
    /// wall.
    ///
    /// The same goes for a drop or a climb at a corner, as long as a body stands at the foot of it beside
    /// the corner and at the top.
    /// </summary>
    public class CornerCrossingTests
    {
        private const int Solid = 0x1;

        /// <summary>
        /// Two strips 16 units wide in a row along a wall at x = 16.5, open floor on their other side. A
        /// body fits across their shared edge only centred on its open end, x = 0, half of it over the
        /// floor beyond; anywhere further along, its far side is in the wall.
        /// </summary>
        [Fact]
        public void TwoStripsAgainstAWallAreLinkedAtTheirOpenCorner()
        {
            var world = StartSolidTests.Boxes((new(16.5f, -400, 0), new(400, 400, 200)));

            var log = ConnectionBuilder.Explain(world, Strip(1, 0f), Strip(2, 100f));

            Assert.Contains(log, line => line.StartsWith("south:") && line.Contains("would connect further along"));
        }

        /// <summary>
        /// The control: the same strips at the edge of a ledge that stops two units past them, with a
        /// fall of hundreds of units beyond. A body centred on the corner has its outer half over the
        /// drop, which is neither area's floor nor anything else's, so nothing is linked.
        /// </summary>
        [Fact]
        public void StripsAtTheEdgeOfADropAreNotLinkedAtTheirCorner()
        {
            var world = Brushes(
                (new(-2, -400, -500), new(400, 400, 0)),     // the ledge, stopping at x = -2
                (new(16.5f, -400, 0), new(400, 400, 200)));  // the wall along the strips

            var log = ConnectionBuilder.Explain(world, Strip(1, 0f), Strip(2, 100f));

            Assert.DoesNotContain(log, line => line.Contains("would connect"));
            Assert.Contains(log, line => line.StartsWith("south:") && line.Contains("everywhere along the span"));
        }

        /// <summary>
        /// The other control: open floor past the corner again, but a panel standing on it across the
        /// line the strips meet on, from x = -20 to -2. A body centred on the corner runs into it.
        /// </summary>
        [Fact]
        public void APanelPastTheCornerStillBlocksIt()
        {
            var world = StartSolidTests.Boxes(
                (new(16.5f, -400, 0), new(400, 400, 200)),
                (new(-20, 99, 0), new(-2, 101, 200)));

            var log = ConnectionBuilder.Explain(world, Strip(1, 0f), Strip(2, 100f));

            Assert.DoesNotContain(log, line => line.Contains("would connect"));
        }

        /// <summary>
        /// A link the clip pulled apart, over open floor, with a post between the two areas from x = 16.5
        /// to 40. The areas no longer face each other - 50 units apart - so the pass looks along the span
        /// they shared before, x 0..40. A crouching body stands at both ends of the crossings along it and
        /// meets the post on every one, which used to remove the link; centred on the span's open end,
        /// x = 0, it passes the post with half a unit to spare, so the link stays.
        /// </summary>
        [Fact]
        public void AStaleLinkABodyCrossesAtTheSpansEndIsKept()
        {
            var world = StartSolidTests.Boxes((new(16.5f, 120, 0), new(40, 130, 200)));
            var nav = Linked(Area(1, 0, 0, 40, 100), Area(2, 0, 150, 40, 250));
            var before = new Dictionary<uint, NavGeometry.Bounds> { [2] = new(0, 100, 40, 250) };

            var dropped = ConnectionBuilder.DropStale(nav, world, before.Keys.ToHashSet(), before);

            Assert.Equal(2, dropped.Refused);
            Assert.Equal(0, dropped.Removed);
            Assert.True(Linked(nav, 1, 2));
            Assert.True(Linked(nav, 2, 1));
        }

        /// <summary>The control: the post runs on to x = -20, past the corner, and both links go.</summary>
        [Fact]
        public void AStaleLinkAWallCrossesTheWholeSpanOfIsStillRemoved()
        {
            var world = StartSolidTests.Boxes((new(-20, 120, 0), new(40, 130, 200)));
            var nav = Linked(Area(1, 0, 0, 40, 100), Area(2, 0, 150, 40, 250));
            var before = new Dictionary<uint, NavGeometry.Bounds> { [2] = new(0, 100, 40, 250) };

            var dropped = ConnectionBuilder.DropStale(nav, world, before.Keys.ToHashSet(), before);

            Assert.Equal(2, dropped.Removed);
            Assert.False(Linked(nav, 1, 2));
            Assert.False(Linked(nav, 2, 1));
        }

        /// <summary>
        /// Two strips 10 units wide in a slot between two walls, linked across their shared edge, which ran
        /// 10 units into the right-hand wall before the clip. No body stands anywhere along it, which leaves
        /// an ordinary link alone; a link the build made only at an end of the span, because a body stood
        /// at the corner there before the clip, is removed.
        /// </summary>
        [Fact]
        public void ALinkMadeAtASpanEndGoesWhenTheClipLeavesNoBodyStandingThere()
        {
            var world = StartSolidTests.Boxes(
                (new(-400, -400, 0), new(-0.5f, 400, 200)),
                (new(10.5f, -400, 0), new(400, 400, 200)));
            var before = new Dictionary<uint, NavGeometry.Bounds> { [1] = new(0, 0, 20, 100), [2] = new(0, 100, 20, 200) };

            var kept = Linked(Area(1, 0, 0, 10, 100), Area(2, 0, 100, 10, 200));
            var left = ConnectionBuilder.DropStale(kept, world, before.Keys.ToHashSet(), before);

            Assert.Equal(2, left.Refused);
            Assert.Equal(0, left.Removed);

            var nav = Linked(Area(1, 0, 0, 10, 100), Area(2, 0, 100, 10, 200));
            var atEnds = new HashSet<(uint, int, uint)> { (1, NavGeometry.South, 2), (2, NavGeometry.North, 1) };
            var dropped = ConnectionBuilder.DropStale(nav, world, before.Keys.ToHashSet(), before, atEnds);

            Assert.Equal(2, dropped.Removed);
            Assert.Equal(2, dropped.RemovedAtEnds);
            Assert.False(Linked(nav, 1, 2));
            Assert.False(Linked(nav, 2, 1));
        }

        /// <summary>
        /// The two strips along the wall again, the first now on top of a ledge 40 units high that ends
        /// where the second begins, at y = 100, with the ledge running on past their open side. A body
        /// steps off the ledge only centred on the corner, x = 0, half of it over the ledge beyond, and
        /// lands far enough out to clear the ledge's face; anywhere further along, its far side is in the
        /// wall.
        /// </summary>
        [Fact]
        public void ADropAtTheCornerIsLinked()
        {
            var world = DropWorld();

            var log = ConnectionBuilder.Explain(world, Strip(1, 0f, 40f), Strip(2, 100f));

            Assert.Contains(log, line => line.StartsWith("south:") &&
                                         line.Contains("would connect further along") && line.EndsWith("at an end of the span"));
        }

        /// <summary>
        /// The control: a block 30 units high stands on the floor at the foot of the ledge beside the
        /// corner, from x = -30 to -2. The body stepping off the corner has nowhere to land beside it - its
        /// outer half comes down on the block - so nothing is linked, though the fall's middle column and
        /// one of its side columns are clear.
        /// </summary>
        [Fact]
        public void ADropAtTheCornerWithNoRoomAtTheFootIsNotLinked()
        {
            var world = DropWorld((new(-30, 100, 0), new(-2, 400, 30)));

            var log = ConnectionBuilder.Explain(world, Strip(1, 0f, 40f), Strip(2, 100f));

            Assert.DoesNotContain(log, line => line.Contains("would connect"));
        }

        /// <summary>
        /// The other way round: the second strip on top of a ledge 40 units high that starts at y = 100 and
        /// runs on past their open side. A body jumps up onto it only centred on the corner, from far
        /// enough back to clear the ledge's face.
        /// </summary>
        [Fact]
        public void AClimbAtTheCornerIsLinked()
        {
            var world = ClimbWorld();

            var log = ConnectionBuilder.Explain(world, Strip(1, 0f), Strip(2, 100f, 40f));

            Assert.Contains(log, line => line.StartsWith("south:") &&
                                         line.Contains("would connect further along") && line.EndsWith("at an end of the span"));
        }

        /// <summary>
        /// The control: a rail along the top of the ledge beside the corner, from x = -7 to -2, with floor
        /// under the body's overhanging half on either side of it. A body centred on the corner at the top
        /// stands in the rail, so nothing is linked.
        /// </summary>
        [Fact]
        public void AClimbAtTheCornerWithNoRoomAtTheTopIsNotLinked()
        {
            var world = ClimbWorld((new(-7, 100, 40), new(-2, 400, 120)));

            var log = ConnectionBuilder.Explain(world, Strip(1, 0f), Strip(2, 100f, 40f));

            Assert.DoesNotContain(log, line => line.Contains("would connect"));
        }

        /// <summary>
        /// The drop and the climb back that the build makes at the corner are recorded as made at an end of
        /// the span, and go when the clip pulls both strips back four units from their open side, leaving
        /// no body standing at either end of any crossing between them. Without the record, both stay.
        /// </summary>
        [Fact]
        public void ADropMadeAtACornerGoesWhenTheClipLeavesNoRoomThere()
        {
            var world = DropWorld();
            var built = new NavFile();
            built.Areas.Add(Strip(1, 0f, 40f));
            built.Areas.Add(Strip(2, 100f));

            var made = ConnectionBuilder.Build(built, world);

            Assert.Equal(1, made.Drops);
            Assert.Equal(1, made.JumpsUp);
            Assert.Contains((1u, NavGeometry.South, 2u), made.AtSpanEnds);
            Assert.Contains((2u, NavGeometry.North, 1u), made.AtSpanEnds);

            var before = new Dictionary<uint, NavGeometry.Bounds> { [1] = new(0, 0, 16, 100), [2] = new(0, 100, 16, 200) };

            var kept = Linked(TestWorlds.Area(1, 4, 0, 16, 100, 40), Area(2, 4, 100, 16, 200));
            var left = ConnectionBuilder.DropStale(kept, world, before.Keys.ToHashSet(), before);

            Assert.Equal(2, left.Refused);
            Assert.Equal(0, left.Removed);

            var nav = Linked(TestWorlds.Area(1, 4, 0, 16, 100, 40), Area(2, 4, 100, 16, 200));
            var dropped = ConnectionBuilder.DropStale(nav, world, before.Keys.ToHashSet(), before, made.AtSpanEnds);

            Assert.Equal(2, dropped.Removed);
            Assert.Equal(2, dropped.RemovedAtEnds);
            Assert.False(Linked(nav, 1, 2));
            Assert.False(Linked(nav, 2, 1));
        }

        /// <summary>
        /// The wall at x = 16.5 and a ledge 40 units high on the near side of y = 100, running on past the
        /// strips' open side, with any further boxes.
        /// </summary>
        private static BspVisibility DropWorld(params (BspFile.Vector3 Mins, BspFile.Vector3 Maxs)[] more)
            => StartSolidTests.Boxes([
                (new(16.5f, -400, 0), new(400, 400, 200)),
                (new(-400, -400, 0), new(16.5f, 100, 40)),
                .. more]);

        /// <summary>The wall at x = 16.5 and a ledge 40 units high on the far side of y = 100, with any further boxes.</summary>
        private static BspVisibility ClimbWorld(params (BspFile.Vector3 Mins, BspFile.Vector3 Maxs)[] more)
            => StartSolidTests.Boxes([
                (new(16.5f, -400, 0), new(400, 400, 200)),
                (new(-400, 100, 0), new(16.5f, 400, 40)),
                .. more]);

        /// <summary>A strip 16 units wide and 100 long, along the wall at x = 16.5, from y, at height z.</summary>
        private static NavArea Strip(uint id, float y, float z = 0f) => TestWorlds.Area(id, 0f, y, 16f, y + 100f, z);

        private static NavArea Area(uint id, float minX, float minY, float maxX, float maxY)
            => TestWorlds.Area(id, minX, minY, maxX, maxY, 0f);

        /// <summary>The two areas, linked both ways across the first's south edge.</summary>
        private static NavFile Linked(NavArea north, NavArea south)
        {
            north.Connections[NavGeometry.South].Add(south.Id);
            south.Connections[NavGeometry.North].Add(north.Id);

            var nav = new NavFile();
            nav.Areas.Add(north);
            nav.Areas.Add(south);
            return nav;
        }

        private static bool Linked(NavFile nav, uint from, uint to) =>
            nav.Areas.Single(a => a.Id == from).Connections.Any(list => list.Contains(to));

        /// <summary>
        /// Open air with solid boxes in it, each given by its corners, and nothing else - no ground but
        /// what the boxes make. Both leaves of the one split are air and list every box, which is all a
        /// trace needs to find them.
        /// </summary>
        private static BspVisibility Brushes(params (BspFile.Vector3 Mins, BspFile.Vector3 Maxs)[] boxes)
        {
            var planes = new List<BspFile.Plane> { TestWorlds.Plane(0, 0, 1, 0) };
            var brushes = new List<BspFile.Brush>();

            foreach (var (mins, maxs) in boxes)
            {
                brushes.Add(new BspFile.Brush { FirstSide = planes.Count, NumSides = 6, Contents = Solid });
                planes.Add(TestWorlds.Plane(1, 0, 0, maxs.X));
                planes.Add(TestWorlds.Plane(-1, 0, 0, -mins.X));
                planes.Add(TestWorlds.Plane(0, 1, 0, maxs.Y));
                planes.Add(TestWorlds.Plane(0, -1, 0, -mins.Y));
                planes.Add(TestWorlds.Plane(0, 0, 1, maxs.Z));
                planes.Add(TestWorlds.Plane(0, 0, -1, -mins.Z));
            }

            var sides = new BspFile.BrushSide[planes.Count];
            for (int i = 0; i < sides.Length; i++)
                sides[i] = new BspFile.BrushSide { PlaneNum = (ushort)i };

            var nodes = new[] { new BspVisibility.Node { PlaneNum = 0, Child0 = -1, Child1 = -2 } };
            var leafs = new[] { new BspVisibility.Leaf { Contents = 0 }, new BspVisibility.Leaf { Contents = 0 } };
            var all = Enumerable.Range(0, boxes.Length).ToArray();

            return BspVisibility.FromGeometry(planes.ToArray(), nodes, leafs, brushes.ToArray(), sides, [all, all]);
        }
    }
}
