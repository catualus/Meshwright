using System;
using System.Collections.Generic;
using System.Linq;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// Level crossings a crouching body walks across only when it is carried at the height it rests at over
    /// the ground under it, or only at a position along the span the other crossings do not try
    /// (<see cref="ConnectionBuilder.BodyWalks"/>), and the crossings that walk still refuses: a gap
    /// narrower than a body, and floor rising more than half a step between the two areas.
    ///
    /// Two areas meet at y = 100 and are crossed southwards. Exercised through
    /// <see cref="ConnectionBuilder.Explain"/>, which runs the real tests in the real order.
    /// </summary>
    public class WalkCrossingTests
    {
        private const int Solid = 0x1;

        /// <summary>The slope the areas stand on: 31 degrees, rising towards +x.</summary>
        private const float Rise = 0.6f;

        /// <summary>
        /// Two areas 18 units wide on ground sloping across the crossing, with a wall at x = 15.5. The
        /// middle's clearance line on the wall's side starts inside it. Every other crossing on a span this
        /// narrow drags a body across at the floor under its middle, half a step up, and the slope under its
        /// uphill side is higher than that, so it starts inside the slope - at the span's ends as well. A body
        /// resting on the slope, on its uphill side, walks straight across anywhere clear of the wall.
        /// </summary>
        [Fact]
        public void ACrossingOnASlopeBesideAWallIsWalked()
        {
            var world = Sloping((15.5f, -1000f, -2000f, 1000f, 1000f, 2000f));

            var log = ConnectionBuilder.Explain(world, Sloped(1, 0f), Sloped(2, 100f));

            Assert.Contains(log, line => line.StartsWith("south:") && line.Contains("REFUSED by Crossing at the middle, would connect further along"));
        }

        /// <summary>
        /// The control: the same slope with a second wall at x = -15, leaving a gap 30.5 units wide between
        /// them. No body fits through it anywhere, carried at any height.
        /// </summary>
        [Fact]
        public void AGapNarrowerThanABodyOnTheSlopeIsStillRefused()
        {
            var world = Sloping((15.5f, -1000f, -2000f, 1000f, 1000f, 2000f), (-1000f, -1000f, -2000f, -15f, 1000f, 2000f));

            var log = ConnectionBuilder.Explain(world, Sloped(1, 0f), Sloped(2, 100f));

            Assert.Contains(log, line => line == "south:   REFUSED by Crossing at the middle and everywhere along the span");
        }

        /// <summary>
        /// Flat ground and a corridor 36 units wide, from x = -12 to 24, off the middle of a 25 unit span
        /// that runs on into the wall on one side. A body fits through it with a unit to spare either side
        /// only centred between x = 5 and 7. The middle's clearance line runs into the wall, and the
        /// positions tried along the span either side of the middle put the body into one wall or the
        /// other; a body walking across from x = 5.5 gets through.
        /// </summary>
        [Fact]
        public void ACorridorABodyWideOffTheMiddleOfTheSpanIsWalked()
        {
            var world = Corridor(24f);

            var log = ConnectionBuilder.Explain(world,
                TestWorlds.Area(1, -12.5f, 0f, 12.5f, 100f, 0f), TestWorlds.Area(2, -12.5f, 100f, 12.5f, 200f, 0f));

            Assert.Contains(log, line => line.StartsWith("south:") && line.Contains("REFUSED by Crossing at the middle, would connect further along"));
        }

        /// <summary>
        /// The same corridor with the areas running on 39 units into the far wall, so their shared span is
        /// 64 units long. A body would walk through it as before, but the walk is tried only across a span
        /// narrower than a body, and on this one the middle and the positions along it are left to the
        /// clearance lines, which find no way through.
        /// </summary>
        [Fact]
        public void AWalkIsTriedOnlyAcrossASpanNarrowerThanABody()
        {
            var world = Corridor(24f);

            var log = ConnectionBuilder.Explain(world,
                TestWorlds.Area(1, -12.5f, 0f, 51.5f, 100f, 0f), TestWorlds.Area(2, -12.5f, 100f, 51.5f, 200f, 0f));

            Assert.True(ConnectionBuilder.BodyWalks(world, 6f, 94f, 0f, 6f, 106f, 0f));
            Assert.Contains(log, line => line == "south:   REFUSED by Crossing at the middle and everywhere along the span");
        }

        /// <summary>
        /// The walk itself, on that corridor: centred on x = 6 it walks; centred on x = 0 its side is in
        /// the wall from the start. Between walls 33 units apart a body fits, but with half a unit to spare
        /// either side at most, and that is not walked: the windows that narrow lie a unit wide along the
        /// span, and nothing following the mesh is that exact.
        /// </summary>
        [Fact]
        public void ABodyWalksOnlyWhereItFitsWithAUnitToSpare()
        {
            Assert.True(ConnectionBuilder.BodyWalks(Corridor(24f), 6f, 94f, 0f, 6f, 106f, 0f));
            Assert.False(ConnectionBuilder.BodyWalks(Corridor(24f), 0f, 94f, 0f, 0f, 106f, 0f));
            Assert.False(ConnectionBuilder.BodyWalks(Corridor(21f), 4.5f, 94f, 0f, 4.5f, 106f, 0f));
        }

        /// <summary>
        /// The build records a link it made only by a walk, and the stale-link pass removes it when the clip
        /// leaves no way across: the corridor's two areas pulled back to x = 3, where no body, walking or
        /// dragged, gets through between the wall at x = -12 and the areas' new edge, and none stands at both
        /// ends of anything tried. Without the record, a link no body stands at both ends of is left alone,
        /// and both stay.
        /// </summary>
        [Fact]
        public void ALinkMadeOnlyByAWalkGoesWhenTheClipLeavesNoWayAcross()
        {
            var world = Corridor(24f);
            var built = new NavFile();
            built.Areas.Add(TestWorlds.Area(1, -12.5f, 0f, 12.5f, 100f, 0f));
            built.Areas.Add(TestWorlds.Area(2, -12.5f, 100f, 12.5f, 200f, 0f));

            var made = ConnectionBuilder.Build(built, world);

            Assert.Contains((1u, NavGeometry.South, 2u), made.Walked);
            Assert.Contains((2u, NavGeometry.North, 1u), made.Walked);
            Assert.Empty(made.AtSpanEnds);

            var before = new Dictionary<uint, NavGeometry.Bounds>
            {
                [1] = new(-12.5f, 0f, 12.5f, 100f), [2] = new(-12.5f, 100f, 12.5f, 200f),
            };

            var kept = Clipped();
            var left = ConnectionBuilder.DropStale(kept, world, before.Keys.ToHashSet(), before, made.AtSpanEnds);

            Assert.Equal(2, left.Refused);
            Assert.Equal(0, left.Removed);

            var nav = Clipped();
            var dropped = ConnectionBuilder.DropStale(nav, world, before.Keys.ToHashSet(), before, made.AtSpanEnds, made.Walked);

            Assert.Equal(2, dropped.Removed);
            Assert.Equal(2, dropped.RemovedAtEnds);
            Assert.All(nav.Areas, area => Assert.All(area.Connections, list => Assert.Empty(list)));
        }

        /// <summary>The corridor's two areas after the clip, x -12..3, linked both ways.</summary>
        private static NavFile Clipped()
        {
            var north = TestWorlds.Area(1, -12f, 0f, 3f, 100f, 0f);
            var south = TestWorlds.Area(2, -12f, 100f, 3f, 200f, 0f);
            north.Connections[NavGeometry.South].Add(2);
            south.Connections[NavGeometry.North].Add(1);

            var nav = new NavFile();
            nav.Areas.Add(north);
            nav.Areas.Add(south);
            return nav;
        }

        /// <summary>
        /// A strip 12 units high and 40 deep between two floors at the same height: under a step, so a walker
        /// gets onto it and off again, but more than half a step above both floors. That is floor in its own
        /// right, not something the body passes over on the way, and walking over it would skip whatever
        /// stands on it, so the walk refuses it as the step-over test does.
        /// </summary>
        [Fact]
        public void ABodyDoesNotWalkOverFloorRisingMoreThanHalfAStep()
        {
            var world = StartSolidTests.Boxes((new(-1000, 100, 0), new(1000, 140, 12)));

            Assert.False(ConnectionBuilder.BodyWalks(world, 0f, 94f, 0f, 0f, 146f, 0f));
        }

        /// <summary>Flat ground between a wall up to x = -12 and one from <paramref name="far"/> on.</summary>
        private static BspVisibility Corridor(float far) => StartSolidTests.Boxes(
            (new(-1000, -1000, 0), new(-12, 1000, 200)),
            (new(far, -1000, 0), new(1000, 1000, 200)));

        /// <summary>An area on the slope, 18 units wide, from <paramref name="minY"/> 100 units south.</summary>
        private static NavArea Sloped(uint id, float minY)
        {
            var area = TestWorlds.Area(id, -9f, minY, 9f, minY + 100f, 0f);
            area.NwCorner[2] = -9f * Rise;
            area.SwZ = -9f * Rise;
            area.SeCorner[2] = 9f * Rise;
            area.NeZ = 9f * Rise;
            return area;
        }

        /// <summary>
        /// Solid ground under the slope, z = <see cref="Rise"/> x, with open air above it and axis-aligned
        /// boxes standing in the air. The tree has the one split, on the ground plane; the air leaf lists
        /// every box, which is all a trace needs to find them.
        /// </summary>
        private static BspVisibility Sloping(params (float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ)[] boxes)
        {
            float length = MathF.Sqrt(1f + Rise * Rise);

            var planes = new List<BspFile.Plane>
            {
                new() { Normal = new BspFile.Vector3(-Rise / length, 0f, 1f / length), Distance = 0f },
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
