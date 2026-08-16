using System.Collections.Generic;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// Losslessness tests for the visibility delta scheme.
    ///
    /// This pass exists to stop a large map's visibility running to 145 MB, and it does that by letting
    /// an area name a neighbour and store only where the two disagree. That is a compression format
    /// invented against an observed engine mesh rather than a documented one, and its failure mode is
    /// the quiet kind: a mesh that loads, that is the right size, and whose bots have the wrong idea
    /// about what they can see. Nothing downstream validates it, and no aggregate the tool prints would
    /// move.
    ///
    /// So the property under test is the only one that matters - apply the compression, resolve it
    /// back, and require exactly the sets that went in. Every test here drives that through a different
    /// shape of input rather than asserting on the encoding itself, because the encoding is free to
    /// change and the round trip is not.
    /// </summary>
    public class VisibilityCompressorTests
    {
        /// <summary>A row of areas, each connected to the next, so neighbours exist to inherit from.</summary>
        private static NavFile Chain(int count)
        {
            var nav = new NavFile();

            for (int i = 0; i < count; i++)
            {
                var area = new NavArea { Id = (uint)(i + 1) };
                area.NwCorner[0] = i * 100; area.NwCorner[1] = 0;
                area.SeCorner[0] = i * 100 + 100; area.SeCorner[1] = 100;
                nav.Areas.Add(area);
            }

            for (int i = 0; i + 1 < count; i++)
            {
                nav.Areas[i].Connections[NavGeometry.East].Add(nav.Areas[i + 1].Id);
                nav.Areas[i + 1].Connections[NavGeometry.West].Add(nav.Areas[i].Id);
            }

            return nav;
        }

        /// <summary>
        /// Every area sees every other but not itself: neighbouring rows differ by exactly two entries
        /// against a full list of <c>count - 1</c>, which is the shape inheritance is meant to exploit
        /// and so the shape that actually exercises the delta path.
        /// </summary>
        private static int[][] NearlyIdentical(int count)
        {
            var visible = new int[count][];

            for (int i = 0; i < count; i++)
            {
                var list = new List<int>();
                for (int j = 0; j < count; j++)
                {
                    if (j != i)
                        list.Add(j);
                }

                visible[i] = list.ToArray();
            }

            return visible;
        }

        /// <summary>Resolves the mesh and requires each area's effective set to match what went in.</summary>
        private static void AssertLossless(NavFile nav, int[][] visible)
        {
            var resolved = VisibilityCompressor.Resolve(nav);

            for (int i = 0; i < nav.Areas.Count; i++)
            {
                var expected = new HashSet<uint>();
                foreach (int j in visible[i])
                    expected.Add(nav.Areas[j].Id);

                Assert.True(expected.SetEquals(resolved[i]),
                    $"area {nav.Areas[i].Id} resolved to [{string.Join(", ", resolved[i])}] " +
                    $"but should see [{string.Join(", ", expected)}]");
            }
        }

        /// <summary>
        /// The headline property, on input shaped to make compression worth doing.
        ///
        /// The compression assertion is not decoration. Without it this passes just as happily when
        /// every area stores a full list and the delta path never runs at all, which is precisely the
        /// regression a losslessness test is least likely to notice.
        /// </summary>
        [Fact]
        public void CompressedVisibilityResolvesBackExactly()
        {
            var nav = Chain(12);
            var visible = NearlyIdentical(12);

            var result = VisibilityCompressor.Apply(nav, visible);

            Assert.True(result.Compressed > 0, "no area inherited, so the delta path was never exercised");
            Assert.True(result.EntriesAfter < result.EntriesBefore,
                $"compression made it bigger: {result.EntriesBefore} -> {result.EntriesAfter}");

            AssertLossless(nav, visible);
        }

        /// <summary>
        /// A parent that sees more than its child does.
        ///
        /// This is the one thing the encoding cannot express by omission: the child has to store an
        /// explicit zero-attribute entry meaning "not visible, whatever my parent says". Dropping that
        /// entry as empty, or reading it back as padding, silently grants the child its parent's extra
        /// sightlines - and since the child's own list only ever gets shorter, nothing about the file
        /// size or the area count would look wrong.
        ///
        /// The shape here is dictated by how parents get chosen, not by taste. Areas are considered in
        /// index order and an area only inherits when the delta is *smaller* than storing its own list,
        /// so area 0 is always the first to compress and its partner is pinned as a full-list parent.
        /// Making area 0 the one that sees less is therefore the only way to put a downward override on
        /// the pass it actually takes. An earlier version of this test had the subset area later in the
        /// chain, which read correctly and exercised nothing: the delta came out larger than the full
        /// list, no parent was chosen, and the test passed against a deliberately broken
        /// <c>Difference</c>.
        /// </summary>
        [Fact]
        public void AParentSeeingMoreThanItsChildIsOverriddenExplicitly()
        {
            var nav = Chain(12);

            var visible = new int[12][];

            // Area 0 sees everything from 3 up; area 1 sees all of that and index 2 besides. The delta
            // is a single entry, so area 0 inherits - and that entry has to be a removal.
            visible[0] = [3, 4, 5, 6, 7, 8, 9, 10, 11];
            visible[1] = [2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

            for (int i = 2; i < 12; i++)
                visible[i] = [0, 1];

            VisibilityCompressor.Apply(nav, visible);

            // Pinned directly rather than inferred from the round trip, so this cannot quietly go back
            // to exercising nothing if the parent-choosing heuristic is retuned.
            Assert.Equal(nav.Areas[1].Id, nav.Areas[0].InheritVisibilityFrom);

            var override0 = Assert.Single(nav.Areas[0].VisibleAreas);
            Assert.Equal(nav.Areas[2].Id, override0.AreaId);
            Assert.Equal(0, override0.Attributes);

            AssertLossless(nav, visible);
        }

        /// <summary>
        /// One level of indirection, guaranteed structurally.
        ///
        /// An area that stores a delta must never itself be somebody's parent, or resolving needs a
        /// walk and a cycle check instead of a single lookup. The class arrived at this the hard way:
        /// rejecting parents that are *already* deltas is not enough on its own, because an area chosen
        /// as a parent early is still free to become a delta when its own turn comes round, quietly
        /// changing what its children resolve to.
        /// </summary>
        [Fact]
        public void NoAreaBothInheritsAndIsInheritedFrom()
        {
            var nav = Chain(16);
            VisibilityCompressor.Apply(nav, NearlyIdentical(16));

            var deltas = new HashSet<uint>();
            foreach (var area in nav.Areas)
            {
                if (area.InheritVisibilityFrom != 0)
                    deltas.Add(area.Id);
            }

            Assert.NotEmpty(deltas);

            foreach (var area in nav.Areas)
            {
                if (area.InheritVisibilityFrom == 0)
                    continue;

                Assert.DoesNotContain(area.InheritVisibilityFrom, deltas);
            }
        }

        /// <summary>
        /// An area with no connections has no candidate to inherit from and must keep a full list.
        /// Naming a parent it does not border would resolve against an unrelated part of the map.
        /// </summary>
        [Fact]
        public void AnIsolatedAreaKeepsItsOwnFullList()
        {
            var nav = Chain(4);

            var orphan = new NavArea { Id = 99 };
            orphan.NwCorner[0] = 10000; orphan.SeCorner[0] = 10100;
            orphan.SeCorner[1] = 100;
            nav.Areas.Add(orphan);

            var visible = new int[5][];
            visible[0] = [1, 2, 3];
            visible[1] = [0, 2, 3];
            visible[2] = [0, 1, 3];
            visible[3] = [0, 1, 2];
            visible[4] = [0, 1, 2, 3];

            VisibilityCompressor.Apply(nav, visible);

            Assert.Equal(0u, orphan.InheritVisibilityFrom);
            Assert.Equal(4, orphan.VisibleAreas.Count);

            AssertLossless(nav, visible);
        }

        /// <summary>
        /// Areas that see nothing at all - the far side of a sealed door, a closed room. An empty set
        /// has to stay empty rather than picking up a neighbour's view by inheriting from it.
        /// </summary>
        [Fact]
        public void AreasSeeingNothingStaySeeingNothing()
        {
            var nav = Chain(5);

            var visible = new int[5][];
            visible[0] = [1, 2];
            visible[1] = [0, 2];
            visible[2] = [0, 1];
            visible[3] = [];
            visible[4] = [];

            VisibilityCompressor.Apply(nav, visible);

            Assert.Equal(0u, nav.Areas[3].InheritVisibilityFrom);
            Assert.Empty(nav.Areas[3].VisibleAreas);

            AssertLossless(nav, visible);
        }

        /// <summary>
        /// Survives the file it is written to. The delta scheme is only worth anything if it means the
        /// same thing after a save and reload, and the zero-attribute override is the entry most at
        /// risk of being dropped on the way through.
        /// </summary>
        [Fact]
        public void CompressionSurvivesASaveAndReload()
        {
            var nav = Chain(10);
            var visible = NearlyIdentical(10);

            VisibilityCompressor.Apply(nav, visible);

            using var stream = new System.IO.MemoryStream();

            using (var w = new System.IO.BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
                nav.Write(w);

            stream.Position = 0;
            using var r = new System.IO.BinaryReader(stream, System.Text.Encoding.ASCII);
            var reloaded = NavFile.Read(r);

            AssertLossless(reloaded, visible);
        }
    }
}
