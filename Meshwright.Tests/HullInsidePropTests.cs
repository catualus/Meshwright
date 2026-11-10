using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That a body swept from inside a static prop is not taken for a body in open air.
    ///
    /// The props are a triangle soup to the sweep, and a triangle sweep only knows what the box
    /// touches. A box wholly inside a prop's hull touches none of its faces, so it swept clear, and
    /// every body test built on the sweep - the flood's step, the crouching body, the bodies the
    /// connection pass stands at either end of a crossing - found room for a body inside a crate, a
    /// vehicle or a building-sized prop.
    /// </summary>
    public class HullInsidePropTests
    {
        private static BspFile.Vector3 V(float x, float y, float z) => new(x, y, z);

        /// <summary>Solid ground at z = 0 with a prop 200 units square and 200 tall standing on it.</summary>
        private static BspVisibility GroundWithBigProp()
        {
            var world = TestWorlds.Ground();
            world.AttachStaticProps(StaticProps.FromHulls(
                LineTraceStartSolidTests.Box(V(-100, -100, 0), V(100, 100, 200))));
            return world;
        }

        private static (bool Hit, float Fraction, bool StartSolid) Sweep(BspVisibility vis,
            BspFile.Vector3 a, BspFile.Vector3 b, BspFile.Vector3 mins, BspFile.Vector3 maxs)
        {
            bool hit = vis.TryTraceHull(a, b, mins, maxs, BspVisibility.GenerationMask,
                out float fraction, out _, out bool startSolid);
            return (hit, fraction, startSolid);
        }

        [Fact]
        public void ABoxWhollyInsideAPropStartsSolid()
        {
            var vis = GroundWithBigProp();

            var up = Sweep(vis, V(0, 0, 50), V(0, 0, 60), BspVisibility.NavTraceMins, BspVisibility.NavTraceMaxs);
            Assert.True(up.StartSolid, "a body wholly inside a prop swept clear");

            // Along, staying inside: no face is ever touched, so nothing but the start says so.
            var along = Sweep(vis, V(0, 0, 9), V(25, 0, 9), BspVisibility.NavTraceMins, BspVisibility.NavTraceMaxs);
            Assert.True(along.StartSolid);
        }

        [Fact]
        public void ABoxBesideAPropIsClear()
        {
            var vis = GroundWithBigProp();

            var up = Sweep(vis, V(150, 0, 50), V(150, 0, 60), BspVisibility.NavTraceMins, BspVisibility.NavTraceMaxs);
            Assert.False(up.StartSolid);
            Assert.False(up.Hit);

            // Above it, resting a unit clear of its top.
            var over = Sweep(vis, V(0, 0, 201), V(0, 0, 210), BspVisibility.NavTraceMins, BspVisibility.NavTraceMaxs);
            Assert.False(over.StartSolid);
            Assert.False(over.Hit);
        }

        [Fact]
        public void AStepInsideAPropIsRefused()
        {
            var vis = GroundWithBigProp();

            Assert.False(Traversability.CanStep(vis, V(0, 0, 0), V(25, 0, 0)),
                "a step was allowed wholly inside a prop");
            Assert.True(Traversability.CanStep(vis, V(150, 0, 0), V(175, 0, 0)));
        }

        /// <summary>
        /// The bodies the connection pass stands at either end of a crossing, and the pocket pass's: a
        /// crouching body on the floor inside a prop is not standing anywhere.
        /// </summary>
        [Fact]
        public void ABodyDoesNotStandInsideAProp()
        {
            var vis = GroundWithBigProp();

            Assert.False(ConnectionBuilder.BodyStands(vis, 0, 0, 0), "a body stood inside a prop");
            Assert.True(ConnectionBuilder.BodyStands(vis, 200, 0, 0));
        }

        /// <summary>
        /// The graze rule (see <see cref="StartSolidTests.AStepAwayFromAWallItsSampleTouchesIsAllowed"/>)
        /// holds against a prop too: a sample on its face steps away from it into the open, and only
        /// into it is refused.
        /// </summary>
        [Fact]
        public void AStepAwayFromAPropFaceItsSampleTouchesIsAllowed()
        {
            var vis = GroundWithBigProp();

            Assert.True(Traversability.CanStep(vis, V(100, 0, 0), V(125, 0, 0)));
            Assert.False(Traversability.CanStep(vis, V(100, 0, 0), V(75, 0, 0)));
        }

        /// <summary>
        /// The other way a box can overlap a convex hull without lying across its surface: by holding
        /// all of it. The box then holds the hull's triangles too, and the triangle sweep already counts
        /// a box overlapping a triangle as starting solid, wherever the triangle lies within it.
        /// </summary>
        [Fact]
        public void ABoxHoldingAWholePropStartsSolid()
        {
            var vis = TestWorlds.Ground();
            vis.AttachStaticProps(StaticProps.FromHulls(LineTraceStartSolidTests.Box(V(-1, -1, 20), V(1, 1, 22))));

            var plate = Sweep(vis, V(0, 0, 9), V(0, 0, 10), V(-16, -16, 0), V(16, 16, 27));
            Assert.True(plate.StartSolid);
        }

        /// <summary>
        /// A box that ends wholly inside a prop had to cross a face to get there, so the sweep stops on
        /// that face. Only the start needed the inside test.
        /// </summary>
        [Fact]
        public void ABoxSweptIntoAPropStopsOnItsFace()
        {
            var vis = GroundWithBigProp();

            var into = Sweep(vis, V(-150, 0, 50), V(0, 0, 50), BspVisibility.NavTraceMins, BspVisibility.NavTraceMaxs);
            Assert.False(into.StartSolid);
            Assert.True(into.Hit);
            Assert.True(into.Fraction < 1f);
            Assert.Equal((150f - 100f - 0.45f) / 150f, into.Fraction, 3);
        }

        /// <summary>Solid ground at z = 0 under a prop slab 50 units thick, off the origin, its underside at <paramref name="bottom"/>.</summary>
        private static BspVisibility GroundUnderPropSlab(float bottom)
        {
            var world = TestWorlds.Ground();
            world.AttachStaticProps(StaticProps.FromHulls(
                LineTraceStartSolidTests.Box(V(-93.18f, -20.13f, bottom), V(341.18f, 414.23f, bottom + 50f))));
            return world;
        }

        /// <summary>
        /// The one way a box did get into a prop without stopping on it: a flat plate, swept straight up
        /// or down, through a level face. See <see cref="TriangleMesh"/>'s MinHalfExtent.
        /// </summary>
        [Fact]
        public void AFlatPlateDoesNotSlipThroughALevelPropFace()
        {
            var vis = GroundUnderPropSlab(30.03f);
            var mins = V(-16, -16, 0);
            var maxs = V(16, 16, 0);

            var up = Sweep(vis, V(7.5f, 70.5f, 9), V(7.5f, 70.5f, 36), mins, maxs);
            Assert.True(up.Hit, "a plate rising under a prop went through its underside");
            Assert.Equal((30.03f - 9f) / 27f, up.Fraction, 2);

            var down = Sweep(vis, V(7.5f, 70.5f, 98.03f), V(7.5f, 70.5f, 79f), mins, maxs);
            Assert.True(down.Hit, "a plate dropping onto a prop went through its top");
            Assert.Equal((98.03f - 80.03f) / 19.03f, down.Fraction, 2);
        }

        /// <summary>
        /// What that cost: a crouching body stood under a prop slab whose underside is lower than its
        /// head. The same slab thirty units higher, clear of a crouching body, is no obstacle.
        /// </summary>
        [Fact]
        public void ACrouchingBodyDoesNotStandWithItsHeadInAProp()
        {
            Assert.False(ConnectionBuilder.BodyStands(GroundUnderPropSlab(30.03f), 7.5f, 70.5f, 0),
                "a crouching body stood with its head inside a prop");
            Assert.True(ConnectionBuilder.BodyStands(GroundUnderPropSlab(60.03f), 7.5f, 70.5f, 0));
        }
    }
}
