using System;
using System.Collections.Generic;
using System.IO;

namespace Meshwright
{
    /// <summary>
    /// The map's static props: which model stands where, at what angle.
    ///
    /// This is the single largest thing Meshwright used to be blind to. Measured on rp_downtown_meowy by
    /// taking the samples <c>fit</c> calls floating and asking the running game what it hits there, 57%
    /// land on a static prop - more than every other cause put together.
    ///
    /// It is tempting to assume props do not matter because the engine generates against
    /// <c>MASK_NPCSOLID_BRUSHONLY</c> and "brush only" sounds like it excludes them. It does not. The
    /// name refers to the contents bits, and a static prop's collision is baked into the world carrying
    /// CONTENTS_SOLID, so the engine's own ground traces stop on props and it builds areas on top of
    /// them. Ignoring them does not match the engine; it disagrees with it.
    ///
    /// **The lump is nested and its record size is version-dependent.** Props live inside
    /// LUMP_GAME_LUMP, which is a directory of sub-lumps, each with its own version and its own
    /// optional LZMA compression - so there are two layers of framing to get through before any prop is
    /// read. The prop record itself grew across versions 4 to 11 as Valve appended fields, and a
    /// hard-coded size reads garbage on any map that does not happen to match.
    ///
    /// The way out is that everything Meshwright needs sits in the first 56 bytes, and that prefix has
    /// not changed since version 4. So the stride is measured from the data - the bytes left after the
    /// dictionary and leaf array, divided by the prop count - and only the stable prefix is read from
    /// each record. That handles versions this was never tested against, including ones that do not
    /// exist yet, which a table of sizes cannot.
    /// </summary>
    public sealed class StaticPropLump
    {
        private const int LumpGameLump = 35;

        /// <summary>'sprp', the game lump holding static props.</summary>
        private const int StaticPropId = 0x73707270;

        /// <summary>
        /// The part of a prop record that has been stable since version 4: origin, angles, model index,
        /// leaf range, solidity, flags, skin, fade distances and lighting origin. Everything later
        /// versions added comes after this.
        /// </summary>
        private const int StablePrefix = 56;

        /// <summary>Model paths, indexed by <see cref="Prop.ModelIndex"/>.</summary>
        public string[] ModelNames { get; private set; } = [];

        public IReadOnlyList<Prop> Props => props;

        private readonly List<Prop> props = [];

        /// <summary>The sub-lump's version, worth reporting because record layout follows it.</summary>
        public int Version { get; private set; }

        /// <summary>
        /// The measured record size and how many records were found. Both are reported because the
        /// stride is inferred from the data rather than known, so it is the first thing to check when a
        /// map's props come out wrong.
        /// </summary>
        public int RecordStride { get; private set; }

        public int RecordCount { get; private set; }

        /// <summary>How many props were skipped for declaring themselves non-solid.</summary>
        public int NonSolid { get; private set; }

        /// <summary>How many records carried each solidity value, so a misread byte shows up as a value
        /// nothing uses rather than as a plausible-looking count.</summary>
        public readonly Dictionary<byte, int> SolidHistogram = [];

        /// <summary>
        /// One placed prop. Angles are Source's (pitch, yaw, roll) in degrees, applied in that engine's
        /// order, which is not the order the names suggest - see <see cref="Rotate"/>.
        /// </summary>
        public readonly record struct Prop(
            BspFile.Vector3 Origin, float Pitch, float Yaw, float Roll,
            int ModelIndex, byte Solid, byte Flags, float Scale);

        /// <summary>
        /// SOLID_NONE. A prop declaring this has no collision at all and the engine walks through it, so
        /// it must not become geometry here either - decals, overlays and detail scenery are placed this
        /// way in large numbers, and treating them as solid would build areas on top of grass.
        /// </summary>
        public const byte SolidNone = 0;

        /// <summary>SOLID_VPHYSICS - collision comes from the model's .phy file. The common case.</summary>
        public const byte SolidVPhysics = 6;

        /// <summary>SOLID_BBOX - collision is the model's bounding box, no .phy needed.</summary>
        public const byte SolidBBox = 2;

        public static StaticPropLump Load(string path)
        {
            var result = new StaticPropLump();

            using var stream = File.OpenRead(path);
            using var r = new BinaryReader(stream);

            r.BaseStream.Seek(8, SeekOrigin.Begin);
            var lumps = new (int Offset, int Length)[BspFile.HeaderLumps];
            for (int i = 0; i < BspFile.HeaderLumps; i++)
            {
                lumps[i] = (r.ReadInt32(), r.ReadInt32());
                r.ReadInt32();
                r.ReadInt32();
            }

            var game = lumps[LumpGameLump];
            if (game.Length <= 0) return result;

            result.ReadGameLump(r, game);
            return result;
        }

        private void ReadGameLump(BinaryReader r, (int Offset, int Length) game)
        {
            r.BaseStream.Seek(game.Offset, SeekOrigin.Begin);
            int count = r.ReadInt32();

            if (count <= 0 || count > 4096) return;

            // The game lump directory. Offsets in it are absolute file offsets, not relative to the
            // game lump - a detail that costs an afternoon if assumed the other way.
            for (int i = 0; i < count; i++)
            {
                int id = r.ReadInt32();
                ushort flags = r.ReadUInt16();
                ushort version = r.ReadUInt16();
                int fileOffset = r.ReadInt32();
                int fileLength = r.ReadInt32();

                if (id != StaticPropId) continue;

                Version = version;

                // Bit 0 means this sub-lump is LZMA-compressed on its own, independently of whether the
                // enclosing lump was. Decompressed size comes from the LZMA header, so the declared
                // length is only the compressed extent.
                byte[] bytes = (flags & 1) != 0
                    ? LzmaLump.Read(r, fileOffset, fileLength)
                    : ReadRaw(r, fileOffset, fileLength);

                ReadProps(bytes, version);
                return;
            }
        }

        private static byte[] ReadRaw(BinaryReader r, int offset, int length)
        {
            r.BaseStream.Seek(offset, SeekOrigin.Begin);
            return r.ReadBytes(length);
        }

        private void ReadProps(byte[] bytes, int version)
        {
            using var ms = new MemoryStream(bytes);
            using var r = new BinaryReader(ms);

            if (ms.Length < 4) return;

            int dictCount = r.ReadInt32();
            if (dictCount < 0 || 4L + (long)dictCount * 128 > ms.Length) return;

            ModelNames = new string[dictCount];
            var name = new byte[128];

            for (int i = 0; i < dictCount; i++)
            {
                if (r.Read(name, 0, 128) != 128) return;

                int end = Array.IndexOf(name, (byte)0);
                ModelNames[i] = System.Text.Encoding.ASCII
                    .GetString(name, 0, end < 0 ? 128 : end)
                    .Replace('\\', '/');
            }

            if (ms.Position + 4 > ms.Length) return;

            int leafCount = r.ReadInt32();
            if (leafCount < 0 || ms.Position + 2L * leafCount > ms.Length) return;
            ms.Seek(2L * leafCount, SeekOrigin.Current);

            if (ms.Position + 4 > ms.Length) return;

            int propCount = r.ReadInt32();
            if (propCount <= 0) return;

            // The stride, measured rather than assumed. Version 4 records are 56 bytes and every later
            // version appends to them; dividing the remaining bytes by the prop count recovers the size
            // this particular map used, including for versions that did not exist when this was written.
            long remaining = ms.Length - ms.Position;
            int stride = (int)(remaining / propCount);

            RecordStride = stride;

            RecordCount = propCount;


            if (stride < StablePrefix) return;

            long first = ms.Position;

            for (int i = 0; i < propCount; i++)
            {
                ms.Seek(first + (long)i * stride, SeekOrigin.Begin);

                var origin = new BspFile.Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                float pitch = r.ReadSingle();
                float yaw = r.ReadSingle();
                float roll = r.ReadSingle();
                int modelIndex = r.ReadUInt16();
                r.ReadUInt16();                 // first leaf
                r.ReadUInt16();                 // leaf count
                byte solid = r.ReadByte();
                byte flags = r.ReadByte();

                SolidHistogram[solid] = SolidHistogram.GetValueOrDefault(solid) + 1;

                if ((uint)modelIndex >= (uint)ModelNames.Length) continue;

                if (solid == SolidNone) { NonSolid++; continue; }

                props.Add(new Prop(origin, pitch, yaw, roll, modelIndex, solid, flags,
                    ReadScale(r, ms, first + (long)i * stride, stride, version)));
            }
        }

        /// <summary>
        /// The uniform scale a version 11 map can give a prop, defaulting to 1 everywhere else.
        ///
        /// Read from the end of the record rather than by counting forwards through the fields that
        /// precede it. Those fields differ between versions and between branches of the engine, so
        /// walking to the scale is exactly the fragility the measured stride avoids; the scale is the
        /// last float in the record, and taking it from the back is stable however the middle is laid
        /// out. A value outside a sane range means the guess was wrong and 1 is used instead, which is
        /// what every map before version 11 means anyway.
        /// </summary>
        private static float ReadScale(BinaryReader r, MemoryStream ms, long recordStart, int stride, int version)
        {
            if (version < 11 || stride < 4) return 1f;

            ms.Seek(recordStart + stride - 4, SeekOrigin.Begin);
            float scale = r.ReadSingle();

            return scale is > 0.001f and < 1000f ? scale : 1f;
        }

        /// <summary>
        /// Rotates a model-space point into world space by a prop's angles.
        ///
        /// Source applies these as yaw about Z, then pitch about Y, then roll about X, and it stores
        /// them in the order (pitch, yaw, roll) - so neither the storage order nor the field names tell
        /// you the multiplication order. Getting it wrong is not obvious in aggregate either: a
        /// misordered rotation still produces a prop-shaped hull in about the right place, and only
        /// shows up as props that are subtly turned the wrong way.
        /// </summary>
        public static BspFile.Vector3 Rotate(BspFile.Vector3 v, float pitch, float yaw, float roll)
        {
            const float ToRadians = MathF.PI / 180f;

            float sy = MathF.Sin(yaw * ToRadians), cy = MathF.Cos(yaw * ToRadians);
            float sp = MathF.Sin(pitch * ToRadians), cp = MathF.Cos(pitch * ToRadians);
            float sr = MathF.Sin(roll * ToRadians), cr = MathF.Cos(roll * ToRadians);

            // Forward, right and up as Source builds them in AngleVectors, then the point expressed in
            // that basis. Written out rather than composed from three matrices so the order is visible.
            float fx = cp * cy, fy = cp * sy, fz = -sp;
            float rx = -sr * sp * cy + cr * sy;
            float ry = -sr * sp * sy - cr * cy;
            float rz = -sr * cp;
            float ux = cr * sp * cy + sr * sy;
            float uy = cr * sp * sy - sr * cy;
            float uz = cr * cp;

            return new BspFile.Vector3(
                v.X * fx - v.Y * rx + v.Z * ux,
                v.X * fy - v.Y * ry + v.Z * uy,
                v.X * fz - v.Y * rz + v.Z * uz);
        }
    }
}
