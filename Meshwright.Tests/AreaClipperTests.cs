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
    }
}
