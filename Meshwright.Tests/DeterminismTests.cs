using System;
using System.IO;
using System.Linq;
using System.Text;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That the same input produces the same mesh, byte for byte, however the work was spread.
    ///
    /// This is not a theoretical property. Two passes lost it and neither showed up as a failure:
    /// <see cref="AreaConnectionFixer"/> collected redundant edges into a ConcurrentBag and then acted
    /// on them in whatever order threads had pushed them, which decides which of two mutually
    /// justifying edges survives; <see cref="AreaMerger"/> walked a HashSet keyed on object identity,
    /// and .NET's default hash for a reference comes from a per-thread random generator, so its
    /// enumeration order genuinely differs between two runs of the same program. Five consecutive
    /// builds of gm_construct produced five different files, with connection counts drifting between
    /// 15,285 and 15,290.
    ///
    /// Nothing caught it, and nothing could have. `verify` round-trips a file against itself, so it
    /// passes on a mesh that differs from the one the previous run made; every quality measure scores
    /// both meshes as equally good, because both are equally good. Only comparing two runs finds it.
    ///
    /// The sharper of the two checks below is the thread-count one. Repeating a pass at the same width
    /// relies on a race actually happening; running it at one thread and then at every core forces the
    /// schedule to differ, so an order-dependent pass has nowhere to hide.
    /// </summary>
    public class DeterminismTests : IDisposable
    {
        private readonly int threads = NavConcurrency.MaxThreads;

        public void Dispose() => NavConcurrency.MaxThreads = threads;

        /// <summary>
        /// A mesh big enough for the parallel passes to actually spread, and shaped to give them work.
        ///
        /// The skip connections are the point of it: a redundant shortcut needs A-&gt;B, B-&gt;C and
        /// A-&gt;C all in the same direction, and a grid of immediate neighbours alone never produces
        /// one. Every area also reaches two along, which makes several thousand triples for the fixup
        /// to find and, crucially, pairs of edges that each justify removing the other.
        /// </summary>
        private static NavFile Grid(int across, int down)
        {
            var nav = new NavFile();
            const float Step = 50f;

            uint Id(int x, int y) => (uint)(y * across + x + 1);

            for (int y = 0; y < down; y++)
            {
                for (int x = 0; x < across; x++)
                {
                    var area = new NavArea { Id = Id(x, y) };

                    area.NwCorner[0] = x * Step;
                    area.NwCorner[1] = y * Step;
                    area.NwCorner[2] = 0;

                    area.SeCorner[0] = (x + 1) * Step;
                    area.SeCorner[1] = (y + 1) * Step;
                    area.SeCorner[2] = 0;

                    nav.Areas.Add(area);
                }
            }

            var byId = nav.Areas.ToDictionary(a => a.Id);

            for (int y = 0; y < down; y++)
            {
                for (int x = 0; x < across; x++)
                {
                    var area = byId[Id(x, y)];

                    if (x + 1 < across) area.Connections[NavGeometry.East].Add(Id(x + 1, y));
                    if (x > 0) area.Connections[NavGeometry.West].Add(Id(x - 1, y));
                    if (y + 1 < down) area.Connections[NavGeometry.South].Add(Id(x, y + 1));
                    if (y > 0) area.Connections[NavGeometry.North].Add(Id(x, y - 1));

                    // Skips at two strides, not one, and that detail is the whole fixture.
                    //
                    // A single stride gives redundant edges whose justification can never be removed:
                    // A->C is dropped because A->B->C exists, and A->B and B->C are immediate
                    // neighbours that nothing touches. The outcome is then order-independent whatever
                    // the pass does, and a test built on it proves nothing.
                    //
                    // Two strides make the removals contend. A->D is justified by A->B->D and by
                    // A->C->D; both of those are themselves redundant and removable; so whether A->D
                    // survives depends on which of them is considered first. That is exactly the
                    // situation the ordering has to settle the same way every time.
                    if (x + 2 < across) area.Connections[NavGeometry.East].Add(Id(x + 2, y));
                    if (x + 3 < across) area.Connections[NavGeometry.East].Add(Id(x + 3, y));
                    if (y + 2 < down) area.Connections[NavGeometry.South].Add(Id(x, y + 2));
                    if (y + 3 < down) area.Connections[NavGeometry.South].Add(Id(x, y + 3));
                }
            }

            return nav;
        }

        private static byte[] Bytes(NavFile nav)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true)) nav.Write(w);
            return ms.ToArray();
        }

        /// <summary>Runs the order-sensitive passes over a fresh copy of the same mesh.</summary>
        private static byte[] Passes(int across, int down)
        {
            var nav = Grid(across, down);

            AreaMerger.Merge(nav);
            AreaSquarer.SquareUp(nav);
            JumpAreaStitcher.Stitch(nav);
            AreaConnectionFixer.Fix(nav);
            NavIntegrity.Prune(nav);

            return Bytes(nav);
        }

        /// <summary>
        /// One thread against every core. An order-dependent pass cannot survive this, where repeating
        /// at a single width might happen not to lose the race on the day.
        /// </summary>
        [Fact]
        public void ThreadCountDoesNotChangeTheMesh()
        {
            NavConcurrency.MaxThreads = 1;
            var single = Passes(40, 40);

            NavConcurrency.MaxThreads = Math.Max(2, Environment.ProcessorCount);
            var parallel = Passes(40, 40);

            Assert.Equal(single, parallel);
        }

        [Fact]
        public void RepeatedRunsAgreeByteForByte()
        {
            NavConcurrency.MaxThreads = Math.Max(2, Environment.ProcessorCount);

            var first = Passes(30, 30);

            for (int run = 2; run <= 12; run++)
                Assert.True(first.SequenceEqual(Passes(30, 30)), $"run {run} differed from run 1");
        }

        /// <summary>
        /// The pass that lost it, checked where the guarantee actually lives.
        ///
        /// Three behavioural tests were written for this first and all three passed with the bug back
        /// in: shuffling the areas, varying the thread count from one to sixteen, and repeating the
        /// pass a dozen times. None of them reproduces it, because the order that decides the outcome
        /// is the order a ConcurrentBag hands items back, and that follows which worker queue each item
        /// landed in - a function of real thread scheduling, not of anything a caller can arrange.
        /// Across separate processes it varied every time; inside one process it would not budge.
        ///
        /// So the assertion is on the order itself. A removal is only safe to apply in a fixed order,
        /// the pass sorts to get one, and this fails the moment that sort is dropped - deterministically,
        /// without needing to lose a race.
        /// </summary>
        [Fact]
        public void TheShortcutFixupAppliesRemovalsInACanonicalOrder()
        {
            var result = AreaConnectionFixer.Fix(Grid(40, 40));

            Assert.True(result.ShortcutsRemoved > 0, "nothing was removed: the pass is untested here");
            Assert.Equal(result.ShortcutsRemoved, result.Removed.Count);

            var sorted = result.Removed
                .OrderBy(r => r.From).ThenBy(r => r.Direction)
                .ThenBy(r => r.Through).ThenBy(r => r.To)
                .ToList();

            Assert.Equal(sorted, result.Removed);
        }

        /// <summary>
        /// And that the order is a property of the mesh rather than of this run: the same graph, with
        /// its areas handed over in a different sequence, must produce the same removals in the same
        /// order.
        /// </summary>
        [Fact]
        public void ThatOrderDoesNotDependOnHowTheAreasWerePresented()
        {
            var plain = AreaConnectionFixer.Fix(Grid(40, 40));

            for (int seed = 1; seed <= 3; seed++)
            {
                var shuffled = AreaConnectionFixer.Fix(Shuffle(Grid(40, 40), seed));
                Assert.Equal(plain.Removed, shuffled.Removed);
            }
        }

        /// <summary>
        /// Merging must not read object identity either. .NET derives a reference's default hash from a
        /// per-thread pseudo-random generator, so a HashSet of areas enumerates in an order set by when
        /// they were allocated - which is not a fact about the mesh. Two logically identical meshes
        /// built by allocating their areas in opposite orders are the case that separates the two.
        ///
        /// Weaker than the check above, and worth saying so: this was fixed defensively alongside the
        /// fixup and never demonstrated to misbehave on a real map, so what follows guards a property
        /// rather than reproducing a known failure.
        /// </summary>
        [Fact]
        public void MergingDoesNotDependOnAllocationOrder()
        {
            var forwards = Grid(25, 25);
            AreaMerger.Merge(forwards);

            var backwards = GridAllocatedBackwards(25, 25);
            AreaMerger.Merge(backwards);

            Assert.Equal(Graph(forwards), Graph(backwards));
        }

        /// <summary>
        /// The mesh as a canonical string: every area by id, with its connections per direction in the
        /// order the file will carry them. Insensitive to the order areas appear in, sensitive to
        /// everything a bot would notice.
        /// </summary>
        private static string Graph(NavFile nav)
        {
            var lines = nav.Areas
                .OrderBy(a => a.Id)
                .Select(a => $"{a.Id}:" + string.Join("|",
                    a.Connections.Select(list => string.Join(",", list))));

            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>Reorders the area list without touching the graph it describes.</summary>
        private static NavFile Shuffle(NavFile nav, int seed)
        {
            var random = new Random(seed);
            var areas = nav.Areas.OrderBy(_ => random.Next()).ToList();

            nav.Areas.Clear();
            nav.Areas.AddRange(areas);

            return nav;
        }

        /// <summary>
        /// The same mesh, with its areas allocated last-first and then put back into the usual order.
        /// Identical in every way the mesh format can express; different in the identity hashes that
        /// <see cref="AreaMerger"/> must not be reading.
        /// </summary>
        private static NavFile GridAllocatedBackwards(int across, int down)
        {
            var source = Grid(across, down);
            var reversed = new NavFile();

            for (int i = source.Areas.Count - 1; i >= 0; i--)
            {
                var from = source.Areas[i];
                var copy = new NavArea { Id = from.Id, AttributeFlags = from.AttributeFlags };

                Array.Copy(from.NwCorner, copy.NwCorner, 3);
                Array.Copy(from.SeCorner, copy.SeCorner, 3);
                copy.NeZ = from.NeZ;
                copy.SwZ = from.SwZ;

                for (int d = 0; d < from.Connections.Length; d++)
                    copy.Connections[d].AddRange(from.Connections[d]);

                reversed.Areas.Add(copy);
            }

            reversed.Areas.Reverse();
            return reversed;
        }

        /// <summary>
        /// The passes must actually have done something, or every assertion above holds trivially and
        /// the test would keep passing after the code it guards stopped working.
        /// </summary>
        [Fact]
        public void TheFixtureExercisesThePassesItClaimsTo()
        {
            var nav = Grid(30, 30);
            int areasBefore = nav.Areas.Count;

            Assert.True(AreaMerger.Merge(nav).Merges > 0, "no merges: the merge pass is untested");

            var withShortcuts = Grid(30, 30);
            Assert.True(AreaConnectionFixer.Fix(withShortcuts).ShortcutsRemoved > 0,
                "no redundant shortcuts: the fixup pass is untested");

            Assert.True(areasBefore >= 900);
        }
    }
}
