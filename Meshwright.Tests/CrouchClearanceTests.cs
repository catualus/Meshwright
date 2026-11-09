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
    /// A crouch-only sample also has to have room for a crouching body, not just a clear line up.
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

        /// <summary>
        /// Flat ground at z = 0 under a crouch-height slab from 64 to 84, as <see cref="Slab"/>, in a
        /// slot between two solid walls: everything from x = <paramref name="right"/> on, and
        /// everything up to x = <paramref name="left"/>, floor to well over the slab.
        /// </summary>
        internal static BspVisibility SlabInSlot(float left, float right)
        {
            var planes = new[]
            {
                Plane(0, 0, 1, 0),                 // 0: ground top, and the split above/below it
                Plane(1, 0, 0, right),             // 1: the right wall's face, as a splitting plane
                Plane(1, 0, 0, left),              // 2: the left wall's face, as a split and a side
                Plane(0, 0, 1, 64),                // 3: slab underside, as a splitting plane
                Plane(0, 0, 1, 84),                // 4: slab top, as a splitting plane and a side
                Plane(0, 0, -1, 500),              // 5: z >= -500
                Plane(-1, 0, 0, 5000),             // 6: x >= -5000
                Plane(1, 0, 0, 5000),              // 7: x <= 5000
                Plane(0, -1, 0, 5000),             // 8: y >= -5000
                Plane(0, 1, 0, 5000),              // 9: y <= 5000
                Plane(0, 0, -1, -64),              // 10: slab underside as a side, z >= 64
                Plane(-1, 0, 0, -right),           // 11: the right wall's face as a side, x >= right
                Plane(0, 0, -1, 0),                // 12: a wall's foot, z >= 0
                Plane(0, 0, 1, 500),               // 13: a wall's top, z <= 500
            };

            // Leaf n is referenced as -(n) - 1.
            var nodes = new[]
            {
                new BspVisibility.Node { PlaneNum = 0, Child0 = 1, Child1 = -1 },  // above / in the ground
                new BspVisibility.Node { PlaneNum = 1, Child0 = -2, Child1 = 2 },  // in the right wall / short of it
                new BspVisibility.Node { PlaneNum = 2, Child0 = 3, Child1 = -3 },  // in the slot / in the left wall
                new BspVisibility.Node { PlaneNum = 3, Child0 = 4, Child1 = -4 },  // at or above the slab / the gap
                new BspVisibility.Node { PlaneNum = 4, Child0 = -5, Child1 = -6 }, // above the slab / in it
            };

            var leafs = new[]
            {
                new BspVisibility.Leaf { Contents = Solid }, // 0: the ground
                new BspVisibility.Leaf { Contents = Solid }, // 1: the right wall
                new BspVisibility.Leaf { Contents = Solid }, // 2: the left wall
                new BspVisibility.Leaf { Contents = 0 },     // 3: the gap between ground and slab
                new BspVisibility.Leaf { Contents = 0 },     // 4: open air above the slab
                new BspVisibility.Leaf { Contents = Solid }, // 5: the slab
            };

            var brushes = new[]
            {
                new BspFile.Brush { FirstSide = 0, NumSides = 6, Contents = Solid },
                new BspFile.Brush { FirstSide = 6, NumSides = 6, Contents = Solid },
                new BspFile.Brush { FirstSide = 12, NumSides = 6, Contents = Solid },
                new BspFile.Brush { FirstSide = 18, NumSides = 6, Contents = Solid },
            };

            var sides = new[]
            {
                Side(0), Side(5), Side(6), Side(7), Side(8), Side(9),
                Side(4), Side(10), Side(6), Side(7), Side(8), Side(9),
                Side(11), Side(7), Side(8), Side(9), Side(12), Side(13),
                Side(2), Side(6), Side(8), Side(9), Side(12), Side(13),
            };

            var leafBrushes = new int[]?[] { new[] { 0 }, new[] { 2 }, new[] { 3 }, null, null, new[] { 1 } };

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
        /// Two floors of one column a few units apart get their own answers, whichever is asked first.
        ///
        /// The answers are cached per column and height, and the height used to be rounded to the
        /// eight units the flood's visited set uses - so the floor asked about second got the first
        /// one's answer. Here the top of a thin slab, with open sky over it, and the ground two units
        /// under the slab, with no room at all, round to the same eight units.
        /// </summary>
        [Fact]
        public void TwoFloorsAFewUnitsApartAreJudgedSeparately()
        {
            var answers = AreaGenerator.SampleClearance(Slab(2f, 4f), Mins, Maxs, 10, 10, 4f, 0f);

            Assert.Equal(AreaGenerator.Clearance.Stand, answers[0]);
            Assert.Equal(AreaGenerator.Clearance.None, answers[1]);
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

        /// <summary>
        /// Crouch-only ground needs room for a crouching body, not just a clear line up from the sample.
        ///
        /// The sample sits in the middle of a slot 28 units wide under a crouch-height ceiling. The line
        /// straight up is clear to the ceiling at 64, but a body is 32 units wide and fits nowhere in
        /// the slot. The same ceiling over a slot 100 units wide is crouch ground.
        /// </summary>
        [Fact]
        public void CrouchGroundTooNarrowForABodyIsNoGround()
        {
            Assert.Equal(AreaGenerator.Clearance.None,
                AreaGenerator.SampleClearance(SlabInSlot(236f, 264f), Mins, Maxs, 10, 10, 0f)[0]);
            Assert.Equal(AreaGenerator.Clearance.Crouch,
                AreaGenerator.SampleClearance(SlabInSlot(200f, 300f), Mins, Maxs, 10, 10, 0f)[0]);
        }

        /// <summary>
        /// The body does not have to be centred on the sample, only somewhere in the sample's own cell.
        ///
        /// A wall 8 units from the sample leaves no room for a body centred there, but half a step
        /// away from the wall there is. koth_viaduct's crouch spots, which Valve marks too, sit in a
        /// wedge under a sloping ceiling like this, and requiring a centred body lost them.
        /// </summary>
        [Fact]
        public void ABodyMayStandOffCentreInItsCell()
        {
            Assert.Equal(AreaGenerator.Clearance.Crouch,
                AreaGenerator.SampleClearance(SlabInSlot(-1000f, 258f), Mins, Maxs, 10, 10, 0f)[0]);
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
