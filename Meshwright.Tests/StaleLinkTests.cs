using System.Collections.Generic;
using System.Linq;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That a link made before the clip is removed when the clip leaves no way across it, and only then.
    ///
    /// Connections are built before the clip on purpose, so that a crossing survives the clip opening a gap
    /// where two areas used to touch. A link the builder's own test refuses on the new shapes is therefore
    /// not wrong by that alone; it goes only when a body standing at both ends gets across nowhere.
    /// </summary>
    public class StaleLinkTests
    {
        /// <summary>
        /// Two rooms meeting along y = 100 with a wall from y 96 to 104 between them. Before the clip both
        /// areas ran to y = 100, into the wall, and were linked; the clip pulled each back to its face of
        /// the wall. A body stands on both sides and crosses nowhere, so both links go.
        /// </summary>
        [Fact]
        public void ALinkTheClipLeftAcrossAWallIsRemoved()
        {
            var world = StartSolidTests.Boxes((new(-400, 96, 0), new(400, 104, 200)));
            var nav = Linked(Area(1, 0, 0, 100, 96), Area(2, 0, 104, 100, 200));
            var before = Before((1, 0, 0, 100, 100), (2, 0, 100, 100, 200));

            var dropped = ConnectionBuilder.DropStale(nav, world, before.Keys.ToHashSet(), before);

            Assert.Equal(2, dropped.Removed);
            Assert.False(Linked(nav, 1, 2), "a link was left through the wall");
            Assert.False(Linked(nav, 2, 1), "a link was left through the wall");
        }

        /// <summary>
        /// The same through the clip itself, with a wall 60 units thick that both areas ran into. The
        /// clip pulls them back to its faces, 60 units apart - too far for the builder to call them
        /// neighbours, so only the footprints the clip records tell this pass they were, and where the
        /// crossing was.
        /// </summary>
        [Fact]
        public void TheClipRecordsTheFootprintsALinkItPulledApartWasMadeOn()
        {
            var world = StartSolidTests.Boxes((new(-400, 70, 0), new(400, 130, 200)));
            var nav = Linked(Area(1, 0, 0, 100, 100), Area(2, 0, 100, 100, 200));

            var clipped = AreaClipper.Clip(nav, world, AreaGenerator.StepSize);
            Assert.Equal(new NavGeometry.Bounds(0, 0, 100, 100), clipped.Before[1]);
            Assert.Equal(new NavGeometry.Bounds(0, 100, 100, 200), clipped.Before[2]);
            Assert.True(NavGeometry.GetBounds(nav.Areas[1]).MinY - NavGeometry.GetBounds(nav.Areas[0]).MaxY > 40f,
                "the clip did not pull the areas apart");

            var dropped = ConnectionBuilder.DropStale(nav, world, clipped.Reshaped, clipped.Before);

            Assert.Equal(2, dropped.Removed);
            Assert.False(Linked(nav, 1, 2), "a link was left through the wall");
        }

        /// <summary>
        /// The build-first case: the clip moved one area 50 units back from its neighbour, further than
        /// the builder accepts as touching, but the floor runs on between them. The builder now refuses
        /// both links; a body walks across, so they stay.
        /// </summary>
        [Fact]
        public void AGapTheClipOpenedOverOpenFloorKeepsItsLinks()
        {
            var world = StartSolidTests.Boxes();
            var nav = Linked(Area(1, 0, 0, 100, 100), Area(2, 0, 150, 100, 250));
            var before = Before((2, 0, 100, 100, 250));

            var dropped = ConnectionBuilder.DropStale(nav, world, before.Keys.ToHashSet(), before);

            Assert.Equal(2, dropped.Refused);
            Assert.Equal(0, dropped.Removed);
            Assert.True(Linked(nav, 1, 2));
            Assert.True(Linked(nav, 2, 1));
        }

        /// <summary>
        /// A 10 unit square in a slot 10 units wide between two blocks, off open floor. No body stands in
        /// it, so no crossing into it gets a body across, but that is a fact about the area, not about the
        /// crossing: a link to such a spot is left as the build made it rather than refused for the
        /// shape of the area at its end.
        /// </summary>
        [Fact]
        public void ALinkToASpotNoBodyStandsInIsLeftAlone()
        {
            var world = StartSolidTests.Boxes(
                (new(-200, 100, 0), new(0, 300, 200)),
                (new(10, 100, 0), new(200, 300, 200)));
            var nav = Linked(Area(1, -50, 0, 60, 100), Area(2, 0, 100, 10, 110));
            var before = Before((2, -5, 100, 20, 125));

            var dropped = ConnectionBuilder.DropStale(nav, world, before.Keys.ToHashSet(), before);

            Assert.Equal(2, dropped.Refused);
            Assert.Equal(0, dropped.Removed);
            Assert.True(Linked(nav, 1, 2));
            Assert.True(Linked(nav, 2, 1));
        }

        /// <summary>
        /// The same kind of spot shut off by a pane: a strip 16 units deep between a wall and a pane a unit
        /// thick, which before the clip ran five units past the pane to meet the room on its other side. No
        /// body stands in the strip, but that is not why nothing gets across - a line through the middle of
        /// the crossing runs into the pane wherever it is tried - so both links go. Window sills behind
        /// glass on the roleplay maps are this shape.
        /// </summary>
        [Fact]
        public void ALinkThroughAPaneToASpotNoBodyStandsInIsRemoved()
        {
            var world = StartSolidTests.Boxes(
                (new(-200, 100, 0), new(200, 101, 200)),
                (new(-200, 117, 0), new(200, 300, 200)));
            var nav = Linked(Area(1, 0, 0, 100, 100), Area(2, 0, 101, 100, 117));
            var before = Before((2, 0, 95, 100, 117));

            var dropped = ConnectionBuilder.DropStale(nav, world, before.Keys.ToHashSet(), before);

            Assert.Equal(2, dropped.Removed);
            Assert.Equal(2, dropped.RemovedWalled);
            Assert.False(Linked(nav, 1, 2), "a link was left through the pane");
            Assert.False(Linked(nav, 2, 1), "a link was left through the pane");
        }

        /// <summary>
        /// A drop off a deck 140 units up onto a slot 20 units wide underneath the deck itself, the deck
        /// running on over the landing. No body stands in the slot, and the walker has no edge to step off
        /// short of the landing, so the drop goes - as off the wall tops above the shop windows on the
        /// roleplay maps, linked before the clip to window displays that then ran out past the glass.
        /// </summary>
        [Fact]
        public void ADropOntoASpotUnderTheFloorItLeavesIsRemoved()
        {
            var world = StartSolidTests.Boxes(
                (new(-400, 0, 120), new(400, 200, 140)),
                (new(-400, 0, 0), new(400, 80, 120)),
                (new(-400, 100, 0), new(400, 200, 120)));
            var nav = LinkedEast(TestWorlds.Area(1, -100, 80, 0, 100, 140f), TestWorlds.Area(2, 0, 80, 100, 100, 0f));
            var before = Before((2, 0, 80, 100, 100));

            var dropped = ConnectionBuilder.DropStale(nav, world, before.Keys.ToHashSet(), before);

            Assert.Equal(1, dropped.Refused);
            Assert.Equal(1, dropped.RemovedWalled);
            Assert.False(Linked(nav, 1, 2), "a drop was left through the deck");
        }

        /// <summary>
        /// The control: the deck ends at the edge, and a step 60 units high stands in the 20 unit gap below
        /// it, before a slot no body stands in. The fall is refused because it lands on the step, but a
        /// walker steps off that again, so nothing in the crossing says it is shut and the link is left as
        /// the build made it.
        /// </summary>
        [Fact]
        public void ADropOntoAStepPartwayDownToASpotNoBodyStandsInIsLeftAlone()
        {
            var world = StartSolidTests.Boxes(
                (new(-400, 0, 120), new(0, 200, 140)),
                (new(0, 80, 0), new(14, 100, 60)),
                (new(0, 0, 0), new(400, 80, 120)),
                (new(0, 100, 0), new(400, 200, 120)));
            var nav = LinkedEast(TestWorlds.Area(1, -100, 80, 0, 100, 140f), TestWorlds.Area(2, 20, 80, 120, 100, 0f));
            var before = Before((2, 20, 80, 120, 100));

            var dropped = ConnectionBuilder.DropStale(nav, world, before.Keys.ToHashSet(), before);

            Assert.Equal(1, dropped.Refused);
            Assert.Equal(0, dropped.Removed);
            Assert.True(Linked(nav, 1, 2));
        }

        /// <summary>
        /// A link between two areas that did not face each other before the clip is not the builder's - a
        /// bridge the jump-area stitcher made, or a hand edit - and is not judged by the builder's
        /// geometry, wall or no wall.
        /// </summary>
        [Fact]
        public void ALinkTheBuilderCouldNotHaveMadeIsLeftAlone()
        {
            var world = StartSolidTests.Boxes((new(-400, 140, 0), new(400, 160, 200)));
            var nav = Linked(Area(1, 0, 0, 100, 96), Area(2, 0, 204, 100, 300));
            var before = Before((1, 0, 0, 100, 100), (2, 0, 200, 100, 300));

            var dropped = ConnectionBuilder.DropStale(nav, world, before.Keys.ToHashSet(), before);

            Assert.Equal(0, dropped.Tested);
            Assert.True(Linked(nav, 1, 2));
            Assert.True(Linked(nav, 2, 1));
        }

        /// <summary>
        /// The control for the first: the same pair with nothing between the rooms is still linked by the
        /// builder's own test, and is not touched.
        /// </summary>
        [Fact]
        public void ALinkTheBuilderStillAcceptsIsKept()
        {
            var world = StartSolidTests.Boxes();
            var nav = Linked(Area(1, 0, 0, 100, 96), Area(2, 0, 104, 100, 200));
            var before = Before((1, 0, 0, 100, 100), (2, 0, 100, 100, 200));

            var dropped = ConnectionBuilder.DropStale(nav, world, before.Keys.ToHashSet(), before);

            Assert.Equal(2, dropped.Tested);
            Assert.Equal(0, dropped.Refused);
            Assert.True(Linked(nav, 1, 2));
        }

        private static NavArea Area(uint id, float minX, float minY, float maxX, float maxY)
            => TestWorlds.Area(id, minX, minY, maxX, maxY, 0f);

        /// <summary>The two areas, linked both ways across the first's south edge.</summary>
        private static NavFile Linked(NavArea north, NavArea south)
        {
            north.Connections[NavGeometry.South].Add(south.Id);
            south.Connections[NavGeometry.North].Add(north.Id);

            var nav = new NavFile();
            nav.Areas.Add(north);
            nav.Areas.Add(south);
            return nav;
        }

        /// <summary>The two areas, the first linked to the second across its east edge, one way only.</summary>
        private static NavFile LinkedEast(NavArea west, NavArea east)
        {
            west.Connections[NavGeometry.East].Add(east.Id);

            var nav = new NavFile();
            nav.Areas.Add(west);
            nav.Areas.Add(east);
            return nav;
        }

        private static Dictionary<uint, NavGeometry.Bounds> Before(
            params (uint Id, float MinX, float MinY, float MaxX, float MaxY)[] areas)
            => areas.ToDictionary(a => a.Id, a => new NavGeometry.Bounds(a.MinX, a.MinY, a.MaxX, a.MaxY));

        private static bool Linked(NavFile nav, uint from, uint to) =>
            nav.Areas.Single(a => a.Id == from).Connections.Any(list => list.Contains(to));
    }
}
