using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That a surface trace reports the nearest surface, not a farther one a leaf it crossed happened
    /// to list.
    ///
    /// A leaf lists every brush that touches it anywhere. The surface trace clips the whole ray against
    /// each listed brush - for a good reason, documented on the tracer - and used to accept whatever it
    /// got, even a hit lying well past the end of the leaf, then stop. On ctf_2fort the water over the
    /// sewer floor listed a sloped brush the ray only met 24 units under that floor, so the floor finder
    /// went straight through the floor, and a strip of the sewer got no areas at all.
    /// </summary>
    public class LeafBrushReachTests
    {
        private const int Solid = 0x1;

        /// <summary>
        /// A floor slab from z = -10 to z = 0 under open air, and below it a wedge whose top rises along
        /// x: under the floor (z = -24) at x = 0, and poking up through the floor into the air leaf by
        /// x = 100. Because it pokes up, the air leaf lists the wedge - which is all it takes.
        /// </summary>
        private static BspVisibility FloorOverWedge()
        {
            var planes = new[]
            {
                Plane(0, 0, 1, 0),                    // 0: floor top, and the split above/below it
                Plane(0, 0, -1, 10),                  // 1: floor underside, z >= -10
                Plane(-1, 0, 0, 5000),                // 2: x >= -5000
                Plane(1, 0, 0, 5000),                 // 3: x <= 5000
                Plane(0, -1, 0, 5000),                // 4: y >= -5000
                Plane(0, 1, 0, 5000),                 // 5: y <= 5000
                Plane(-0.4472136f, 0, 0.8944272f, -21.466253f), // 6: wedge top, z = -24 + x / 2
                Plane(0, 0, -1, 100),                 // 7: wedge underside, z >= -100
                Plane(-1, 0, 0, 200),                 // 8: wedge, x >= -200
                Plane(1, 0, 0, 200),                  // 9: wedge, x <= 200
            };

            // Leaf n is referenced as -(n) - 1. Air above the floor plane, solid below it: the tree is
            // only what the trace descends, and every brush is found through the leaf lists.
            var nodes = new[] { new BspVisibility.Node { PlaneNum = 0, Child0 = -1, Child1 = -2 } };

            var leafs = new[]
            {
                new BspVisibility.Leaf { Contents = 0 },     // 0: the air, which the wedge pokes into
                new BspVisibility.Leaf { Contents = Solid }, // 1: below the floor plane
            };

            var brushes = new[]
            {
                new BspFile.Brush { FirstSide = 0, NumSides = 6, Contents = Solid },  // the floor
                new BspFile.Brush { FirstSide = 6, NumSides = 6, Contents = Solid },  // the wedge
            };

            var sides = new[]
            {
                Side(0), Side(1), Side(2), Side(3), Side(4), Side(5),
                Side(6), Side(7), Side(8), Side(9), Side(4), Side(5),
            };

            var leafBrushes = new int[]?[] { new[] { 1 }, new[] { 0, 1 } };

            return BspVisibility.FromGeometry(planes, nodes, leafs, brushes, sides, leafBrushes);
        }

        private static BspFile.Plane Plane(float x, float y, float z, float distance)
            => new() { Normal = new BspFile.Vector3(x, y, z), Distance = distance };

        private static BspFile.BrushSide Side(int plane) => new() { PlaneNum = (ushort)plane };

        /// <summary>
        /// The case that was broken: straight down at x = 0 the floor at z = 0 comes first, and the
        /// wedge 24 units under it is not what the trace reports.
        /// </summary>
        [Fact]
        public void TheFloorIsFoundBeforeABrushTheAirAboveItListsBelowIt()
        {
            var world = FloorOverWedge();

            Assert.True(world.TryTraceSurface(new BspFile.Vector3(0, 0, 50), new BspFile.Vector3(0, 0, -200),
                BspVisibility.GroundMask, out var hit, out var normal));
            Assert.Equal(0f, hit.Z, 2);
            Assert.Equal(1f, normal.Z, 3);
        }

        /// <summary>
        /// The control: where the wedge does rise above the floor, inside the air leaf, it is the
        /// surface - so a hit inside the leaf is still taken, with the face the ray came through.
        /// </summary>
        [Fact]
        public void TheWedgeIsStillFoundWhereItRisesIntoTheAir()
        {
            var world = FloorOverWedge();

            Assert.True(world.TryTraceSurface(new BspFile.Vector3(100, 0, 50), new BspFile.Vector3(100, 0, -200),
                BspVisibility.GroundMask, out var hit, out var normal));
            Assert.Equal(26f, hit.Z, 1);
            Assert.Equal(0.894f, normal.Z, 2);
        }
    }
}
