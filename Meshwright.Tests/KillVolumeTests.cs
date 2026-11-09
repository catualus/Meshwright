using System;
using System.IO;
using System.Linq;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That ground inside a trigger_hurt which kills on contact gets no mesh, and that ground inside a
    /// hurt that is off, filtered to one team or too weak to kill still does.
    ///
    /// Every trigger is non-solid, so the flood walked down into death pits like any other drop: on
    /// pl_upward it meshed 1,510 areas of out-of-bounds valley under a 9,999-damage volume that Valve's
    /// mesh leaves all but empty, and that ground was reachable only by falling in.
    /// </summary>
    public class KillVolumeTests
    {
        private const int Solid = 0x1;
        private const int Models = 64;

        private static int[] KillVolumes(string lump)
            => BspModels.SelectKillVolumes(lump, Models).Select(v => v.Index).ToArray();

        private const string Pit = "\"classname\" \"trigger_hurt\" \"model\" \"*3\" \"damage\" \"9999\" " +
                                   "\"damagetype\" \"32\" \"spawnflags\" \"1\" \"StartDisabled\" \"0\"";

        [Fact]
        public void AnAlwaysOnLethalHurtIsAKillVolume()
        {
            Assert.Equal([3], KillVolumes("{ " + Pit + " }"));
            Assert.Equal([3], KillVolumes("{ \"classname\" \"trigger_hurt\" \"model\" \"*3\" \"damage\" \"100\" \"spawnflags\" \"64\" }"));
        }

        /// <summary>
        /// The volumes Valve's meshes do cover: map logic switches it on later (pl_badwater's kill brush
        /// between its closing doors), it hurts one team (TF2's spawn-room kills), it rides a payload
        /// cart, it is a slow hazard rather than a death, or it heals.
        /// </summary>
        [Theory]
        [InlineData("\"StartDisabled\" \"1\"")]
        [InlineData("\"parentname\" \"minecart_tracktrain\"")]
        [InlineData("\"filtername\" \"filter_team_red\"")]
        public void AHurtThatIsOffMovesOrIsFilteredIsNot(string extra)
        {
            string lump = "{ " + Pit.Replace("\"StartDisabled\" \"0\"", "") + " " + extra + " }\n" +
                          "{ \"classname\" \"filter_activator_tfteam\" \"targetname\" \"filter_team_red\" \"TeamNum\" \"2\" }";

            Assert.Empty(KillVolumes(lump));
        }

        [Theory]
        [InlineData("10", "1")]     // a slow hazard, walked through and out of
        [InlineData("-50", "1")]    // a healing volume
        [InlineData("9999", "2")]   // NPCs only
        [InlineData("9999", "0")]   // touches nothing
        public void AHurtThatCannotKillAPlayerIsNot(string damage, string spawnflags)
        {
            Assert.Empty(KillVolumes(
                $"{{ \"classname\" \"trigger_hurt\" \"model\" \"*3\" \"damage\" \"{damage}\" \"spawnflags\" \"{spawnflags}\" }}"));
        }

        /// <summary>A filter name that names no entity is no filter, to the engine and here.</summary>
        [Fact]
        public void AFilterThatNamesNothingFiltersNothing()
        {
            Assert.Equal([3], KillVolumes("{ " + Pit + " \"filtername\" \"filter_missing\" }"));
        }

        /// <summary>Other triggers are still nothing at all, as they were.</summary>
        [Fact]
        public void OtherTriggersAreNotKillVolumes()
        {
            Assert.Empty(KillVolumes("{ \"classname\" \"trigger_multiple\" \"model\" \"*3\" \"spawnflags\" \"1\" }"));
        }

        /// <summary>
        /// Flat ground at z = 0 under a 400 by 400 map, and a trigger_hurt slab over everything east of
        /// x = 110, from z = -50 to z = 40: its tree a subtree of its own (head node 1) beside the
        /// world's, as vbsp lays out a brush model, and solid like every compiled trigger brush. A spawn
        /// stands at the west end. <paramref name="hurt"/> is the keyvalues of the trigger_hurt.
        /// </summary>
        private static NavFile GenerateBesidePit(string hurt)
        {
            var planes = new[]
            {
                Plane(0, 0, 1, 0),        // 0: ground top, the world's split
                Plane(0, 0, -1, 500),     // 1: ground underside
                Plane(-1, 0, 0, 5000),    // 2: x >= -5000
                Plane(1, 0, 0, 5000),     // 3: x <= 5000
                Plane(0, -1, 0, 5000),    // 4: y >= -5000
                Plane(0, 1, 0, 5000),     // 5: y <= 5000
                Plane(0, 0, 1, 40),       // 6: slab top, the model's first split
                Plane(0, 0, -1, 50),      // 7: slab underside, z >= -50
                Plane(-1, 0, 0, -110),    // 8: slab, x >= 110, and the model's second split
                Plane(1, 0, 0, 5000),     // 9: slab, x <= 5000
                Plane(0, -1, 0, 5000),    // 10: slab, y >= -5000
                Plane(0, 1, 0, 5000),     // 11: slab, y <= 5000
            };

            // Leaf n is referenced as -(n) - 1.
            var nodes = new[]
            {
                new BspVisibility.Node { PlaneNum = 0, Child0 = -1, Child1 = -2 }, // the world
                new BspVisibility.Node { PlaneNum = 6, Child0 = -3, Child1 = 2 },  // the slab: above its top
                new BspVisibility.Node { PlaneNum = 8, Child0 = -5, Child1 = -4 }, // west of x = 110, or in it
            };

            var leafs = new[]
            {
                new BspVisibility.Leaf { Contents = 0 },     // 0: the world's air
                new BspVisibility.Leaf { Contents = Solid }, // 1: the ground
                new BspVisibility.Leaf { Contents = 0 },     // 2: above the slab
                new BspVisibility.Leaf { Contents = Solid }, // 3: the slab
                new BspVisibility.Leaf { Contents = 0 },     // 4: west of the slab
            };

            var brushes = new[]
            {
                new BspFile.Brush { FirstSide = 0, NumSides = 6, Contents = Solid },
                new BspFile.Brush { FirstSide = 6, NumSides = 6, Contents = Solid },
            };

            var sides = Enumerable.Range(0, 12).Select(Side).ToArray();

            var vis = BspVisibility.FromGeometry(planes, nodes, leafs, brushes, sides,
                [null, [0], null, [1], null]);

            string path = Path.Combine(Path.GetTempPath(), $"mw_test_{Guid.NewGuid():N}.bsp");
            try
            {
                TestWorlds.WriteBsp(path,
                    "{ \"classname\" \"worldspawn\" }\n" +
                    "{ \"classname\" \"info_player_start\" \"origin\" \"-150 0 1\" }\n" +
                    "{ \"classname\" \"trigger_hurt\" \"model\" \"*1\" " + hurt + " }",
                    [(new(-200, -200, -500), new(200, 200, 500)), (new(110, -5000, -50), new(5000, 5000, 40))],
                    [], headNodes: [0, 1]);

                var bsp = BspFile.Load(path);
                vis.AttachModels(
                    BspModels.Load(path, bsp),
                    BspModels.Load(path, bsp, forMovement: true),
                    BspModels.Load(path, bsp, forMovement: true, forGround: true));
                vis.AttachKillVolumes(BspModels.LoadKillVolumes(path, bsp));

                var nav = new NavFile();
                AreaGenerator.Generate(nav, vis, bsp);
                return nav;
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static BspFile.Plane Plane(float x, float y, float z, float distance)
            => new() { Normal = new BspFile.Vector3(x, y, z), Distance = distance };

        private static BspFile.BrushSide Side(int plane) => new() { PlaneNum = (ushort)plane };

        /// <summary>
        /// The case that was broken: the ground under a lethal hurt is meshed nowhere, and the ground
        /// beside it still is. The trigger is not a wall either - the flood is stopped by the kill
        /// volume, not by anything solid.
        /// </summary>
        [Fact]
        public void GroundInsideALethalHurtGetsNoMesh()
        {
            var nav = GenerateBesidePit("\"damage\" \"9999\" \"spawnflags\" \"1\"");

            Assert.NotEmpty(nav.Areas);
            Assert.Contains(nav.Areas, a => NavGeometry.Contains(a, -150, 0));
            // Samples fall every 25 units from x = -200, so x = 100 is the last one outside the volume
            // and its area ends half a step on, at 112.5. The next, at 125, is inside.
            Assert.All(nav.Areas, a => Assert.True(NavGeometry.GetBounds(a).MaxX <= 112.5f,
                $"area {a.Id} reaches x = {NavGeometry.GetBounds(a).MaxX} inside the kill volume"));
        }

        /// <summary>
        /// The control: the same volume switched off at the start is ground like any other, so the
        /// mesh runs right across it - which is also what shows the case above was the kill volume.
        /// </summary>
        [Fact]
        public void GroundInsideADisabledHurtIsMeshed()
        {
            var nav = GenerateBesidePit("\"damage\" \"9999\" \"spawnflags\" \"1\" \"StartDisabled\" \"1\"");

            Assert.Contains(nav.Areas, a => NavGeometry.GetBounds(a).MaxX > 150f);
        }
    }
}
