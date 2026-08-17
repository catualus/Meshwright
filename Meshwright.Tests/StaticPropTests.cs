using System;
using System.IO;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// The static prop pipeline's parsing, pinned where it can be pinned without a game installed.
    ///
    /// These cover the parts that were actually got wrong while building it, which is a different set
    /// from the parts that look hardest. The .phy tree walk is the gnarliest code and is not tested
    /// here - it needs a real model, and asserting against a hand-built fake would only prove the fake
    /// matches the parser. What is tested is everything whose correctness is decidable on its own:
    ///
    /// - the record stride inference, because a fixed size reads garbage on half the map versions in
    ///   circulation and the bug is silent;
    /// - the rotation, because a wrong multiplication order still produces prop-shaped geometry in
    ///   roughly the right place, so nothing downstream notices;
    /// - the refusal to invent collision, because the fallback that guessed a bounding box measurably
    ///   destroyed real mesh and someone will be tempted to reinstate it.
    ///
    /// The .phy parse is instead checked against the engine directly, by comparing `props -column`
    /// output with what the game reports in the same column, and against each model's own declared
    /// bounds via `props -model`. That found a real bug - the axis conversion was turning the wrong way
    /// about X - which no self-consistent unit test would have.
    /// </summary>
    public class StaticPropLumpTests
    {
        /// <summary>
        /// Builds a BSP holding nothing but a static prop game lump, at a chosen record stride.
        ///
        /// The point is the stride: prop records grew from 56 bytes to 72 across lump versions 4 to 11
        /// by appending fields, and the reader is supposed to measure the size rather than know it.
        /// Writing a map with a deliberately unfamiliar stride is the only way to test that, and it is
        /// also the case a real map will present the day Valve appends another field.
        /// </summary>
        private static string WriteBsp(int lumpVersion, int stride, params (float X, float Y, float Z, float Yaw, byte Solid)[] props)
        {
            const int LumpGameLump = 35;
            string path = Path.Combine(Path.GetTempPath(), $"mw_props_{Guid.NewGuid():N}.bsp");

            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            w.Write(0x50534256);            // 'VBSP'
            w.Write(20);                    // version

            long directory = ms.Position;
            for (int i = 0; i < BspFile.HeaderLumps * 4; i++) w.Write(0);
            w.Write(0);                     // map revision

            int gameLumpOffset = (int)ms.Position;

            // The game lump directory: one entry, uncompressed, pointing at an absolute file offset.
            w.Write(1);
            w.Write(0x73707270);            // 'sprp'
            w.Write((ushort)0);             // flags: not compressed
            w.Write((ushort)lumpVersion);

            long fixupOffset = ms.Position;
            w.Write(0);                     // file offset, patched below
            w.Write(0);                     // file length, patched below

            int propsAt = (int)ms.Position;

            w.Write(1);                                             // one model in the dictionary
            var name = new byte[128];
            "models/test/thing.mdl"u8.CopyTo(name);
            w.Write(name);

            w.Write(0);                                             // no leaf entries
            w.Write(props.Length);

            foreach (var p in props)
            {
                long start = ms.Position;

                w.Write(p.X); w.Write(p.Y); w.Write(p.Z);
                w.Write(0f); w.Write(p.Yaw); w.Write(0f);           // pitch, yaw, roll
                w.Write((ushort)0);                                 // model index
                w.Write((ushort)0);                                 // first leaf
                w.Write((ushort)0);                                 // leaf count
                w.Write(p.Solid);
                w.Write((byte)0);                                   // flags

                while (ms.Position - start < stride) w.Write((byte)0);
            }

            int propsLength = (int)ms.Position - propsAt;

            ms.Seek(fixupOffset, SeekOrigin.Begin);
            w.Write(propsAt);
            w.Write(propsLength);

            ms.Seek(directory + LumpGameLump * 16, SeekOrigin.Begin);
            w.Write(gameLumpOffset);
            w.Write((int)(propsAt + propsLength - gameLumpOffset));

            File.WriteAllBytes(path, ms.ToArray());
            return path;
        }

        [Fact]
        public void ReadsPropsAtTheStrideTheMapActuallyUses()
        {
            // 88 bytes is not a size any released version uses. That is the point: the reader must take
            // the stride from the data, so a version it has never seen still parses.
            string path = WriteBsp(lumpVersion: 10, stride: 88,
                (100f, 200f, 300f, 90f, StaticPropLump.SolidVPhysics),
                (400f, 500f, 600f, 45f, StaticPropLump.SolidVPhysics));

            try
            {
                var lump = StaticPropLump.Load(path);

                Assert.Equal(88, lump.RecordStride);
                Assert.Equal(2, lump.RecordCount);
                Assert.Equal(2, lump.Props.Count);

                Assert.Equal(100f, lump.Props[0].Origin.X);
                Assert.Equal(200f, lump.Props[0].Origin.Y);
                Assert.Equal(300f, lump.Props[0].Origin.Z);
                Assert.Equal(90f, lump.Props[0].Yaw);

                Assert.Equal(400f, lump.Props[1].Origin.X);
                Assert.Equal(45f, lump.Props[1].Yaw);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void SkipsPropsThatDeclareThemselvesNonSolid()
        {
            // SOLID_NONE props outnumber solid ones on some maps - 729 of 2,252 on rp_downtown_meowy,
            // and 178 of 182 on gm_construct. Treating them as collision would build areas on top of
            // grass tufts and light fittings.
            string path = WriteBsp(lumpVersion: 6, stride: 64,
                (0f, 0f, 0f, 0f, StaticPropLump.SolidNone),
                (10f, 0f, 0f, 0f, StaticPropLump.SolidVPhysics),
                (20f, 0f, 0f, 0f, StaticPropLump.SolidNone));

            try
            {
                var lump = StaticPropLump.Load(path);

                Assert.Single(lump.Props);
                Assert.Equal(10f, lump.Props[0].Origin.X);
                Assert.Equal(2, lump.NonSolid);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void SurvivesAMapWithNoPropsAtAll()
        {
            string path = WriteBsp(lumpVersion: 4, stride: 56);

            try
            {
                var lump = StaticPropLump.Load(path);
                Assert.Empty(lump.Props);
            }
            finally { File.Delete(path); }
        }

        [Theory]
        [InlineData(0f, 1f, 0f, 0f)]       // no rotation leaves +X alone
        [InlineData(90f, 0f, 1f, 0f)]      // yaw 90 turns +X to +Y
        [InlineData(180f, -1f, 0f, 0f)]
        [InlineData(270f, 0f, -1f, 0f)]
        public void YawTurnsAboutTheVerticalAxis(float yaw, float x, float y, float z)
        {
            var turned = StaticPropLump.Rotate(new BspFile.Vector3(1, 0, 0), 0f, yaw, 0f);

            Assert.Equal(x, turned.X, 3);
            Assert.Equal(y, turned.Y, 3);
            Assert.Equal(z, turned.Z, 3);
        }

        [Fact]
        public void PitchTipsForwardAboutTheSideAxis()
        {
            // Source's pitch is nose-down positive, so +90 sends the forward axis straight down. Getting
            // the sign backwards leaves props tipped the wrong way, which looks plausible in aggregate.
            var turned = StaticPropLump.Rotate(new BspFile.Vector3(1, 0, 0), 90f, 0f, 0f);

            Assert.Equal(0f, turned.X, 3);
            Assert.Equal(0f, turned.Y, 3);
            Assert.Equal(-1f, turned.Z, 3);
        }

        [Fact]
        public void RotationPreservesLength()
        {
            // The one property that holds for every angle. A rotation built from the wrong basis vectors
            // usually scales or shears, and this catches that without pinning a convention.
            var v = new BspFile.Vector3(3, -4, 12);
            var turned = StaticPropLump.Rotate(v, 37f, -114f, 61f);

            float before = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
            float after = MathF.Sqrt(turned.X * turned.X + turned.Y * turned.Y + turned.Z * turned.Z);

            Assert.Equal(before, after, 3);
        }
    }

    public class PhyFileTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(4)]
        [InlineData(15)]
        public void TooShortToHoldAHeaderYieldsNothing(int length) =>
            Assert.Equal(0, PhyFile.Parse(new byte[length]).TriangleCount);

        [Fact]
        public void GarbageIsRejectedRatherThanThrown()
        {
            // Third-party models are the norm in Garry's Mod and some of them are broken. One bad .phy
            // must cost that prop's collision, not the whole build - so every path out of the parser is
            // a return, and this is the test that says so.
            var random = new Random(1234);
            var bytes = new byte[4096];
            random.NextBytes(bytes);

            var phy = PhyFile.Parse(bytes);

            Assert.Equal(0, phy.SolidsParsed);
            Assert.Equal(0, phy.TriangleCount);
        }

        [Fact]
        public void ASolidCountThatWouldRunOffTheEndIsRefused()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            w.Write(16);                    // header size
            w.Write(0);                     // id
            w.Write(1_000_000);             // solid count, wildly beyond the file
            w.Write(0);                     // checksum

            Assert.Equal(0, PhyFile.Parse(ms.ToArray()).TriangleCount);
        }
    }

    public class StudioModelTests
    {
        private static byte[] Header(int version, float[] hull, int flags)
        {
            var bytes = new byte[512];

            using var ms = new MemoryStream(bytes);
            using var w = new BinaryWriter(ms);

            w.Write(0x54534449);            // 'IDST'
            w.Write(version);

            ms.Seek(104, SeekOrigin.Begin);
            foreach (float f in hull) w.Write(f);

            ms.Seek(104 + 48, SeekOrigin.Begin);
            w.Write(flags);

            return bytes;
        }

        [Fact]
        public void ReadsCollisionBoundsAndTheStaticPropFlag()
        {
            var model = StudioModel.Parse(Header(48, [-16f, -20f, 0f, 16f, 20f, 72f], 0x10));

            Assert.True(model.Valid);
            Assert.True(model.StaticProp);
            Assert.Equal(-16f, model.HullMin.X);
            Assert.Equal(72f, model.HullMax.Z);
        }

        [Fact]
        public void AnEmptyBoundingBoxIsNotAShape()
        {
            // Both corners at the origin describes nothing. Accepting it would place a point-sized solid
            // at every prop using that model, which is worse than having no collision for it.
            var model = StudioModel.Parse(Header(48, [0f, 0f, 0f, 0f, 0f, 0f], 0x10));

            Assert.False(model.Valid);
            Assert.Empty(model.AsTriangles());
        }

        [Fact]
        public void SomethingThatIsNotAModelIsRejected() =>
            Assert.False(StudioModel.Parse(new byte[512]).Valid);

        [Fact]
        public void TheBoxBecomesTwelveTrianglesSpanningItsCorners()
        {
            var model = StudioModel.Parse(Header(48, [-1f, -2f, -3f, 4f, 5f, 6f], 0x10));
            var tris = model.AsTriangles();

            Assert.Equal(36, tris.Length);

            float minX = float.MaxValue, maxZ = float.MinValue;
            foreach (var v in tris)
            {
                minX = MathF.Min(minX, v.X);
                maxZ = MathF.Max(maxZ, v.Z);
            }

            Assert.Equal(-1f, minX);
            Assert.Equal(6f, maxZ);
        }
    }
}
