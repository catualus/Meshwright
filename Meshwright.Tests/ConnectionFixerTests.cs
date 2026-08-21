using System.Linq;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That tidying redundant connections never removes the last route between two areas.
    ///
    /// The shortcut rule is Valve's: drop A-&gt;C when A-&gt;B-&gt;C already goes the same way. What makes it
    /// dangerous is the batching. Collect every redundant edge, then remove them all, and the detour
    /// justifying one removal may have been removed by another - so a rule that is only ever supposed to
    /// delete duplicate information deletes the information instead.
    ///
    /// It is not a theoretical concern. On rp_downtown_meowy it cut four edges, each the single way into
    /// a region, stranding 336 areas that the engine's own mesh walks to perfectly well.
    /// </summary>
    public class ConnectionFixerTests
    {
        private static NavArea Area(uint id)
        {
            var area = new NavArea { Id = id };

            area.NwCorner[0] = id * 100f; area.NwCorner[1] = 0; area.NwCorner[2] = 0;
            area.SeCorner[0] = id * 100f + 50f; area.SeCorner[1] = 50; area.SeCorner[2] = 0;

            return area;
        }

        [Fact]
        public void TwoShortcutsThatJustifyEachOtherDoNotBothGo()
        {
            // The minimal way to lose an area. A reaches both B and C; B reaches C and C reaches B, so
            // each of A's edges looks redundant through the other. Removing both leaves A joined to
            // nothing, and each removal was individually defensible.
            var nav = new NavFile();
            var a = Area(1);
            var b = Area(2);
            var c = Area(3);

            a.Connections[0].Add(2);
            a.Connections[0].Add(3);
            b.Connections[0].Add(3);
            c.Connections[0].Add(2);

            nav.Areas.Add(a);
            nav.Areas.Add(b);
            nav.Areas.Add(c);

            AreaConnectionFixer.Fix(nav);

            Assert.NotEmpty(a.Connections[0]);

            // And both are still reachable from A, which is the property that actually matters.
            var reached = NavReachability.Reached(nav, [new BspFile.Vector3(125, 25, 0)]);

            Assert.Contains(2u, reached);
            Assert.Contains(3u, reached);
        }

        [Fact]
        public void APlainRedundantShortcutIsStillRemoved()
        {
            // The fix must not disable the rule. A->C alongside A->B->C is exactly what it is for.
            var nav = new NavFile();
            var a = Area(1);
            var b = Area(2);
            var c = Area(3);

            a.Connections[0].Add(2);
            a.Connections[0].Add(3);
            b.Connections[0].Add(3);

            nav.Areas.Add(a);
            nav.Areas.Add(b);
            nav.Areas.Add(c);

            var result = AreaConnectionFixer.Fix(nav);

            Assert.Equal(1, result.ShortcutsRemoved);
            Assert.Equal([2u], a.Connections[0]);
        }

        [Fact]
        public void ChainedRemovalsKeepEverythingReachable()
        {
            // A longer version of the same trap: several removals in one batch, each justified by a
            // detour that another removal is about to take away.
            var nav = new NavFile();
            var areas = Enumerable.Range(1, 6).Select(i => Area((uint)i)).ToList();

            foreach (var area in areas) nav.Areas.Add(area);

            // Every area links to every later one, so almost every edge looks like a shortcut past some
            // intermediate. Only the step-by-step chain is genuinely needed.
            for (uint from = 1; from <= 6; from++)
                for (uint to = from + 1; to <= 6; to++)
                    areas[(int)from - 1].Connections[0].Add(to);

            AreaConnectionFixer.Fix(nav);

            var reached = NavReachability.Reached(nav, [new BspFile.Vector3(125, 25, 0)]);

            for (uint id = 1; id <= 6; id++)
                Assert.Contains(id, reached);
        }
    }
}
