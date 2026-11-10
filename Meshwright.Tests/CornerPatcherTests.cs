using System.Collections.Generic;
using System.Linq;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That a corner patch is only built over floor.
    ///
    /// Two areas touching only at a corner get a small patch area dropped into the gap beside the
    /// corner, wired to both. The patch is flat, at the height of the corner, and nothing used to ask
    /// whether there was any ground under it: on koth_viaduct the corner of a raised platform touched
    /// the corner of an area 35 units below it, and both patches built between them hung off the
    /// platform's edge over the drop.
    /// </summary>
    public class CornerPatcherTests
    {
        private const int Solid = 0x1;

        /// <summary>
        /// Two areas meeting only at the point (50, 50): one to the north-west of it, one to the
        /// south-east, with nothing in either of the two quadrants beside the corner.
        /// </summary>
        private static NavFile CornerToCorner()
        {
            var nav = new NavFile();
            nav.Areas.Add(TestWorlds.Area(1, 0f, 0f, 50f, 50f, 0f));
            nav.Areas.Add(TestWorlds.Area(2, 50f, 50f, 100f, 100f, 0f));
            return nav;
        }

        /// <summary>
        /// Floor under z = 0 only where the two areas are - north-west and south-east of (50, 50) - and
        /// open air all the way down in the other two quadrants.
        /// </summary>
        private static BspVisibility Checkerboard()
        {
            static BspFile.Plane Plane(float x, float y, float z, float d) => TestWorlds.Plane(x, y, z, d);

            // Leaf n is referenced as -(n) - 1.
            const int Air = -1, Floor = -2;

            var planes = new List<BspFile.Plane>
            {
                Plane(0, 0, 1, 0),      // 0: the floor's top face
                Plane(1, 0, 0, 50),     // 1: x = 50
                Plane(0, 1, 0, 50),     // 2: y = 50
            };

            var nodes = new List<BspVisibility.Node>
            {
                new() { PlaneNum = 0, Child0 = Air, Child1 = 1 },
                new() { PlaneNum = 1, Child0 = 2, Child1 = 3 },
                new() { PlaneNum = 2, Child0 = Floor, Child1 = Air },   // east: floor to the south only
                new() { PlaneNum = 2, Child0 = Air, Child1 = Floor },   // west: floor to the north only
            };

            var leafs = new List<BspVisibility.Leaf>
            {
                new() { Contents = 0 },
                new() { Contents = Solid },
            };

            return BspVisibility.FromGeometry([.. planes], [.. nodes], [.. leafs], [], [],
                new int[]?[leafs.Count]);
        }

        /// <summary>The control: with floor all round the corner, the touch is patched.</summary>
        [Fact]
        public void ACornerOnlyTouchOverFloorIsPatched()
        {
            var nav = CornerToCorner();

            var result = CornerPatcher.Patch(nav, TestWorlds.Ground());

            Assert.True(result.PatchesAdded > 0);
            Assert.True(nav.Areas.Count > 2);
            Assert.Contains(nav.Areas, a => a.Id > 2 && a.Connections.Any(l => l.Contains(1u)));
        }

        /// <summary>
        /// The same two areas with a drop in both quadrants beside the corner get no patch: either one
        /// would be an area standing on nothing.
        /// </summary>
        [Fact]
        public void NoPatchIsBuiltOverADrop()
        {
            var nav = CornerToCorner();

            var result = CornerPatcher.Patch(nav, Checkerboard());

            Assert.Equal(0, result.PatchesAdded);
            Assert.Equal(2, nav.Areas.Count);
            Assert.All(nav.Areas, a => Assert.All(a.Connections, Assert.Empty));
        }
    }
}
