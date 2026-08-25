using System.Linq;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That every id in a written mesh points at something.
    ///
    /// These exist because the defect they cover was invisible to every other check in the project. A
    /// mesh full of dangling references round-trips byte for byte, reloads with identical counts, and
    /// scores normally on ground fit and coverage - a reader that resolves ids lazily never asks whether
    /// they resolve. Garry's Mod resolves all of them at load, and answered a generated
    /// rp_downtown_meowy with 9,631 copies of "CNavArea::PostLoad: Corrupt navigation data".
    ///
    /// So the rule is pinned here rather than left to the next in-game run to notice.
    /// </summary>
    public class NavIntegrityTests
    {
        private static NavArea Area(uint id, float x, float y)
        {
            var area = new NavArea { Id = id };

            area.NwCorner[0] = x; area.NwCorner[1] = y; area.NwCorner[2] = 0;
            area.SeCorner[0] = x + 50; area.SeCorner[1] = y + 50; area.SeCorner[2] = 0;

            return area;
        }

        [Fact]
        public void ALadderNamingAnAreaThatIsNotThereIsCleared()
        {
            // The other direction of the area/ladder relationship. An area's list of ladders was
            // already swept; a ladder's five area ids were not, so a ladder built against an area a
            // later pass then discarded kept naming it - the same dangling reference, and the engine
            // refuses it at load the same way.
            var nav = new NavFile();
            nav.Areas.Add(Area(1, 0, 0));

            nav.Ladders.Add(new NavLadder
            {
                Id = 1,
                BottomAreaId = 1,       // real
                TopForwardAreaId = 77,  // gone
                TopLeftAreaId = 78,     // gone
            });

            var pruned = NavIntegrity.Prune(nav);

            Assert.Equal(2, pruned.LadderEndpoints);
            Assert.Equal(1u, nav.Ladders[0].BottomAreaId);
            Assert.Equal(0u, nav.Ladders[0].TopForwardAreaId);
            Assert.Equal(0u, nav.Ladders[0].TopLeftAreaId);
        }

        [Fact]
        public void AConnectionToAnAreaThatIsNotThereIsRemoved()
        {
            var nav = new NavFile();
            var a = Area(1, 0, 0);

            a.Connections[0].Add(2);        // gone
            a.Connections[1].Add(99);       // never existed
            nav.Areas.Add(a);

            var pruned = NavIntegrity.Prune(nav);

            Assert.Equal(2, pruned.Connections);
            Assert.All(a.Connections, list => Assert.Empty(list));
        }

        [Fact]
        public void AnAreaConnectedToItselfIsRemoved()
        {
            // Merging produces these: absorb your own neighbour and any link between the two becomes a
            // self-reference, which the engine treats as corrupt just like a missing one.
            var nav = new NavFile();
            var a = Area(1, 0, 0);

            a.Connections[0].Add(1);
            nav.Areas.Add(a);

            Assert.Equal(1, NavIntegrity.Prune(nav).SelfConnections);
            Assert.Empty(a.Connections[0]);
        }

        [Fact]
        public void DuplicateConnectionsInOneDirectionCollapse()
        {
            var nav = new NavFile();
            var a = Area(1, 0, 0);
            var b = Area(2, 50, 0);

            a.Connections[0].Add(2);
            a.Connections[0].Add(2);
            nav.Areas.Add(a);
            nav.Areas.Add(b);

            Assert.Equal(1, NavIntegrity.Prune(nav).Duplicates);
            Assert.Single(a.Connections[0]);
        }

        [Fact]
        public void LiveReferencesAreLeftAlone()
        {
            var nav = new NavFile();
            var a = Area(1, 0, 0);
            var b = Area(2, 50, 0);

            a.Connections[0].Add(2);
            b.Connections[2].Add(1);
            nav.Areas.Add(a);
            nav.Areas.Add(b);

            Assert.Equal(0, NavIntegrity.Prune(nav).Total);
            Assert.Single(a.Connections[0]);
            Assert.Single(b.Connections[2]);
        }

        [Fact]
        public void StaleVisibilityFromAnEarlierAreaSetIsRemoved()
        {
            // The big one by volume. Regenerating areas leaves every stored visibility pair naming ids
            // that no longer exist - two million of them on rp_downtown_meowy - until the visibility
            // pass rewrites them. Anything saved before that point carries them.
            var nav = new NavFile();
            var a = Area(1, 0, 0);

            a.VisibleAreas.Add(new VisibleArea { AreaId = 1, Attributes = 1 });
            a.VisibleAreas.Add(new VisibleArea { AreaId = 777, Attributes = 1 });
            nav.Areas.Add(a);

            Assert.Equal(1, NavIntegrity.Prune(nav).Visibility);
            Assert.Single(a.VisibleAreas);
            Assert.Equal(1u, a.VisibleAreas[0].AreaId);
        }

        [Fact]
        public void AnInheritedVisibilityParentThatIsNotThereIsCleared()
        {
            // The other area id in the record, and the one the sweep used to walk straight past. The
            // engine resolves it at load and takes that area's visible set as the base for this one's,
            // so a parent that no longer exists fails exactly like a connection to a deleted area.
            var nav = new NavFile();
            var a = Area(1, 0, 0);

            a.InheritVisibilityFrom = 77;
            nav.Areas.Add(a);

            var pruned = NavIntegrity.Prune(nav);

            Assert.Equal(1, pruned.Inherits);
            Assert.Equal(0u, a.InheritVisibilityFrom);
        }

        [Fact]
        public void InheritingFromZeroIsNotAMiss()
        {
            // Zero is the encoding's "no parent", not an id. VisibilityCompressor writes it for every
            // area that stores its own list outright, which on a compressed mesh is most of them -
            // treating it as dangling would report every one of those as corruption.
            var nav = new NavFile();
            var a = Area(1, 0, 0);

            a.InheritVisibilityFrom = 0;
            nav.Areas.Add(a);

            Assert.Equal(0, NavIntegrity.Prune(nav).Inherits);
        }

        [Fact]
        public void AnAreaInheritingFromItselfIsCleared()
        {
            // Resolves to a cycle rather than a base set, so it is corrupt even though the id is one
            // that exists - which is why containment alone is not the test.
            var nav = new NavFile();
            var a = Area(1, 0, 0);

            a.InheritVisibilityFrom = 1;
            nav.Areas.Add(a);

            Assert.Equal(1, NavIntegrity.Prune(nav).Inherits);
            Assert.Equal(0u, a.InheritVisibilityFrom);
        }
    }

    /// <summary>
    /// That merging moves references rather than orphaning them.
    ///
    /// Pruning would make the file loadable either way, so what these pin is the half that pruning
    /// cannot fix: an absorbed area's own connections have to survive onto whoever absorbed it, or the
    /// merged area inherits its neighbour's footprint without its routes and the mesh quietly loses
    /// connectivity while looking complete.
    /// </summary>
    public class AreaMergeReferenceTests
    {
        private static NavArea Strip(uint id, float x0, float x1)
        {
            var area = new NavArea { Id = id };

            area.NwCorner[0] = x0; area.NwCorner[1] = 0; area.NwCorner[2] = 0;
            area.SeCorner[0] = x1; area.SeCorner[1] = 50; area.SeCorner[2] = 0;
            area.NeZ = 0; area.SwZ = 0;

            return area;
        }

        [Fact]
        public void ReferencesToAnAbsorbedAreaFollowItIntoItsSuccessor()
        {
            // west and east are mergeable neighbours; far points at east and must end up pointing at
            // west once east is gone.
            var nav = new NavFile();
            var west = Strip(1, 0, 50);
            var east = Strip(2, 50, 100);
            var far = Strip(3, 500, 550);

            far.Connections[0].Add(2);
            east.Connections[1].Add(3);

            nav.Areas.Add(west);
            nav.Areas.Add(east);
            nav.Areas.Add(far);

            AreaMerger.Merge(nav);

            Assert.DoesNotContain(nav.Areas, a => a.Id == 2);

            // Nothing dangles, and the link is repointed rather than dropped.
            Assert.Equal(0, NavIntegrity.Prune(nav).Total);
            Assert.Contains(1u, far.Connections[0]);
        }

        [Fact]
        public void TheAbsorbedAreasOwnConnectionsAreCarriedAcross()
        {
            var nav = new NavFile();
            var west = Strip(1, 0, 50);
            var east = Strip(2, 50, 100);
            var far = Strip(3, 500, 550);

            east.Connections[1].Add(3);

            nav.Areas.Add(west);
            nav.Areas.Add(east);
            nav.Areas.Add(far);

            AreaMerger.Merge(nav);

            var survivor = nav.Areas.Single(a => a.Id == 1);

            Assert.Contains(3u, survivor.Connections.SelectMany(c => c));
            Assert.Equal(0, NavIntegrity.Prune(nav).Total);
        }
    }
}
