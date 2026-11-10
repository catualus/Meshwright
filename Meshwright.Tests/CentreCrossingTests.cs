using System;
using System.Collections.Generic;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// Crossings a body makes at the middle of a shared edge that the builder used to refuse there: over
    /// ground sloping across the crossing, and off a ledge whose floor runs on past the area.
    ///
    /// Exercised through <see cref="ConnectionBuilder.Explain"/>, which runs the real tests in the real
    /// order and says which one refused.
    /// </summary>
    public class CentreCrossingTests
    {
        private const int Solid = 0x1;

        /// <summary>
        /// Two areas in a row on ground sloping at 40 degrees across the crossing, rising towards +x.
        /// The body standing in the middle rests on the slope's uphill side, 13 units above the middle
        /// sixteen units across, and crosses straight over. The lowest clearance line on that side, 8
        /// units above the *middle*, runs through the slope - everywhere along the edge, because the
        /// slope is the same everywhere. Measured from the ground under it, it is clear.
        /// </summary>
        [Fact]
        public void ACrossingOverGroundSlopingAcrossItIsLinked()
        {
            const float Angle = 40f * MathF.PI / 180f;
            float rise = MathF.Tan(Angle);

            var world = World(new BspFile.Vector3(-MathF.Sin(Angle), 0f, MathF.Cos(Angle)), 0f);
            var first = Sloped(1, -50f, 0f, 50f, 100f, rise);
            var second = Sloped(2, -50f, 100f, 50f, 200f, rise);

            var log = ConnectionBuilder.Explain(world, first, second);

            Assert.Contains(log, line => line == "south:   would connect");
        }

        /// <summary>
        /// The control: flat ground with a kerb 20 units high - more than a step - beside the middle of
        /// the crossing, under the outer line on one side. A walker cannot step onto it, so it is not a
        /// floor the line is lifted onto, and the middle is still refused. Away from the kerb the edge is
        /// open, which is where the crossing goes.
        /// </summary>
        [Fact]
        public void AKerbHigherThanAStepBesideTheMiddleStillRefusesIt()
        {
            var world = World(new BspFile.Vector3(0f, 0f, 1f), 0f, (10f, 80f, 0f, 40f, 120f, 20f));
            var first = TestWorlds.Area(1, -50f, 0f, 50f, 100f, 0f);
            var second = TestWorlds.Area(2, -50f, 100f, 50f, 200f, 0f);

            var log = ConnectionBuilder.Explain(world, first, second);

            Assert.Contains(log, line => line.StartsWith("south:") &&
                                         line.Contains("REFUSED by Crossing at the middle, would connect further along"));
        }

        /// <summary>
        /// An area that stops twelve units short of a ledge, whose floor runs on to the lip at y = 112,
        /// and the ground 128 units below starting eight units past it. The fall used to be tested from
        /// two units past the area, inside the ledge, and was refused; from past the lip it is clear.
        /// </summary>
        [Fact]
        public void ADropOffALedgeIsTestedFromItsLip()
        {
            var world = World(new BspFile.Vector3(0f, 0f, 1f), -128f, (-1000f, -1000f, -128f, 1000f, 112f, 0f));
            var upper = TestWorlds.Area(1, -50f, 0f, 50f, 100f, 0f);
            var lower = TestWorlds.Area(2, -50f, 120f, 50f, 220f, -128f);

            var log = ConnectionBuilder.Explain(world, upper, lower);

            Assert.Contains(log, line => line == "south:   would connect");
        }

        /// <summary>
        /// The pit's own shape before the clip: the lower area runs right up to the upper one, six and a
        /// half units into the wall under the lip, so its landing point is inside the ledge. The walker
        /// lands past the lip instead, still inside the lower area.
        /// </summary>
        [Fact]
        public void ADropLandsPastTheLipWhenTheAreaBelowRunsUnderIt()
        {
            var world = World(new BspFile.Vector3(0f, 0f, 1f), -128f, (-1000f, -1000f, -128f, 1000f, 106.5f, 0f));
            var upper = TestWorlds.Area(1, -50f, 0f, 50f, 100f, 0f);
            var lower = TestWorlds.Area(2, -50f, 100f, 50f, 220f, -128f);

            var log = ConnectionBuilder.Explain(world, upper, lower);

            Assert.Contains(log, line => line == "south:   would connect");
        }

        /// <summary>
        /// The control: the same drop where the upper floor is a slab eight units thick that runs on
        /// over the whole of the lower area - a basement under an upper storey. The floor has no lip
        /// anywhere near the landing, so there is no fall to make, and the drop is refused rather than
        /// linked through the slab.
        /// </summary>
        [Fact]
        public void ADropIsNotLinkedThroughTheFloorAboveTheLanding()
        {
            var world = World(new BspFile.Vector3(0f, 0f, 1f), -128f, (-1000f, -1000f, -8f, 1000f, 1000f, 0f));
            var upper = TestWorlds.Area(1, -50f, 0f, 50f, 100f, 0f);
            var lower = TestWorlds.Area(2, -50f, 120f, 50f, 220f, -128f);

            var log = ConnectionBuilder.Explain(world, upper, lower);

            Assert.DoesNotContain(log, line => line.Contains("would connect"));
            Assert.Contains(log, line => line.StartsWith("south:") && line.Contains("REFUSED by Fall"));
        }

        /// <summary>An area on ground rising <paramref name="rise"/> units per unit of x.</summary>
        private static NavArea Sloped(uint id, float minX, float minY, float maxX, float maxY, float rise)
        {
            var area = TestWorlds.Area(id, minX, minY, maxX, maxY, 0f);
            area.NwCorner[2] = minX * rise;
            area.SwZ = minX * rise;
            area.SeCorner[2] = maxX * rise;
            area.NeZ = maxX * rise;
            return area;
        }

        /// <summary>
        /// Solid ground under a plane through the given normal and distance, with open air above it and
        /// axis-aligned boxes standing in the air. The tree has the one split, on the ground plane; the
        /// air leaf lists every box, which is all a trace needs to find them.
        /// </summary>
        private static BspVisibility World(BspFile.Vector3 groundNormal, float groundDistance,
            params (float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ)[] boxes)
        {
            var planes = new List<BspFile.Plane>
            {
                new() { Normal = groundNormal, Distance = groundDistance },   // the ground's top, and the split
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
