using System.Linq;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// Which entities count as player spawns, and that the entity lump is split where the engine
    /// splits it.
    ///
    /// Spawns are the only seeds a from-scratch run has and the only place the reachability check can
    /// start, so a spawn class not recognised here is a map that generates nothing and reports nothing
    /// reachable, with no error anywhere to say why.
    /// </summary>
    public class SpawnPointTests
    {
        [Theory]
        [InlineData("gmod_player_start")]
        [InlineData("info_player_start")]
        [InlineData("info_player_deathmatch")]
        [InlineData("info_player_counterterrorist")]
        [InlineData("info_player_teamspawn")]
        [InlineData("ins_spawnpoint")]
        [InlineData("aoc_spawnpoint")]
        [InlineData("dys_spawn_point")]
        [InlineData("diprip_start_team_red")]
        [InlineData("diprip_start_team_blue")]
        [InlineData("info_survivor_rescue")]
        [InlineData("INFO_PLAYER_START")]
        public void GarrysModSpawnsPlayersHere(string classname) =>
            Assert.True(AreaGenerator.IsSpawnClass(classname));

        /// <summary>
        /// info_survivor_position is the one GMod deliberately leaves out, because a stock Left 4 Dead
        /// map has one sealed in a box under the floor. The rest are entities that only look related.
        /// </summary>
        [Theory]
        [InlineData("info_survivor_position")]
        [InlineData("info_target")]
        [InlineData("info_node")]
        [InlineData("prop_physics")]
        [InlineData("info_playerstart")]
        public void NotASpawn(string classname) =>
            Assert.False(AreaGenerator.IsSpawnClass(classname));

        [Fact]
        public void GModsOwnSpawnEntityIsFound()
        {
            const string lump = """
                {
                "classname" "worldspawn"
                }
                {
                "origin" "64 -128 32"
                "classname" "gmod_player_start"
                }
                """;

            var spawn = Assert.Single(AreaGenerator.SpawnPositions(lump));

            Assert.Equal(64f, spawn.X);
            Assert.Equal(-128f, spawn.Y);
            Assert.Equal(32f, spawn.Z);
        }

        /// <summary>
        /// The regex this replaced ended an entity at the first closing brace, quoted or not. Lua writes
        /// every table in braces, so a spawn carrying some in an output lost everything after it -
        /// here, its classname.
        /// </summary>
        [Fact]
        public void ABraceInsideAValueDoesNotEndTheEntity()
        {
            const string lump = """
                {
                "classname" "lua_run"
                "Code" "local t = { x = 1 } print(t.x)"
                }
                {
                "origin" "10 20 30"
                "OnUser1" "!self,RunPassedCode,hook.Run('Spawned', {}),0,-1"
                "classname" "info_player_start"
                }
                """;

            var spawn = Assert.Single(AreaGenerator.SpawnPositions(lump));

            Assert.Equal(10f, spawn.X);
            Assert.Equal(30f, spawn.Z);
        }

        [Fact]
        public void EverySpawnIsFoundInLumpOrder()
        {
            const string lump = """
                { "classname" "info_player_start" "origin" "1 0 0" }
                { "classname" "ins_spawnpoint" "origin" "2 0 0" }
                { "classname" "info_target" "origin" "3 0 0" }
                { "classname" "dys_spawn_point" "origin" "4 0 0" }
                """;

            Assert.Equal([1f, 2f, 4f], AreaGenerator.SpawnPositions(lump).Select(p => p.X));
        }

        [Fact]
        public void ASpawnWithoutAUsableOriginIsSkipped()
        {
            const string lump = """
                { "classname" "info_player_start" }
                { "classname" "info_player_start" "origin" "nonsense" }
                { "classname" "info_player_start" "origin" "5 6 7" }
                """;

            Assert.Equal(5f, Assert.Single(AreaGenerator.SpawnPositions(lump)).X);
        }
    }

    public class BspEntitiesTests
    {
        [Fact]
        public void ReadsEachEntitysKeyValues()
        {
            var entities = BspEntities.Parse("""
                {
                "classname" "worldspawn"
                "skyname" "sky_day01_01"
                }
                {
                "classname" "light"
                "_light" "255 255 255 200"
                }
                """);

            Assert.Equal(2, entities.Count);
            Assert.Equal("worldspawn", entities[0]["classname"]);
            Assert.Equal("sky_day01_01", entities[0]["skyname"]);
            Assert.Equal("255 255 255 200", entities[1]["_light"]);
        }

        /// <summary>Braces inside quotes are text, whether they balance or not.</summary>
        [Fact]
        public void BracesInsideQuotesAreText()
        {
            var entities = BspEntities.Parse("""
                { "classname" "lua_run" "Code" "if x then t = { end" }
                { "classname" "lua_run" "Code" "} } }" }
                { "classname" "info_target" }
                """);

            Assert.Equal(3, entities.Count);
            Assert.Equal("if x then t = { end", entities[0]["Code"]);
            Assert.Equal("} } }", entities[1]["Code"]);
            Assert.Equal("info_target", entities[2]["classname"]);
        }

        [Fact]
        public void KeysIgnoreCaseAndTheLastValueWins()
        {
            var entity = Assert.Single(BspEntities.Parse("""{ "ClassName" "a" "origin" "1 2 3" "ORIGIN" "4 5 6" }"""));

            Assert.Equal("a", entity["classname"]);
            Assert.Equal("4 5 6", entity["origin"]);
        }

        [Fact]
        public void EmptyValuesAndUnquotedTokensAreRead()
        {
            var entity = Assert.Single(BspEntities.Parse("{ \"targetname\" \"\" classname info_target }"));

            Assert.Equal("", entity["targetname"]);
            Assert.Equal("info_target", entity["classname"]);
        }

        /// <summary>The engine's tokenizer skips these between tokens, so a hand-edited lump may have them.</summary>
        [Fact]
        public void CommentsBetweenTokensAreSkipped()
        {
            var entity = Assert.Single(BspEntities.Parse("""
                {
                // the spawn
                "classname" "info_player_start"
                "message" "see http://example.com"
                }
                """));

            Assert.Equal("info_player_start", entity["classname"]);
            Assert.Equal("see http://example.com", entity["message"]);
        }

        /// <summary>
        /// A broken entity costs itself, not the lump. One never closed is dropped when the next opens,
        /// so the two do not merge; one cut off at the end of the lump is dropped too.
        /// </summary>
        [Fact]
        public void AnUnclosedEntityIsDroppedRatherThanMerged()
        {
            var entities = BspEntities.Parse("""
                { "classname" "broken" "origin" "1 1 1"
                { "classname" "info_player_start" "origin" "2 2 2" }
                { "classname" "cut_off"
                """);

            var entity = Assert.Single(entities);
            Assert.Equal("info_player_start", entity["classname"]);
            Assert.Equal("2 2 2", entity["origin"]);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   \n\t  ")]
        [InlineData("\"stray\" \"text\"")]
        public void NothingToReadYieldsNothing(string lump) =>
            Assert.Empty(BspEntities.Parse(lump));
    }
}
