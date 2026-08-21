using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// Where a model gets read from, and in what order.
    ///
    /// This is tested because it is the part with no visible failure mode. If the priority is wrong, a
    /// map that ships a modified prop silently collides against the stock one - the build succeeds, the
    /// mesh looks reasonable, and the areas are subtly in the wrong place. Nothing downstream can tell.
    ///
    /// The pakfile path in particular had no coverage from real use: every model on both development
    /// maps resolves from loose files, so the zip reader inside a .bsp was written, wired up and never
    /// executed. A synthetic map is the only way to exercise it without shipping a fixture.
    /// </summary>
    public class GameFilesTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), $"mw_files_{Guid.NewGuid():N}");

        /// <summary>The mod directory, whose <c>maps</c> subdirectory holds the .bsp.</summary>
        private string Mod => Path.Combine(root, "garrysmod");

        public GameFilesTests() => Directory.CreateDirectory(Path.Combine(Mod, "maps"));

        public void Dispose()
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }

        /// <summary>
        /// Writes a .bsp carrying nothing but a pakfile lump, holding the given entries.
        /// </summary>
        private string WriteBsp(string name, params (string Path, string Body)[] packed)
        {
            const int LumpPakfile = 40;
            string path = Path.Combine(Mod, "maps", name);

            byte[] zip;

            using (var zipStream = new MemoryStream())
            {
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var (entryPath, body) in packed)
                    {
                        using var writer = new StreamWriter(archive.CreateEntry(entryPath).Open());
                        writer.Write(body);
                    }
                }

                zip = zipStream.ToArray();
            }

            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            w.Write(0x50534256);            // 'VBSP'
            w.Write(20);

            long directory = ms.Position;
            for (int i = 0; i < BspFile.HeaderLumps * 4; i++) w.Write(0);
            w.Write(0);                     // map revision

            int pakAt = (int)ms.Position;

            if (packed.Length > 0) w.Write(zip);

            ms.Seek(directory + LumpPakfile * 16, SeekOrigin.Begin);
            w.Write(pakAt);
            w.Write(packed.Length > 0 ? zip.Length : 0);

            File.WriteAllBytes(path, ms.ToArray());
            return path;
        }

        private void WriteLoose(string relativePath, string body)
        {
            string full = Path.Combine(Mod, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, body);
        }

        private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

        [Fact]
        public void ReadsAModelPackedInsideTheMap()
        {
            string bsp = WriteBsp("packed.bsp", ("models/custom/thing.phy", "from the pakfile"));

            using var files = GameFiles.Open(bsp);

            Assert.Equal(1, files.PakfileEntries);
            Assert.True(files.TryRead("models/custom/thing.phy", out var bytes));
            Assert.Equal("from the pakfile", Text(bytes));
            Assert.Equal(1, files.ReadsFromPakfile);
        }

        [Fact]
        public void ThePakfileWinsOverALooseFileOfTheSameName()
        {
            // The case the priority order exists for. A mapper packs a modified prop precisely because
            // the installed one is not what they want; reading the installed one instead is silent and
            // wrong.
            WriteLoose("models/custom/thing.phy", "installed");
            string bsp = WriteBsp("override.bsp", ("models/custom/thing.phy", "packed"));

            using var files = GameFiles.Open(bsp);

            Assert.True(files.TryRead("models/custom/thing.phy", out var bytes));
            Assert.Equal("packed", Text(bytes));
        }

        [Fact]
        public void FallsBackToALooseFileWhenTheMapPacksNothing()
        {
            WriteLoose("models/custom/thing.phy", "installed");
            string bsp = WriteBsp("plain.bsp");

            using var files = GameFiles.Open(bsp);

            Assert.True(files.TryRead("models/custom/thing.phy", out var bytes));
            Assert.Equal("installed", Text(bytes));
            Assert.Equal(1, files.ReadsFromDisk);
        }

        [Fact]
        public void BackslashesAndLeadingSlashesResolveTheSameWay()
        {
            // Model paths come out of the prop lump in Windows form and out of a VPK in Unix form, and
            // the two have to name the same file or half a map's props go missing on one of them.
            WriteLoose("models/custom/thing.phy", "installed");
            string bsp = WriteBsp("slashes.bsp");

            using var files = GameFiles.Open(bsp);

            Assert.True(files.TryRead(@"models\custom\thing.phy", out _));
            Assert.True(files.TryRead("/models/custom/thing.phy", out _));
        }

        [Fact]
        public void AMissingFileIsReportedRatherThanThrown()
        {
            string bsp = WriteBsp("empty.bsp");

            using var files = GameFiles.Open(bsp);

            Assert.False(files.TryRead("models/nothing/here.phy", out var bytes));
            Assert.Empty(bytes);
            Assert.Contains("models/nothing/here.phy", files.Missing);
        }

        [Fact]
        public void AMapWithACorruptPakfileStillResolvesLooseContent()
        {
            // Third-party maps are the norm and some are damaged. A pakfile that will not open must cost
            // the packed content, not the whole lookup - the build has to keep going.
            string bsp = Path.Combine(Mod, "maps", "corrupt.bsp");

            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(0x50534256);
                w.Write(20);

                long directory = ms.Position;
                for (int i = 0; i < BspFile.HeaderLumps * 4; i++) w.Write(0);
                w.Write(0);

                int at = (int)ms.Position;
                w.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });     // not a zip

                ms.Seek(directory + 40 * 16, SeekOrigin.Begin);
                w.Write(at);
                w.Write(8);

                File.WriteAllBytes(bsp, ms.ToArray());
            }

            WriteLoose("models/custom/thing.phy", "installed");

            using var files = GameFiles.Open(bsp);

            Assert.Equal(0, files.PakfileEntries);
            Assert.True(files.TryRead("models/custom/thing.phy", out var bytes));
            Assert.Equal("installed", Text(bytes));
        }
    }
}
