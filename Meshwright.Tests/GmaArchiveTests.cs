using System;
using System.IO;
using System.Text;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// Reading a Garry's Mod addon archive.
    ///
    /// The part worth pinning is the offsets, because nothing in the format stores one. A .gma is a
    /// header, then an index naming every file with its size, then all the bodies run together - so
    /// where a body starts is the sum of every size declared before it, and an error anywhere in the
    /// index silently shifts everything after it. The result would be a model that parses as garbage
    /// rather than a read that fails.
    ///
    /// Real archives are covered too, but only up to the header: 69 of this machine's 70 subscribed
    /// addons index successfully, which says the header and index walk match what the game writes.
    /// Neither development map reads a model out of one, so the body path has no coverage from real
    /// use, which is exactly why it has coverage here.
    /// </summary>
    public class GmaArchiveTests : IDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), $"mw_gma_{Guid.NewGuid():N}.gma");

        public void Dispose()
        {
            try { File.Delete(path); } catch (IOException) { }
        }

        private static void WriteString(BinaryWriter w, string value)
        {
            w.Write(Encoding.ASCII.GetBytes(value));
            w.Write((byte)0);
        }

        /// <summary>Builds an addon holding the given files, in the order given.</summary>
        private string Write(int version, params (string Path, string Body)[] files)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            w.Write("GMAD"u8);
            w.Write((byte)version);
            w.Write(0UL);                       // uploader
            w.Write(0UL);                       // timestamp

            if (version > 1) w.Write((byte)0);  // no required content, terminated immediately

            WriteString(w, "test addon");
            WriteString(w, "description");
            WriteString(w, "author");
            w.Write(1);                         // addon version

            for (int i = 0; i < files.Length; i++)
            {
                w.Write((uint)(i + 1));
                WriteString(w, files[i].Path);
                w.Write((long)files[i].Body.Length);
                w.Write(0u);                    // crc, not checked
            }

            w.Write(0u);                        // index terminator

            foreach (var (_, body) in files)
                w.Write(Encoding.ASCII.GetBytes(body));

            File.WriteAllBytes(path, ms.ToArray());
            return path;
        }

        [Fact]
        public void ReadsAFileFromTheMiddleOfTheArchive()
        {
            // Three files of different lengths, so a body can only be found by accumulating the sizes
            // ahead of it. Reading the second is the case that catches an off-by-one in the index walk.
            Write(3,
                ("models/a/first.phy", "AAAA"),
                ("models/b/second.phy", "BBBBBBBBBB"),
                ("models/c/third.phy", "CC"));

            using var gma = GmaArchive.TryOpen(path);

            Assert.NotNull(gma);
            Assert.Equal(3, gma!.FileCount);

            Assert.True(gma.TryRead("models/b/second.phy", out var bytes));
            Assert.Equal("BBBBBBBBBB", Encoding.ASCII.GetString(bytes));

            Assert.True(gma.TryRead("models/c/third.phy", out var last));
            Assert.Equal("CC", Encoding.ASCII.GetString(last));
        }

        [Fact]
        public void BackslashesInTheIndexMatchForwardSlashLookups()
        {
            // Addon authors pack paths in either form, and a prop always names its model with forward
            // slashes. Half a map's props go missing if the two do not meet.
            Write(3, (@"models\props\crate.phy", "X"));

            using var gma = GmaArchive.TryOpen(path);

            Assert.True(gma!.TryRead("models/props/crate.phy", out _));
        }

        [Fact]
        public void AVersionOneArchiveHasNoRequiredContentBlock()
        {
            // Version 1 omits the string list entirely. Reading it anyway consumes the addon name and
            // everything after it lands one field out.
            Write(1, ("models/old.phy", "OLD"));

            using var gma = GmaArchive.TryOpen(path);

            Assert.True(gma!.TryRead("models/old.phy", out var bytes));
            Assert.Equal("OLD", Encoding.ASCII.GetString(bytes));
        }

        [Fact]
        public void SomethingThatIsNotAnAddonIsRejected()
        {
            File.WriteAllBytes(path, new byte[64]);

            Assert.Null(GmaArchive.TryOpen(path));
        }

        [Fact]
        public void ATruncatedArchiveDoesNotThrow()
        {
            // Downloads get interrupted. One damaged addon must cost that addon, not the build.
            Write(3, ("models/a.phy", "AAAA"), ("models/b.phy", "BBBB"));

            var all = File.ReadAllBytes(path);
            File.WriteAllBytes(path, all[..(all.Length - 6)]);

            var gma = GmaArchive.TryOpen(path);

            // Whatever it manages to index must at least not claim a body that runs off the end.
            if (gma is not null)
            {
                Assert.False(gma.TryRead("models/b.phy", out _));
                gma.Dispose();
            }
        }
    }
}
