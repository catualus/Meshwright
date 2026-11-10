using System;
using System.IO;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That a clip brush every walker collides with is ground, and one only NPCs collide with is not.
    ///
    /// TF2 smooths its stairs with a tools/toolsclip ramp laid over the steps, and lays clip slabs a few
    /// units over sunken floors. Those brushes carry CONTENTS_PLAYERCLIP and CONTENTS_MONSTERCLIP both.
    /// The ground mask left monsterclip out - rightly, for NPC clip on its own - and so left these out
    /// too, while every headroom test still met them through the monsterclip bit. The steps under the
    /// ramp had the ramp in their headroom and the ramp itself was not ground, so the flood had nowhere
    /// to stand: 919 of the 1,128 Valve routes ctf_2fort failed broke first on such a surface.
    /// </summary>
    public class ClipBrushTests
    {
        private const int Solid = 0x1;
        private const int PlayerClip = 0x10000;
        private const int MonsterClip = 0x20000;

        private static readonly BspFile.Vector3 Mins = new(0, 0, -500);
        private static readonly BspFile.Vector3 Maxs = new(1000, 1000, 500);

        /// <summary>
        /// Solid ground with its top at z = 0, under a slab of <paramref name="contents"/> from z = 2 to
        /// z = 12 - a floor with a clip brush laid just over it, as on 2fort's sewer floor.
        /// </summary>
        private static BspVisibility ClipOverGround(int contents)
        {
            var planes = new[]
            {
                Plane(0, 0, 1, 0),        // 0: ground top, and the split above/below it
                Plane(0, 0, 1, 2),        // 1: slab underside, as a splitting plane
                Plane(0, 0, 1, 12),       // 2: slab top, as a splitting plane and a side
                Plane(0, 0, -1, 500),     // 3: ground underside, z >= -500
                Plane(-1, 0, 0, 5000),    // 4: x >= -5000
                Plane(1, 0, 0, 5000),     // 5: x <= 5000
                Plane(0, -1, 0, 5000),    // 6: y >= -5000
                Plane(0, 1, 0, 5000),     // 7: y <= 5000
                Plane(0, 0, -1, -2),      // 8: slab underside as a side, z >= 2
            };

            // Leaf n is referenced as -(n) - 1.
            var nodes = new[]
            {
                new BspVisibility.Node { PlaneNum = 0, Child0 = 1, Child1 = -1 },  // above / in the ground
                new BspVisibility.Node { PlaneNum = 1, Child0 = 2, Child1 = -2 },  // at or above the slab / the gap
                new BspVisibility.Node { PlaneNum = 2, Child0 = -3, Child1 = -4 }, // above the slab / in it
            };

            var leafs = new[]
            {
                new BspVisibility.Leaf { Contents = Solid },    // 0: the ground
                new BspVisibility.Leaf { Contents = 0 },        // 1: the gap between ground and slab
                new BspVisibility.Leaf { Contents = 0 },        // 2: open air above the slab
                new BspVisibility.Leaf { Contents = contents }, // 3: the slab
            };

            var brushes = new[]
            {
                new BspFile.Brush { FirstSide = 0, NumSides = 6, Contents = Solid },
                new BspFile.Brush { FirstSide = 6, NumSides = 6, Contents = contents },
            };

            var sides = new[]
            {
                Side(0), Side(3), Side(4), Side(5), Side(6), Side(7),
                Side(2), Side(8), Side(4), Side(5), Side(6), Side(7),
            };

            var leafBrushes = new int[]?[] { new[] { 0 }, null, null, new[] { 1 } };

            return BspVisibility.FromGeometry(planes, nodes, leafs, brushes, sides, leafBrushes);
        }

        private static BspFile.Plane Plane(float x, float y, float z, float distance)
            => new() { Normal = new BspFile.Vector3(x, y, z), Distance = distance };

        private static BspFile.BrushSide Side(int plane) => new() { PlaneNum = (ushort)plane };

        /// <summary>The floors a downward column through the slab finds, highest first.</summary>
        private static float[] Floors(BspVisibility world)
        {
            var into = new BspVisibility.FloorSample[4];
            int count = world.EnumerateFloors(10, 10, 200, -100, into);
            var heights = new float[count];
            for (int i = 0; i < count; i++) heights[i] = into[i].Z;
            return heights;
        }

        /// <summary>
        /// The case that was broken: the top of a player-and-NPC clip slab is a floor, and a walker
        /// stands on it.
        /// </summary>
        [Fact]
        public void APlayerAndNpcClipSlabIsGround()
        {
            var world = ClipOverGround(PlayerClip | MonsterClip);

            Assert.Equal(12f, Floors(world)[0], 2);
            Assert.Equal(AreaGenerator.Clearance.Stand,
                AreaGenerator.SampleClearance(world, Mins, Maxs, 10, 10, 12f)[0]);
        }

        /// <summary>
        /// And the floor under it, two units down and inside nothing but the gap, has no room for a
        /// body: the clip still blocks headroom, as it always did.
        /// </summary>
        [Fact]
        public void TheFloorUnderAClipSlabIsStillNotStandable()
        {
            var world = ClipOverGround(PlayerClip | MonsterClip);

            Assert.Equal(AreaGenerator.Clearance.None,
                AreaGenerator.SampleClearance(world, Mins, Maxs, 10, 10, 0f)[0]);
        }

        /// <summary>
        /// The control the ground mask's monsterclip exclusion exists for: NPC clip on its own stops
        /// NPCs and not players, and its top is not a floor. Unchanged.
        /// </summary>
        [Fact]
        public void NpcClipOnItsOwnIsStillNotGround()
        {
            var world = ClipOverGround(MonsterClip);

            Assert.DoesNotContain(12f, Floors(world));
            Assert.Equal(AreaGenerator.Clearance.None,
                AreaGenerator.SampleClearance(world, Mins, Maxs, 10, 10, 12f)[0]);
        }

        /// <summary>
        /// Player clip alone stops players and nothing else. For Garry's Mod, whose NextBots walk
        /// through it, it stays neither floor nor wall; for TF2, whose bots are players, it is both -
        /// pl_badwater's player-clip ramp over a staircase is the surface Valve's mesh lies on.
        /// </summary>
        [Fact]
        public void PlayerClipAloneIsGroundOnlyWhenTheWalkersArePlayers()
        {
            bool original = NavConstants.PlayerClipStopsEveryWalker;

            try
            {
                NavConstants.PlayerClipStopsEveryWalker = false;
                var npcs = ClipOverGround(PlayerClip);

                Assert.DoesNotContain(12f, Floors(npcs));
                Assert.Equal(AreaGenerator.Clearance.Stand,
                    AreaGenerator.SampleClearance(npcs, Mins, Maxs, 10, 10, 0f)[0]);

                NavConstants.PlayerClipStopsEveryWalker = true;
                var players = ClipOverGround(PlayerClip);

                Assert.Equal(12f, Floors(players)[0], 2);
                Assert.Equal([AreaGenerator.Clearance.Stand, AreaGenerator.Clearance.None],
                    AreaGenerator.SampleClearance(players, Mins, Maxs, 10, 10, 12f, 0f));
            }
            finally
            {
                NavConstants.PlayerClipStopsEveryWalker = original;
            }
        }

        /// <summary>
        /// TF2 is recognised from the game name Compile Pal passes, and anything else leaves player clip
        /// alone. The run itself fails at once - there is no output path - but the game is applied first.
        /// </summary>
        [Theory]
        [InlineData("Team Fortress 2", true)]
        [InlineData("Garry's Mod", false)]
        public void TheGameNameDecidesWhetherPlayerClipStopsEveryWalker(string game, bool expected)
        {
            bool original = NavConstants.PlayerClipStopsEveryWalker;
            bool counterStrike = NavConstants.UseCounterStrikeLimits;
            var (stdout, stderr) = (Console.Out, Console.Error);

            try
            {
                NavConstants.PlayerClipStopsEveryWalker = !expected;
                Console.SetOut(TextWriter.Null);
                Console.SetError(TextWriter.Null);

                Program.Main(["generate", "missing.bsp", "-o", "-gamename", game]);

                Assert.Equal(expected, NavConstants.PlayerClipStopsEveryWalker);
            }
            finally
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                NavConstants.PlayerClipStopsEveryWalker = original;
                NavConstants.UseCounterStrikeLimits = counterStrike;
            }
        }

        /// <summary>
        /// The bit marking a player-and-NPC clip brush is Meshwright's own, added to a copy: the
        /// <see cref="BspFile"/>'s brushes, which other readers share, keep the contents vbsp wrote.
        /// </summary>
        [Fact]
        public void TaggingFullClipLeavesTheSourceBrushesAlone()
        {
            var source = new[]
            {
                new BspFile.Brush { Contents = PlayerClip | MonsterClip },
                new BspFile.Brush { Contents = MonsterClip },
                new BspFile.Brush { Contents = PlayerClip },
            };

            var tagged = BspVisibility.WithFullClip(source);

            Assert.Equal(PlayerClip | MonsterClip | BspVisibility.ContentsFullClip, tagged[0].Contents);
            Assert.Equal(MonsterClip, tagged[1].Contents);
            Assert.Equal(PlayerClip, tagged[2].Contents);
            Assert.Equal(PlayerClip | MonsterClip, source[0].Contents);

            Assert.Equal(PlayerClip | BspVisibility.ContentsFullClip,
                BspVisibility.WithFullClip(source, playerClipStopsEveryWalker: true)[2].Contents);
        }
    }
}
