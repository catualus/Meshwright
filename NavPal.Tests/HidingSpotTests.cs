using NavPal;
using Xunit;

namespace NavPal.Tests
{
    /// <summary>
    /// The corner-shelter rule behind hiding spots - Valve's <c>ComputeHidingSpots</c>.
    ///
    /// These exist because the rule was wrong in a way only a comparison against an engine-made mesh
    /// revealed. A generated mesh reported cover in a quarter of its areas against the engine's tenth,
    /// and every individual verdict looked defensible: the old rule asked whether a neighbour reached
    /// the end of an edge within a unit, which is the right *idea* evaluated far too strictly. Nothing
    /// about the mesh, the area count or the spot positions looked wrong - only the total did, and only
    /// against a reference.
    ///
    /// The scoring is pure arithmetic over the connection lists, so it pins without any traced
    /// geometry. The cover classification next to it does need a BSP and is not covered here.
    /// </summary>
    public class HidingSpotCornerTests
    {
        private const int NW = 0, NE = 1, SE = 2, SW = 3;

        private static NavArea Quad(uint id, float x0, float y0, float x1, float y1,
            NavAttributes attributes = NavAttributes.None)
        {
            var area = new NavArea { Id = id, AttributeFlags = (int)attributes };
            area.NwCorner[0] = x0; area.NwCorner[1] = y0;
            area.SeCorner[0] = x1; area.SeCorner[1] = y1;
            return area;
        }

        /// <summary>The subject area, 100 units square, plus whatever neighbours a test adds.</summary>
        private static (NavFile Nav, NavArea Area) Room()
        {
            var nav = new NavFile();
            var area = Quad(1, 0, 0, 100, 100);
            nav.Areas.Add(area);
            return (nav, area);
        }

        /// <summary>Links two areas both ways, which is what stops them reading as a one-way drop.</summary>
        private static void Connect(NavArea from, NavArea to, int direction)
        {
            from.Connections[direction].Add(to.Id);
            to.Connections[NavGeometry.Opposite(direction)].Add(from.Id);
        }

        /// <summary>
        /// A room with nothing connected to it is walled on all four sides, so every corner scores from
        /// both of the edges meeting there. This is the case that falls out of the empty-extent
        /// arithmetic rather than a branch, so it is worth pinning on its own.
        /// </summary>
        [Fact]
        public void AnAreaWithNoNeighboursIsShelteredAtEveryCorner()
        {
            var (nav, area) = Room();

            Assert.Equal([2, 2, 2, 2], HidingSpotFinder.CornerScores(nav, area));
        }

        /// <summary>
        /// A neighbour spanning a whole wall opens it, and the two corners on that wall stop being
        /// corners. The other two are untouched.
        /// </summary>
        [Fact]
        public void AFullWidthNeighbourOpensTheCornersOnItsWall()
        {
            var (nav, area) = Room();
            var north = Quad(2, 0, -100, 100, 0);
            nav.Areas.Add(north);
            Connect(area, north, NavGeometry.North);

            var scores = HidingSpotFinder.CornerScores(nav, area);

            Assert.Equal(1, scores[NW]);
            Assert.Equal(1, scores[NE]);
            Assert.Equal(2, scores[SE]);
            Assert.Equal(2, scores[SW]);
        }

        /// <summary>
        /// A doorway in the middle of a wall leaves both of that wall's corners sheltered - which is the
        /// point of measuring the neighbours' extent rather than merely noting one exists.
        /// </summary>
        [Fact]
        public void ADoorwayInTheMiddleLeavesBothCornersSheltered()
        {
            var (nav, area) = Room();
            var doorway = Quad(2, 40, -100, 60, 0);
            nav.Areas.Add(doorway);
            Connect(area, doorway, NavGeometry.North);

            Assert.Equal([2, 2, 2, 2], HidingSpotFinder.CornerScores(nav, area));
        }

        /// <summary>
        /// The regression that motivated the rewrite.
        ///
        /// A neighbour stopping ten units short of a corner is a tiling seam, not a wall, and Valve
        /// wants a full twenty before it counts. The previous rule used a one-unit tolerance, so a gap
        /// like this scored as cover - and on a generated mesh, whose areas are clipped back to
        /// geometry and so rarely line up exactly, gaps like this are everywhere. That single tolerance
        /// is most of the difference between 872 spots and the engine's 267.
        /// </summary>
        [Fact]
        public void AGapNarrowerThanTheCornerSizeIsNotCover()
        {
            var (nav, area) = Room();
            var north = Quad(2, 10, -100, 100, 0);   // stops 10 units short of the north-west corner
            nav.Areas.Add(north);
            Connect(area, north, NavGeometry.North);

            var scores = HidingSpotFinder.CornerScores(nav, area);

            Assert.Equal(1, scores[NW]);
            Assert.Equal(1, scores[NE]);
        }

        /// <summary>And a gap wider than it still is, so the threshold is a threshold and not a floor.</summary>
        [Fact]
        public void AGapWiderThanTheCornerSizeIsCover()
        {
            var (nav, area) = Room();
            var north = Quad(2, 25, -100, 100, 0);   // 25 units short: past the 20 unit corner size
            nav.Areas.Add(north);
            Connect(area, north, NavGeometry.North);

            var scores = HidingSpotFinder.CornerScores(nav, area);

            Assert.Equal(2, scores[NW]);
            Assert.Equal(1, scores[NE]);
        }

        /// <summary>
        /// A one-way link is a drop, not a doorway. Valve skips these with its own note that the
        /// discontinuity may itself be the cover - a ledge overlooking a room does not stop the ledge's
        /// corners being corners.
        /// </summary>
        [Fact]
        public void AOneWayConnectionDoesNotOpenAWall()
        {
            var (nav, area) = Room();
            var below = Quad(2, 0, -100, 100, 0);
            nav.Areas.Add(below);

            // outgoing only: the drop is traversable one way, so `below` never links back
            area.Connections[NavGeometry.North].Add(below.Id);

            Assert.Equal([2, 2, 2, 2], HidingSpotFinder.CornerScores(nav, area));
        }

        /// <summary>
        /// A jump area stands in for steep ground rather than a route, so it does not count as an
        /// opening even when it is linked both ways.
        /// </summary>
        [Fact]
        public void AJumpAreaNeighbourDoesNotOpenAWall()
        {
            var (nav, area) = Room();
            var steep = Quad(2, 0, -100, 100, 0, NavAttributes.Jump);
            nav.Areas.Add(steep);
            Connect(area, steep, NavGeometry.North);

            Assert.Equal([2, 2, 2, 2], HidingSpotFinder.CornerScores(nav, area));
        }

        /// <summary>
        /// Two walls meeting at one corner is what cover means. A single wall with both other sides open
        /// scores one and is somewhere you are merely standing against a wall, in the open.
        /// </summary>
        [Fact]
        public void ASingleWallIsNotCover()
        {
            var (nav, area) = Room();

            var north = Quad(2, 0, -100, 100, 0);
            var south = Quad(3, 0, 100, 100, 200);
            var east = Quad(4, 100, 0, 200, 100);

            nav.Areas.Add(north);
            nav.Areas.Add(south);
            nav.Areas.Add(east);

            Connect(area, north, NavGeometry.North);
            Connect(area, south, NavGeometry.South);
            Connect(area, east, NavGeometry.East);

            // Only the west wall is solid, so the two western corners score one apiece and nothing
            // reaches two.
            var scores = HidingSpotFinder.CornerScores(nav, area);

            Assert.Equal(1, scores[NW]);
            Assert.Equal(1, scores[SW]);
            Assert.Equal(0, scores[NE]);
            Assert.Equal(0, scores[SE]);
        }

        /// <summary>
        /// A connection naming an area that no longer exists must not crash or silently count as an
        /// opening. Clipping deletes slivers and the passes that follow have to tolerate the gap.
        /// </summary>
        [Fact]
        public void ADanglingConnectionIsIgnored()
        {
            var (nav, area) = Room();
            area.Connections[NavGeometry.North].Add(999);

            Assert.Equal([2, 2, 2, 2], HidingSpotFinder.CornerScores(nav, area));
        }
    }

    /// <summary>
    /// Where a spot lands inside its area.
    ///
    /// The plain inset covers almost every case; the fallbacks exist for areas narrower than two insets,
    /// which a mesh acquires as soon as anything clips areas back to real geometry. Placing a spot
    /// outside its own area is not a crash - it is a bot walking to cover that is through a wall.
    /// </summary>
    public class HidingSpotPositionTests
    {
        private const int NW = 0, NE = 1, SE = 2, SW = 3;

        private static NavArea Quad(float x0, float y0, float x1, float y1)
        {
            var area = new NavArea { Id = 1 };
            area.NwCorner[0] = x0; area.NwCorner[1] = y0;
            area.SeCorner[0] = x1; area.SeCorner[1] = y1;
            return area;
        }

        [Fact]
        public void EachCornerInsetsTowardTheMiddle()
        {
            var area = Quad(0, 0, 100, 100);

            Assert.Equal((12.5f, 12.5f), HidingSpotFinder.SpotPosition(area, NW));
            Assert.Equal((87.5f, 12.5f), HidingSpotFinder.SpotPosition(area, NE));
            Assert.Equal((87.5f, 87.5f), HidingSpotFinder.SpotPosition(area, SE));
            Assert.Equal((12.5f, 87.5f), HidingSpotFinder.SpotPosition(area, SW));
        }

        /// <summary>
        /// An area narrower than two insets: the plain inset overshoots the far side, so the fallback
        /// relaxes that axis to the area's own half-width and the spot lands on the centre line.
        /// </summary>
        [Fact]
        public void ANarrowAreaFallsBackToItsOwnHalfWidth()
        {
            var area = Quad(0, 0, 10, 100);
            var (x, y) = HidingSpotFinder.SpotPosition(area, NW);

            Assert.Equal(5f, x, 3);
            Assert.Equal(12.5f, y, 3);
            Assert.True(NavGeometry.Contains(area, x, y));
        }

        /// <summary>Every corner of every awkward shape still lands on the area it belongs to.</summary>
        [Theory]
        [InlineData(0f, 0f, 10f, 10f)]      // smaller than one inset in both axes
        [InlineData(0f, 0f, 4f, 400f)]      // a clipped sliver
        [InlineData(0f, 0f, 25f, 25f)]      // exactly one sampling step
        [InlineData(-50f, -50f, 50f, 50f)]  // straddling the origin
        public void SpotsAlwaysLandOnTheirOwnArea(float x0, float y0, float x1, float y1)
        {
            var area = Quad(x0, y0, x1, y1);

            for (int corner = 0; corner < 4; corner++)
            {
                var (x, y) = HidingSpotFinder.SpotPosition(area, corner);

                Assert.True(NavGeometry.Contains(area, x, y),
                    $"corner {corner} of ({x0} {y0})-({x1} {y1}) placed at ({x} {y}), outside the area");
            }
        }
    }
}
