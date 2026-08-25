using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// Tests for sweeping a box through the BSP tree.
    ///
    /// This is the test that was missing when a horizontal sweep stopped seeing walls. The tree
    /// descent collapsed whenever the swept segment ran parallel to a splitting plane, which is not a
    /// corner case: every generation sweep is horizontal and every floor plane is horizontal. The
    /// visible result was three passes downstream, as nav areas grown straight through buildings on
    /// rp_downtown_meowy, and nothing between here and there reported anything wrong.
    /// </summary>
    public class HullTraceTests
    {
        /// <summary>
        /// A room split by a wall, built as the smallest tree that reproduces the failure.
        ///
        /// The floor plane at z = 0 is the one that matters. A sweep running along at z = 9 is parallel
        /// to it, and a box 55 tall straddles it, so the descent has to walk into both children with
        /// the whole segment. The wall sits in the child the broken version handed a zero length
        /// segment to.
        /// </summary>
        private static BspVisibility Room()
        {
            const int Solid = 0x1;

            var planes = new[]
            {
                Plane(0, 0, 1, 0),      // 0: the floor, and the split the sweep runs parallel to
                Plane(0, 1, 0, 10),     // 1: wall's near face, as a splitting plane
                Plane(0, 1, 0, 20),     // 2: wall's far face, as a splitting plane
                Plane(0, -1, 0, -10),   // 3: brush side, y >= 10
                Plane(-1, 0, 0, 1000),  // 4: brush side, x >= -1000
                Plane(1, 0, 0, 1000),   // 5: brush side, x <= 1000
                Plane(0, 0, -1, 0),     // 6: brush side, z >= 0
                Plane(0, 0, 1, 100),    // 7: brush side, z <= 100
            };

            // Leaf n is referenced as -(n) - 1.
            var nodes = new[]
            {
                new BspVisibility.Node { PlaneNum = 0, Child0 = 1,  Child1 = -1 }, // above / below the floor
                new BspVisibility.Node { PlaneNum = 1, Child0 = 2,  Child1 = -2 }, // past / before the wall face
                new BspVisibility.Node { PlaneNum = 2, Child0 = -3, Child1 = -4 }, // past the wall / inside it
            };

            var leafs = new[]
            {
                new BspVisibility.Leaf { Contents = 0 },
                new BspVisibility.Leaf { Contents = 0 },
                new BspVisibility.Leaf { Contents = 0 },
                new BspVisibility.Leaf { Contents = Solid },
            };

            var brushes = new[] { new BspFile.Brush { FirstSide = 0, NumSides = 6, Contents = Solid } };

            var sides = new[]
            {
                Side(3), Side(2), Side(4), Side(5), Side(6), Side(7),
            };

            // Only the leaf inside the wall carries it.
            var leafBrushes = new int[]?[] { null, null, null, new[] { 0 } };

            return BspVisibility.FromGeometry(planes, nodes, leafs, brushes, sides, leafBrushes);
        }

        private static BspFile.Plane Plane(float x, float y, float z, float distance)
            => new() { Normal = new BspFile.Vector3(x, y, z), Distance = distance };

        private static BspFile.BrushSide Side(int plane)
            => new() { PlaneNum = (ushort)plane };

        /// <summary>
        /// The regression. A box tall enough to straddle the floor it travels over still has to find a
        /// wall standing on that floor.
        ///
        /// Valve's generation box is 55 units tall and sits on the trace point rather than being
        /// centred on it, so this is the shape every link test in the generator actually sweeps.
        /// </summary>
        [Fact]
        public void ATallBoxStillFindsAWallItSweepsInto()
        {
            var vis = Room();

            bool hit = vis.TryTraceHull(
                new BspFile.Vector3(0, -5, 9),
                new BspFile.Vector3(0, 25, 9),
                BspVisibility.NavTraceMins, BspVisibility.NavTraceMaxs,
                BspVisibility.GenerationMask,
                out float fraction, out _, out bool startSolid);

            Assert.True(hit, "the sweep ran through a solid wall and reported nothing");
            Assert.False(startSolid);
            Assert.InRange(fraction, 0f, 1f);
        }

        /// <summary>
        /// The same wall with a box too short to reach the floor plane, which is the case that kept
        /// working throughout and so proves the geometry above is not simply always blocking.
        /// </summary>
        [Fact]
        public void AShortBoxFindsTheSameWall()
        {
            var vis = Room();

            bool hit = vis.TryTraceHull(
                new BspFile.Vector3(0, -5, 9),
                new BspFile.Vector3(0, 25, 9),
                new BspFile.Vector3(-0.45f, -0.45f, 0f), new BspFile.Vector3(0.45f, 0.45f, 1f),
                BspVisibility.GenerationMask, out _, out _, out _);

            Assert.True(hit);
        }

        /// <summary>Open floor short of the wall stays open, so the fix does not simply block everything.</summary>
        [Fact]
        public void GroundShortOfTheWallStaysClear()
        {
            var vis = Room();

            bool hit = vis.TryTraceHull(
                new BspFile.Vector3(0, -30, 9),
                new BspFile.Vector3(0, 5, 9),
                BspVisibility.NavTraceMins, BspVisibility.NavTraceMaxs,
                BspVisibility.GenerationMask, out _, out _, out _);

            Assert.False(hit);
        }
    }
}
