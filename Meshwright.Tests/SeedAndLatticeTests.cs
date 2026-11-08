using System;
using System.Linq;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// Where the flood starts, and the grid it samples on.
    ///
    /// Two faults that never looked like faults. A spawn standing on its floor seeded the flood at its
    /// own height, which hashes to the same cell as the floor under it, and that floor was then never
    /// accepted: a 25 unit hole under every spawn, ladder dismount and lift stop. And the grid started
    /// at the corner of the world, so areas generated to finish an engine-made mesh had their edges
    /// wherever that corner happened to fall rather than on the multiples of 25 the engine's own meshes
    /// use, and overlapped every seam too far to connect it.
    /// </summary>
    public class SeedAndLatticeTests
    {
        private static NavFile Generate(string entities, BspFile.Vector3 mins, BspFile.Vector3 maxs,
            bool squareUp = true)
        {
            var bsp = TestWorlds.Map(entities, [(mins, maxs)]);
            var nav = new NavFile();

            AreaGenerator.Generate(nav, TestWorlds.Ground(), bsp, squareUp: squareUp);
            return nav;
        }

        private static string Spawn(float x, float y, float z) => $$"""
            {
            "classname" "worldspawn"
            }
            {
            "classname" "info_player_start"
            "origin" "{{x}} {{y}} {{z}}"
            }
            """;

        /// <summary>
        /// The regression. A spawn a unit above flat ground - as mappers place them, and as
        /// gm_construct's all are - has ground under it in the mesh.
        /// </summary>
        [Fact]
        public void ASpawnStandingOnItsFloorHasAnAreaUnderIt()
        {
            var nav = Generate(Spawn(103, 103, 1),
                new BspFile.Vector3(-197, -197, -500), new BspFile.Vector3(300, 300, 500));

            Assert.Contains(nav.Areas, a => NavGeometry.Contains(a, 103, 103) &&
                                            MathF.Abs(NavGeometry.SurfaceZ(a, 103, 103)) < 1f);
        }

        /// <summary>
        /// The same with the spawn floating a little way up, which the engine drops onto the ground
        /// before seeding from it and so does this.
        /// </summary>
        [Fact]
        public void ASpawnAboveItsFloorIsSetDownOnIt()
        {
            var nav = Generate(Spawn(103, 103, 30),
                new BspFile.Vector3(-197, -197, -500), new BspFile.Vector3(300, 300, 500));

            Assert.Contains(nav.Areas, a => NavGeometry.Contains(a, 103, 103) &&
                                            MathF.Abs(NavGeometry.SurfaceZ(a, 103, 103)) < 1f);
        }

        /// <summary>
        /// The regression for seeded runs: in a world whose corner sits 3 units off a multiple of 25 -
        /// gm_flatgrass's does - areas generated beside an existing mesh with its edges on multiples of
        /// 25, as the engine's are, have theirs there too.
        /// </summary>
        [Fact]
        public void AreasGeneratedBesideAnEngineMeshShareItsGrid()
        {
            var bsp = TestWorlds.Map(Spawn(103, 103, 1),
                [(new BspFile.Vector3(-197, -197, -500), new BspFile.Vector3(300, 300, 500))]);

            var nav = new NavFile();
            nav.Areas.Add(TestWorlds.Area(1, 250, 250, 275, 275, 0));

            // Without squaring up, which splits long areas evenly and so puts the edges between the
            // pieces wherever the length divides; those are inside the new ground, not on a seam.
            var result = AreaGenerator.Generate(nav, TestWorlds.Ground(), bsp, squareUp: false);

            var generated = nav.Areas.Where(a => a.Id >= result.FirstGeneratedId).ToList();
            Assert.NotEmpty(generated);

            foreach (var area in generated)
            {
                foreach (float edge in new[] { area.NwCorner[0], area.NwCorner[1], area.SeCorner[0], area.SeCorner[1] })
                    Assert.Equal(0f, Distance(edge, 0f), 0.01f);
            }
        }

        /// <summary>
        /// A run from scratch keeps the grid it always had, from the world's corner: nodes on it, edges
        /// half a step either side. Moving it gains nothing a seam needs and changes every mesh.
        /// </summary>
        [Fact]
        public void AFreshRunStillSamplesFromTheCornerOfTheWorld()
        {
            var nav = Generate(Spawn(103, 103, 1),
                new BspFile.Vector3(-197, -197, -500), new BspFile.Vector3(300, 300, 500), squareUp: false);

            Assert.NotEmpty(nav.Areas);

            foreach (var area in nav.Areas)
            {
                foreach (float edge in new[] { area.NwCorner[0], area.NwCorner[1], area.SeCorner[0], area.SeCorner[1] })
                    Assert.Equal(0f, Distance(edge, -197f - AreaGenerator.StepSize / 2f), 0.01f);
            }
        }

        /// <summary>How far <paramref name="value"/> is from the nearest point of a grid through <paramref name="phase"/>.</summary>
        private static float Distance(float value, float phase)
        {
            float r = (value - phase) % AreaGenerator.StepSize;
            if (r < 0) r += AreaGenerator.StepSize;

            // 24.999 is as good as 0.
            return MathF.Min(r, AreaGenerator.StepSize - r);
        }

        [Theory]
        [InlineData(-4096f, 0f, -4112.5f)]
        [InlineData(-4112.5f, 0f, -4112.5f)]
        [InlineData(15f, 0f, 12.5f)]
        [InlineData(12.5f, 0f, 12.5f)]
        [InlineData(0f, 0f, -12.5f)]
        [InlineData(3f, 15f, 2.5f)]
        public void TheGridStartsWhereEdgesFallOnThePhase(float worldMin, float edgePhase, float expected)
        {
            float start = AreaGenerator.LatticeStart(worldMin, edgePhase);

            Assert.Equal(expected, start);
            Assert.True(start <= worldMin && start > worldMin - AreaGenerator.StepSize);
        }

        [Fact]
        public void TheEdgePhaseIsTheOneMostEdgesShare()
        {
            var areas = new[]
            {
                TestWorlds.Area(1, 0, 3, 50, 28, 0),
                TestWorlds.Area(2, 50, 28, 100, 53, 0),
                TestWorlds.Area(3, 112.5f, 3, 137.5f, 28, 0),   // hand-placed, off the grid in X
            };

            Assert.Equal(0f, AreaGenerator.EdgePhase(areas, x: true));
            Assert.Equal(3f, AreaGenerator.EdgePhase(areas, x: false));
            Assert.Null(AreaGenerator.EdgePhase([], x: true));
        }
    }
}
