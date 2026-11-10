using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That the sampling flood's caches and visited set give the answers the dictionaries they replaced
    /// gave, however many threads ask at once.
    ///
    /// The flood's per-column caches used to be ConcurrentDictionaries, and spinning on their locks was
    /// two thirds of the sampling stage's CPU. They are now slots in an array, one per grid column,
    /// filled lock-free: the first thread to work a column out publishes it, and any thread that raced
    /// it adopts what was published. That is only sound if a racing duplicate is invisible - every
    /// reader of a column sees the one value that won, and it is the value a single thread would have
    /// worked out alone. These pin that down, along with the visited set's rule for floors either side
    /// of a band edge, which moved from a dictionary keyed on the band into a chain per column.
    /// </summary>
    public class FloodCacheTests : IDisposable
    {
        private readonly int threads = NavConcurrency.MaxThreads;

        public void Dispose() => NavConcurrency.MaxThreads = threads;

        private static readonly BspFile.Vector3 Mins = new(0, 0, -500);
        private static readonly BspFile.Vector3 Maxs = new(1000, 1000, 500);

        /// <summary>
        /// A world with every kind of answer in it: ground under a crouch-height slab, in a slot between
        /// two walls that is wide enough for a body in places, the slab's top with room to stand, the
        /// walls' tops, and columns with two floors and with none.
        /// </summary>
        private static BspVisibility Mixed() => CrouchClearanceTests.SlabInSlot(200f, 300f);

        private static int Workers => Math.Max(4, Environment.ProcessorCount);

        /// <summary>
        /// The regression the lock-free caches could introduce: many threads racing for every column
        /// and every floor's clearance slot get the same floors - the very same array - and the same
        /// clearance as a single thread asking alone.
        /// </summary>
        [Fact]
        public void RacingLookupsAgreeWithASerialRun()
        {
            var serial = AreaGenerator.SampleColumns(Mixed(), Mins, Maxs, threads: 1, out _);

            // The fixture has to exercise what it claims to: floors stacked in a column, and all three
            // answers, or agreeing proves little.
            var answers = serial.SelectMany(c => c).Select(f => f.Clearance).ToHashSet();
            Assert.Contains(serial, c => c.Length >= 2);
            Assert.Contains(AreaGenerator.Clearance.Stand, answers);
            Assert.Contains(AreaGenerator.Clearance.Crouch, answers);
            Assert.Contains(AreaGenerator.Clearance.None, answers);

            for (int run = 0; run < 5; run++)
            {
                var racing = AreaGenerator.SampleColumns(Mixed(), Mins, Maxs, Workers, out bool agreed);

                Assert.True(agreed, $"run {run}: threads were handed different answers for one column");
                Assert.Equal(serial.Length, racing.Length);

                for (int i = 0; i < serial.Length; i++)
                    Assert.Equal(serial[i], racing[i]);
            }
        }

        /// <summary>
        /// A height that is not one of its column's floors is not cached, and still gets the answer the
        /// geometry gives: the same one asked alone or between two floors that are cached.
        /// </summary>
        [Fact]
        public void AHeightOffTheColumnsFloorsGetsTheSameAnswerCachedOrNot()
        {
            var alone = AreaGenerator.SampleClearance(Mixed(), Mins, Maxs, 10, 10, 30f);
            var between = AreaGenerator.SampleClearance(Mixed(), Mins, Maxs, 10, 10, 0f, 30f, 84f, 30f);

            Assert.Equal(alone[0], between[1]);
            Assert.Equal(alone[0], between[3]);
        }

        /// <summary>
        /// Steps recorded from many threads at once - several cells of one column among them, as two
        /// workers expanding two floors of a column in the same level do - are all found again, and
        /// matched on the exact heights at both ends, as the dictionary keyed on the cell matched them.
        /// </summary>
        [Fact]
        public void PassagesRecordedConcurrentlyAreAllFound()
        {
            var expanded = new List<(int, int, float, (int, int, float)[])>();
            var asked = new List<(int Gx, int Gy, float Z, int Dx, int Dy, float ToZ)>();
            var expected = new List<bool>();

            void Ask(int gx, int gy, float z, int dx, int dy, float toZ, bool swept)
            {
                asked.Add((gx, gy, z, dx, dy, toZ));
                expected.Add(swept);
            }

            for (int gx = 0; gx < 20; gx++)
            {
                for (int gy = 0; gy < 20; gy++)
                {
                    // Three floors of every column, in three different bands.
                    foreach (float z in new[] { 0f, 84f, 200.5f })
                        expanded.Add((gx, gy, z, [(1, 0, z + 1f), (0, -1, z - 30f)]));

                    Ask(gx, gy, 84f, 1, 0, 85f, swept: true);
                    Ask(gx, gy, 200.5f, 0, -1, 170.5f, swept: true);
                    Ask(gx, gy, 0f, 1, 0, 1f, swept: true);

                    // The right cell and direction but another destination, a direction never
                    // swept, and a height in a recorded cell's band that is not the cell's own.
                    Ask(gx, gy, 84f, 1, 0, 84f, swept: false);
                    Ask(gx, gy, 84f, -1, 0, 85f, swept: false);
                    Ask(gx, gy, 85f, 1, 0, 85f, swept: false);

                    // A band nothing was recorded in.
                    Ask(gx, gy, 300f, 1, 0, 301f, swept: false);
                }
            }

            // And outside the grid, where nothing is ever recorded.
            Ask(-1, 0, 0f, 1, 0, 1f, swept: false);
            Ask(0, 1000, 0f, 1, 0, 1f, swept: false);

            var found = AreaGenerator.SamplePassages(Mixed(), Mins, Maxs, expanded, Workers, asked);

            Assert.Equal(expected, found);
        }

        /// <summary>
        /// The visited set, against the dictionary it replaced: a cell is seen when its own band of its
        /// column is claimed, or the band either side holds a floor within half a band of it.
        /// </summary>
        private sealed class Reference
        {
            private readonly Dictionary<(int, int, int), float> claimed = [];

            private static int Band(float z) => (int)MathF.Round(z / 8f);

            public bool Seen(int gx, int gy, float z)
            {
                int band = Band(z);

                return claimed.ContainsKey((gx, gy, band)) ||
                       claimed.TryGetValue((gx, gy, band - 1), out float below) && z - below < 4f ||
                       claimed.TryGetValue((gx, gy, band + 1), out float above) && above - z < 4f;
            }

            public bool Claim(int gx, int gy, float z) => !Seen(gx, gy, z) && claimed.TryAdd((gx, gy, Band(z)), z);
        }

        /// <summary>
        /// Random claims and questions, many to a column and clustered around band edges, answered
        /// exactly as the dictionary answered them - in the order given, since which floor of a pair
        /// is claimed first is what decides which one the flood keeps.
        /// </summary>
        [Fact]
        public void TheVisitedSetAnswersAsTheDictionaryDid()
        {
            var random = new Random(32);
            const int Columns = 7, Rows = 5;

            for (int round = 0; round < 20; round++)
            {
                (int, int, float) Pick()
                {
                    // Heights near band edges (multiples of 8 plus 4), where the rule matters.
                    float edge = random.Next(-40, 40) * 8f + 4f;
                    return (random.Next(Columns), random.Next(Rows), edge + (float)(random.NextDouble() * 12 - 6));
                }

                var claims = Enumerable.Range(0, 400).Select(_ => Pick()).ToList();
                var asks = Enumerable.Range(0, 400).Select(_ => Pick()).ToList();

                var reference = new Reference();
                bool[] expectedClaims = [.. claims.Select(c => reference.Claim(c.Item1, c.Item2, c.Item3))];
                bool[] expectedSeen = [.. asks.Select(c => reference.Seen(c.Item1, c.Item2, c.Item3))];

                var (granted, seen) = AreaGenerator.SampleClaims(Columns, Rows, claims, asks);

                Assert.Equal(expectedClaims, granted);
                Assert.Equal(expectedSeen, seen);
                Assert.Contains(true, granted);
                Assert.Contains(false, granted);
            }
        }

        /// <summary>
        /// PR #32's case directly: the terrain and the brush face 1.3 units under it, either side of the
        /// band edge at -196, are one cell, and the lower one - claimed first in the flood's order - is
        /// the one kept. Floors further apart across the same edge are two.
        /// </summary>
        [Fact]
        public void TwoFloorsAcrossABandEdgeAreOneCell()
        {
            var (granted, seen) = AreaGenerator.SampleClaims(4, 4,
                [(1, 1, -197f), (1, 1, -195.70874f), (2, 2, -199.5f), (2, 2, -192.5f)],
                [(1, 1, -195.70874f), (1, 1, -190f), (2, 2, -194f)]);

            Assert.Equal([true, false, true, true], granted);
            Assert.Equal([true, false, true], seen);
        }

        /// <summary>A cell outside the grid would land in some other column's chain, so it is refused.</summary>
        [Fact]
        public void ACellOutsideTheGridIsRefused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                AreaGenerator.SampleClaims(4, 4, [(4, 0, 0f)], []));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                AreaGenerator.SampleClaims(4, 4, [], [(-1, 1, 0f)]));
        }

        /// <summary>The mesh the flood makes, and its account of the cells, links and nodes behind it.</summary>
        private static (byte[] Mesh, int Visited, string Account) Flood(int threads)
        {
            NavConcurrency.MaxThreads = threads;

            var bsp = TestWorlds.Map("""
                {
                "classname" "worldspawn"
                }
                {
                "classname" "info_player_start"
                "origin" "500 500 1"
                }
                {
                "classname" "info_player_start"
                "origin" "500 500 85"
                }
                """,
                [(Mins, Maxs)]);

            // Wide enough to flood: crouch-height ground under the slab, standing room on top of it.
            var nav = new NavFile();
            var result = AreaGenerator.Generate(nav, CrouchClearanceTests.SlabInSlot(100f, 900f), bsp);

            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true)) nav.Write(w);

            return (ms.ToArray(), result.Visited,
                $"visited {result.Visited}, uncovered {result.Uncovered}; {string.Join("; ", result.Notes)}");
        }

        /// <summary>
        /// The whole flood, from the caches up: one thread against every core, and repeated runs. The
        /// merge folds the slab's top into a few large areas, so the account of the nodes and links
        /// the areas were built from is compared as well as the mesh.
        /// </summary>
        [Fact]
        public void TheFloodIsTheSameAtOneThreadAndAtEvery()
        {
            var single = Flood(1);
            Assert.True(single.Visited > 1000, $"the flood visited {single.Visited} cells: the test proves little");

            for (int run = 0; run < 3; run++)
            {
                var parallel = Flood(Workers);

                Assert.Equal(single.Account, parallel.Account);
                Assert.Equal(single.Mesh, parallel.Mesh);
            }
        }
    }
}
