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
    }
}
