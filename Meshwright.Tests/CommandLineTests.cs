using System;
using System.IO;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That a flag left without its value is refused by name rather than handed the next argument.
    ///
    /// Compile Pal makes this an ordinary case rather than a typo. A parameter that takes a value,
    /// ticked with the value left empty, reaches the command line as the bare flag, followed by the
    /// -gamename it appends to every step. Read naively, "-o" then named the output "-gamename": the
    /// mesh went to a file of that name in Compile Pal's folder, the real .nav was untouched, and the
    /// step reported success.
    /// </summary>
    public class CommandLineTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), $"mw_cli_{Guid.NewGuid():N}");

        public CommandLineTests() => Directory.CreateDirectory(root);

        public void Dispose()
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }

        /// <summary>What Compile Pal passes for a ticked parameter whose value was left empty.</summary>
        private static string[] FromCompilePal(string flag) =>
            ["generate", "map.bsp", flag, "-gamename", "Garry's Mod"];

        [Fact]
        public void AValueIsRead()
        {
            Assert.Equal("out.nav", Program.FlagValue(["generate", "map.bsp", "-o", "out.nav"], "-o"));
            Assert.Equal("out.nav", Program.FlagValue(["generate", "map.bsp", "-O", "out.nav"], "-o"));
        }

        [Fact]
        public void AnAbsentFlagIsNull() =>
            Assert.Null(Program.FlagValue(["generate", "map.bsp", "-novisibility"], "-o"));

        [Fact]
        public void AnEmptyOutputIsRefusedRatherThanNamedAfterTheNextFlag()
        {
            var refused = Assert.Throws<ArgumentException>(
                () => Program.FlagValue(FromCompilePal("-o"), "-o"));

            Assert.StartsWith("-o needs a file path", refused.Message);
            Assert.Contains("-gamename", refused.Message);
            Assert.Contains("Compile Pal", refused.Message);
        }

        [Theory]
        [InlineData("-o")]
        [InlineData("-threads")]
        [InlineData("-content")]
        [InlineData("-maxviewdistance")]
        [InlineData("-d")]
        public void EveryValueFlagRefusesAMissingValue(string flag)
        {
            Assert.Throws<ArgumentException>(() => Program.FlagValue(FromCompilePal(flag), flag));
            Assert.Throws<ArgumentException>(() => Program.FlagValue(["generate", "map.bsp", flag], flag));
            Assert.Throws<ArgumentException>(() => Program.FlagValue(["generate", "map.bsp", flag, ""], flag));
        }

        /// <summary>
        /// An empty "Thread limit" used to fail late with "-threads expects a positive integer, got
        /// '-gamename'", which names the wrong thing; and a trailing -threads was silently ignored.
        /// </summary>
        [Fact]
        public void AnEmptyThreadLimitIsRefusedByName()
        {
            var refused = Assert.Throws<ArgumentException>(
                () => Program.TryGetThreadCount(FromCompilePal("-threads"), out _));

            Assert.StartsWith("-threads needs a thread count", refused.Message);

            Assert.Throws<ArgumentException>(
                () => Program.TryGetThreadCount(["generate", "map.bsp", "-threads"], out _));
        }

        [Fact]
        public void AThreadLimitIsStillRead()
        {
            Assert.True(Program.TryGetThreadCount(["generate", "map.bsp", "-threads", "8"], out int threads));
            Assert.Equal(8, threads);

            Assert.False(Program.TryGetThreadCount(["generate", "map.bsp"], out _));
        }

        /// <summary>
        /// Refused as a missing value, not as a directory called "-gamename" that does not exist - the
        /// second is true but sends the reader looking for the wrong mistake.
        /// </summary>
        [Fact]
        public void AnEmptyContentDirectoryIsRefusedByName()
        {
            var original = GameFiles.AdditionalRoots;

            try
            {
                var refused = Assert.Throws<ArgumentException>(
                    () => Program.ApplyContentRoots(FromCompilePal("-content")));

                Assert.StartsWith("-content needs a directory", refused.Message);
                Assert.Same(original, GameFiles.AdditionalRoots);
            }
            finally
            {
                GameFiles.AdditionalRoots = original;
            }
        }

        /// <summary>
        /// The reported failure, end to end: the run stops before touching the map, says which flag is
        /// missing its value, and writes nothing.
        /// </summary>
        [Fact]
        public void GenerateWithAnEmptyOutputStopsBeforeWritingAnything()
        {
            string bsp = Path.Combine(root, "map.bsp");
            File.WriteAllText(bsp, "never read");

            var output = new StringWriter();
            var (stdout, stderr) = (Console.Out, Console.Error);
            bool counterStrike = NavConstants.UseCounterStrikeLimits;
            int code;

            Console.SetOut(output);
            Console.SetError(output);

            try
            {
                code = Program.Main(["generate", bsp, "-o", "-gamename", "Garry's Mod"]);
            }
            finally
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                NavConstants.UseCounterStrikeLimits = counterStrike;
            }

            Assert.Equal(1, code);
            Assert.Contains("-o needs a file path", output.ToString());
            Assert.False(File.Exists(Path.Combine(Environment.CurrentDirectory, "-gamename")));
            Assert.Equal(["map.bsp"], Array.ConvertAll(Directory.GetFiles(root), f => Path.GetFileName(f)));
        }

        /// <summary>
        /// No flag read this way takes a negative number, so anything starting with '-' is the next
        /// flag. The one place a negative value is legitimate, -at's coordinates, reads them itself.
        /// </summary>
        [Fact]
        public void ANegativeValueReadsAsTheNextFlag() =>
            Assert.Throws<ArgumentException>(
                () => Program.FlagValue(["build-visibility", "a.bsp", "a.nav", "-d", "-500"], "-d"));
    }
}
