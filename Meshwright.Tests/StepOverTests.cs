using System.Collections.Generic;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// Crossings over a thin obstacle no higher than a step, which a walker steps onto and off again and
    /// the clearance lines read as a wall (<see cref="ConnectionBuilder.StepsOver"/>), and the obstacles
    /// that still refuse a crossing: one too deep, too high, or under too low a ceiling.
    ///
    /// Two areas on flat ground at z = 0 meet at y = 100, and whatever stands between them spans the
    /// whole edge, so there is nowhere along it or at its ends to go round. Exercised through
    /// <see cref="ConnectionBuilder.Explain"/>, which runs the real tests in the real order.
    /// </summary>
    public class StepOverTests
    {
        private const int Solid = 0x1;

        /// <summary>A bar 12 units high and 4 deep, straddling the edge: stepped over.</summary>
        [Fact]
        public void ATwelveUnitBarBetweenTwoFloorsIsLinked()
        {
            var world = World((-1000f, 98f, 0f, 1000f, 102f, 12f));

            var log = ConnectionBuilder.Explain(world, Near(), Far(100f));

            Assert.Contains(log, line => line == "south:   would connect");
        }

        /// <summary>
        /// A floor raised 12 units over 64 units of depth is a floor in its own right: the areas either
        /// side are too far apart to face each other, and each links to the area on top of it by a step.
        /// Asked directly whether a walker steps over it from one floor to the other, the answer is no -
        /// it is more than a body deep.
        /// </summary>
        [Fact]
        public void ARaisedFloorDeeperThanABodyIsAStepUpNotAStepOver()
        {
            var world = World((-1000f, 100f, 0f, 1000f, 164f, 12f));
            var near = Near();
            var raised = TestWorlds.Area(2, -50f, 100f, 50f, 164f, 12f);
            var far = TestWorlds.Area(3, -50f, 164f, 50f, 264f, 0f);

            Assert.Contains(ConnectionBuilder.Explain(world, near, raised), line => line == "south:   would connect");
            Assert.Contains(ConnectionBuilder.Explain(world, raised, far), line => line == "south:   would connect");
            Assert.DoesNotContain(ConnectionBuilder.Explain(world, near, far), line => line.Contains("would connect"));

            Assert.False(ConnectionBuilder.StepsOver(world, 0f, 94f, 0f, 0f, 170f, 0f));
        }

        /// <summary>
        /// The same height raised over 40 units with no area on it: the two floors face each other across
        /// it, and the crossing is still refused, as the obstacle is more than a body deep.
        /// </summary>
        [Fact]
        public void ARaisedStripDeeperThanABodyIsNotSteppedOver()
        {
            var world = World((-1000f, 100f, 0f, 1000f, 140f, 12f));

            var log = ConnectionBuilder.Explain(world, Near(), Far(140f));

            Assert.Contains(log, line => line == "south:   REFUSED by Crossing at the middle and everywhere along the span");
        }

        /// <summary>A wall 30 units high, more than a step: still a wall.</summary>
        [Fact]
        public void AThirtyUnitWallStillBlocks()
        {
            var world = World((-1000f, 98f, 0f, 1000f, 102f, 30f));

            var log = ConnectionBuilder.Explain(world, Near(), Far(100f));

            Assert.Contains(log, line => line == "south:   REFUSED by Crossing at the middle and everywhere along the span");
        }

        /// <summary>
        /// The 12 unit bar with a beam over it 50 units up, leaving 62 units of room either side but not
        /// a crouching body's headroom over the bar's top.
        /// </summary>
        [Fact]
        public void ABarUnderACeilingTooLowAboveItStillBlocks()
        {
            var world = World((-1000f, 98f, 0f, 1000f, 102f, 12f), (-1000f, 90f, 62f, 1000f, 110f, 80f));

            var log = ConnectionBuilder.Explain(world, Near(), Far(100f));

            Assert.Contains(log, line => line == "south:   REFUSED by Crossing at the middle and everywhere along the span");
        }

        private static NavArea Near() => TestWorlds.Area(1, -50f, 0f, 50f, 100f, 0f);

        private static NavArea Far(float start) => TestWorlds.Area(2, -50f, start, 50f, start + 100f, 0f);

        /// <summary>
        /// Flat solid ground with its top at z = 0, open air above it and axis-aligned boxes standing in
        /// the air. The tree has the one split, on the ground plane; the air leaf lists every box.
        /// </summary>
        private static BspVisibility World(params (float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ)[] boxes)
        {
            var planes = new List<BspFile.Plane>
            {
                TestWorlds.Plane(0, 0, 1, 0),         // the ground's top, and the split
                TestWorlds.Plane(0, 0, -1, 2000),     // z >= -2000
                TestWorlds.Plane(-1, 0, 0, 2000),     // x >= -2000
                TestWorlds.Plane(1, 0, 0, 2000),      // x <= 2000
                TestWorlds.Plane(0, -1, 0, 2000),     // y >= -2000
                TestWorlds.Plane(0, 1, 0, 2000),      // y <= 2000
            };

            var brushes = new List<BspFile.Brush> { new() { FirstSide = 0, NumSides = 6, Contents = Solid } };

            foreach (var (minX, minY, minZ, maxX, maxY, maxZ) in boxes)
            {
                brushes.Add(new BspFile.Brush { FirstSide = planes.Count, NumSides = 6, Contents = Solid });
                planes.Add(TestWorlds.Plane(1, 0, 0, maxX));
                planes.Add(TestWorlds.Plane(-1, 0, 0, -minX));
                planes.Add(TestWorlds.Plane(0, 1, 0, maxY));
                planes.Add(TestWorlds.Plane(0, -1, 0, -minY));
                planes.Add(TestWorlds.Plane(0, 0, 1, maxZ));
                planes.Add(TestWorlds.Plane(0, 0, -1, -minZ));
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
