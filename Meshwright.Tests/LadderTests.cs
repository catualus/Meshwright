using System.Collections.Generic;
using System.Linq;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// Finding ladders and wiring them to the mesh.
    ///
    /// Four separate ways a climbable ladder came out unclimbable: a top area looked for only within a
    /// step of the top of the brush, when real landings sit up to a player's height under it; ladder
    /// ends left on areas the clip or the jump-area stitch then deleted, and ladder links scrubbed with
    /// area ids; a ladder stacked from two brushes treated as two half ladders; and HL2-style
    /// func_useableladder entities not read at all.
    /// </summary>
    public class LadderTests
    {
        private const int ContentsLadder = 0x20000000;

        private static LadderFinder.LadderBrush Brush(float minX, float minY, float minZ,
            float maxX, float maxY, float maxZ)
        {
            bool thinOnX = maxX - minX <= maxY - minY;

            return new LadderFinder.LadderBrush
            {
                Material = "test",
                Mins = new BspFile.Vector3(minX, minY, minZ),
                Maxs = new BspFile.Vector3(maxX, maxY, maxZ),
                Normal = thinOnX ? new BspFile.Vector3(1, 0, 0) : new BspFile.Vector3(0, 1, 0),
                DirectionIsProvisional = true,
            };
        }

        // ---------------------------------------------------------------- stacked brushes

        [Fact]
        public void TwoBrushesStackedWithASmallGapAreOneLadder()
        {
            var merged = LadderFinder.MergeStacked([Brush(-16, -2, 0, 16, 2, 100), Brush(-16, -2, 104, 16, 2, 200)]);

            var ladder = Assert.Single(merged);
            Assert.Equal(0f, ladder.Mins.Z);
            Assert.Equal(200f, ladder.Maxs.Z);
            Assert.Equal(2, ladder.Pieces);
        }

        /// <summary>
        /// The same pair split at a floor - ground beside the ladder where the pieces meet - stays two
        /// ladders. Each already has ground at both ends, and joined, the climb would pass the floor
        /// with no way off onto it.
        /// </summary>
        [Fact]
        public void PiecesSplitAtAFloorStaySeparate()
        {
            var pieces = new List<LadderFinder.LadderBrush> { Brush(-16, -2, 0, 16, 2, 100), Brush(-16, -2, 104, 16, 2, 200) };

            Assert.Equal(2, LadderFinder.MergeStacked(pieces, TestWorlds.Ground(100)).Count);
            Assert.Single(LadderFinder.MergeStacked(pieces, TestWorlds.Ground(0)));
        }

        [Fact]
        public void LaddersFarApartVerticallyStaySeparate()
        {
            Assert.Equal(2, LadderFinder.MergeStacked([Brush(-16, -2, 0, 16, 2, 100), Brush(-16, -2, 140, 16, 2, 240)]).Count);
        }

        [Fact]
        public void LaddersSideBySideStaySeparate()
        {
            Assert.Equal(2, LadderFinder.MergeStacked([Brush(-16, -2, 0, 16, 2, 100), Brush(16, -2, 100, 48, 2, 200)]).Count);
        }

        [Fact]
        public void LaddersOnDifferentAxesStaySeparate()
        {
            Assert.Equal(2, LadderFinder.MergeStacked([Brush(-16, -2, 0, 16, 2, 100), Brush(-2, -16, 100, 2, 16, 200)]).Count);
        }

        /// <summary>
        /// From the map itself: two CONTENTS_LADDER brushes, the upper one too short to count as a
        /// ladder alone, come back as one ladder of their combined height.
        /// </summary>
        [Fact]
        public void TheFinderJoinsStackedBrushesBeforeJudgingTheirHeight()
        {
            var bsp = TestWorlds.Map("""{ "classname" "worldspawn" }""",
                [(new BspFile.Vector3(-1000, -1000, -500), new BspFile.Vector3(1000, 1000, 500))],
                new TestWorlds.Box(new BspFile.Vector3(-16, -2, 0), new BspFile.Vector3(16, 2, 100), ContentsLadder),
                new TestWorlds.Box(new BspFile.Vector3(-16, -2, 102), new BspFile.Vector3(16, 2, 120), ContentsLadder));

            var ladder = Assert.Single(LadderFinder.Find(bsp));
            Assert.Equal(120f, ladder.Height, 0.01f);
            Assert.Equal(2, ladder.Pieces);
        }

        // ---------------------------------------------------------------- func_useableladder

        /// <summary>gm_construct's, which Hammer wrote in world space 24 units from the origin.</summary>
        private const string Useable = """
            {
            "classname" "func_useableladder"
            "origin" "-2920 -1052 -96"
            "point0" "-2920 -1076 -104.64"
            "point1" "-2920 -1076 -7.36"
            }
            """;

        [Fact]
        public void AUseableLadderWithWorldPointsIsWhereItsPointsAre()
        {
            var ladder = Assert.Single(LadderFinder.FindUseable(Useable, []));

            Assert.False(ladder.AxisKnown);
            Assert.Equal(-2920f, ladder.Bottom.X, 0.01f);
            Assert.Equal(-1076f, ladder.Bottom.Y, 0.01f);
            Assert.Equal(-104.64f, ladder.Bottom.Z, 0.01f);
            Assert.Equal(-7.36f, ladder.Top.Z, 0.01f);
        }

        [Fact]
        public void AUseableLadderWithPointsRelativeToItsOriginIsMovedThere()
        {
            const string lump = """
                {
                "classname" "func_useableladder"
                "origin" "2000 3000 100"
                "point0" "0 -24 0"
                "point1" "0 -24 128"
                }
                """;

            var ladder = Assert.Single(LadderFinder.FindUseable(lump, []));

            Assert.Equal(2000f, ladder.Bottom.X, 0.01f);
            Assert.Equal(2976f, ladder.Bottom.Y, 0.01f);
            Assert.Equal(100f, ladder.Bottom.Z, 0.01f);
            Assert.Equal(228f, ladder.Top.Z, 0.01f);
        }

        /// <summary>gm_construct's ladder is a brush as well, and one climb gets one nav ladder.</summary>
        [Fact]
        public void AUseableLadderABrushAlreadyCoversIsSkipped()
        {
            var brush = Brush(-2931.5f, -1060, -144, -2908.5f, -1054, -16);

            Assert.Empty(LadderFinder.FindUseable(Useable, [brush]));
        }

        // ---------------------------------------------------------------- top areas

        /// <summary>A ladder facing north from a wall: floor to the north at its foot, landing south at the top.</summary>
        private static LadderFinder.LadderBrush WallLadder(float top) => Brush(-16, -2, 0, 16, 2, top);

        private static NavFile Mesh(params NavArea[] areas)
        {
            var nav = new NavFile();
            nav.Areas.AddRange(areas);
            return nav;
        }

        /// <summary>
        /// The regression: a landing 44 units under the top of the brush, as one of rp_downtown_tits'
        /// is, is the ladder's top area.
        /// </summary>
        [Fact]
        public void ALandingWellUnderTheTopOfTheBrushIsItsTopArea()
        {
            var floor = TestWorlds.Area(1, -100, -200, 100, -10, 0);
            var landing = TestWorlds.Area(2, -100, 10, 100, 200, 100);
            var nav = Mesh(floor, landing);

            LadderBuilder.Build(nav, [WallLadder(144)]);

            var ladder = Assert.Single(nav.Ladders);
            Assert.Equal(1u, ladder.BottomAreaId);
            Assert.Equal(2u, ladder.TopForwardAreaId);
            Assert.Contains(ladder.Id, landing.Ladders[1]);
        }

        /// <summary>A floor further down than a player is tall is somewhere the ladder passes, not its top.</summary>
        [Fact]
        public void AFloorMoreThanAPlayerBelowTheTopIsNotItsTopArea()
        {
            var nav = Mesh(TestWorlds.Area(1, -100, -200, 100, -10, 0), TestWorlds.Area(2, -100, 10, 100, 200, 60));

            LadderBuilder.Build(nav, [WallLadder(144)]);

            var ladder = Assert.Single(nav.Ladders);
            Assert.Equal(0u, ladder.TopForwardAreaId);
        }

        /// <summary>
        /// With the landing taken from the highest area in the window, a landing beats the floor of
        /// the storey below it.
        /// </summary>
        [Fact]
        public void TheLandingWinsOverAFloorBelowIt()
        {
            var floor = TestWorlds.Area(1, -100, -200, 100, -10, 0);
            var lower = TestWorlds.Area(2, -100, 10, 100, 200, 90);
            var landing = TestWorlds.Area(3, -100, 10, 100, 200, 140);

            var nav = Mesh(floor, lower, landing);

            LadderBuilder.Build(nav, [WallLadder(144)]);

            Assert.Equal(3u, Assert.Single(nav.Ladders).TopForwardAreaId);
        }

        /// <summary>
        /// The floor at the foot of a short ladder is inside the landing window, and is still not a top:
        /// a ladder whose top leads back to its own base leads nowhere.
        /// </summary>
        [Fact]
        public void AShortLaddersOwnFloorIsNotItsTop()
        {
            var nav = Mesh(TestWorlds.Area(1, -200, -200, 200, 200, 0));

            LadderBuilder.Build(nav, [WallLadder(50)]);

            var ladder = Assert.Single(nav.Ladders);
            Assert.Equal(1u, ladder.BottomAreaId);
            Assert.Equal(0u, ladder.TopForwardAreaId);
            Assert.Equal(0u, ladder.TopBehindAreaId);
            Assert.Equal(0u, ladder.TopLeftAreaId);
            Assert.Equal(0u, ladder.TopRightAreaId);
        }

        /// <summary>
        /// A func_useableladder faces away from the landing it is climbed onto, and the nav ladder sits
        /// on its face, half a player back from where the climber stands.
        /// </summary>
        [Fact]
        public void AUseableLadderFacesAwayFromItsLanding()
        {
            var floor = TestWorlds.Area(1, -200, -200, 200, 200, 0);
            var landing = TestWorlds.Area(2, -100, 20, 100, 200, 96);
            var nav = Mesh(floor, landing);

            var useable = new LadderFinder.LadderBrush
            {
                Material = "func_useableladder",
                Mins = new BspFile.Vector3(-16, -16, 0),
                Maxs = new BspFile.Vector3(16, 16, 100),
                Normal = new BspFile.Vector3(1, 0, 0),
                DirectionIsProvisional = true,
                AxisKnown = false,
            };

            LadderBuilder.Build(nav, [useable]);

            var ladder = Assert.Single(nav.Ladders);
            Assert.Equal((uint)NavGeometry.North, ladder.Direction);
            Assert.Equal(1u, ladder.BottomAreaId);
            Assert.Equal(2u, ladder.TopForwardAreaId);
            Assert.Equal(16f, ladder.Top[1], 0.01f);
        }

        // ---------------------------------------------------------------- ends on deleted areas

        /// <summary>
        /// Solid ground under z = 0 with everything east of x = -50 walled in, from leaf contents alone.
        /// </summary>
        private static BspVisibility WalledEast()
        {
            var planes = new[] { TestWorlds.Plane(0, 0, 1, 0), TestWorlds.Plane(1, 0, 0, -50) };

            // Leaf n is referenced as -(n) - 1.
            var nodes = new[]
            {
                new BspVisibility.Node { PlaneNum = 0, Child0 = 1, Child1 = -1 },  // above / below the ground
                new BspVisibility.Node { PlaneNum = 1, Child0 = -2, Child1 = -3 }, // in the wall / west of it
            };

            var leafs = new[]
            {
                new BspVisibility.Leaf { Contents = 1 }, // 0: the ground
                new BspVisibility.Leaf { Contents = 1 }, // 1: the wall
                new BspVisibility.Leaf { Contents = 0 }, // 2: open air
            };

            return BspVisibility.FromGeometry(planes, nodes, leafs, [], [], new int[]?[leafs.Length]);
        }

        /// <summary>
        /// The regression in the clip's cleanup: discarding area 1 must not cut the links to ladder 1,
        /// which share nothing but the number. The ladder end on the discarded area is what goes.
        /// </summary>
        [Fact]
        public void DiscardingASliverLeavesLadderLinksAloneAndClearsTheEndOnIt()
        {
            var buried = TestWorlds.Area(1, 0, 0, 25, 100, 0);
            var open = TestWorlds.Area(2, -200, 0, -100, 100, 0);
            open.Ladders[1].Add(1);

            var nav = Mesh(buried, open);
            nav.Ladders.Add(new NavLadder { Id = 1, BottomAreaId = 1, TopForwardAreaId = 2 });

            var result = AreaClipper.Clip(nav, WalledEast(), AreaGenerator.StepSize);

            Assert.Equal(1, result.Discarded);
            Assert.Equal([1u], Assert.Single(nav.Areas).Ladders[1]);
            Assert.Equal(0u, nav.Ladders[0].BottomAreaId);
            Assert.Equal(2u, nav.Ladders[0].TopForwardAreaId);
        }

        [Fact]
        public void StitchingOutAJumpAreaClearsALadderEndOnIt()
        {
            var jump = TestWorlds.Area(1, 0, 0, 25, 25, 0);
            jump.AttributeFlags = (int)NavAttributes.Jump;

            var nav = Mesh(jump, TestWorlds.Area(2, 25, 0, 50, 25, 0));
            nav.Ladders.Add(new NavLadder { Id = 1, BottomAreaId = 1, TopLeftAreaId = 2 });

            JumpAreaStitcher.Stitch(nav);

            Assert.Equal(0u, nav.Ladders[0].BottomAreaId);
            Assert.Equal(2u, nav.Ladders[0].TopLeftAreaId);
        }
    }
}
