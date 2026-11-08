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
    }
}
