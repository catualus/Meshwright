using System;
using System.IO;
using System.Linq;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// Which maps a batch decides it was pointed at.
    ///
    /// Worth its own tests because the mistakes are quiet ones. A flag's value read as a map name
    /// stops the whole run with "no such file"; a directory silently matching nothing looks like a
    /// batch that finished instantly; and a pack processed in filesystem order rather than a stable one
    /// makes two runs of the same command impossible to compare.
    /// </summary>
    public class BatchTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), $"mw_batch_{Guid.NewGuid():N}");

        public BatchTests() => Directory.CreateDirectory(root);

        public void Dispose()
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }

        private string Map(string name)
        {
            string path = Path.Combine(root, name);
            File.WriteAllText(path, "not really a bsp, but it exists");
            return path;
        }

        private static string[] Args(params string[] rest) => ["batch", .. rest];

        [Fact]
        public void ADirectoryYieldsTheMapsInIt()
        {
            Map("b.bsp");
            Map("a.bsp");
            Map("notes.txt");

            var found = Program.CollectMaps(Args(root));

            Assert.Equal(2, found.Count);
            Assert.All(found, f => Assert.EndsWith(".bsp", f));
        }

        /// <summary>Two runs of the same command must process the same maps in the same order.</summary>
        [Fact]
        public void TheOrderIsStableRatherThanWhateverTheFilesystemSays()
        {
            Map("zebra.bsp");
            Map("alpha.bsp");
            Map("middle.bsp");

            var found = Program.CollectMaps(Args(root));

            Assert.Equal(
                found.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(),
                found);
        }

        [Fact]
        public void APatternMatchesWhatItShould()
        {
            Map("gm_one.bsp");
            Map("gm_two.bsp");
            Map("rp_three.bsp");

            var found = Program.CollectMaps(Args(Path.Combine(root, "gm_*.bsp")));

            Assert.Equal(2, found.Count);
            Assert.All(found, f => Assert.Contains("gm_", f));
        }

        /// <summary>
        /// The one that would break a real command line. "-threads 8" must not be read as a request to
        /// process a map called 8, which fails the whole batch before it starts.
        /// </summary>
        [Fact]
        public void AFlagsValueIsNotMistakenForAMap()
        {
            string map = Map("only.bsp");

            var found = Program.CollectMaps(Args(map, "-threads", "8", "-maxviewdistance", "3000",
                "-game", "cs", "-novisibility"));

            Assert.Equal([map], found.Select(Path.GetFullPath));
        }

        [Fact]
        public void TheSameMapNamedTwiceIsProcessedOnce()
        {
            string map = Map("once.bsp");

            Assert.Single(Program.CollectMaps(Args(map, root)));
        }

        [Fact]
        public void SomethingThatIsNotThereIsRefusedRatherThanSkipped()
        {
            Assert.Throws<FileNotFoundException>(
                () => Program.CollectMaps(Args(Path.Combine(root, "absent.bsp"))));
        }

        /// <summary>An empty directory is not an error, but it is also not a batch.</summary>
        [Fact]
        public void AnEmptyDirectoryYieldsNothing()
        {
            Assert.Empty(Program.CollectMaps(Args(root)));
        }

        [Fact]
        public void FilesThatAreNotMapsAreIgnored()
        {
            Map("real.bsp");
            Map("readme.txt");
            Map("map.nav");
            Map("map.bsp.mwresume");

            Assert.Single(Program.CollectMaps(Args(root)));
        }
    }
}
