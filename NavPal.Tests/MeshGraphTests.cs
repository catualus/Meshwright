using System;
using System.Collections.Generic;
using NavPal;
using Xunit;

namespace NavPal.Tests
{
    /// <summary>
    /// The spatial lookup every "what is near here" question in the pipeline goes through.
    ///
    /// Worth pinning because of who depends on it and how they fail. Ladder tops, ladder bases, lift
    /// riders and lift landings are all found by <see cref="NavGeometry.Index.FindAt"/> with a height
    /// and a tolerance, and each of those passes then writes a connection on the strength of the
    /// answer. A lookup that picks the wrong storey does not produce a missing link, which is
    /// recoverable; it produces a confident link between two floors of a building.
    /// </summary>
    public class NavGeometryIndexTests
    {
        private static NavArea Quad(uint id, float x0, float y0, float x1, float y1, float z)
        {
            var area = new NavArea { Id = id };
            area.NwCorner[0] = x0; area.NwCorner[1] = y0; area.NwCorner[2] = z;
            area.SeCorner[0] = x1; area.SeCorner[1] = y1; area.SeCorner[2] = z;
            area.NeZ = z;
            area.SwZ = z;
            return area;
        }

        /// <summary>
        /// Stacked storeys at the same footprint - a stairwell, a lift shaft, a basement under a room.
        /// The nearest surface to the reference height wins, not the first one indexed.
        /// </summary>
        [Fact]
        public void FindAtPicksTheNearestStoreyNotTheFirst()
        {
            var areas = new List<NavArea>
            {
                Quad(1, 0, 0, 100, 100, 0),
                Quad(2, 0, 0, 100, 100, 200),
                Quad(3, 0, 0, 100, 100, 400),
            };

            var index = new NavGeometry.Index(areas);

            Assert.Equal(0, index.FindAt(50, 50, 10, 64));
            Assert.Equal(1, index.FindAt(50, 50, 190, 64));
            Assert.Equal(2, index.FindAt(50, 50, 410, 64));
        }

        /// <summary>
        /// Tolerance is a real bound, not a preference. Every caller passes one chosen to mean "close
        /// enough to step onto", so returning a nearest-but-far answer would defeat the check at the
        /// call site rather than at the lookup.
        /// </summary>
        [Fact]
        public void FindAtRefusesAnythingOutsideTheTolerance()
        {
            var areas = new List<NavArea> { Quad(1, 0, 0, 100, 100, 0) };
            var index = new NavGeometry.Index(areas);

            Assert.Equal(0, index.FindAt(50, 50, 20, 24));
            Assert.Equal(-1, index.FindAt(50, 50, 200, 24));
        }

        [Fact]
        public void FindAtReturnsNothingOutsideEveryFootprint()
        {
            var areas = new List<NavArea> { Quad(1, 0, 0, 100, 100, 0) };
            var index = new NavGeometry.Index(areas);

            Assert.Equal(-1, index.FindAt(500, 500, 0, 64));
        }

        /// <summary>
        /// An area wider than the index's own cell size is registered in every cell it crosses, so a
        /// naive walk would return it once per cell. Callers treat the result as a set - the elevator
        /// pass builds a landing list from it and links every entry - so a duplicate is a duplicated
        /// connection.
        /// </summary>
        [Fact]
        public void OverlappingYieldsEachAreaOnce()
        {
            // 2000 units across against a 256-unit cell, so this spans eight cells in each axis.
            var areas = new List<NavArea> { Quad(1, 0, 0, 2000, 2000, 0) };
            var index = new NavGeometry.Index(areas);

            var seen = new List<int>(index.Overlapping(-100, -100, 2100, 2100));

            Assert.Single(seen);
            Assert.Equal(0, seen[0]);
        }

        [Fact]
        public void OverlappingExcludesAreasOutsideTheRectangle()
        {
            var areas = new List<NavArea>
            {
                Quad(1, 0, 0, 100, 100, 0),
                Quad(2, 5000, 5000, 5100, 5100, 0),
            };

            var index = new NavGeometry.Index(areas);
            var seen = new List<int>(index.Overlapping(-10, -10, 110, 110));

            Assert.Single(seen);
            Assert.Equal(0, seen[0]);
        }
    }

    /// <summary>
    /// Redundant-shortcut removal - Valve's <c>FixConnections</c>.
    ///
    /// Removing A-&gt;C because A-&gt;B-&gt;C exists is only safe while "same direction" really is part
    /// of the rule. Drop that qualifier and the pass starts deleting the only link between two areas
    /// whenever some unrelated third area happens to bridge them the long way round, which is a
    /// disconnection rather than a tidy-up and would show up as bots refusing a route they can see.
    /// </summary>
    public class AreaConnectionFixerTests
    {
        private static NavArea Quad(uint id)
        {
            var area = new NavArea { Id = id };
            area.SeCorner[0] = 100; area.SeCorner[1] = 100;
            return area;
        }

        [Fact]
        public void RemovesADirectLinkThatIsAlreadyReachableInTheSameDirection()
        {
            var nav = new NavFile();
            var a = Quad(1);
            var b = Quad(2);
            var c = Quad(3);

            a.Connections[NavGeometry.East].Add(b.Id);
            a.Connections[NavGeometry.East].Add(c.Id);
            b.Connections[NavGeometry.East].Add(c.Id);

            nav.Areas.Add(a);
            nav.Areas.Add(b);
            nav.Areas.Add(c);

            var result = AreaConnectionFixer.Fix(nav);

            Assert.Equal(1, result.ShortcutsRemoved);
            Assert.Equal([2u], a.Connections[NavGeometry.East]);
        }

        /// <summary>
        /// The indirect route leaves on a different heading, so it is not the same movement and the
        /// direct link is the only thing expressing it.
        /// </summary>
        [Fact]
        public void KeepsADirectLinkWhenTheDetourTurnsACorner()
        {
            var nav = new NavFile();
            var a = Quad(1);
            var b = Quad(2);
            var c = Quad(3);

            a.Connections[NavGeometry.East].Add(b.Id);
            a.Connections[NavGeometry.East].Add(c.Id);
            b.Connections[NavGeometry.South].Add(c.Id);   // not east: a different move

            nav.Areas.Add(a);
            nav.Areas.Add(b);
            nav.Areas.Add(c);

            Assert.Equal(0, AreaConnectionFixer.Fix(nav).ShortcutsRemoved);
            Assert.Contains(3u, a.Connections[NavGeometry.East]);
        }

        [Fact]
        public void LeavesAPlainChainAlone()
        {
            var nav = new NavFile();
            var a = Quad(1);
            var b = Quad(2);

            a.Connections[NavGeometry.East].Add(b.Id);

            nav.Areas.Add(a);
            nav.Areas.Add(b);

            Assert.Equal(0, AreaConnectionFixer.Fix(nav).ShortcutsRemoved);
            Assert.Contains(2u, a.Connections[NavGeometry.East]);
        }

        /// <summary>
        /// A two-cycle in one direction. Nothing stops a generated mesh containing one, and the scan
        /// walks a neighbour's outgoing links in the same direction it arrived on, so the guard against
        /// treating an area as its own shortcut has to hold.
        /// </summary>
        [Fact]
        public void ACycleDoesNotDeleteItsOwnLinks()
        {
            var nav = new NavFile();
            var a = Quad(1);
            var b = Quad(2);

            a.Connections[NavGeometry.East].Add(b.Id);
            b.Connections[NavGeometry.East].Add(a.Id);

            nav.Areas.Add(a);
            nav.Areas.Add(b);

            Assert.Equal(0, AreaConnectionFixer.Fix(nav).ShortcutsRemoved);
            Assert.Contains(2u, a.Connections[NavGeometry.East]);
            Assert.Contains(1u, b.Connections[NavGeometry.East]);
        }
    }

    /// <summary>
    /// Splitting long areas back to roughly square - Valve's <c>SquareUpAreas</c>.
    ///
    /// The invariant that matters is not the count but the surface. This pass runs after merging, on
    /// areas whose four corner heights are what make them sit on the ground properly, and a split that
    /// does not evaluate the parent's own surface at the cut leaves both halves describing ground
    /// neither of them covers. That reads in game as a mesh that tilts at seams which were flat a pass
    /// earlier, and no count or coverage figure moves.
    /// </summary>
    public class AreaSquarerTests
    {
        private static NavArea Quad(uint id, float x0, float y0, float x1, float y1,
            float nw, float ne, float se, float sw)
        {
            var area = new NavArea { Id = id };
            area.NwCorner[0] = x0; area.NwCorner[1] = y0; area.NwCorner[2] = nw;
            area.SeCorner[0] = x1; area.SeCorner[1] = y1; area.SeCorner[2] = se;
            area.NeZ = ne;
            area.SwZ = sw;
            return area;
        }

        private static NavArea Flat(uint id, float x0, float y0, float x1, float y1)
            => Quad(id, x0, y0, x1, y1, 0, 0, 0, 0);

        [Fact]
        public void ALongCorridorIsSplit()
        {
            var nav = new NavFile();
            nav.Areas.Add(Flat(1, 0, 0, 1000, 100));

            var result = AreaSquarer.SquareUp(nav);

            Assert.Equal(1, result.Split);
            Assert.True(nav.Areas.Count > 1, "a 10:1 area was left as one piece");

            foreach (var area in nav.Areas)
            {
                var b = NavGeometry.GetBounds(area);
                Assert.True(MathF.Max(b.Width, b.Depth) <= MathF.Min(b.Width, b.Depth) * 3f + 0.01f,
                    $"left an area {b.Width} x {b.Depth}, still past the aspect tolerance");
            }
        }

        [Fact]
        public void ASquareAreaIsLeftAlone()
        {
            var nav = new NavFile();
            nav.Areas.Add(Flat(1, 0, 0, 400, 400));

            Assert.Equal(0, AreaSquarer.SquareUp(nav).Split);
            Assert.Single(nav.Areas);
        }

        /// <summary>
        /// Shape alone does not justify a cut. A small area twice as long as it is wide is not a sliver,
        /// and splitting it only adds areas for a path search to walk through.
        /// </summary>
        [Fact]
        public void AShortAreaSurvivesAnAwkwardShape()
        {
            var nav = new NavFile();
            nav.Areas.Add(Flat(1, 0, 0, 150, 25));   // 6:1, but shorter than the split threshold

            Assert.Equal(0, AreaSquarer.SquareUp(nav).Split);
            Assert.Single(nav.Areas);
        }

        /// <summary>
        /// The real property: wherever the ground was before the split, it is in the same place after.
        ///
        /// Sampled across the whole footprint rather than at the cut, because an off-by-one in which
        /// corner receives the interpolated height tilts a half without moving the seam.
        /// </summary>
        [Fact]
        public void SplittingPreservesTheSurfaceExactly()
        {
            // A ramp climbing 40 units along X, so every cut has a genuinely different height each side.
            var reference = Quad(1, 0, 0, 400, 100, nw: 0, ne: 40, se: 40, sw: 0);

            var nav = new NavFile();
            nav.Areas.Add(Quad(1, 0, 0, 400, 100, nw: 0, ne: 40, se: 40, sw: 0));

            AreaSquarer.SquareUp(nav);
            Assert.True(nav.Areas.Count > 1, "the fixture did not actually split");

            for (float x = 2f; x < 400f; x += 7f)
            {
                for (float y = 2f; y < 100f; y += 13f)
                {
                    NavArea? covering = null;
                    foreach (var area in nav.Areas)
                    {
                        if (!NavGeometry.Contains(area, x, y))
                            continue;

                        covering = area;
                        break;
                    }

                    Assert.True(covering is not null, $"({x}, {y}) is covered by no area after splitting");
                    Assert.Equal(NavGeometry.SurfaceZ(reference, x, y),
                        NavGeometry.SurfaceZ(covering, x, y), 3);
                }
            }
        }

        /// <summary>
        /// Ids have to stay unique: they are what connections, ladders and visibility all reference, and
        /// a duplicate silently redirects every link naming it.
        /// </summary>
        [Fact]
        public void EveryPieceGetsItsOwnId()
        {
            var nav = new NavFile();
            nav.Areas.Add(Flat(1, 0, 0, 1600, 100));
            nav.Areas.Add(Flat(2, 0, 200, 1600, 300));

            AreaSquarer.SquareUp(nav);

            var ids = new HashSet<uint>();
            foreach (var area in nav.Areas)
                Assert.True(ids.Add(area.Id), $"id {area.Id} was handed out twice");
        }

        /// <summary>
        /// The footprint is conserved - splitting redistributes ground, it does not lose or invent any.
        /// </summary>
        [Fact]
        public void SplittingConservesTotalArea()
        {
            var nav = new NavFile();
            nav.Areas.Add(Flat(1, 0, 0, 1000, 100));

            AreaSquarer.SquareUp(nav);

            float total = 0;
            foreach (var area in nav.Areas)
            {
                var b = NavGeometry.GetBounds(area);
                total += b.Width * b.Depth;
            }

            Assert.Equal(1000f * 100f, total, 1);
        }
    }
}
