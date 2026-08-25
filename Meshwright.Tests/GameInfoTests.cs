using System;
using System.IO;
using System.Linq;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That a mod's declared search paths are read the way the engine reads them.
    ///
    /// Worth testing carefully because getting it wrong is quiet. A search path that resolves to the
    /// wrong directory finds nothing, a model that resolves nowhere contributes no collision, and the
    /// mesh simply floats over whatever prop used it - which reads as a generator bug rather than as a
    /// content path that was never mounted.
    /// </summary>
    public class GameInfoTests : IDisposable
    {
        private readonly string install = Path.Combine(Path.GetTempPath(), $"mw_gi_{Guid.NewGuid():N}");

        private string Mod => Path.Combine(install, "garrysmod");

        public GameInfoTests()
        {
            Directory.CreateDirectory(Mod);
            Directory.CreateDirectory(Path.Combine(install, "sourceengine"));
            Directory.CreateDirectory(Path.Combine(install, "platform"));
        }

        public void Dispose()
        {
            try { Directory.Delete(install, recursive: true); } catch (IOException) { }
        }

        private string[] Paths(string text) =>
            GameInfo.Parse(text, Mod).Select(m => m.Path).ToArray();

        /// <summary>Garry's Mod's own file, which exercises every feature at once.</summary>
        private const string Real = """
            "GameInfo"
            {
                game    "Garry's Mod"
                FileSystem
                {
                    SteamAppId  4000
                    SearchPaths
                    {
                        // None of this matters really
                        game+mod            garrysmod/addons/*
                        game+mod            garrysmod/garrysmod.vpk
                        game                |all_source_engine_paths|sourceengine/hl2_misc.vpk
                        platform            |all_source_engine_paths|platform/platform_misc.vpk
                        mod+mod_write+default_write_path        |gameinfo_path|.
                        game+game_write     garrysmod
                        gamebin             garrysmod/bin
                        game                |all_source_engine_paths|sourceengine
                    }
                }
            }
            """;

        [Fact]
        public void TheModDirectoryMacroResolvesToTheModDirectory()
        {
            Assert.Contains(Path.GetFullPath(Mod), Paths(Real));
        }

        [Fact]
        public void TheEnginePathsMacroResolvesToTheInstallRoot()
        {
            Assert.Contains(Path.GetFullPath(Path.Combine(install, "sourceengine")), Paths(Real));
        }

        /// <summary>A bare entry is relative to the install root, not to the mod directory.</summary>
        [Fact]
        public void ABareEntryIsRelativeToTheInstallRoot()
        {
            Assert.Contains(Path.GetFullPath(Path.Combine(install, "garrysmod")), Paths(Real));
        }

        /// <summary>
        /// gamebin is executables. Mounting it would search a directory of DLLs for models on every
        /// lookup that missed, which is pure cost.
        /// </summary>
        [Fact]
        public void PathsThatCarryNoContentAreSkipped()
        {
            Assert.DoesNotContain(Path.GetFullPath(Path.Combine(install, "garrysmod", "bin")), Paths(Real));
        }

        [Fact]
        public void ArchivesAreDistinguishedFromDirectories()
        {
            var mounts = GameInfo.Parse(Real, Mod);

            Assert.Contains(mounts, m => m.IsArchive && m.Path.EndsWith("hl2_misc.vpk"));
            Assert.Contains(mounts, m => !m.IsArchive && m.Path.EndsWith("sourceengine"));
        }

        /// <summary>How every installed addon gets mounted as a root in its own right.</summary>
        [Fact]
        public void AWildcardExpandsToTheDirectoriesItStandsFor()
        {
            string addons = Path.Combine(Mod, "addons");
            Directory.CreateDirectory(Path.Combine(addons, "one"));
            Directory.CreateDirectory(Path.Combine(addons, "two"));

            var paths = Paths(Real);

            Assert.Contains(Path.GetFullPath(Path.Combine(addons, "one")), paths);
            Assert.Contains(Path.GetFullPath(Path.Combine(addons, "two")), paths);

            // The wildcard itself is never a path.
            Assert.DoesNotContain(paths, p => p.EndsWith("*"));
        }

        /// <summary>Order is priority: a mod that overrides a stock asset relies on being searched first.</summary>
        [Fact]
        public void OrderIsPreserved()
        {
            var paths = Paths(Real);

            int modDir = Array.IndexOf(paths, Path.GetFullPath(Mod));
            int baseGame = Array.IndexOf(paths, Path.GetFullPath(Path.Combine(install, "sourceengine")));

            Assert.True(modDir >= 0 && baseGame >= 0);
            Assert.True(modDir < baseGame, "the mod's own directory must be searched before the base game's");
        }

        [Fact]
        public void CommentsInsideTheBlockAreIgnored()
        {
            var paths = Paths(Real);
            Assert.DoesNotContain(paths, p => p.Contains("None") || p.Contains("matters"));
        }

        [Fact]
        public void AMissingFileIsNotAnError()
        {
            Assert.Empty(GameInfo.Read(Mod));
        }

        [Theory]
        [InlineData("")]
        [InlineData("not keyvalues at all")]
        [InlineData("\"GameInfo\" { FileSystem { } }")]
        [InlineData("\"GameInfo\" { FileSystem { SearchPaths ")]
        [InlineData("\"GameInfo\" { FileSystem { SearchPaths { game")]
        public void MalformedContentYieldsNothingRatherThanThrowing(string text)
        {
            Assert.Empty(GameInfo.Parse(text, Mod));
        }

        [Fact]
        public void ReadFindsTheFileOnDisk()
        {
            File.WriteAllText(Path.Combine(Mod, GameInfo.FileName), Real);

            Assert.Contains(GameInfo.Read(Mod), m => m.Path == Path.GetFullPath(Mod));
        }
    }
}
