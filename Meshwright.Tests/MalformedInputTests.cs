using System;
using System.IO;
using System.Text;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That a file which is corrupt, truncated, or simply not what it claims is refused rather than
    /// acted on.
    ///
    /// Every count in these formats is read off disk and then used to size an array or bound a loop.
    /// The failure mode without checking is not a wrong answer - it is an OutOfMemoryException, an
    /// overflow, or a multi-gigabyte allocation from four bytes that happened to be 0xFFFFFFFF, and none
    /// of those says anything useful about the file that caused them.
    ///
    /// These matter because the inputs are not the user's own work. A .bsp is a map from the workshop or
    /// from whoever submitted it to a build server, and a .nav beside it is whatever was there.
    /// </summary>
    public class MalformedInputTests
    {
        private static byte[] NavWith(Action<BinaryWriter> body)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
            {
                w.Write(NavFile.Magic);
                w.Write(16u);       // version
                w.Write(0u);        // sub version
                w.Write(0u);        // bsp size
                w.Write((byte)0);   // is analysed
                w.Write((ushort)0); // no places
                w.Write((byte)0);   // no unnamed areas
                body(w);
            }

            return ms.ToArray();
        }

        private static NavFile Read(byte[] bytes)
        {
            using var ms = new MemoryStream(bytes, writable: false);
            using var r = new BinaryReader(ms, Encoding.ASCII);
            return NavFile.Read(r);
        }

        /// <summary>
        /// The record that runs to millions on an analysed mesh, and the one whose count was used to set
        /// a List capacity directly. 0xFFFFFFFF asked for a two-billion-element list.
        /// </summary>
        [Fact]
        public void AVisibleAreaCountLargerThanTheFileIsRefused()
        {
            var bytes = NavWith(w =>
            {
                w.Write(1u);                       // one area
                w.Write(1u);                       // id
                w.Write(0);                        // attributes
                for (int i = 0; i < 8; i++) w.Write(0f);   // corners, NeZ, SwZ
                for (int d = 0; d < 4; d++) w.Write(0u);   // no connections
                w.Write((byte)0);                  // no hiding spots
                w.Write(0u);                       // no encounters
                w.Write((ushort)0);                // place
                for (int d = 0; d < 2; d++) w.Write(0u);   // no ladder links
                for (int i = 0; i < 2; i++) w.Write(0f);   // occupy times
                for (int i = 0; i < 4; i++) w.Write(0f);   // light intensity
                w.Write(uint.MaxValue);            // visible areas: a lie
            });

            var ex = Assert.Throws<InvalidDataException>(() => Read(bytes));
            Assert.Contains("visible areas", ex.Message);
        }

        [Fact]
        public void AnAreaCountLargerThanTheFileIsRefused()
        {
            var bytes = NavWith(w => w.Write(uint.MaxValue));

            var ex = Assert.Throws<InvalidDataException>(() => Read(bytes));
            Assert.Contains("areas", ex.Message);
        }

        [Fact]
        public void ALadderCountLargerThanTheFileIsRefused()
        {
            var bytes = NavWith(w =>
            {
                w.Write(0u);                // no areas
                w.Write(uint.MaxValue);     // ladders: a lie
            });

            var ex = Assert.Throws<InvalidDataException>(() => Read(bytes));
            Assert.Contains("ladders", ex.Message);
        }

        [Fact]
        public void AConnectionCountLargerThanTheFileIsRefused()
        {
            var bytes = NavWith(w =>
            {
                w.Write(1u);
                w.Write(1u);
                w.Write(0);
                for (int i = 0; i < 8; i++) w.Write(0f);
                w.Write(uint.MaxValue);     // north connections: a lie

                // Padding, so the area-count check upstream is satisfied and this test reaches the
                // check it is actually about.
                w.Write(new byte[96]);
            });

            var ex = Assert.Throws<InvalidDataException>(() => Read(bytes));
            Assert.Contains("connections", ex.Message);
        }

        /// <summary>A real mesh still round-trips; the checks must not reject valid files.</summary>
        [Fact]
        public void AnHonestFileIsStillRead()
        {
            var nav = new NavFile();
            var area = new NavArea { Id = 7 };
            area.SeCorner[0] = 50; area.SeCorner[1] = 50;
            area.VisibleAreas.Add(new VisibleArea { AreaId = 7, Attributes = 1 });
            nav.Areas.Add(area);

            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true)) nav.Write(w);

            var read = Read(ms.ToArray());

            Assert.Single(read.Areas);
            Assert.Equal(7u, read.Areas[0].Id);
            Assert.Single(read.Areas[0].VisibleAreas);
        }

        /// <summary>
        /// A lump table entry pointing outside the file. Every lump reader funnels through LzmaLump,
        /// and BinaryReader.ReadBytes sizes its buffer from the count before reading a byte - so an
        /// unchecked length near int.MaxValue is an allocation request, not a short read.
        /// </summary>
        [Theory]
        [InlineData(0, int.MaxValue)]      // starts inside the file, runs far past the end
        [InlineData(-1, 16)]               // negative offset
        [InlineData(1_000_000, 16)]        // starts past the end
        public void ALumpPointingOutsideTheFileYieldsNothing(int offset, int length)
        {
            var file = new byte[64];

            using var ms = new MemoryStream(file, writable: false);
            using var r = new BinaryReader(ms);

            var bytes = LzmaLump.Read(r, offset, length);

            Assert.True(bytes.Length <= file.Length);
        }
    }
}
