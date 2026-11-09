using System.Collections.Generic;
using System.Linq;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That the pairs the clip reshapes are tested again on their new shapes.
    ///
    /// Connections are built before the clip, deliberately, and nothing re-tested them afterwards. So
    /// a pair that only abuts once the clip has pulled one area back out from under the other was
    /// never linked, however plain the drop between them.
    /// </summary>
    public class RelinkTests
    {
        private const uint Upper = 1, Lower = 2;

        /// <summary>
        /// A block 80 units high with its east face at x = 100, an area on its top, and an area on the
        /// ground east of it that runs 20 units back underneath it - the way a generated area overshoots
        /// into whatever stops it. Overlapped by 20 units, the two are one area over another rather
        /// than neighbours, so the build has nothing to link. The clip pulls the lower one back to the
        /// face, and from there the drop off the block onto it is an ordinary one.
        /// </summary>
        [Fact]
        public void ADropOntoAnAreaTheClipPullsOutFromUnderALedgeIsLinked()
        {
            var world = StartSolidTests.Boxes((new(-400, -400, 0), new(100, 400, 80)));
            var nav = new NavFile();
            nav.Areas.Add(TestWorlds.Area(Upper, 0f, 0f, 100f, 100f, 80f));
            nav.Areas.Add(TestWorlds.Area(Lower, 80f, 0f, 200f, 100f, 0f));

            ConnectionBuilder.Build(nav, world);
            Assert.False(Linked(nav, Upper, Lower), "the build linked areas that overlap by 20 units");

            var clipped = AreaClipper.Clip(nav, world, AreaGenerator.StepSize);
            Assert.Contains(Lower, clipped.Reshaped);

            var relinked = ConnectionBuilder.Relink(nav, world, clipped.Reshaped);

            Assert.True(Linked(nav, Upper, Lower), "the drop off the ledge was never linked");
            Assert.Equal(1, relinked.Drops);
            Assert.Contains(Lower, Area(nav, Upper).Connections[NavGeometry.East]);
        }

        /// <summary>
        /// The control: the same pair, with the clip never told it moved anything, stays unlinked. A
        /// pair whose shapes are what they were at the build gets the build's answer and is not tested
        /// twice.
        /// </summary>
        [Fact]
        public void APairNeitherOfWhoseAreasMovedIsNotTestedAgain()
        {
            var world = StartSolidTests.Boxes((new(-400, -400, 0), new(100, 400, 80)));
            var nav = new NavFile();
            nav.Areas.Add(TestWorlds.Area(Upper, 0f, 0f, 100f, 100f, 80f));
            nav.Areas.Add(TestWorlds.Area(Lower, 100f, 0f, 200f, 100f, 0f));

            var relinked = ConnectionBuilder.Relink(nav, world, new HashSet<uint> { 99 });

            Assert.Equal(0, relinked.Total);
            Assert.False(Linked(nav, Upper, Lower));
        }

        /// <summary>
        /// Only adds. A link made before the clip survives a relink that would refuse it now, because
        /// the build-first order exists for the links the clip's new gaps would cost.
        /// </summary>
        [Fact]
        public void ALinkMadeBeforeTheClipIsNeverRemoved()
        {
            var world = StartSolidTests.Boxes((new(-400, 95, 0), new(400, 105, 200)));   // a wall between them
            var nav = new NavFile();
            var first = TestWorlds.Area(1, 0f, 0f, 100f, 100f, 0f);
            var second = TestWorlds.Area(2, 0f, 100f, 100f, 200f, 0f);
            first.Connections[NavGeometry.South].Add(2);
            nav.Areas.Add(first);
            nav.Areas.Add(second);

            ConnectionBuilder.Relink(nav, world, new HashSet<uint> { 1, 2 });

            Assert.Contains(2u, first.Connections[NavGeometry.South]);
            Assert.DoesNotContain(1u, second.Connections[NavGeometry.North]);
        }

        /// <summary>
        /// Two areas 24 units wide meet along y = 100, with two thin posts astride the edge at x 3..5 and
        /// x 19..21. The build's middle crossing is three lines at x = -4, 12 and 28, which pass between
        /// and beside the posts, so the build links them. A body 32 units wide fits nowhere along that
        /// edge - wherever it is centred, one post or the other is inside it - and the relink, sweeping
        /// a body through the middle of a span narrower than one, refuses it.
        ///
        /// That is the shape the clip leaves against walls: a strip pulled back to the face it ran into,
        /// its shared span narrower than a body, with lines either side of its middle grazing the face.
        /// </summary>
        [Fact]
        public void ARelinkSweepsABodyThroughTheMiddleOfASpanNarrowerThanOne()
        {
            var world = StartSolidTests.Boxes(
                (new(3, 95, 0), new(5, 105, 200)),
                (new(19, 95, 0), new(21, 105, 200)));

            var built = TwoStrips();
            ConnectionBuilder.Build(built, world);
            Assert.True(Linked(built, 1, 2), "the control: the build's lines miss both posts");

            var relinked = TwoStrips();
            ConnectionBuilder.Relink(relinked, world, new HashSet<uint> { 1, 2 });
            Assert.False(Linked(relinked, 1, 2), "a body was taken through two posts 14 units apart");
            Assert.False(Linked(relinked, 2, 1), "a body was taken through two posts 14 units apart");
        }

        /// <summary>
        /// The same edge with nothing on it: the sweep is not what refused it, so the relink links it.
        /// </summary>
        [Fact]
        public void ARelinkStillLinksANarrowSpanABodyFitsThrough()
        {
            var world = StartSolidTests.Boxes();
            var nav = TwoStrips();

            ConnectionBuilder.Relink(nav, world, new HashSet<uint> { 1 });

            Assert.True(Linked(nav, 1, 2));
            Assert.True(Linked(nav, 2, 1));
        }

        private static NavFile TwoStrips()
        {
            var nav = new NavFile();
            nav.Areas.Add(TestWorlds.Area(1, 0f, 0f, 24f, 100f, 0f));
            nav.Areas.Add(TestWorlds.Area(2, 0f, 100f, 24f, 200f, 0f));
            return nav;
        }

        private static NavArea Area(NavFile nav, uint id) => nav.Areas.Single(a => a.Id == id);

        private static bool Linked(NavFile nav, uint from, uint to) =>
            Area(nav, from).Connections.Any(list => list.Contains(to));
    }
}
