using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That a shared edge a body can only cross away from its middle is linked, and that one a body
    /// cannot cross anywhere along is not.
    ///
    /// Off the middle, crossings were only tried with the whole body over the shared span, a body's
    /// half-width in from each end. A span narrower than a body then had no position to try but the
    /// middle, so a ledge a crouching body fits on only at its lip, half of it over the drop, was
    /// meshed and never linked to anything: gm_flatgrass has a ring of them under the rim of its pit.
    ///
    /// Exercised through <see cref="ConnectionBuilder.Explain"/>, which runs the real tests in the real
    /// order and says which one refused.
    /// </summary>
    public class OffCentreCrossingTests
    {
        private const int Solid = 0x1;

        /// <summary>
        /// Two crouch areas in a row along a ledge 25 units deep, against a wall at x = 300 and under a
        /// ceiling at 64, open on the other side. A body crossing between them in the middle of the
        /// ledge has its far side in the wall; one at the open edge, hanging past it, has room.
        /// </summary>
        [Fact]
        public void ALedgeABodyFitsOnlyAtItsLipIsLinked()
        {
            var world = CrouchClearanceTests.SlabInSlot(-1000f, 300f);
            var first = Ledge(1, 0f);
            var second = Ledge(2, 100f);

            var log = ConnectionBuilder.Explain(world, first, second);

            Assert.Contains(log, line => line.StartsWith("south:") && line.Contains("would connect further along"));
        }

        /// <summary>
        /// The control: a slot 28 units wide between two walls under the same ceiling, with the two
        /// areas filling it. A body is 32 wide and fits nowhere along the shared edge, overhanging an end
        /// or not, so there is still nothing to link.
        /// </summary>
        [Fact]
        public void ASlotTooNarrowForABodyIsStillNotLinked()
        {
            var world = CrouchClearanceTests.SlabInSlot(272f, 300f);
            var first = Crouch(TestWorlds.Area(1, 272f, 0f, 300f, 100f, 0f));
            var second = Crouch(TestWorlds.Area(2, 272f, 100f, 300f, 200f, 0f));

            var log = ConnectionBuilder.Explain(world, first, second);

            Assert.DoesNotContain(log, line => line.Contains("would connect"));
            Assert.Contains(log, line => line.StartsWith("south:") && line.Contains("everywhere along the span"));
        }

        /// <summary>
        /// A post two units thick at the middle of a shared edge 24 units long. Every position along the
        /// edge puts a body's side within 16 units of it, so no body gets past; but the clearance lines
        /// are 16 units apart, and with the body's centre at either end of the span all three miss
        /// the post. Only the body swept across the overhanging positions refuses them.
        /// </summary>
        [Fact]
        public void APostBetweenTheClearanceLinesStillBlocksABodyOverhangingTheSpan()
        {
            var first = TestWorlds.Area(1, 0f, 0f, 24f, 100f, 0f);
            var second = TestWorlds.Area(2, 0f, 100f, 24f, 200f, 0f);

            var log = ConnectionBuilder.Explain(PostOnGround(), first, second);

            Assert.DoesNotContain(log, line => line.Contains("would connect"));
            Assert.Contains(log, line => line.StartsWith("south:") && line.Contains("everywhere along the span"));
        }

        /// <summary>A crouch area 25 units deep against the wall at x = 300, 100 long from y.</summary>
        private static NavArea Ledge(uint id, float y) => Crouch(TestWorlds.Area(id, 275f, y, 300f, y + 100f, 0f));

        private static NavArea Crouch(NavArea area)
        {
            area.AttributeFlags |= (int)NavAttributes.Crouch;
            return area;
        }

        /// <summary>
        /// Open air over solid ground at z = 0, with a post from the ground to z = 200 at x 11..13,
        /// y 95..105 - astride the edge at y = 100 the areas above share.
        /// </summary>
        private static BspVisibility PostOnGround()
        {
            var planes = new[]
            {
                Plane(0, 0, 1, 0),        // 0: ground top, and the split above/below it
                Plane(0, 0, -1, 500),     // 1: z >= -500
                Plane(-1, 0, 0, 5000),    // 2: x >= -5000
                Plane(1, 0, 0, 5000),     // 3: x <= 5000
                Plane(0, -1, 0, 5000),    // 4: y >= -5000
                Plane(0, 1, 0, 5000),     // 5: y <= 5000
                Plane(1, 0, 0, 11),       // 6: the post's west face, as a split
                Plane(1, 0, 0, 13),       // 7: its east face, as a split and a side
                Plane(0, 1, 0, 95),       // 8: its north face, as a split
                Plane(0, 1, 0, 105),      // 9: its south face, as a split and a side
                Plane(0, 0, 1, 200),      // 10: its top, as a split and a side
                Plane(-1, 0, 0, -11),     // 11: the west face as a side, x >= 11
                Plane(0, -1, 0, -95),     // 12: the north face as a side, y >= 95
                Plane(0, 0, -1, 0),       // 13: its foot as a side, z >= 0
            };

            // Leaf n is referenced as -(n) - 1.
            var nodes = new[]
            {
                new BspVisibility.Node { PlaneNum = 0, Child0 = 1, Child1 = -2 },  // above / in the ground
                new BspVisibility.Node { PlaneNum = 6, Child0 = 2, Child1 = -1 },  // east of x = 11 / west
                new BspVisibility.Node { PlaneNum = 7, Child0 = -1, Child1 = 3 },  // east of the post / in its x
                new BspVisibility.Node { PlaneNum = 8, Child0 = 4, Child1 = -1 },  // south of y = 95 / north
                new BspVisibility.Node { PlaneNum = 9, Child0 = -1, Child1 = 5 },  // south of the post / in its y
                new BspVisibility.Node { PlaneNum = 10, Child0 = -1, Child1 = -3 }, // above the post / in it
            };

            var leafs = new[]
            {
                new BspVisibility.Leaf { Contents = 0 },     // 0: open air
                new BspVisibility.Leaf { Contents = Solid }, // 1: the ground
                new BspVisibility.Leaf { Contents = Solid }, // 2: the post
            };

            var brushes = new[]
            {
                new BspFile.Brush { FirstSide = 0, NumSides = 6, Contents = Solid },
                new BspFile.Brush { FirstSide = 6, NumSides = 6, Contents = Solid },
            };

            var sides = new[]
            {
                Side(0), Side(1), Side(2), Side(3), Side(4), Side(5),
                Side(7), Side(11), Side(9), Side(12), Side(10), Side(13),
            };

            var leafBrushes = new int[]?[] { null, new[] { 0 }, new[] { 1 } };

            return BspVisibility.FromGeometry(planes, nodes, leafs, brushes, sides, leafBrushes);
        }

        private static BspFile.Plane Plane(float x, float y, float z, float distance)
            => new() { Normal = new BspFile.Vector3(x, y, z), Distance = distance };

        private static BspFile.BrushSide Side(int plane) => new() { PlaneNum = (ushort)plane };
    }
}
