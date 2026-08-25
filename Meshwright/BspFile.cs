using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Meshwright
{
    /// <summary>
    /// Minimal BSP reader covering the geometry lumps the nav tooling needs.
    ///
    /// Compile Pal's own <c>BSPPack/BSP.cs</c> reads this format too, but only the entity and texture
    /// lumps - it is an asset extractor, not a geometry parser. This reads brushes, brushsides, planes
    /// and texinfo so ladder brushes can be located by material and so the visibility pass has
    /// collision geometry to trace against.
    ///
    /// Layout reference: https://developer.valvesoftware.com/wiki/BSP_(Source)
    /// </summary>
    public sealed class BspFile
    {
        public const int HeaderLumps = 64;

        // lump indices we care about
        private const int LumpEntities = 0;
        private const int LumpPlanes = 1;
        private const int LumpTexdata = 2;
        private const int LumpTexinfo = 6;
        private const int LumpModels = 14;
        private const int LumpBrushes = 18;
        private const int LumpBrushSides = 19;
        private const int LumpTexdataStringData = 43;
        private const int LumpTexdataStringTable = 44;

        public int Version { get; private set; }
        public int MapRevision { get; private set; }

        public string EntityLump { get; private set; } = string.Empty;
        public Plane[] Planes { get; private set; } = [];
        public TexInfo[] TexInfos { get; private set; } = [];
        public TexData[] TexDatas { get; private set; } = [];
        public Brush[] Brushes { get; private set; } = [];
        public BrushSide[] BrushSides { get; private set; } = [];

        /// <summary>
        /// Bounds of every BSP model. Index 0 is worldspawn; the rest belong to brush entities and are
        /// expressed relative to the entity's own origin, not in world space.
        /// </summary>
        public BrushModel[] BrushModelBounds { get; private set; } = [];

        /// <summary>Material names, indexed the same as <see cref="TexDatas"/> via NameStringTableId.</summary>
        public string[] MaterialNames { get; private set; } = [];

        private readonly LumpEntry[] lumps = new LumpEntry[HeaderLumps];

        private struct LumpEntry
        {
            public int Offset;
            public int Length;
            public int Version;
        }

        public static BspFile Load(string path)
        {
            var bsp = new BspFile();
            using var stream = File.OpenRead(path);
            using var r = new BinaryReader(stream, Encoding.ASCII);
            bsp.Read(r);
            return bsp;
        }

        private void Read(BinaryReader r)
        {
            int ident = r.ReadInt32();
            if (ident != 0x50534256) // 'VBSP' little-endian
                throw new InvalidDataException($"Not a Source BSP: ident 0x{ident:X8}");

            Version = r.ReadInt32();

            for (int i = 0; i < HeaderLumps; i++)
            {
                lumps[i] = new LumpEntry
                {
                    Offset = r.ReadInt32(),
                    Length = r.ReadInt32(),
                    Version = r.ReadInt32(),
                };
                r.ReadInt32(); // fourCC, only meaningful for compressed lumps
            }

            MapRevision = r.ReadInt32();

            EntityLump = ReadLumpString(r, LumpEntities);
            Planes = ReadLumpArray(r, LumpPlanes, Plane.SizeOf, Plane.Read);
            TexInfos = ReadLumpArray(r, LumpTexinfo, TexInfo.SizeOf, TexInfo.Read);
            TexDatas = ReadLumpArray(r, LumpTexdata, TexData.SizeOf, TexData.Read);
            Brushes = ReadLumpArray(r, LumpBrushes, Brush.SizeOf, Brush.Read);
            BrushSides = ReadLumpArray(r, LumpBrushSides, BrushSide.SizeOf, BrushSide.Read);

            BrushModelBounds = ReadLumpArray(r, LumpModels, BrushModel.SizeOf, BrushModel.Read);

            MaterialNames = ReadMaterialNames(r);
        }

        private string ReadLumpString(BinaryReader r, int index)
        {
            var lump = lumps[index];
            if (lump.Length <= 0) return string.Empty;

            var bytes = LzmaLump.Read(r, lump.Offset, lump.Length);
            return Encoding.ASCII.GetString(bytes).TrimEnd('\0');
        }

        private T[] ReadLumpArray<T>(BinaryReader r, int index, int elementSize, Func<BinaryReader, T> read)
        {
            var lump = lumps[index];
            if (lump.Length <= 0) return [];

            var bytes = LzmaLump.Read(r, lump.Offset, lump.Length);
            int count = bytes.Length / elementSize;
            var items = new T[count];

            using var ms = new MemoryStream(bytes);
            using var lr = new BinaryReader(ms);
            for (int i = 0; i < count; i++)
                items[i] = read(lr);

            return items;
        }

        /// <summary>
        /// Material names are stored indirectly: a table of int offsets into a blob of NUL-terminated
        /// strings. TexData.NameStringTableId indexes the table, not the blob.
        /// </summary>
        private string[] ReadMaterialNames(BinaryReader r)
        {
            var tableLump = lumps[LumpTexdataStringTable];
            var dataLump = lumps[LumpTexdataStringData];

            if (tableLump.Length <= 0 || dataLump.Length <= 0)
                return [];

            var tableBytes = LzmaLump.Read(r, tableLump.Offset, tableLump.Length);
            int count = tableBytes.Length / sizeof(int);
            var offsets = new int[count];

            using (var ms = new MemoryStream(tableBytes))
            using (var lr = new BinaryReader(ms))
            {
                for (int i = 0; i < count; i++)
                    offsets[i] = lr.ReadInt32();
            }

            var blob = LzmaLump.Read(r, dataLump.Offset, dataLump.Length);

            var names = new string[count];
            for (int i = 0; i < count; i++)
            {
                int start = offsets[i];
                if (start < 0 || start >= blob.Length) { names[i] = string.Empty; continue; }

                int end = Array.IndexOf(blob, (byte)0, start);
                if (end < 0) end = blob.Length;

                names[i] = Encoding.ASCII.GetString(blob, start, end - start);
            }

            return names;
        }

        /// <summary>Material name for a brush side, or empty if it has none.</summary>
        public string GetMaterialName(BrushSide side)
        {
            if (side.TexInfo < 0 || side.TexInfo >= TexInfos.Length)
                return string.Empty;

            int texData = TexInfos[side.TexInfo].TexData;
            if (texData < 0 || texData >= TexDatas.Length)
                return string.Empty;

            int nameId = TexDatas[texData].NameStringTableId;
            if (nameId < 0 || nameId >= MaterialNames.Length)
                return string.Empty;

            return MaterialNames[nameId];
        }

        /// <summary>
        /// Axis-aligned bounds of a brush, derived from its side planes.
        ///
        /// Source brushes are convex volumes defined by half-spaces rather than by explicit vertices,
        /// so bounds come from the six axis-aligned planes. Every brush vbsp emits carries bevel planes
        /// on all six axes, which is what makes this reliable without full plane intersection.
        /// Returns false if the brush lacks a bound on some axis.
        /// </summary>
        public bool TryGetBrushBounds(Brush brush, out Vector3 mins, out Vector3 maxs)
        {
            // Accumulated in locals: Vector3 is readonly, and this builds its result one axis at a time.
            float mnx = float.MaxValue, mny = float.MaxValue, mnz = float.MaxValue;
            float mxx = float.MinValue, mxy = float.MinValue, mxz = float.MinValue;

            for (int i = 0; i < brush.NumSides; i++)
            {
                int sideIndex = brush.FirstSide + i;
                if (sideIndex < 0 || sideIndex >= BrushSides.Length) continue;

                var side = BrushSides[sideIndex];
                if (side.PlaneNum >= Planes.Length) continue;

                var plane = Planes[side.PlaneNum];
                var n = plane.Normal;

                // only axis-aligned planes contribute to an AABB
                if (n.X > 0.999f) mxx = Math.Min(mxx == float.MinValue ? plane.Distance : mxx, plane.Distance);
                else if (n.X < -0.999f) mnx = Math.Max(mnx == float.MaxValue ? -plane.Distance : mnx, -plane.Distance);
                else if (n.Y > 0.999f) mxy = Math.Min(mxy == float.MinValue ? plane.Distance : mxy, plane.Distance);
                else if (n.Y < -0.999f) mny = Math.Max(mny == float.MaxValue ? -plane.Distance : mny, -plane.Distance);
                else if (n.Z > 0.999f) mxz = Math.Min(mxz == float.MinValue ? plane.Distance : mxz, plane.Distance);
                else if (n.Z < -0.999f) mnz = Math.Max(mnz == float.MaxValue ? -plane.Distance : mnz, -plane.Distance);
            }

            mins = new Vector3(mnx, mny, mnz);
            maxs = new Vector3(mxx, mxy, mxz);

            return mnx != float.MaxValue && mny != float.MaxValue && mnz != float.MaxValue
                && mxx != float.MinValue && mxy != float.MinValue && mxz != float.MinValue;
        }

        /// <summary>
        /// Readonly so the compiler stops defending it.
        ///
        /// A mutable struct reached through a readonly reference - a <c>readonly</c> field, an <c>in</c>
        /// parameter, a static - has to be copied before any member is touched, because the compiler
        /// cannot prove the member will not write to it. This type is threaded through every trace in
        /// the program and those copies were being made on the hottest paths there are. Marking it
        /// readonly is free at the source level and removes them.
        /// </summary>
        public readonly struct Vector3
        {
            public readonly float X, Y, Z;
            public Vector3(float x, float y, float z) { X = x; Y = y; Z = z; }
            public override string ToString() => $"({X:F1} {Y:F1} {Z:F1})";
        }

        public struct Plane
        {
            public const int SizeOf = 20;

            public Vector3 Normal;
            public float Distance;
            public int Type;

            public static Plane Read(BinaryReader r) => new()
            {
                Normal = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                Distance = r.ReadSingle(),
                Type = r.ReadInt32(),
            };
        }

        public struct TexInfo
        {
            public const int SizeOf = 72;

            public int Flags;
            public int TexData;

            public static TexInfo Read(BinaryReader r)
            {
                r.BaseStream.Seek(32, SeekOrigin.Current); // textureVecs[2][4]
                r.BaseStream.Seek(32, SeekOrigin.Current); // lightmapVecs[2][4]
                return new TexInfo { Flags = r.ReadInt32(), TexData = r.ReadInt32() };
            }
        }

        public struct TexData
        {
            public const int SizeOf = 32;

            public Vector3 Reflectivity;
            public int NameStringTableId;
            public int Width, Height;
            public int ViewWidth, ViewHeight;

            public static TexData Read(BinaryReader r) => new()
            {
                Reflectivity = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                NameStringTableId = r.ReadInt32(),
                Width = r.ReadInt32(),
                Height = r.ReadInt32(),
                ViewWidth = r.ReadInt32(),
                ViewHeight = r.ReadInt32(),
            };
        }

        public struct BrushModel
        {
            public const int SizeOf = 48;

            public Vector3 Mins, Maxs;
            public int HeadNode;

            public static BrushModel Read(BinaryReader r)
            {
                var model = new BrushModel
                {
                    Mins = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                    Maxs = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                };

                r.BaseStream.Seek(12, SeekOrigin.Current); // lump origin; the entity keyvalue positions it
                model.HeadNode = r.ReadInt32();
                r.BaseStream.Seek(8, SeekOrigin.Current);  // firstface, numfaces

                return model;
            }
        }

        public struct Brush
        {
            public const int SizeOf = 12;

            public int FirstSide;
            public int NumSides;
            public int Contents;

            public static Brush Read(BinaryReader r) => new()
            {
                FirstSide = r.ReadInt32(),
                NumSides = r.ReadInt32(),
                Contents = r.ReadInt32(),
            };
        }

        public struct BrushSide
        {
            public const int SizeOf = 8;

            public ushort PlaneNum;
            public short TexInfo;
            public short DispInfo;
            public short Bevel;

            public static BrushSide Read(BinaryReader r) => new()
            {
                PlaneNum = r.ReadUInt16(),
                TexInfo = r.ReadInt16(),
                DispInfo = r.ReadInt16(),
                Bevel = r.ReadInt16(),
            };
        }
    }
}
