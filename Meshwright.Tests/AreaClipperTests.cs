using System.Collections.Generic;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That pulling an area's edges back to the geometry leaves an area behind.
    ///
    /// The clipper had no tests, and the case it got wrong is the commonest one it sees: a strip one
    /// node wide, 25 units across, standing against a wall. Each edge was probed from a full sampling
    /// step inside it, which on a strip that narrow is the opposite edge - and once the first edge had
    /// been pulled back to the wall, a point past it, inside the wall. That probe read no room at all
    /// and moved its edge to the far side of the other one. The area came out inside out and was
    /// thrown away as a sliver, so the walkable ground along a wall was lost wherever a node sat
    /// within a step of it.
    ///
    /// Every case runs in both axes and against both sides, because the four edges are four separate
    /// copies of the same code and the bug was in all of them.
    /// </summary>
    public class AreaClipperTests
    {
        private const int Solid = 0x1;

        private const float Step = AreaGenerator.StepSize;

        /// <summary>How far the strip runs along the wall. Long enough to be a strip rather than a square.</summary>
        private const float Length = 100f;

        /// <summary>
        /// Solid ground under z = 0, with a wall standing on it to either side of a strip, or both.
        ///
        /// Built from leaf contents alone: the clipper only asks whether a point is solid and where a
        /// segment first enters solid, and a leaf answers both exactly at its splitting plane.
        /// </summary>
        /// <param name="acrossX">Whether the walls face east and west rather than north and south.</param>
        /// <param name="low">Where the wall on the low side (west or north) ends; null for no wall.</param>
        /// <param name="high">Where the wall on the high side (east or south) begins; null for no wall.</param>
        private static BspVisibility World(bool acrossX, float? low, float? high)
        {
            var planes = new List<BspFile.Plane> { Plane(0, 0, 1, 0) };
            var leafs = new List<BspVisibility.Leaf>
            {
                new() { Contents = Solid },     // 0: the ground
                new() { Contents = 0 },         // 1: open air
            };

            // Node 0 is the root and splits off the ground. What sits above it is only known once the
            // walls are built, so it is filled in last.
            var nodes = new List<BspVisibility.Node> { default };

            // Leaf n is referenced as -(n) - 1.
            static int Leaf(int n) => -n - 1;

            int above = Leaf(1);

            if (low is float lowFace)
            {
                planes.Add(Across(acrossX, lowFace));
                leafs.Add(new BspVisibility.Leaf { Contents = Solid });
                nodes.Add(new BspVisibility.Node
                {
                    PlaneNum = planes.Count - 1, Child0 = above, Child1 = Leaf(leafs.Count - 1),
                });
                above = nodes.Count - 1;
            }

            if (high is float highFace)
            {
                planes.Add(Across(acrossX, highFace));
                leafs.Add(new BspVisibility.Leaf { Contents = Solid });
                nodes.Add(new BspVisibility.Node
                {
                    PlaneNum = planes.Count - 1, Child0 = Leaf(leafs.Count - 1), Child1 = above,
                });
                above = nodes.Count - 1;
            }

            nodes[0] = new BspVisibility.Node { PlaneNum = 0, Child0 = above, Child1 = Leaf(0) };

            return BspVisibility.FromGeometry([.. planes], [.. nodes], [.. leafs], [], [],
                new int[]?[leafs.Count]);
        }

        private static BspFile.Plane Plane(float x, float y, float z, float distance)
            => new() { Normal = new BspFile.Vector3(x, y, z), Distance = distance };

        private static BspFile.Plane Across(bool acrossX, float at)
            => acrossX ? Plane(1, 0, 0, at) : Plane(0, 1, 0, at);

        /// <summary>
        /// One flat area on the ground, <paramref name="width"/> across the walls and
        /// <see cref="Length"/> along them.
        /// </summary>
        private static NavFile Strip(bool acrossX, float width = Step)
        {
            float maxX = acrossX ? width : Length;
            float maxY = acrossX ? Length : width;

            var area = new NavArea { Id = 1 };
            area.SeCorner[0] = maxX;
            area.SeCorner[1] = maxY;

            var nav = new NavFile();
            nav.Areas.Add(area);
            return nav;
        }

        /// <summary>The area's extent across the walls, then along them.</summary>
        private static (float Min, float Max, float AlongMin, float AlongMax) Extent(NavArea area, bool acrossX)
        {
            var b = NavGeometry.GetBounds(area);
            return acrossX ? (b.MinX, b.MaxX, b.MinY, b.MaxY) : (b.MinY, b.MaxY, b.MinX, b.MaxX);
        }

        /// <summary>
        /// The corners are still the right way round: the north-west one at the minimum in both axes.
        /// The bounds normalise an inverted pair, so they cannot show this on their own.
        /// </summary>
        private static void AssertNotInsideOut(NavArea area)
        {
            Assert.True(area.NwCorner[0] < area.SeCorner[0], "west edge is east of the east edge");
            Assert.True(area.NwCorner[1] < area.SeCorner[1], "north edge is south of the south edge");
        }

        /// <summary>
        /// The regression. A one-node strip against a wall keeps the ground between its open edge and
        /// the wall face, on whichever side the wall is.
        ///
        /// The face sits 8 units past the node, so the strip overhangs it by 4.5 - the reported case.
        /// The far edge is open floor and must not move.
        /// </summary>
        [Theory]
        [InlineData(true, true)]      // wall to the east
        [InlineData(true, false)]     // wall to the west
        [InlineData(false, true)]     // wall to the south
        [InlineData(false, false)]    // wall to the north
        public void AStripAgainstOneWallIsClippedToItAndKept(bool acrossX, bool wallOnHighSide)
        {
            const float Overhang = 4.5f;

            float face = wallOnHighSide ? Step - Overhang : Overhang;
            var vis = wallOnHighSide ? World(acrossX, null, face) : World(acrossX, face, null);
            var nav = Strip(acrossX);

            var result = AreaClipper.Clip(nav, vis, Step);

            Assert.Equal(0, result.Discarded);
            var area = Assert.Single(nav.Areas);
            AssertNotInsideOut(area);

            var (min, max, alongMin, alongMax) = Extent(area, acrossX);

            Assert.Equal(wallOnHighSide ? 0f : face, min, 0.1f);
            Assert.Equal(wallOnHighSide ? face : Step, max, 0.1f);

            Assert.Equal(0f, alongMin, 0.1f);
            Assert.Equal(Length, alongMax, 0.1f);
        }

        /// <summary>
        /// The same strip in a corridor with a wall on each side, overhanging both. Each edge comes back
        /// to its own wall and the floor between them survives.
        ///
        /// This is the case clamping the probe to the opposite edge would still get wrong: the opposite
        /// edge is inside the other wall, so a probe starting there reads no room and collapses the
        /// area onto it.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AStripBetweenTwoWallsIsClippedToBoth(bool acrossX)
        {
            const float Low = 3f, High = 21f;

            var nav = Strip(acrossX);

            var result = AreaClipper.Clip(nav, World(acrossX, Low, High), Step);

            Assert.Equal(0, result.Discarded);
            var area = Assert.Single(nav.Areas);
            AssertNotInsideOut(area);

            var (min, max, _, _) = Extent(area, acrossX);

            Assert.Equal(Low, min, 0.1f);
            Assert.Equal(High, max, 0.1f);
        }

        /// <summary>The control: a strip on open ground has nothing to be clipped to and stays as it is.</summary>
        [Fact]
        public void AStripOnOpenGroundIsLeftAlone()
        {
            var nav = Strip(acrossX: true);

            var result = AreaClipper.Clip(nav, World(acrossX: true, null, null), Step);

            Assert.Equal(0, result.Clipped);
            var (min, max, _, _) = Extent(Assert.Single(nav.Areas), acrossX: true);

            Assert.Equal(0f, min);
            Assert.Equal(Step, max);
        }

        /// <summary>
        /// The other control. Probing a narrow area from its centre must not let a strip that is wholly
        /// inside a wall survive: it still shrinks to a sliver and is discarded.
        /// </summary>
        [Fact]
        public void AStripBuriedInAWallIsStillDiscarded()
        {
            var nav = Strip(acrossX: true);

            var result = AreaClipper.Clip(nav, World(acrossX: true, null, -50f), Step);

            Assert.Equal(1, result.Discarded);
            Assert.Empty(nav.Areas);
        }

        /// <summary>
        /// A wide area that has run more than a step into a wall still walks back out of it over
        /// several passes, which is how areas wider than two steps were always clipped and still are.
        /// </summary>
        [Fact]
        public void AWideAreaStillWalksBackOutOfAWallStepByStep()
        {
            const float Face = 60f;

            var nav = Strip(acrossX: true, width: 4 * Step);

            AreaClipper.Clip(nav, World(acrossX: true, null, Face), Step);

            var area = Assert.Single(nav.Areas);
            AssertNotInsideOut(area);

            var (min, max, _, _) = Extent(area, acrossX: true);

            Assert.Equal(0f, min);
            Assert.Equal(Face, max, 0.1f);
        }

        /// <summary>
        /// A floor under z = 0 that ends at a west-facing ledge: solid east of <paramref name="ledge"/>,
        /// open air everywhere else. Where <paramref name="notchFrom"/> is given, the ledge steps back
        /// to <paramref name="notchLedge"/> south of that line, so the floor's west edge is not straight.
        /// </summary>
        private static BspVisibility Ledge(float ledge, float? notchFrom = null, float notchLedge = 0f)
        {
            var planes = new List<BspFile.Plane> { Plane(0, 0, 1, 0) };
            var leafs = new List<BspVisibility.Leaf>
            {
                new() { Contents = 0 },         // 0: open air
                new() { Contents = Solid },     // 1: floor
            };

            // Leaf n is referenced as -(n) - 1.
            const int Air = -1, Floor = -2;

            var nodes = new List<BspVisibility.Node> { default };

            int Split(float x)
            {
                planes.Add(Plane(1, 0, 0, x));
                nodes.Add(new BspVisibility.Node { PlaneNum = planes.Count - 1, Child0 = Floor, Child1 = Air });
                return nodes.Count - 1;
            }

            int below = Split(ledge);

            if (notchFrom is float y)
            {
                int north = below, south = Split(notchLedge);
                planes.Add(Plane(0, 1, 0, y));
                nodes.Add(new BspVisibility.Node { PlaneNum = planes.Count - 1, Child0 = south, Child1 = north });
                below = nodes.Count - 1;
            }

            nodes[0] = new BspVisibility.Node { PlaneNum = 0, Child0 = Air, Child1 = below };

            return BspVisibility.FromGeometry([.. planes], [.. nodes], [.. leafs], [], [],
                new int[]?[leafs.Count]);
        }

        private static NavFile Square(float minX, float minY, float maxX, float maxY, float z = 0f)
        {
            var area = new NavArea { Id = 1 };
            area.NwCorner[0] = minX;
            area.NwCorner[1] = minY;
            area.NwCorner[2] = z;
            area.SeCorner[0] = maxX;
            area.SeCorner[1] = maxY;
            area.SeCorner[2] = z;
            area.NeZ = z;
            area.SwZ = z;

            var nav = new NavFile();
            nav.Areas.Add(area);
            return nav;
        }

        /// <summary>
        /// An area overhanging a ledge on its west side loses the overhang there, and nowhere else.
        ///
        /// The south edge's probe line nearest the west side starts over the drop. Its floor reading
        /// used to pull the whole south edge back a step, over floor that carries on, because the
        /// south edge is clipped before the west one and the floor readings are combined with their
        /// minimum. The second line in is over the drop too, so this covers a line that is not the
        /// outermost as well.
        /// </summary>
        [Fact]
        public void AnOverhangOnOneSideDoesNotPullTheEdgesBesideItBack()
        {
            const float Drop = 2f;

            var nav = Square(-15f, 0f, 35f, 100f);

            AreaClipper.Clip(nav, Ledge(Drop), Step);

            var b = NavGeometry.GetBounds(Assert.Single(nav.Areas));

            Assert.InRange(b.MinX, Drop, Drop + Step / 2f);
            Assert.Equal(35f, b.MaxX);
            Assert.Equal(0f, b.MinY);
            Assert.Equal(100f, b.MaxY);
        }

        /// <summary>
        /// The same, where the line nearest the west side does have floor where it starts and runs off
        /// the ledge only further on, because the ledge steps back towards the corner. The west edge's
        /// own lines take the overhang away and the south edge stays where it is.
        /// </summary>
        [Fact]
        public void ALedgeSteppingBackAtACornerDoesNotPullTheEdgeBesideItBack()
        {
            var nav = Square(0f, 0f, 100f, 100f);

            AreaClipper.Clip(nav, Ledge(8f, notchFrom: 80f, notchLedge: 12f), Step);

            var b = NavGeometry.GetBounds(Assert.Single(nav.Areas));

            Assert.InRange(b.MinX, 12f, 12f + Step / 2f);
            Assert.Equal(100f, b.MaxX);
            Assert.Equal(0f, b.MinY);
            Assert.Equal(100f, b.MaxY);
        }

        /// <summary>
        /// The control for both: an area hanging over a drop by more than a step on every line of an
        /// edge is still walked back out of it, step by step, once every line has its say.
        /// </summary>
        [Fact]
        public void AnAreaOverhangingALedgeByMoreThanAStepStillWalksBack()
        {
            const float Drop = 60f;

            var nav = Square(0f, 0f, 150f, 100f);

            AreaClipper.Clip(nav, Ledge(Drop), Step);

            var b = NavGeometry.GetBounds(Assert.Single(nav.Areas));

            Assert.InRange(b.MinX, Drop, Drop + Step / 2f);
            Assert.Equal(150f, b.MaxX);
            Assert.Equal(0f, b.MinY);
            Assert.Equal(100f, b.MaxY);
        }

        /// <summary>
        /// Terrain two units above an area's surface is floor under it.
        ///
        /// The floor search walks down from a step above the surface in four-unit segments, and a
        /// displacement triangle only counts as hit strictly inside one, so terrain exactly two units
        /// up sits where one segment ends and the next begins and was missed by both. Every probe
        /// line read no floor, and the area was pulled back on all four sides until it was discarded.
        /// </summary>
        [Fact]
        public void TerrainOnASegmentBoundaryIsStillFloor()
        {
            const float Terrain = 2f;

            // Nothing solid but a slab far below, and flat terrain over all of it.
            var planes = new[] { Plane(0, 0, 1, -1000f) };
            var nodes = new[] { new BspVisibility.Node { PlaneNum = 0, Child0 = -1, Child1 = -2 } };
            var leafs = new[] { new BspVisibility.Leaf { Contents = 0 }, new BspVisibility.Leaf { Contents = Solid } };
            var vis = BspVisibility.FromGeometry(planes, nodes, leafs, [], [], new int[]?[leafs.Length]);

            BspFile.Vector3 V(float x, float y) => new(x, y, Terrain);
            vis.AttachDisplacements(BspDisplacements.FromTriangles(
                [V(-500, -500), V(500, -500), V(500, 500), V(-500, -500), V(500, 500), V(-500, 500)],
                [Solid, Solid]));

            var nav = Square(0f, 0f, 100f, 100f);

            var result = AreaClipper.Clip(nav, vis, Step);

            Assert.Equal(0, result.Clipped);
            var b = NavGeometry.GetBounds(Assert.Single(nav.Areas));
            Assert.Equal((0f, 0f, 100f, 100f), (b.MinX, b.MinY, b.MaxX, b.MaxY));
        }

        /// <summary>
        /// Solid ground under z = 0 with open air above, cut through where <paramref name="slotFrom"/> is
        /// given by a bottomless slot running east-west between it and <paramref name="slotTo"/>.
        /// </summary>
        private static BspVisibility SlottedGround(float? slotFrom = null, float slotTo = 0f)
        {
            var planes = new List<BspFile.Plane> { Plane(0, 0, 1, 0) };
            var leafs = new List<BspVisibility.Leaf>
            {
                new() { Contents = 0 },         // 0: open air
                new() { Contents = Solid },     // 1: the ground
            };

            // Leaf n is referenced as -(n) - 1.
            const int Air = -1, Ground = -2;

            var nodes = new List<BspVisibility.Node> { default };
            int below = Ground;

            if (slotFrom is float from)
            {
                planes.Add(Plane(0, 1, 0, from));
                nodes.Add(new BspVisibility.Node { PlaneNum = planes.Count - 1, Child0 = Air, Child1 = Ground });
                int slot = nodes.Count - 1;

                planes.Add(Plane(0, 1, 0, slotTo));
                nodes.Add(new BspVisibility.Node { PlaneNum = planes.Count - 1, Child0 = Ground, Child1 = slot });
                below = nodes.Count - 1;
            }

            nodes[0] = new BspVisibility.Node { PlaneNum = 0, Child0 = Air, Child1 = below };

            return BspVisibility.FromGeometry([.. planes], [.. nodes], [.. leafs], [], [],
                new int[]?[leafs.Count]);
        }

        /// <summary>
        /// A floor under z = 0 that is the convex corner of a platform: solid only where x is past
        /// <paramref name="x"/> and y past <paramref name="y"/>, open air everywhere else.
        /// </summary>
        private static BspVisibility PlatformCorner(float x, float y)
        {
            var planes = new[] { Plane(0, 0, 1, 0), Plane(1, 0, 0, x), Plane(0, 1, 0, y) };

            // Leaf n is referenced as -(n) - 1.
            const int Air = -1, Floor = -2;

            var nodes = new[]
            {
                new BspVisibility.Node { PlaneNum = 0, Child0 = Air, Child1 = 1 },
                new BspVisibility.Node { PlaneNum = 1, Child0 = 2, Child1 = Air },
                new BspVisibility.Node { PlaneNum = 2, Child0 = Floor, Child1 = Air },
            };

            var leafs = new[] { new BspVisibility.Leaf { Contents = 0 }, new BspVisibility.Leaf { Contents = Solid } };

            return BspVisibility.FromGeometry(planes, nodes, leafs, [], [], new int[]?[leafs.Length]);
        }

        /// <summary>The id of the area under test in <see cref="Route"/>; the two below it are its neighbours.</summary>
        private const uint Between = 3;

        /// <summary>
        /// An area that is the only link between one to its north and one to its south, the way
        /// <c>Spare</c> finds a route: connected on opposite sides to two areas not connected to each
        /// other. The neighbours are given lower ids, so with <see cref="Between"/> as the first
        /// generated id the clip leaves them exactly as they are, whatever the floor under them.
        /// </summary>
        private static NavFile Route(float minX, float minY, float maxX, float maxY)
        {
            var north = TestWorlds.Area(1, minX - 20f, minY - 50f, maxX + 20f, minY, 0f);
            var south = TestWorlds.Area(2, minX - 20f, maxY, maxX + 20f, maxY + 50f, 0f);
            var area = TestWorlds.Area(Between, minX, minY, maxX, maxY, 0f);

            area.Connections[NavGeometry.North].Add(1);
            area.Connections[NavGeometry.South].Add(2);
            north.Connections[NavGeometry.South].Add(Between);
            south.Connections[NavGeometry.North].Add(Between);

            var nav = new NavFile();
            nav.Areas.AddRange([north, south, area]);
            return nav;
        }

        private static NavGeometry.Bounds BoundsOf(NavFile nav, uint id)
            => NavGeometry.GetBounds(Assert.Single(nav.Areas, a => a.Id == id));

        /// <summary>
        /// The control for the next two: a strip eight units wide that is the only way between two
        /// areas, on floor all the way across, survives the discard as it is. That is what sparing
        /// exists for.
        /// </summary>
        [Fact]
        public void ANarrowStripThatIsTheOnlyRouteIsSpared()
        {
            var nav = Route(0f, 0f, 8f, 100f);

            var result = AreaClipper.Clip(nav, SlottedGround(), Step, firstGeneratedId: Between);

            Assert.Equal(0, result.Discarded);
            Assert.Equal(0, result.Trimmed);
            var b = BoundsOf(nav, Between);
            Assert.Equal((0f, 0f, 8f, 100f), (b.MinX, b.MinY, b.MaxX, b.MaxY));
        }

        /// <summary>
        /// The same strip running a little way off a ledge along its west side is kept, and pulled
        /// back to the ledge first.
        ///
        /// The clip itself cannot move that edge: the strip is so narrow that its probe starts on the
        /// centre line, and an edge never comes closer to its probe's start than
        /// <see cref="AreaClipper"/>'s four-unit minimum overhang. The nine points the spare test
        /// samples sit at the quarters, all on floor, so the strip used to be kept with its west side
        /// over nothing - which is how strips and squares a few units wide, kept as routes, came to
        /// float on the benchmark maps.
        /// </summary>
        [Fact]
        public void ASparedStripIsPulledBackOffTheDropItOverhangs()
        {
            const float Lip = 0.7f;

            var nav = Route(0f, 0f, 8f, 100f);

            var result = AreaClipper.Clip(nav, Ledge(Lip), Step, firstGeneratedId: Between);

            Assert.Equal(0, result.Discarded);
            Assert.Equal(1, result.Trimmed);
            Assert.Contains(Between, result.Reshaped);

            var b = BoundsOf(nav, Between);
            Assert.InRange(b.MinX, Lip, Lip + 1f);
            Assert.Equal((8f, 0f, 100f), (b.MaxX, b.MinY, b.MaxY));
        }

        /// <summary>
        /// A strip crossing a gap narrower than a step is still kept whole.
        ///
        /// The slot crosses the strip a tenth of the way along, on one of the lines the clip probes for
        /// the strip's long edges, so that line has no floor from either edge to the middle. That is not
        /// an overhang pulling either edge back would fix, and a body walks across it, so the line is
        /// left out of the trim rather than costing the route.
        /// </summary>
        [Fact]
        public void ASparedStripAcrossANarrowGapIsKeptWhole()
        {
            var nav = Route(0f, 0f, 8f, 100f);

            var result = AreaClipper.Clip(nav, SlottedGround(8f, 12f), Step, firstGeneratedId: Between);

            Assert.Equal(0, result.Discarded);
            Assert.Equal(0, result.Trimmed);
            var b = BoundsOf(nav, Between);
            Assert.Equal((0f, 0f, 8f, 100f), (b.MinX, b.MinY, b.MaxX, b.MaxY));
        }

        /// <summary>
        /// A crack six units wide under an area pulls none of its edges back.
        ///
        /// The crack runs east-west from y = 85 to 91. The east and west edges each have a probe line
        /// at y = 90 running along it, with no floor anywhere on it, and the south edge's lines all
        /// cross it, with no floor at the second of their four points. Read as a ledge, the first
        /// pulled both side edges back to four units from their probes' start, and the second the
        /// south edge back to six units from its. A body walks straight across it.
        /// </summary>
        [Fact]
        public void ACrackUnderTheProbeLinesDoesNotPullAnEdgeBack()
        {
            var nav = Square(0f, 0f, 100f, 100f);

            var result = AreaClipper.Clip(nav, SlottedGround(85f, 91f), Step);

            Assert.Equal(0, result.Clipped);
            var b = NavGeometry.GetBounds(Assert.Single(nav.Areas));
            Assert.Equal((0f, 0f, 100f, 100f), (b.MinX, b.MinY, b.MaxX, b.MaxY));
        }

        /// <summary>
        /// The control: where the gap is wider than a crack - forty units, from y = 80 to 120 - the
        /// floor resuming beyond it does not make it one. The south edge still comes back to the
        /// ledge at y = 80, and the east and west edges, whose lines at y = 90 run over the gap, are
        /// left alone once it has.
        /// </summary>
        [Fact]
        public void AGapWiderThanACrackIsStillALedge()
        {
            var nav = Square(0f, 0f, 100f, 100f);

            AreaClipper.Clip(nav, SlottedGround(80f, 120f), Step);

            var b = NavGeometry.GetBounds(Assert.Single(nav.Areas));
            Assert.InRange(b.MaxY, 75f, 80f);
            Assert.Equal((0f, 0f, 100f), (b.MinX, b.MinY, b.MaxX));
        }

        /// <summary>
        /// The crack test on its own: a point in a crack narrower than half a body is over one, along
        /// it or across it; a point past a ledge, or in a gap wider than that, is not, however close
        /// the floor behind it is.
        /// </summary>
        [Theory]
        [InlineData(88f, 50f, 85f, 91f, true)]      // in a six-unit crack
        [InlineData(85.5f, 50f, 85f, 86f, true)]    // in a one-unit seam
        [InlineData(81f, 50f, 80f, 120f, false)]    // a unit into a forty-unit gap
        [InlineData(90.5f, 50f, 80f, 97f, false)]     // in the middle of a gap just wider than a crack
        public void OnlyAGapNarrowerThanHalfABodyIsACrack(float y, float x, float from, float to, bool crack)
        {
            var nav = Square(0f, 0f, 100f, 100f);
            var vis = SlottedGround(from, to);

            Assert.Equal(crack, AreaClipper.OverCrack(vis, nav.Areas[0], x, y, NavConstants.StepHeight));
        }

        /// <summary>
        /// Solid ground under z = 0 with open air above, alternately floor and gap across y: floor south
        /// of the first boundary, a gap to the next, floor to the one after, and so on.
        /// </summary>
        private static BspVisibility Strips(params float[] boundaries)
        {
            var planes = new List<BspFile.Plane> { Plane(0, 0, 1, 0) };
            var leafs = new List<BspVisibility.Leaf>
            {
                new() { Contents = 0 },         // 0: open air
                new() { Contents = Solid },     // 1: the ground
            };

            // Leaf n is referenced as -(n) - 1.
            const int Air = -1, Ground = -2;

            var nodes = new List<BspVisibility.Node> { default };

            // Built from the north end: each node splits off the strip south of its boundary.
            int north = boundaries.Length % 2 == 0 ? Ground : Air;

            for (int i = boundaries.Length - 1; i >= 0; i--)
            {
                planes.Add(Plane(0, 1, 0, boundaries[i]));
                nodes.Add(new BspVisibility.Node
                {
                    PlaneNum = planes.Count - 1, Child0 = north, Child1 = i % 2 == 0 ? Ground : Air,
                });
                north = nodes.Count - 1;
            }

            nodes[0] = new BspVisibility.Node { PlaneNum = 0, Child0 = Air, Child1 = north };

            return BspVisibility.FromGeometry([.. planes], [.. nodes], [.. leafs], [], [],
                new int[]?[leafs.Count]);
        }

        /// <summary>
        /// A gap is measured, not only bounded, so a trench fifteen units across is a crack asked from
        /// near its side or from its middle, and one seventeen across is not from either.
        /// </summary>
        [Theory]
        [InlineData(81f, 95f, true)]
        [InlineData(87.5f, 95f, true)]
        [InlineData(81f, 97f, false)]
        [InlineData(88.5f, 97f, false)]
        public void AGapIsMeasuredWhereverItIsAskedFrom(float y, float to, bool crack)
        {
            var nav = Square(0f, 0f, 100f, 100f);

            Assert.Equal(crack, AreaClipper.OverCrack(SlottedGround(80f, to), nav.Areas[0], 50f, y,
                NavConstants.StepHeight));
        }

        /// <summary>
        /// A rail a unit and a half wide down a trench thirty across is not the far side of a crack: the
        /// floor beyond a crack has to carry on. A beam three units wide does, from either side of it.
        /// </summary>
        [Theory]
        [InlineData(85f, 90f, false)]       // the rail, from the gap south of it
        [InlineData(85f, 91.5f, true)]      // the beam, from the south
        [InlineData(94.5f, 91.5f, true)]    // the beam, from the north
        public void ARailDownATrenchIsNotTheFarSideOfACrack(float y, float railTo, bool crack)
        {
            var nav = Square(0f, 0f, 100f, 100f);

            Assert.Equal(crack, AreaClipper.OverCrack(Strips(80f, 88.5f, railTo, 100f), nav.Areas[0], 50f, y,
                NavConstants.StepHeight));
        }

        /// <summary>The same, off a ledge: floor behind the point and none in front of it is not a crack.</summary>
        [Fact]
        public void APointPastALedgeIsNotOverACrack()
        {
            var nav = Square(-20f, 0f, 80f, 100f);

            Assert.False(AreaClipper.OverCrack(Ledge(0f), nav.Areas[0], -1f, 50f, NavConstants.StepHeight));
        }

        /// <summary>
        /// A one-node square on the corner of a platform, with a drop on two sides, which the clip
        /// leaves too small to keep is clipped again from where it started, the way every area was
        /// clipped before the patient clip.
        ///
        /// Patiently, it would stop at the platform's corner as a square about fourteen units across
        /// with floor under all of it, and as the only route between its two neighbours it would be
        /// spared. Clipped the old way, from its original shape, the edges facing away from the drop
        /// are pulled back by lines running over it, it ends up mostly over the drop, and it is
        /// discarded - so this fails if the fallback is dropped, or if it reruns the clip on the
        /// patient result instead of on the area as it was.
        /// </summary>
        [Fact]
        public void ASquareThePatientClipLeavesTooSmallIsClippedTheOldWay()
        {
            var nav = Route(0f, 0f, Step, Step);

            var result = AreaClipper.Clip(nav, PlatformCorner(10f, 10f), Step, firstGeneratedId: Between);

            Assert.Equal(1, result.Discarded);
            Assert.DoesNotContain(nav.Areas, a => a.Id == Between);
        }
    }
}
