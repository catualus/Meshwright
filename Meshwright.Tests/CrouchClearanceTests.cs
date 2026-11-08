using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That the sampler tells ground a player can only crouch on from ground with no room at all.
    ///
    /// Crouch-only ground is judged on a second test that standing ground skips: four floor probes a few
    /// units out on each side, which have to find the same floor the sample stands on. Those probes used
    /// to start a standing player's height up, which under a crouch-height ceiling is inside the ceiling,
    /// so they found the ceiling instead of the floor and every crouch-only sample was thrown away.
    /// </summary>
    public class CrouchClearanceTests
    {
        private const int Solid = 0x1;

        private static readonly BspFile.Vector3 Mins = new(0, 0, -500);
        private static readonly BspFile.Vector3 Maxs = new(1000, 1000, 500);

        /// <summary>
        /// Flat ground at z = 0 under a slab 20 units thick whose underside is at
        /// <paramref name="ceiling"/>, both reaching well past the sampled bounds.
        /// </summary>
        private static BspVisibility UnderSlab(float ceiling) => Slab(ceiling, ceiling + 20f);

        /// <summary>Flat ground at z = 0 under a slab from <paramref name="bottom"/> to <paramref name="top"/>.</summary>
        private static BspVisibility Slab(float bottom, float top)
        {
            var planes = new[]
            {
                Plane(0, 0, 1, 0),                 // 0: ground top, and the split above/below it
                Plane(0, 0, 1, bottom),            // 1: slab underside, as a splitting plane
                Plane(0, 0, 1, top),               // 2: slab top, as a splitting plane and a side
                Plane(0, 0, -1, 500),              // 3: ground underside, z >= -500
                Plane(-1, 0, 0, 5000),             // 4: x >= -5000
                Plane(1, 0, 0, 5000),              // 5: x <= 5000
                Plane(0, -1, 0, 5000),             // 6: y >= -5000
                Plane(0, 1, 0, 5000),              // 7: y <= 5000
                Plane(0, 0, -1, -bottom),          // 8: slab underside as a side, z >= bottom
            };

            // Leaf n is referenced as -(n) - 1.
            var nodes = new[]
            {
                new BspVisibility.Node { PlaneNum = 0, Child0 = 1, Child1 = -1 },  // above / in the ground
                new BspVisibility.Node { PlaneNum = 1, Child0 = 2, Child1 = -2 },  // at or above the slab / the gap
                new BspVisibility.Node { PlaneNum = 2, Child0 = -3, Child1 = -4 }, // above the slab / in it
            };

            var leafs = new[]
            {
                new BspVisibility.Leaf { Contents = Solid }, // 0: the ground
                new BspVisibility.Leaf { Contents = 0 },     // 1: the gap between ground and slab
                new BspVisibility.Leaf { Contents = 0 },     // 2: open air above the slab
                new BspVisibility.Leaf { Contents = Solid }, // 3: the slab
            };

            var brushes = new[]
            {
                new BspFile.Brush { FirstSide = 0, NumSides = 6, Contents = Solid },
                new BspFile.Brush { FirstSide = 6, NumSides = 6, Contents = Solid },
            };

            var sides = new[]
            {
                Side(0), Side(3), Side(4), Side(5), Side(6), Side(7),
                Side(2), Side(8), Side(4), Side(5), Side(6), Side(7),
            };

            var leafBrushes = new int[]?[] { new[] { 0 }, null, null, new[] { 1 } };

            return BspVisibility.FromGeometry(planes, nodes, leafs, brushes, sides, leafBrushes);
        }

        private static BspFile.Plane Plane(float x, float y, float z, float distance)
            => new() { Normal = new BspFile.Vector3(x, y, z), Distance = distance };

        private static BspFile.BrushSide Side(int plane) => new() { PlaneNum = (ushort)plane };

        private static AreaGenerator.Clearance At(float ceiling)
            => AreaGenerator.SampleClearance(UnderSlab(ceiling), Mins, Maxs, 10, 10, 0f)[0];

        /// <summary>
        /// The case that was broken: a ceiling 64 units up is too low to stand under and high enough to
        /// crouch under, and the floor beside the sample is the same flat floor.
        /// </summary>
        [Fact]
        public void LevelFloorUnderALowCeilingIsCrouchGround()
        {
            Assert.Equal(AreaGenerator.Clearance.Crouch, At(64f));
        }

        /// <summary>Under a ceiling just above crouching height, the same.</summary>
        [Fact]
        public void CrouchRoomJustAboveTheCrouchHullIsCrouchGround()
        {
            Assert.Equal(AreaGenerator.Clearance.Crouch, At(NavConstants.HumanCrouchHeight + 2f));
        }

        /// <summary>
        /// Crouch-only ground does not lead up through its own ceiling.
        ///
        /// The slab is four units thick with its top 64 units over the ground: in reach of a crouch
        /// jump, with open sky above it, so the climb passes every test that looks only at the column
        /// being climbed into, and the sweep across runs above the slab. The flood stepped up onto a
        /// roof from the attic under it this way once crouch-only ground started being accepted.
        /// </summary>
        [Fact]
        public void ACrouchFloorDoesNotClimbThroughItsOwnCeiling()
        {
            var world = Slab(60f, 64f);

            Assert.Equal(AreaGenerator.Clearance.Crouch, AreaGenerator.SampleClearance(world, Mins, Maxs, 10, 10, 0f)[0]);
            Assert.DoesNotContain(AreaGenerator.SampleNeighbours(world, Mins, Maxs, 10, 10, 0f), c => c.Z > 32f);
        }

        /// <summary>The controls either side: no room even to crouch, and room to stand.</summary>
        [Fact]
        public void TooLowForACrouchIsNoGroundAndHighEnoughIsStanding()
        {
            Assert.Equal(AreaGenerator.Clearance.None, At(40f));
            Assert.Equal(AreaGenerator.Clearance.Stand, At(100f));
        }
    }
}
