using System.Linq;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// Which brush entities block a trace, for sight and for movement.
    ///
    /// Treated as walls, the volumes TF2 puts around every spawn left ctf_2fort with no mesh at all,
    /// and closed doors cut every room behind them off from the rest of an RP map. Sight still has to
    /// stop at a closed door, so the two sets differ.
    /// </summary>
    public class BlockingEntityTests
    {
        private const int Models = 64;

        private static int[] Blocking(string lump, bool forMovement)
            => BspModels.SelectBlocking(lump, Models, forMovement).Select(b => b.Index).ToArray();

        [Theory]
        [InlineData("func_respawnroom")]
        [InlineData("func_respawnroomvisualizer")]
        [InlineData("func_regenerate")]
        [InlineData("func_capturezone")]
        [InlineData("func_nobuild")]
        [InlineData("func_buyzone")]
        [InlineData("trigger_multiple")]
        public void TriggerVolumesBlockNothing(string classname)
        {
            string lump = $$"""{ "classname" "{{classname}}" "model" "*3" }""";

            Assert.Empty(Blocking(lump, forMovement: false));
            Assert.Empty(Blocking(lump, forMovement: true));
        }

        [Theory]
        [InlineData("func_door")]
        [InlineData("func_door_rotating")]
        public void DoorsBlockSightButNotMovement(string classname)
        {
            string lump = $$"""{ "classname" "{{classname}}" "model" "*4" }""";

            Assert.Equal([4], Blocking(lump, forMovement: false));
            Assert.Empty(Blocking(lump, forMovement: true));
        }

        [Fact]
        public void ABreakableIsWalkedThroughOnlyWhenItCanBreak()
        {
            Assert.Empty(Blocking("""{ "classname" "func_breakable" "model" "*5" "health" "50" }""", forMovement: true));
            Assert.Empty(Blocking("""{ "classname" "func_breakable" "model" "*5" }""", forMovement: true));
            Assert.Equal([5], Blocking("""{ "classname" "func_breakable" "model" "*5" "health" "0" }""", forMovement: true));
        }

        /// <summary>
        /// Shop windows stay walls for movement on purpose: a path through glass is not one an NPC on a
        /// roleplay map should take. Sight passes them by content (CONTENTS_WINDOW) anyway.
        /// </summary>
        [Fact]
        public void GlassStillBlocksMovement()
        {
            Assert.Equal([6], Blocking("""{ "classname" "func_breakable_surf" "model" "*6" }""", forMovement: true));
        }

        /// <summary>
        /// An unnamed toggle brush can never be switched off, so on a roleplay map it is a wall. A named
        /// one is a gate map logic opens - TF2's round gates and spawn doors are exactly that.
        /// </summary>
        [Fact]
        public void AFuncBrushWallStaysSolidUnlessItCanBeSwitchedOff()
        {
            Assert.Equal([7], Blocking("""{ "classname" "func_brush" "model" "*7" }""", forMovement: true));
            Assert.Equal([7], Blocking("""{ "classname" "func_brush" "model" "*7" "solidity" "2" "targetname" "gate" }""", forMovement: true));
            Assert.Empty(Blocking("""{ "classname" "func_brush" "model" "*7" "solidity" "0" "StartDisabled" "1" }""", forMovement: true));
            Assert.Empty(Blocking("""{ "classname" "func_brush" "model" "*7" "targetname" "gate_2nd_spawn" }""", forMovement: true));
            Assert.Equal([7], Blocking("""{ "classname" "func_brush" "model" "*7" "targetname" "gate_2nd_spawn" }""", forMovement: false));
            Assert.Empty(Blocking("""{ "classname" "func_brush" "model" "*7" "solidity" "1" }""", forMovement: false));
        }

        /// <summary>
        /// Of the brushes a body walks through, only a toggle brush switched on when the map starts is
        /// ground to stand on meanwhile. A door's top is in its frame, a breakable is walked through
        /// because it breaks, and a toggle brush that starts switched off is not there at all.
        /// </summary>
        [Fact]
        public void OnlyToggleBrushesSwitchedOnAtTheStartAreGroundABodyPasses()
        {
            int[] Ground(string lump)
                => BspModels.SelectBlocking(lump, Models, forMovement: true, forGround: true).Select(b => b.Index).ToArray();

            Assert.Equal([7], Ground("""{ "classname" "func_brush" "model" "*7" "targetname" "cap1_stairs" }"""));
            Assert.Equal([7], Ground("""{ "classname" "func_brush" "model" "*7" }"""));
            Assert.Empty(Ground("""{ "classname" "func_brush" "model" "*7" "StartDisabled" "1" }"""));
            Assert.Empty(Ground("""{ "classname" "func_brush" "model" "*7" "solidity" "1" "targetname" "ghost" }"""));
            Assert.Empty(Ground("""{ "classname" "func_door" "model" "*7" }"""));
            Assert.Empty(Ground("""{ "classname" "func_breakable" "model" "*7" "health" "50" }"""));
        }

        /// <summary>
        /// A brace inside a keyvalue (Lua in a lua_run, say) used to end the entity there, so the door
        /// after it was read with the wrong keys and the wrong model.
        /// </summary>
        [Fact]
        public void BracesInsideValuesDoNotSplitEntities()
        {
            string lump = """
                { "classname" "lua_run" "Code" "local t = { a = 1 } print(t)" }
                { "classname" "func_wall" "model" "*9" "origin" "8 16 32" }
                """;

            var blocking = BspModels.SelectBlocking(lump, Models);

            var (index, origin) = Assert.Single(blocking);
            Assert.Equal(9, index);
            Assert.Equal(new BspFile.Vector3(8, 16, 32), origin);
        }

        /// <summary>
        /// A brush entity whose bounds are not numbers - a NaN in the model lump, an origin such as
        /// "nan 0 0" or one past float's range - is left out, and the others are still found. Kept, one such model
        /// carried its NaN up the hierarchy's bounds to the root, and no trace found any brush entity
        /// on the map at all.
        /// </summary>
        [Theory]
        [InlineData("nan-bound")]
        [InlineData("nan-origin")]
        [InlineData("inf-origin")]
        public void ABrushEntityWithBoundsThatAreNotNumbersHidesNoOther(string spoilt)
        {
            var models = new (BspFile.Vector3 Mins, BspFile.Vector3 Maxs)[]
            {
                (new BspFile.Vector3(-5000, -5000, -5000), new BspFile.Vector3(5000, 5000, 5000)),
                (new BspFile.Vector3(0, 0, 0), new BspFile.Vector3(64, 64, 64)),
                (new BspFile.Vector3(200, 0, 0),
                    new BspFile.Vector3(264, 64, spoilt == "nan-bound" ? float.NaN : 64)),
                (new BspFile.Vector3(400, 0, 0), new BspFile.Vector3(464, 64, 64)),
                (new BspFile.Vector3(600, 0, 0), new BspFile.Vector3(664, 64, 64)),
                (new BspFile.Vector3(800, 0, 0), new BspFile.Vector3(864, 64, 64)),
            };

            string origin = spoilt switch
            {
                "nan-origin" => "nan 0 0",
                "inf-origin" => "1e39 0 0", // past float's range, so it parses as infinity
                _ => "0 0 0",
            };

            var entities = new System.Text.StringBuilder("{ \"classname\" \"worldspawn\" }\n");
            for (int i = 1; i < models.Length; i++)
            {
                entities.Append($"{{ \"classname\" \"func_brush\" \"model\" \"*{i}\" " +
                                $"\"origin\" \"{(i == 2 ? origin : "0 0 0")}\" }}\n");
            }

            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mw_test_{System.Guid.NewGuid():N}.bsp");
            try
            {
                TestWorlds.WriteBsp(path, entities.ToString(), models, [], [-1, -1, -1, -1, -1, -1]);
                var set = BspModels.Load(path, BspFile.Load(path));

                Assert.Equal(4, set.BlockingModelCount);

                var heads = new int[8];
                var shifts = new BspFile.Vector3[8];

                // Through every good model's box, end to end.
                int found = set.Gather(new BspFile.Vector3(-100, 32, 32), new BspFile.Vector3(1000, 32, 32),
                    heads, shifts, out bool truncated);

                Assert.False(truncated);
                Assert.Equal(4, found);

                // And straight down through one of them.
                int column = set.Gather(new BspFile.Vector3(632, 32, 200), new BspFile.Vector3(632, 32, -200),
                    heads, shifts, out _);

                Assert.Equal(1, column);
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        }
    }
}
