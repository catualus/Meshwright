using System.Collections.Generic;
using System.Linq;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That a generated area cut off from everything is removed when no body fits in it, and kept
    /// whenever one does, or whenever a seed or a ladder put it there.
    ///
    /// The flood gives a cell to any floor a column holds, so a slot between two props a body could
    /// never enter still becomes an area. Once the connection passes stopped linking it through the
    /// props it was an island no spawn reaches, counted with the ground the movement pass failed to
    /// join. The world here is the smallest version: flat ground, and a slot 10 units wide between two
    /// walls, which a crouching body 32 units wide does not fit in anywhere.
    /// </summary>
    public class PocketRemoverTests
    {
        /// <summary>The slot's floor: 10 units wide between walls at x = 0 and x = 10.</summary>
        private static NavArea Slot(uint id) => TestWorlds.Area(id, 0f, 0f, 10f, 25f, 0f);

        /// <summary>Two walls 10 units apart, floor to well over a body's height, either side of the
        /// slot, on open ground.</summary>
        private static BspVisibility Walls() => StartSolidTests.Boxes(
            (new BspFile.Vector3(-100, -100, 0), new BspFile.Vector3(0, 100, 200)),
            (new BspFile.Vector3(10, -100, 0), new BspFile.Vector3(110, 100, 200)));

        /// <summary>
        /// Open ground far from the walls: a row of areas 1 to 6, each joined both ways to the next,
        /// with a spawn on area 1 - the mesh the slot is cut off from, too large a group to be a pocket.
        /// </summary>
        private static NavFile Mesh(params NavArea[] more)
        {
            var nav = new NavFile();

            for (uint id = 1; id <= 6; id++)
            {
                float x = 500f + (id - 1) * 50f;
                var area = TestWorlds.Area(id, x, 0f, x + 50f, 50f, 0f);

                if (id > 1) area.Connections[NavGeometry.West].Add(id - 1);
                if (id < 6) area.Connections[NavGeometry.East].Add(id + 1);

                nav.Areas.Add(area);
            }

            nav.Areas.AddRange(more);
            return nav;
        }

        private static readonly BspFile.Vector3 Spawn = new(525, 25, 0);

        private static PocketRemover.Result Remove(NavFile nav, BspVisibility vis, uint firstGeneratedId = 1,
            params BspFile.Vector3[] seeds)
            => PocketRemover.Remove(nav, vis, firstGeneratedId, [Spawn], [Spawn, .. seeds]);

        [Fact]
        public void ALinklessSlotNoBodyFitsInIsRemoved()
        {
            var vis = Walls();
            var nav = Mesh(Slot(10));

            Assert.False(PocketRemover.HasRoom(vis, Slot(10)));

            var removed = Remove(nav, vis);

            Assert.Equal(1, removed.Removed);
            Assert.Equal(1, removed.Pockets);
            Assert.Equal([10u], removed.RemovedIds);
            Assert.Equal([1u, 2u, 3u, 4u, 5u, 6u], nav.Areas.Select(a => a.Id));
        }

        /// <summary>
        /// Every reference the rest of the mesh can hold to a removed area goes with it: visibility
        /// entries and the area a list inherits from, encounters that start or end on it, and spot
        /// sightings naming the hiding spots it took with it.
        /// </summary>
        [Fact]
        public void EveryReferenceToARemovedPocketGoesWithIt()
        {
            var vis = Walls();
            var slot = Slot(10);
            slot.HidingSpots.Add(new HidingSpot { Id = 30 });

            var nav = Mesh(slot);
            var a = nav.Areas[0];
            a.HidingSpots.Add(new HidingSpot { Id = 10 });
            a.VisibleAreas.Add(new VisibleArea { AreaId = 2, Attributes = 1 });
            a.VisibleAreas.Add(new VisibleArea { AreaId = 10, Attributes = 1 });
            nav.Areas[1].InheritVisibilityFrom = 10;

            var toSlot = new SpotEncounter { FromAreaId = 2, ToAreaId = 10 };
            var across = new SpotEncounter { FromAreaId = 2, ToAreaId = 1 };
            across.Spots.Add((10, 0));
            across.Spots.Add((30, 128));
            a.Encounters.Add(toSlot);
            a.Encounters.Add(across);

            Assert.Equal(1, Remove(nav, vis).Removed);

            Assert.Equal([2u], a.VisibleAreas.Select(v => v.AreaId));
            Assert.Equal(0u, nav.Areas[1].InheritVisibilityFrom);
            Assert.Equal([across], a.Encounters);
            Assert.Equal([10u], across.Spots.Select(s => s.SpotId));
            Assert.Equal(0, NavIntegrity.Prune(nav).Total);
        }

        /// <summary>The control: the same link-less area on open ground, where a body fits, is ground
        /// the movement pass failed to join and stays to be counted.</summary>
        [Fact]
        public void ALinklessAreaABodyFitsInIsKept()
        {
            var vis = TestWorlds.Ground();
            var nav = Mesh(Slot(10));

            Assert.True(PocketRemover.HasRoom(vis, Slot(10)));

            var removed = Remove(nav, vis);

            Assert.Equal(0, removed.Removed);
            Assert.Equal(1, removed.Roomy);
            Assert.Contains(nav.Areas, a => a.Id == 10);
        }

        /// <summary>The slot, but where the flood started: a spawn stands in it.</summary>
        [Fact]
        public void ASeededPocketIsKept()
        {
            var vis = Walls();
            var nav = Mesh(Slot(10));

            var removed = PocketRemover.Remove(nav, vis, 1, [Spawn, new(5, 12, 0)], [Spawn, new(5, 12, 0)]);

            Assert.Equal(0, removed.Removed);
            Assert.Equal(1, removed.Seeded);
            Assert.Contains(nav.Areas, a => a.Id == 10);
        }

        /// <summary>The slot at a ladder's foot, seeded from the ladder brush.</summary>
        [Fact]
        public void APocketAtALadderSeedIsKept()
        {
            var vis = Walls();
            var nav = Mesh(Slot(10));

            Assert.Equal(0, Remove(nav, vis, 1, new BspFile.Vector3(5, -20, 0)).Removed);
            Assert.Contains(nav.Areas, a => a.Id == 10);
        }

        /// <summary>The slot as a ladder's bottom area: removing it would leave the ladder's foot
        /// pointing at nothing.</summary>
        [Fact]
        public void APocketALadderNamesIsKept()
        {
            var vis = Walls();
            var nav = Mesh(Slot(10));
            nav.Ladders.Add(new NavLadder { Id = 1, BottomAreaId = 10 });

            Assert.Equal(0, Remove(nav, vis).Removed);
            Assert.Contains(nav.Areas, a => a.Id == 10);
        }

        /// <summary>A pocket linked to the mesh is not cut off, however little room it has.</summary>
        [Fact]
        public void APocketWithALinkToTheMeshIsKept()
        {
            var vis = Walls();
            var slot = Slot(10);
            slot.Connections[NavGeometry.East].Add(1);
            var nav = Mesh(slot);

            Assert.Equal(0, Remove(nav, vis).Removed);
            Assert.Contains(nav.Areas, a => a.Id == 10);
        }

        /// <summary>Nothing that was in the mesh before the run is touched: here the whole mesh was.</summary>
        [Fact]
        public void AnAuthoredPocketIsKept()
        {
            var vis = Walls();
            var nav = Mesh(Slot(10));

            Assert.Equal(0, Remove(nav, vis, firstGeneratedId: 11).Removed);
            Assert.Contains(nav.Areas, a => a.Id == 10);
        }

        /// <summary>
        /// Open ground with a prop over it like a low table: a slab from 10 to 22 units up, well wider
        /// than the slot's footprint. Ten units under it is no room for a body; its top is.
        /// </summary>
        private static BspVisibility UnderATable()
        {
            var vis = TestWorlds.Ground();
            vis.AttachStaticProps(StaticProps.FromHulls(LineTraceStartSolidTests.Box(
                new BspFile.Vector3(-100, -100, 10), new BspFile.Vector3(110, 125, 22))));
            return vis;
        }

        /// <summary>
        /// The regression, ctf_2fort's shape: an area on the floor under a prop, where no body fits, is
        /// not given room for the body that stands on the prop's top. The floor search starts a step over
        /// the area, inside the prop, and reports the prop's underside; a body rested over that comes to
        /// rest on the top, 22 units over the area it was asked about.
        /// </summary>
        [Fact]
        public void ABodyOnAPropOverTheAreaIsNoRoomInIt()
        {
            var vis = UnderATable();
            var nav = Mesh(Slot(10));

            Assert.False(PocketRemover.HasRoom(vis, Slot(10)));
            Assert.Equal([10u], Remove(nav, vis).RemovedIds);
        }

        /// <summary>The control: the same prop with the area on its top, where a body does stand.</summary>
        [Fact]
        public void AnAreaOnAPropsTopHasRoom()
        {
            var vis = UnderATable();
            var top = TestWorlds.Area(10, 0f, 0f, 10f, 25f, 22f);

            Assert.True(PocketRemover.HasRoom(vis, top));
        }
    }
}
