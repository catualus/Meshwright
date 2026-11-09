using System.Collections.Generic;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That a body starting inside something is not taken for a body in open air.
    ///
    /// <see cref="BspVisibility.TryTraceHull"/> reports whether the box *entered* something. A box that
    /// starts inside a brush and is swept out of it enters nothing, so the trace says "no hit" and
    /// only its start-solid flag says where the box began. Every caller that read "no hit" as clear
    /// let a body start in solid: a step out from under a ceiling it could not stand under, a step
    /// through a fence its start point touched, a crouching body whose footprint began inside a crate.
    /// </summary>
    public class StartSolidTests
    {
        private const int Solid = 0x1;

        /// <summary>
        /// The control for the graze rule: a sample on the face of a wall - which is where a grid point
        /// lands whenever its grid line meets a face - steps straight away from the wall into open floor.
        /// The box starts touching the wall and is dragged out of it, so it starts solid, and refusing
        /// that made the foot of every such wall a dead end.
        /// </summary>
        [Fact]
        public void AStepAwayFromAWallItsSampleTouchesIsAllowed()
        {
            var world = Boxes((new(-100, -100, 0), new(0, 100, 200)));

            Assert.True(Traversability.CanStep(world, new(0, 0, 0), new(25, 0, 0)));
            Assert.False(Traversability.CanStep(world, new(0, 0, 0), new(-25, 0, 0)));
        }

        /// <summary>
        /// A fence a third of a unit thick stands a tenth of a unit from the sample, inside the 0.9 unit
        /// box but clear of the sample point. The step across it starts in the fence and leaves it, so
        /// the sweep reported no hit and the step was allowed straight through.
        /// </summary>
        [Fact]
        public void AStepDoesNotPassThroughAFenceItsStartTouches()
        {
            var world = Boxes((new(0.1f, -100, 0), new(0.4f, 100, 200)));

            Assert.False(Traversability.CanStep(world, new(0, 0, 0), new(25, 0, 0)),
                "the step passed through a fence because the box started inside it");
            Assert.True(Traversability.CanStep(world, new(0, 0, 0), new(-25, 0, 0)));
        }

        /// <summary>
        /// A slab 30 units over the floor reaches 10 units past the sample, which is far too low to
        /// stand or crouch under. Stepping out from under it starts the box in the slab and drags it
        /// out into the open, which the sweep reported as no hit.
        /// </summary>
        [Fact]
        public void AStepOutFromUnderALowCeilingIsRefused()
        {
            var world = Boxes((new(-100, -100, 30), new(10, 100, 50)));

            Assert.False(Traversability.CanStep(world, new(0, 0, 0), new(25, 0, 0)),
                "a step out from under a ceiling 30 units up was allowed");
            Assert.True(Traversability.CanStep(world, new(25, 0, 0), new(50, 0, 0)));
        }

        /// <summary>
        /// A crouching body does not fit where every place it could stand in the cell overlaps a post.
        ///
        /// Ground at z = 0 under a crouch-height ceiling at 64, the sample at (250, 250), and a post 2
        /// units square and 40 tall just off the sample: clear of the headroom line straight up, but
        /// inside the 32 unit plate however the body shifts within the cell. Every plate starts inside
        /// the post and rises out of it below the crouching height, so the sweep reports no hit, and
        /// that used to be a fit. The same ceiling with no post is crouch ground.
        /// </summary>
        [Fact]
        public void ACrouchingBodyThatStartsInAPostDoesNotFit()
        {
            var ceiling = (new BspFile.Vector3(-1000, -1000, 64), new BspFile.Vector3(2000, 2000, 84));
            var post = (new BspFile.Vector3(251, 251, 0), new BspFile.Vector3(253, 253, 40));
            var mins = new BspFile.Vector3(0, 0, -500);
            var maxs = new BspFile.Vector3(1000, 1000, 500);

            Assert.Equal(AreaGenerator.Clearance.None,
                AreaGenerator.SampleClearance(Boxes(ceiling, post), mins, maxs, 10, 10, 0f)[0]);
            Assert.Equal(AreaGenerator.Clearance.Crouch,
                AreaGenerator.SampleClearance(Boxes(ceiling), mins, maxs, 10, 10, 0f)[0]);
        }

        /// <summary>
        /// Solid ground with its top at z = 0, and boxes standing in the open air above it, each given
        /// by its corners. The tree has the one split at the ground; the air leaf lists every box,
        /// which is all a trace needs to find them.
        /// </summary>
        internal static BspVisibility Boxes(params (BspFile.Vector3 Mins, BspFile.Vector3 Maxs)[] boxes)
        {
            var planes = new List<BspFile.Plane>
            {
                TestWorlds.Plane(0, 0, 1, 0),        // the ground's top face, and the split above/below it
                TestWorlds.Plane(0, 0, -1, 500),     // z >= -500
                TestWorlds.Plane(-1, 0, 0, 5000),    // x >= -5000
                TestWorlds.Plane(1, 0, 0, 5000),     // x <= 5000
                TestWorlds.Plane(0, -1, 0, 5000),    // y >= -5000
                TestWorlds.Plane(0, 1, 0, 5000),     // y <= 5000
            };

            var brushes = new List<BspFile.Brush> { new() { FirstSide = 0, NumSides = 6, Contents = Solid } };

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

            var leafs = new[]
            {
                new BspVisibility.Leaf { Contents = 0 },     // 0: open air, with the boxes standing in it
                new BspVisibility.Leaf { Contents = Solid }, // 1: the ground
            };

            var standing = new int[boxes.Length];
            for (int i = 0; i < standing.Length; i++)
                standing[i] = i + 1;

            return BspVisibility.FromGeometry(planes.ToArray(), nodes, leafs, brushes.ToArray(), sides,
                [standing, [0]]);
        }
    }
}
