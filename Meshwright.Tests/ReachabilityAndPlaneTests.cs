using System.Linq;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That the merge only joins areas describing one surface.
    ///
    /// The condition it used to apply was that the two heights along the shared seam agree, which is a
    /// weaker and different claim: a flight of stairs meets the landing at its top at exactly the
    /// landing's height, so a seam test passes it, and the merged quad is then one long ramp from the
    /// foot of the flight to the far side of the landing. The visible result in game is a slope where a
    /// staircase is, cutting down through the treads and hanging in the air over the landing.
    /// </summary>
    public class AreaMergeCoplanarityTests
    {
        private static NavArea Quad(uint id, float x0, float y0, float x1, float y1,
            float nw, float ne, float se, float sw)
        {
            var a = new NavArea { Id = id };

            a.NwCorner[0] = x0; a.NwCorner[1] = y0; a.NwCorner[2] = nw;
            a.SeCorner[0] = x1; a.SeCorner[1] = y1; a.SeCorner[2] = se;
            a.NeZ = ne; a.SwZ = sw;

            return a;
        }

        [Fact]
        public void AFlightDoesNotMergeIntoTheLandingAtItsTop()
        {
            var nav = new NavFile();

            // Climbing east, 0 to 96 over 100 units.
            nav.Areas.Add(Quad(1, 0, 0, 100, 100, 0, 96, 96, 0));

            // Flat, at the height the flight arrives at: the seam heights agree exactly.
            nav.Areas.Add(Quad(2, 100, 0, 200, 100, 96, 96, 96, 96));

            var result = AreaMerger.Merge(nav);

            Assert.Equal(0, result.Merges);
            Assert.Equal(2, nav.Areas.Count);
            Assert.True(result.NotCoplanar > 0);

            // The height test cannot be what refused it - that is the whole point.
            Assert.Equal(0, result.HeightMismatch);
        }

        [Fact]
        public void TwoStretchesOfTheSameSlopeStillMerge()
        {
            var nav = new NavFile();

            // One continuous ramp at the same gradient, cut in two.
            nav.Areas.Add(Quad(1, 0, 0, 100, 100, 0, 48, 48, 0));
            nav.Areas.Add(Quad(2, 100, 0, 200, 100, 48, 96, 96, 48));

            var result = AreaMerger.Merge(nav);

            Assert.Equal(1, result.Merges);
            Assert.Single(nav.Areas);

            // And the merged quad still describes the ramp: the seam is where it always was.
            Assert.Equal(48f, NavGeometry.SurfaceZ(nav.Areas[0], 100, 50), 1);
        }

        [Fact]
        public void FlatGroundStillMerges()
        {
            var nav = new NavFile();
            nav.Areas.Add(Quad(1, 0, 0, 100, 100, 0, 0, 0, 0));
            nav.Areas.Add(Quad(2, 100, 0, 200, 100, 0, 0, 0, 0));

            Assert.Equal(1, AreaMerger.Merge(nav).Merges);
        }

        /// <summary>
        /// The edge index is built once a pass and merging moves edges, so an area that has absorbed a
        /// neighbour perpendicular to a seam is still filed under the span it had before. Taking it on
        /// that stale key merges two areas that no longer share a full edge, and because the survivor
        /// keeps its own span, the ground the partner had grown into is deleted with the partner.
        ///
        /// Ordered so B absorbs C southward first, and only then does A look B up by B's original,
        /// now-wrong, Y span.
        /// </summary>
        [Fact]
        public void MergingNeverDropsGroundAPartnerHasGrownInto()
        {
            var nav = new NavFile();

            nav.Areas.Add(Quad(2, 50, 0, 100, 50, 0, 0, 0, 0));     // B
            nav.Areas.Add(Quad(3, 50, 50, 100, 100, 0, 0, 0, 0));   // C, south of B
            nav.Areas.Add(Quad(1, 0, 0, 50, 50, 0, 0, 0, 0));       // A, west of B's old span

            float before = nav.Areas.Sum(Footprint);

            AreaMerger.Merge(nav);

            Assert.Equal(before, nav.Areas.Sum(Footprint), 1);
        }

        /// <summary>
        /// Areas that were in the mesh before the run started are not the merge's to touch.
        ///
        /// Valve never faces this: nav_generate wipes the mesh first, so every area their
        /// MergeGeneratedAreas sees is one the same run just made. Here the pass is handed whatever was
        /// loaded, and with -generateareas that includes a mesh someone edited in game - where fusing
        /// two hand-placed areas retires an id and quietly changes work a person did on purpose.
        /// </summary>
        [Fact]
        public void AreasThatWereAlreadyInTheMeshAreNotMerged()
        {
            var nav = new NavFile();
            nav.Areas.Add(Quad(1, 0, 0, 100, 100, 0, 0, 0, 0));
            nav.Areas.Add(Quad(2, 100, 0, 200, 100, 0, 0, 0, 0));

            // Everything below id 3 was here before this run, so neither of these qualifies.
            Assert.Equal(0, AreaMerger.Merge(nav, null, firstGeneratedId: 3).Merges);
            Assert.Equal(2, nav.Areas.Count);
        }

        /// <summary>
        /// The mixed case, which is the one a real -generateareas run produces: new ground grown up
        /// against a mesh that was already there. Two generated areas merge with each other; neither
        /// pulls the hand-placed one in with them.
        /// </summary>
        [Fact]
        public void GeneratedAreasMergeWithEachOtherButNotIntoAnOlderNeighbour()
        {
            var nav = new NavFile();
            nav.Areas.Add(Quad(1, 0, 0, 100, 100, 0, 0, 0, 0));      // was already here
            nav.Areas.Add(Quad(7, 100, 0, 200, 100, 0, 0, 0, 0));    // generated
            nav.Areas.Add(Quad(8, 200, 0, 300, 100, 0, 0, 0, 0));    // generated

            var result = AreaMerger.Merge(nav, null, firstGeneratedId: 7);

            Assert.Equal(1, result.Merges);
            Assert.Equal(2, nav.Areas.Count);
            Assert.Contains(nav.Areas, a => a.Id == 1);

            var merged = nav.Areas.Single(a => a.Id == 7);
            Assert.Equal(100f, NavGeometry.GetBounds(merged).MinX, 1);
            Assert.Equal(300f, NavGeometry.GetBounds(merged).MaxX, 1);
        }

        private static float Footprint(NavArea area)
        {
            var b = NavGeometry.GetBounds(area);
            return b.Width * b.Depth;
        }

        [Fact]
        public void TheNormalOfAFlatAreaPointsStraightUp()
        {
            var n = NavGeometry.ComputeNormal(Quad(1, 0, 0, 100, 100, 0, 0, 0, 0));

            Assert.Equal(0f, n.X, 3);
            Assert.Equal(0f, n.Y, 3);
            Assert.Equal(1f, n.Z, 3);
        }
    }

    /// <summary>
    /// That the reachability flood describes the graph a bot actually walks.
    ///
    /// It is used for two things now rather than one, and the second raised the bar. Reporting a ladder
    /// top as stranded is a wrong number in a diagnostic; deleting it, which
    /// <see cref="NavReachability.PruneUnreachable"/> would have done, is losing the mesh.
    /// </summary>
    public class NavReachabilityTests
    {
        private static NavArea Area(uint id, float x, float y, float z = 0)
        {
            var a = new NavArea { Id = id };

            a.NwCorner[0] = x; a.NwCorner[1] = y; a.NwCorner[2] = z;
            a.SeCorner[0] = x + 50; a.SeCorner[1] = y + 50; a.SeCorner[2] = z;
            a.NeZ = z; a.SwZ = z;

            return a;
        }

        /// <summary>A chain of n areas, each linked to the next, starting at the origin.</summary>
        private static NavFile Chain(int n, float z = 0, uint firstId = 1)
        {
            var nav = new NavFile();

            for (uint i = 0; i < n; i++)
                nav.Areas.Add(Area(firstId + i, i * 50, 0, z));

            for (int i = 0; i + 1 < n; i++)
            {
                nav.Areas[i].Connections[NavGeometry.East].Add(nav.Areas[i + 1].Id);
                nav.Areas[i + 1].Connections[NavGeometry.West].Add(nav.Areas[i].Id);
            }

            return nav;
        }

        private static BspFile.Vector3[] SpawnAtOrigin() => [new BspFile.Vector3(25, 25, 0)];

        [Fact]
        public void ALadderIsClimbedAsWellAsDescended()
        {
            var nav = new NavFile();
            nav.Areas.Add(Area(1, 0, 0));           // ground, where the spawn is
            nav.Areas.Add(Area(2, 0, 0, 400));      // a roof, reachable only up the ladder

            nav.Ladders.Add(new NavLadder
            {
                Id = 1,
                BottomAreaId = 1,
                TopForwardAreaId = 2,
            });

            var reached = NavReachability.Reached(nav, SpawnAtOrigin());

            Assert.Contains(1u, reached);
            Assert.Contains(2u, reached);
        }

        [Fact]
        public void AConnectionToAnAreaThatIsNotThereIsNotAReachableArea()
        {
            var nav = Chain(2);
            nav.Areas[1].Connections[NavGeometry.East].Add(999);

            var analysis = NavReachability.Analyse(nav, SpawnAtOrigin());

            Assert.Equal(2, analysis.Reachable);
            Assert.Equal(0, analysis.Unreachable);
        }

        [Fact]
        public void PruningTakesStraySamplesAndLeavesStructure()
        {
            // Sized so the unreachable part stays under the share ceiling - the guard against deleting
            // most of a mesh is a separate rule with its own test below, and it would fire first here.
            var nav = Chain(60);                                    // reachable from the spawn

            // A stray sample sealed in a void: one area, connected to nothing.
            nav.Areas.Add(Area(500, 0, 4000, -500));

            // And a whole wing that lost its staircase - unreachable, but real ground.
            var wing = Chain(20, -800, firstId: 100);
            nav.Areas.AddRange(wing.Areas);

            var pruned = NavReachability.PruneUnreachable(nav, SpawnAtOrigin());

            Assert.False(pruned.Refused);
            Assert.Equal(1, pruned.Removed);
            Assert.Equal(20, pruned.Stranded);

            Assert.DoesNotContain(nav.Areas, a => a.Id == 500);
            Assert.Equal(20, nav.Areas.Count(a => a.Id is >= 100 and < 500));
        }

        [Fact]
        public void PruningRefusesWhenItWouldTakeMostOfTheMesh()
        {
            var nav = Chain(2);
            nav.Areas.AddRange(Chain(20, -800, firstId: 100).Areas);

            // A ceiling low enough that the 20 unreachable areas of 22 exceed it.
            var pruned = NavReachability.PruneUnreachable(nav, SpawnAtOrigin(), maximumIslandSize: 1000);

            Assert.True(pruned.Refused);
            Assert.Equal(0, pruned.Removed);
            Assert.Equal(22, nav.Areas.Count);
            Assert.NotNull(pruned.Note);
        }

        [Fact]
        public void PruningDoesNothingWithoutASpawnThatResolves()
        {
            var nav = Chain(3);
            var pruned = NavReachability.PruneUnreachable(nav, []);

            Assert.True(pruned.Refused);
            Assert.Equal(3, nav.Areas.Count);
        }
    }
}
