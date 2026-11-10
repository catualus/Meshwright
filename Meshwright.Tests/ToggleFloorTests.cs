using System;
using System.IO;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That a toggle func_brush switched on when the map starts is ground to stand on, while a body
    /// still passes it.
    ///
    /// The movement traces walk through every named toggle brush, so that the ground either side of a
    /// round gate is linked for when the gate opens. That made the brush air for the floor finder as
    /// well, and TF2 builds floors that way: cp_dustbowl's only stairs up to the first stage's raised
    /// yard and pl_badwater's cover over the final pit are toggle brushes that go away later in the
    /// round. Neither got any mesh, and Valve's meshes cover both.
    /// </summary>
    public class ToggleFloorTests
    {
        private const int Solid = 0x1;

        /// <summary>
        /// Ground at z = 0, and a brush entity over it: a 100-unit slab from z = 16 to z = 64, its tree
        /// a subtree of its own (head node 1) beside the world's, as vbsp lays out a brush model. <paramref name="entity"/> is the keyvalues of the entity owning model *1.
        /// </summary>
        private static BspVisibility WorldWithBlock(string entity)
        {
            var planes = new[]
            {
                Plane(0, 0, 1, 0),        // 0: ground top, the world's split
                Plane(0, 0, -1, 500),     // 1: ground underside
                Plane(-1, 0, 0, 5000),    // 2: x >= -5000
                Plane(1, 0, 0, 5000),     // 3: x <= 5000
                Plane(0, -1, 0, 5000),    // 4: y >= -5000
                Plane(0, 1, 0, 5000),     // 5: y <= 5000
                Plane(0, 0, 1, 64),       // 6: block top, the model's split
                Plane(0, 0, -1, -16),     // 7: block underside, z >= 16
                Plane(-1, 0, 0, 0),       // 8: block, x >= 0
                Plane(1, 0, 0, 100),      // 9: block, x <= 100
                Plane(0, -1, 0, 0),       // 10: block, y >= 0
                Plane(0, 1, 0, 100),      // 11: block, y <= 100
            };

            // Leaf n is referenced as -(n) - 1.
            var nodes = new[]
            {
                new BspVisibility.Node { PlaneNum = 0, Child0 = -1, Child1 = -2 }, // the world
                new BspVisibility.Node { PlaneNum = 6, Child0 = -3, Child1 = -4 }, // the block's model
            };

            var leafs = new[]
            {
                new BspVisibility.Leaf { Contents = 0 },     // 0: the world's air
                new BspVisibility.Leaf { Contents = Solid }, // 1: the ground
                new BspVisibility.Leaf { Contents = 0 },     // 2: above the block
                new BspVisibility.Leaf { Contents = Solid }, // 3: the block
            };

            var brushes = new[]
            {
                new BspFile.Brush { FirstSide = 0, NumSides = 6, Contents = Solid },
                new BspFile.Brush { FirstSide = 6, NumSides = 6, Contents = Solid },
            };

            var sides = new[]
            {
                Side(0), Side(1), Side(2), Side(3), Side(4), Side(5),
                Side(6), Side(7), Side(8), Side(9), Side(10), Side(11),
            };

            var vis = BspVisibility.FromGeometry(planes, nodes, leafs, brushes, sides,
                [null, [0], null, [1]]);

            // The entity lump and the model bounds come from a file, as they do for a real map.
            string path = Path.Combine(Path.GetTempPath(), $"mw_test_{Guid.NewGuid():N}.bsp");
            try
            {
                TestWorlds.WriteBsp(path,
                    "{ \"classname\" \"worldspawn\" }\n{ \"classname\" \"func_brush\" \"model\" \"*1\" " + entity + " }",
                    [(new(-5000, -5000, -500), new(5000, 5000, 0)), (new(0, 0, 16), new(100, 100, 64))],
                    [], headNodes: [0, 1]);

                var bsp = BspFile.Load(path);
                vis.AttachModels(
                    BspModels.Load(path, bsp),
                    BspModels.Load(path, bsp, forMovement: true),
                    BspModels.Load(path, bsp, forMovement: true, forGround: true));
            }
            finally
            {
                File.Delete(path);
            }

            return vis;
        }

        private static BspFile.Plane Plane(float x, float y, float z, float distance)
            => new() { Normal = new BspFile.Vector3(x, y, z), Distance = distance };

        private static BspFile.BrushSide Side(int plane) => new() { PlaneNum = (ushort)plane };

        private static float FloorUnder(BspVisibility vis, float x, float y)
        {
            Assert.True(vis.TryTraceSurface(new BspFile.Vector3(x, y, 200), new BspFile.Vector3(x, y, -100),
                BspVisibility.GroundMask, out var hit, out _));
            return hit.Z;
        }

        /// <summary>
        /// The case that was broken: a named toggle brush that starts switched on - cp_dustbowl's
        /// cap1_stairs - is a floor. The floor finder lands on its top and still finds the ground under
        /// it, and a body's movement trace still passes through it as it did.
        /// </summary>
        [Fact]
        public void AToggleBrushSwitchedOnAtTheStartIsAFloorABodyStillPasses()
        {
            var vis = WorldWithBlock("\"targetname\" \"cap1_stairs\"");

            Assert.Equal(64f, FloorUnder(vis, 50, 50), 2);

            var into = new BspVisibility.FloorSample[4];
            int count = vis.EnumerateFloors(50, 50, 200, -100, into);
            Assert.Equal(2, count);
            Assert.Equal(0f, into[1].Z, 2);

            Assert.True(vis.IsLineClear(new BspFile.Vector3(50, 50, 2), new BspFile.Vector3(50, 50, 50),
                BspVisibility.GenerationMask));
        }

        /// <summary>One that starts switched off is not there to stand on, and stays air.</summary>
        [Fact]
        public void AToggleBrushSwitchedOffAtTheStartIsNoFloor()
        {
            var vis = WorldWithBlock("\"targetname\" \"cap1_fence_brush\" \"StartDisabled\" \"1\"");

            Assert.Equal(0f, FloorUnder(vis, 50, 50), 2);
        }

        /// <summary>
        /// The control: an unnamed toggle brush can never be switched off, so it was a wall and a floor
        /// before and still is both.
        /// </summary>
        [Fact]
        public void AnUnnamedToggleBrushIsStillAWallAndAFloor()
        {
            var vis = WorldWithBlock("");

            Assert.Equal(64f, FloorUnder(vis, 50, 50), 2);
            Assert.False(vis.IsLineClear(new BspFile.Vector3(50, 50, 2), new BspFile.Vector3(50, 50, 50),
                BspVisibility.GenerationMask));
        }
    }
}
