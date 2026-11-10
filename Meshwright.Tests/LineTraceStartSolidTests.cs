using System;
using System.Collections.Generic;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That a line trace starting inside something is not taken for one starting in open air.
    ///
    /// The line traces have no start-solid flag. A segment starting inside a brush is blocked, because
    /// it starts inside - so a point test, a zero-length line, sees brushes. Triangles are different:
    /// a segment owns <c>(epsilon, 1 + epsilon]</c> of itself so that a trace starting on a surface does not stop on
    /// it, and one starting *inside* a prop's hull crosses nothing until it leaves, and nothing at all
    /// if it never does. Every caller that guarded a trace with a point test, or read "clear" as open
    /// air, was blind to props: floors sampled inside them, headroom inside them, crossings and falls
    /// straight through them.
    /// </summary>
    public class LineTraceStartSolidTests
    {
        /// <summary>A closed box as twelve triangles, wound however the corners fall.</summary>
        internal static BspFile.Vector3[] Box(BspFile.Vector3 mins, BspFile.Vector3 maxs)
        {
            var c = new BspFile.Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                c[i] = new BspFile.Vector3((i & 1) == 0 ? mins.X : maxs.X, (i & 2) == 0 ? mins.Y : maxs.Y,
                    (i & 4) == 0 ? mins.Z : maxs.Z);
            }

            int[] faces =
            [
                0, 2, 3, 0, 3, 1, 4, 5, 7, 4, 7, 6, 0, 1, 5, 0, 5, 4,
                2, 6, 7, 2, 7, 3, 0, 4, 6, 0, 6, 2, 1, 3, 7, 1, 7, 5,
            ];

            var tris = new BspFile.Vector3[faces.Length];
            for (int i = 0; i < faces.Length; i++) tris[i] = c[faces[i]];
            return tris;
        }

        /// <summary>A world with props in it: <paramref name="world"/>'s brushes and one prop per box.</summary>
        private static BspVisibility WithProps(BspVisibility world,
            params (BspFile.Vector3 Mins, BspFile.Vector3 Maxs)[] props)
        {
            var hulls = new List<BspFile.Vector3[]>();
            foreach (var (mins, maxs) in props) hulls.Add(Box(mins, maxs));

            world.AttachStaticProps(StaticProps.FromHulls([.. hulls]));
            return world;
        }

        /// <summary>Solid ground at z = 0 with one prop box standing on it.</summary>
        private static BspVisibility GroundWithProp(BspFile.Vector3 mins, BspFile.Vector3 maxs)
            => WithProps(TestWorlds.Ground(), (mins, maxs));

        private static BspFile.Vector3 V(float x, float y, float z) => new(x, y, z);

        [Fact]
        public void ABoxContainsItsInsideButNotItsFaceOrOutside()
        {
            var solids = ConvexSolids.Build(Box(V(0, 0, 0), V(10, 20, 30)), [(0, 12)]);

            Assert.Equal(1, solids.Count);
            Assert.True(solids.Contains(V(5, 10, 15)));
            Assert.True(solids.Contains(V(0.5f, 19.5f, 29.5f)));

            // On a face is on the surface, not in it: a floor sampled on top of a prop stands there.
            Assert.False(solids.Contains(V(5, 10, 30)));
            Assert.False(solids.Contains(V(5, 10, 31)));
            Assert.False(solids.Contains(V(-1, 10, 15)));
        }

        /// <summary>
        /// Winding is not relied on - a .phy does not give an outward one reliably - so flipping half
        /// the triangles changes nothing.
        /// </summary>
        [Fact]
        public void ContainmentDoesNotDependOnWinding()
        {
            var box = Box(V(0, 0, 0), V(10, 10, 10));
            for (int t = 0; t < 12; t += 2)
                (box[t * 3 + 1], box[t * 3 + 2]) = (box[t * 3 + 2], box[t * 3 + 1]);

            var solids = ConvexSolids.Build(box, [(0, 12)]);

            Assert.True(solids.Contains(V(5, 5, 5)));
            Assert.False(solids.Contains(V(5, 5, 11)));
        }

        /// <summary>A piece with no volume has no inside, and is dropped rather than containing the plane.</summary>
        [Fact]
        public void AFlatPieceIsNotASolid()
        {
            var flat = new[]
            {
                V(0, 0, 0), V(10, 0, 0), V(10, 10, 0), V(0, 0, 0), V(10, 10, 0), V(0, 10, 0),
                V(0, 0, 0), V(10, 10, 0), V(10, 0, 0), V(0, 0, 0), V(0, 10, 0), V(10, 10, 0),
            };

            var solids = ConvexSolids.Build(flat, [(0, 4)]);

            Assert.Equal(0, solids.Count);
            Assert.False(solids.Contains(V(5, 5, 0)));
        }

        /// <summary>Each piece of a prop is its own convex solid; a point in either is inside.</summary>
        [Fact]
        public void EachPieceIsTestedOnItsOwn()
        {
            var tris = new List<BspFile.Vector3>(Box(V(0, 0, 0), V(10, 10, 10)));
            tris.AddRange(Box(V(100, 0, 0), V(110, 10, 10)));

            var solids = ConvexSolids.Build([.. tris], [(0, 12), (12, 12)]);

            Assert.Equal(2, solids.Count);
            Assert.True(solids.Contains(V(5, 5, 5)));
            Assert.True(solids.Contains(V(105, 5, 5)));

            // Between them is outside both, though it is inside the hull the two would make together.
            Assert.False(solids.Contains(V(50, 5, 5)));
        }

        /// <summary>
        /// Pinned rather than fixed: a brush entity answers a start inside it as the world does. The
        /// line is blocked, the surface trace stops where it starts, and the point is solid - so for
        /// brushes of either kind the point test and the line agree, and only props needed teaching.
        /// </summary>
        [Fact]
        public void ABrushEntityTreatsAStartInsideItAsTheWorldDoes()
        {
            var vis = ToggleFloorTests.WorldWithBlock("");
            var inside = V(50, 50, 40);

            Assert.True(vis.IsPointSolid(50, 50, 40, BspVisibility.GenerationMask));
            Assert.False(vis.IsLineClear(inside, V(50, 50, 200), BspVisibility.GenerationMask));
            Assert.True(vis.TryTraceSurface(inside, V(50, 50, -100), BspVisibility.GroundMask, out var hit, out _));
            Assert.Equal(40f, hit.Z);
        }

        /// <summary>
        /// The point test sees props. A zero-length line never crosses a triangle, so it never did,
        /// and every guard built on it let a probe start inside a prop.
        /// </summary>
        [Fact]
        public void APointInsideAPropIsSolid()
        {
            var vis = GroundWithProp(V(-20, -20, 0), V(20, 20, 100));

            Assert.True(vis.IsPointSolid(0, 0, 50, BspVisibility.GenerationMask));
            Assert.True(vis.IsPointSolid(0, 0, 50, BspVisibility.GroundMask));
            Assert.False(vis.IsPointSolid(0, 0, 101, BspVisibility.GenerationMask));
            Assert.False(vis.IsPointSolid(30, 0, 50, BspVisibility.GenerationMask));

            // The line traces themselves are unchanged: a line wholly inside the prop is still clear.
            Assert.True(vis.IsLineClear(V(0, 0, 10), V(0, 0, 90), BspVisibility.GenerationMask));
        }

        /// <summary>
        /// The floor walk goes through a prop it lands on, as it goes through a brush. It used to step
        /// a quarter unit into the prop, be told that was open air, and sample the floor the prop
        /// stands on as ground - inside the prop.
        /// </summary>
        [Fact]
        public void TheFloorWalkFindsNoFloorInsideAProp()
        {
            var vis = GroundWithProp(V(-20, -20, -4), V(20, 20, 100));

            Span<BspVisibility.FloorSample> samples = stackalloc BspVisibility.FloorSample[16];
            int count = vis.EnumerateFloors(0, 0, 400, -400, samples);

            Assert.Equal(1, count);
            Assert.Equal(100f, samples[0].Z, 3);
        }

        /// <summary>
        /// A column whose top is inside solid - every column the generator walks, near enough, the top
        /// of the map being sealed - does not start with a "floor" where it starts.
        /// </summary>
        [Fact]
        public void TheFloorWalkDoesNotSampleWhereItStartsInsideSolid()
        {
            var vis = StartSolidTests.Boxes((V(-100, -100, 100), V(100, 100, 120)));

            Span<BspVisibility.FloorSample> samples = stackalloc BspVisibility.FloorSample[16];
            int count = vis.EnumerateFloors(0, 0, 110, -400, samples);

            Assert.Equal(1, count);
            Assert.Equal(0f, samples[0].Z, 3);
        }

        /// <summary>
        /// Pinned rather than fixed: the floor search does not answer a start inside a prop as it does
        /// one inside a brush. Inside a brush it reports floor at the start; inside a prop it carries on
        /// through the prop to what is under it. Answering like a brush was tried and kept areas running
        /// into props that the clipper's floor check used to pull back, so a caller that must not take
        /// the inside of a prop for open air asks the point test first.
        /// </summary>
        [Fact]
        public void TheFloorSearchCarriesOnThroughAPropItStartsIn()
        {
            var prop = GroundWithProp(V(-20, -20, -4), V(20, 20, 100));
            var brush = StartSolidTests.Boxes((V(-20, -20, 0), V(20, 20, 100)));

            Assert.True(StairMarker.TryFindFloor(brush, 0, 0, 50, 100, out float inBrush));
            Assert.Equal(50f, inBrush);

            Assert.True(StairMarker.TryFindFloor(prop, 0, 0, 50, 100, out float inProp));
            Assert.Equal(0f, inProp, 1);

            // From above, the prop's top is the floor.
            Assert.True(StairMarker.TryFindFloor(prop, 0, 0, 110, 20, out float onTop));
            Assert.Equal(100f, onTop, 1);
        }

        /// <summary>
        /// No room to stand where the headroom line starts inside a prop. The sample is real floor -
        /// the prop hangs a unit above it, and the walk lands in that gap - but the line up from it
        /// starts inside the prop and, the prop being taller than a player, never leaves it.
        /// </summary>
        [Fact]
        public void NoHeadroomWhereTheHeadroomLineStartsInsideAProp()
        {
            var vis = WithProps(StartSolidTests.Boxes(), (V(240, 240, 1), V(260, 260, 97)));
            var mins = V(0, 0, -500);
            var maxs = V(1000, 1000, 500);

            Assert.Equal(AreaGenerator.Clearance.None,
                AreaGenerator.SampleClearance(vis, mins, maxs, 10, 10, 0f)[0]);

            // The same floor a step aside, clear of the prop, is ground.
            Assert.Equal(AreaGenerator.Clearance.Stand,
                AreaGenerator.SampleClearance(vis, mins, maxs, 12, 10, 0f)[0]);
        }

        /// <summary>A foot inside a prop has started in solid, and there is no room there.</summary>
        [Fact]
        public void NoRoomForAFootInsideAProp()
        {
            var vis = GroundWithProp(V(-20, -20, -4), V(20, 20, 100));

            Assert.False(ConnectionBuilder.HasRoom(vis, 0, 0, 0, out bool startedSolid));
            Assert.True(startedSolid);

            Assert.True(ConnectionBuilder.HasRoom(vis, 50, 0, 0, out startedSolid));
            Assert.False(startedSolid);
        }

        /// <summary>
        /// An outer clearance column is not lifted onto a floor found from inside a prop: that floor is
        /// under the prop, and lifting the line onto it carries the line over the prop.
        /// </summary>
        [Fact]
        public void AClearanceColumnIsNotLiftedOntoFloorInsideAProp()
        {
            var vis = WithProps(StartSolidTests.Boxes((V(-50, -50, 0), V(50, 50, 10))),
                (V(-20, -20, 5), V(20, 20, 100)));

            Assert.Equal(0f, ConnectionBuilder.SideFloor(vis, 0, 0, 0f));

            // Beside the prop the block is a step up, and lifts the column as it should.
            Assert.Equal(10f, ConnectionBuilder.SideFloor(vis, 40, 0, 0f), 1);
        }

        /// <summary>
        /// The half of a body hanging past the end of a span is not over floor when it is inside a
        /// prop - the span ends against the prop.
        /// </summary>
        [Fact]
        public void AnOverhangIntoAPropIsNotSupported()
        {
            var vis = GroundWithProp(V(-100, 2, -4), V(100, 100, 100));

            Assert.False(ConnectionBuilder.OverhangSupported(vis, NavGeometry.East, 1f, -6, 0, 0, 6, 0, 0));

            // With the prop moved off, the floor carries on and the overhang stands on it.
            var open = GroundWithProp(V(-100, 200, -4), V(100, 300, 100));
            Assert.True(ConnectionBuilder.OverhangSupported(open, NavGeometry.East, 1f, -6, 0, 0, 6, 0, 0));
        }

        /// <summary>The clearance lines refuse a crossing that runs wholly inside a prop.</summary>
        [Fact]
        public void ClearanceLinesInsideAPropAreNotClear()
        {
            var vis = GroundWithProp(V(-50, -50, -4), V(50, 50, 100));

            Assert.False(ConnectionBuilder.LinesClear(vis, -20, 0, 20, 0, 0f, 0f, 16f, sideFloors: false));
            Assert.True(ConnectionBuilder.LinesClear(vis, 80, 0, 120, 0, 0f, 0f, 16f, sideFloors: false));
        }

        /// <summary>
        /// A ledge a hundred units up, ground below it, and a prop pillar standing in front of it from
        /// the ground to above the ledge. A drop off the ledge onto the ground beyond the pillar has to
        /// fall through the pillar, and the middle column of the fall lay wholly inside it.
        /// </summary>
        [Fact]
        public void AFallThroughAPropIsRefused()
        {
            var ledge = (V(-200, -100, 0), V(0, 100, 100));
            var to = TestWorlds.Area(2, 0, -100, 200, 100, 0);

            var pillar = WithProps(StartSolidTests.Boxes(ledge), (V(15, -30, -10), V(30, 30, 150)));
            Assert.False(ConnectionBuilder.CanFallTo(pillar, to, -6, 0, 100, 40, 0, 0, 100));

            var open = WithProps(StartSolidTests.Boxes(ledge), (V(15, 300, -10), V(30, 360, 150)));
            Assert.True(ConnectionBuilder.CanFallTo(open, to, -6, 0, 100, 40, 0, 0, 100));
        }

        /// <summary>
        /// The floor being stepped off carries on through a prop standing on it, as it would through a
        /// brush: the probe for the lip starting inside the prop crossed nothing and called it the lip.
        /// </summary>
        [Fact]
        public void TheLipIsNotInsideAProp()
        {
            var ledge = (V(-200, -100, 0), V(0, 100, 100));
            var vis = WithProps(StartSolidTests.Boxes(ledge), (V(0, -30, -200), V(40, 30, 150)));

            Assert.True(ConnectionBuilder.Lip(vis, -6, 0, 100, 46, 0, 46, 62) > 40f);

            // Without the prop the lip is the ledge's own edge.
            Assert.True(ConnectionBuilder.Lip(StartSolidTests.Boxes(ledge), -6, 0, 100, 46, 0, 46, 62) <= 8f);
        }

        /// <summary>
        /// A lift's floor is not where a probe started inside the ceiling over it. A probe starting in
        /// solid is reported as a hit right there, facing up, and three of the five did - so the median
        /// put the floor a unit above the platform's top instead of on its real floor.
        /// </summary>
        [Fact]
        public void APlatformFloorProbeStartingInSolidIsIgnored()
        {
            var vis = StartSolidTests.Boxes(
                (V(0, 0, 0), V(100, 100, 4)),          // the platform's floor
                (V(-50, -50, 10.5f), V(60, 150, 40))); // a ceiling over most of it

            Assert.Equal(4f, ElevatorConnector.StandingSurface(vis, V(0, 0, 0), V(100, 100, 10))!.Value, 1);
        }

        /// <summary>
        /// A hatch dropping into a recess just its own size is not a lift: at the bottom of its travel
        /// there is nothing beside it but the recess walls. The landing probes there all start inside
        /// those walls, and a start in solid used to read as floor a step above the stop.
        /// </summary>
        [Fact]
        public void ALandingProbeStartingInAWallIsNotALanding()
        {
            var recess = StartSolidTests.Boxes(
                (V(-200, -200, 0), V(0, 264, 100)),
                (V(64, -200, 0), V(264, 264, 100)),
                (V(0, -200, 0), V(64, 0, 100)),
                (V(0, 64, 0), V(64, 264, 100)));

            Assert.False(ElevatorConnector.HasLandings(recess, V(0, 0, 92), V(64, 64, 100), [100f, 8f]));

            // A lift with a ledge beside its top and open floor beside its bottom has both.
            var lift = StartSolidTests.Boxes((V(64, -200, 0), V(264, 264, 100)));
            Assert.True(ElevatorConnector.HasLandings(lift, V(0, 0, 92), V(64, 64, 100), [100f, 0f]));
        }
    }
}
