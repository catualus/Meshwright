using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Meshwright
{
    /// <summary>
    /// Reader/writer for Source engine navigation meshes (.nav), format version 16.
    ///
    /// Field order is transcribed from Valve's public source-sdk-2013:
    ///   CNavMesh::Save    - nav_file.cpp
    ///   CNavArea::Save    - nav_file.cpp:219  (not nav_area.cpp, despite the class)
    ///   HidingSpot::Save  - nav_mesh.cpp:3067
    ///   CNavLadder::Save  - nav_ladder.cpp
    ///
    /// Everything is little-endian. The area record contains several fixed-size arrays whose lengths
    /// come from engine constants (MAX_NAV_TEAMS = 2, NUM_CORNERS = 4, NUM_DIRECTIONS = 4,
    /// NUM_LADDER_DIRECTIONS = 2); getting any of them wrong desynchronises every following byte
    /// rather than failing loudly, which is why <see cref="NavFile"/> is gated on a byte-for-byte
    /// round-trip test rather than on "it parsed without throwing".
    /// </summary>
    public sealed class NavFile
    {
        public const uint Magic = 0xFEEDFACE;
        public const uint CurrentVersion = 16;

        public uint Version { get; set; } = CurrentVersion;
        public uint SubVersion { get; set; }
        public uint BspSize { get; set; }
        public bool IsAnalyzed { get; set; }
        public bool HasUnnamedAreas { get; set; }

        public List<string> Places { get; } = [];
        public List<NavArea> Areas { get; } = [];
        public List<NavLadder> Ladders { get; } = [];

        /// <summary>
        /// Bytes after the ladder section. Derived classes may append custom data via
        /// CNavMesh::SaveCustomData; preserving it verbatim keeps the round-trip exact and avoids
        /// silently discarding game-specific state we do not model.
        /// </summary>
        public byte[] TrailingData { get; set; } = [];

        /// <summary>
        /// Reads a mesh from disk.
        ///
        /// The file is pulled into memory in one go and parsed from there, rather than read field by
        /// field off a <see cref="FileStream"/>. That sounds like a wash and is not: an analysed mesh is
        /// millions of four- and one-byte fields - gm_construct's carries 1.37 million visible-area
        /// records alone - and every one of those was a call through the stream's buffering machinery.
        /// Measured on that mesh, loading took 425ms against 51ms for the entire BSP beside it, which
        /// made reading the previous result the most expensive thing most commands did.
        ///
        /// A nav file is a few megabytes, so holding it whole costs nothing worth counting.
        /// </summary>
        public static NavFile Load(string path)
        {
            using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
            using var r = new BinaryReader(stream, Encoding.ASCII);
            return Read(r);
        }

        /// <summary>
        /// Writes a mesh to disk, buffered whole for the same reason <see cref="Load"/> reads it whole.
        /// </summary>
        public void Save(string path)
        {
            using var buffer = new MemoryStream();

            using (var w = new BinaryWriter(buffer, Encoding.ASCII, leaveOpen: true))
                Write(w);

            using var file = File.Create(path);
            buffer.Position = 0;
            buffer.CopyTo(file);
        }

        /// <summary>
        /// A record count the remaining bytes could not possibly supply, refused before anything is
        /// sized from it.
        ///
        /// Every count in this format is read straight off disk and then used to allocate or to bound a
        /// loop. A file that is corrupt, truncated, or simply not the mesh it claims to be hands over
        /// whatever those four bytes happened to be - and 0xFFFFFFFF as a visible-area count is an
        /// immediate request for a two-billion-element list, which fails as an OutOfMemoryException or
        /// an overflow rather than as "this file is not valid".
        ///
        /// Checked against the bytes actually left in the stream, which is exact rather than a guessed
        /// ceiling: a record cannot be smaller than its fixed fields, so a count larger than
        /// <c>remaining / bytesEach</c> describes a file that does not exist. Reading is done from a
        /// MemoryStream over the whole file, so the length is always known.
        /// </summary>
        internal static int Counted(BinaryReader r, uint count, int bytesEach, string what)
        {
            long remaining = r.BaseStream.Length - r.BaseStream.Position;
            long possible = remaining / Math.Max(1, bytesEach);

            if (count > possible)
            {
                throw new InvalidDataException(
                    $"Corrupt nav file: claims {count:N0} {what} with {remaining:N0} bytes left, " +
                    $"which is room for at most {possible:N0}.");
            }

            return (int)count;
        }

        public static NavFile Read(BinaryReader r)
        {
            var nav = new NavFile();

            uint magic = r.ReadUInt32();
            if (magic != Magic)
                throw new InvalidDataException($"Not a nav file: expected magic 0x{Magic:X8}, got 0x{magic:X8}");

            nav.Version = r.ReadUInt32();
            if (nav.Version > CurrentVersion || nav.Version < 4)
                throw new InvalidDataException($"Unsupported nav version {nav.Version} (supported: 4-{CurrentVersion})");

            if (nav.Version >= 10)
                nav.SubVersion = r.ReadUInt32();

            nav.BspSize = r.ReadUInt32();

            if (nav.Version >= 14)
                nav.IsAnalyzed = r.ReadByte() != 0;

            // place directory: names are stored once and referenced by index from each area
            ushort placeCount = r.ReadUInt16();
            Counted(r, placeCount, 3, "places");   // a name is at least a length and a NUL

            for (int i = 0; i < placeCount; i++)
            {
                ushort length = r.ReadUInt16();
                Counted(r, length, 1, "bytes of place name");
                // length includes the trailing NUL
                string name = Encoding.ASCII.GetString(r.ReadBytes(length)).TrimEnd('\0');
                nav.Places.Add(name);
            }

            if (nav.Version >= 12)
                nav.HasUnnamedAreas = r.ReadByte() != 0;

            // 60 is a lower bound on an area record: ids, corners, four connection counts, a hiding
            // spot count, an encounter count, a place index, two ladder counts and the occupy times,
            // before any of the optional or variable-length parts.
            int areaCount = Counted(r, r.ReadUInt32(), 60, "areas");
            nav.Areas.Capacity = areaCount;

            for (int i = 0; i < areaCount; i++)
                nav.Areas.Add(NavArea.Read(r, nav.Version));

            int ladderCount = Counted(r, r.ReadUInt32(), NavLadder.SizeOf, "ladders");
            nav.Ladders.Capacity = ladderCount;

            for (int i = 0; i < ladderCount; i++)
                nav.Ladders.Add(NavLadder.Read(r));

            // Clamped to int rather than cast: a file over 2GB would otherwise wrap to a negative
            // length. Nothing here writes one, but this reads files it did not write.
            long remaining = r.BaseStream.Length - r.BaseStream.Position;
            nav.TrailingData = remaining > 0 ? r.ReadBytes((int)Math.Min(remaining, int.MaxValue)) : [];

            return nav;
        }

        public void Write(BinaryWriter w)
        {
            w.Write(Magic);
            w.Write(Version);

            if (Version >= 10)
                w.Write(SubVersion);

            w.Write(BspSize);

            if (Version >= 14)
                w.Write((byte)(IsAnalyzed ? 1 : 0));

            w.Write((ushort)Places.Count);
            foreach (var place in Places)
            {
                var bytes = Encoding.ASCII.GetBytes(place);
                w.Write((ushort)(bytes.Length + 1)); // length includes the NUL
                w.Write(bytes);
                w.Write((byte)0);
            }

            if (Version >= 12)
                w.Write((byte)(HasUnnamedAreas ? 1 : 0));

            w.Write((uint)Areas.Count);
            foreach (var area in Areas)
                area.Write(w, Version);

            w.Write((uint)Ladders.Count);
            foreach (var ladder in Ladders)
                ladder.Write(w);

            w.Write(TrailingData);
        }
    }

    public sealed class NavArea
    {
        public const int NumDirections = 4;        // N, E, S, W
        public const int NumLadderDirections = 2;  // up, down
        public const int NumCorners = 4;
        public const int MaxNavTeams = 2;

        public uint Id { get; set; }
        public int AttributeFlags { get; set; }

        public float[] NwCorner { get; set; } = new float[3];
        public float[] SeCorner { get; set; } = new float[3];
        public float NeZ { get; set; }
        public float SwZ { get; set; }

        /// <summary>Adjacent area ids, indexed by direction.</summary>
        public List<uint>[] Connections { get; } = CreateLists(NumDirections);

        public List<HidingSpot> HidingSpots { get; } = [];
        public List<SpotEncounter> Encounters { get; } = [];

        public ushort PlaceIndex { get; set; }

        /// <summary>Connected ladder ids, indexed by ladder direction.</summary>
        public List<uint>[] Ladders { get; } = CreateLists(NumLadderDirections);

        public float[] EarliestOccupyTime { get; set; } = new float[MaxNavTeams];
        public float[] LightIntensity { get; set; } = new float[NumCorners];

        public List<VisibleArea> VisibleAreas { get; } = [];
        public uint InheritVisibilityFrom { get; set; }

        private static List<uint>[] CreateLists(int count)
        {
            var lists = new List<uint>[count];
            for (int i = 0; i < count; i++)
                lists[i] = [];
            return lists;
        }

        public static NavArea Read(BinaryReader r, uint version)
        {
            var area = new NavArea
            {
                Id = r.ReadUInt32(),
                AttributeFlags = r.ReadInt32(),
            };

            for (int i = 0; i < 3; i++) area.NwCorner[i] = r.ReadSingle();
            for (int i = 0; i < 3; i++) area.SeCorner[i] = r.ReadSingle();

            area.NeZ = r.ReadSingle();
            area.SwZ = r.ReadSingle();

            for (int d = 0; d < NumDirections; d++)
            {
                int count = NavFile.Counted(r, r.ReadUInt32(), 4, "connections");
                for (int i = 0; i < count; i++)
                    area.Connections[d].Add(r.ReadUInt32());
            }

            byte hidingSpotCount = r.ReadByte();
            NavFile.Counted(r, hidingSpotCount, HidingSpot.SizeOf, "hiding spots");

            for (int i = 0; i < hidingSpotCount; i++)
                area.HidingSpots.Add(HidingSpot.Read(r));

            int encounterCount = NavFile.Counted(r, r.ReadUInt32(), 11, "encounters");
            for (int i = 0; i < encounterCount; i++)
                area.Encounters.Add(SpotEncounter.Read(r));

            area.PlaceIndex = r.ReadUInt16();

            for (int d = 0; d < NumLadderDirections; d++)
            {
                int count = NavFile.Counted(r, r.ReadUInt32(), 4, "ladder links");
                for (int i = 0; i < count; i++)
                    area.Ladders[d].Add(r.ReadUInt32());
            }

            for (int i = 0; i < MaxNavTeams; i++)
                area.EarliestOccupyTime[i] = r.ReadSingle();

            if (version >= 11)
            {
                for (int i = 0; i < NumCorners; i++)
                    area.LightIntensity[i] = r.ReadSingle();
            }

            if (version >= 16)
            {
                // Five bytes each, and the count is checked before Capacity is set from it - this is
                // the record that runs to millions on an analysed mesh and the one where a bad count
                // asks for a multi-gigabyte allocation instead of failing as a bad file.
                int visibleCount = NavFile.Counted(r, r.ReadUInt32(), 5, "visible areas");

                // These five-byte records are the overwhelming bulk of an analysed mesh - 1.37 million
                // of them on gm_construct, 96% of the file - so reading them as one block and decoding
                // in place looks like the obvious win. It was tried and measured slightly *worse*:
                // BinaryReader over the MemoryStream Load already hands it is about as quick per field,
                // and blocking it up only adds a seven-megabyte allocation and copy. The win here was
                // buffering the file at all, not the shape of the loop over it.
                area.VisibleAreas.Capacity = (int)visibleCount;

                for (uint i = 0; i < visibleCount; i++)
                    area.VisibleAreas.Add(new VisibleArea { AreaId = r.ReadUInt32(), Attributes = r.ReadByte() });

                area.InheritVisibilityFrom = r.ReadUInt32();
            }

            return area;
        }

        public void Write(BinaryWriter w, uint version)
        {
            w.Write(Id);
            w.Write(AttributeFlags);

            foreach (var f in NwCorner) w.Write(f);
            foreach (var f in SeCorner) w.Write(f);

            w.Write(NeZ);
            w.Write(SwZ);

            for (int d = 0; d < NumDirections; d++)
            {
                w.Write((uint)Connections[d].Count);
                foreach (var id in Connections[d]) w.Write(id);
            }

            // the engine truncates at 255 and warns; mirror that rather than writing a bad count
            byte hidingSpotCount = (byte)Math.Min(HidingSpots.Count, 255);
            w.Write(hidingSpotCount);
            for (int i = 0; i < hidingSpotCount; i++)
                HidingSpots[i].Write(w);

            w.Write((uint)Encounters.Count);
            foreach (var e in Encounters) e.Write(w);

            w.Write(PlaceIndex);

            for (int d = 0; d < NumLadderDirections; d++)
            {
                w.Write((uint)Ladders[d].Count);
                foreach (var id in Ladders[d]) w.Write(id);
            }

            for (int i = 0; i < MaxNavTeams; i++)
                w.Write(EarliestOccupyTime[i]);

            if (version >= 11)
            {
                for (int i = 0; i < NumCorners; i++)
                    w.Write(LightIntensity[i]);
            }

            if (version >= 16)
            {
                w.Write((uint)VisibleAreas.Count);
                foreach (var v in VisibleAreas)
                {
                    w.Write(v.AreaId);
                    w.Write(v.Attributes);
                }

                w.Write(InheritVisibilityFrom);
            }
        }
    }

    public sealed class HidingSpot
    {
        [Flags]
        public enum SpotFlags : byte
        {
            InCover = 0x01,
            GoodSniperSpot = 0x02,
            IdealSniperSpot = 0x04,
            Exposed = 0x08,
        }

        /// <summary>Bytes on disk: id, position and the flag byte.</summary>
        public const int SizeOf = 4 + 12 + 1;

        public uint Id { get; set; }
        public float[] Position { get; set; } = new float[3];
        public byte Flags { get; set; }

        public static HidingSpot Read(BinaryReader r)
        {
            var spot = new HidingSpot { Id = r.ReadUInt32() };
            for (int i = 0; i < 3; i++) spot.Position[i] = r.ReadSingle();
            spot.Flags = r.ReadByte();
            return spot;
        }

        public void Write(BinaryWriter w)
        {
            w.Write(Id);
            foreach (var f in Position) w.Write(f);
            w.Write(Flags);
        }
    }

    public sealed class SpotEncounter
    {
        public uint FromAreaId { get; set; }
        public byte FromDirection { get; set; }
        public uint ToAreaId { get; set; }
        public byte ToDirection { get; set; }

        /// <summary>Ordered spots along the path, with a quantised parametric distance (255 * t).</summary>
        public List<(uint SpotId, byte T)> Spots { get; } = [];

        public static SpotEncounter Read(BinaryReader r)
        {
            var e = new SpotEncounter
            {
                FromAreaId = r.ReadUInt32(),
                FromDirection = r.ReadByte(),
                ToAreaId = r.ReadUInt32(),
                ToDirection = r.ReadByte(),
            };

            byte spotCount = r.ReadByte();
            NavFile.Counted(r, spotCount, 5, "encounter spots");

            for (int i = 0; i < spotCount; i++)
                e.Spots.Add((r.ReadUInt32(), r.ReadByte()));

            return e;
        }

        public void Write(BinaryWriter w)
        {
            w.Write(FromAreaId);
            w.Write(FromDirection);
            w.Write(ToAreaId);
            w.Write(ToDirection);

            byte spotCount = (byte)Math.Min(Spots.Count, 255);
            w.Write(spotCount);
            for (int i = 0; i < spotCount; i++)
            {
                w.Write(Spots[i].SpotId);
                w.Write(Spots[i].T);
            }
        }
    }

    public struct VisibleArea
    {
        public uint AreaId;
        public byte Attributes;
    }

    public sealed class NavLadder
    {
        /// <summary>Bytes on disk: id, width, two points, length, direction and five area ids.</summary>
        public const int SizeOf = 4 + 4 + 12 + 12 + 4 + 4 + (5 * 4);

        public uint Id { get; set; }
        public float Width { get; set; }
        public float[] Top { get; set; } = new float[3];
        public float[] Bottom { get; set; } = new float[3];
        public float Length { get; set; }
        public uint Direction { get; set; }

        // 0 means "no connection"
        public uint TopForwardAreaId { get; set; }
        public uint TopLeftAreaId { get; set; }
        public uint TopRightAreaId { get; set; }
        public uint TopBehindAreaId { get; set; }
        public uint BottomAreaId { get; set; }

        public static NavLadder Read(BinaryReader r)
        {
            var l = new NavLadder
            {
                Id = r.ReadUInt32(),
                Width = r.ReadSingle(),
            };

            for (int i = 0; i < 3; i++) l.Top[i] = r.ReadSingle();
            for (int i = 0; i < 3; i++) l.Bottom[i] = r.ReadSingle();

            l.Length = r.ReadSingle();
            l.Direction = r.ReadUInt32();
            l.TopForwardAreaId = r.ReadUInt32();
            l.TopLeftAreaId = r.ReadUInt32();
            l.TopRightAreaId = r.ReadUInt32();
            l.TopBehindAreaId = r.ReadUInt32();
            l.BottomAreaId = r.ReadUInt32();

            return l;
        }

        public void Write(BinaryWriter w)
        {
            w.Write(Id);
            w.Write(Width);
            foreach (var f in Top) w.Write(f);
            foreach (var f in Bottom) w.Write(f);
            w.Write(Length);
            w.Write(Direction);
            w.Write(TopForwardAreaId);
            w.Write(TopLeftAreaId);
            w.Write(TopRightAreaId);
            w.Write(TopBehindAreaId);
            w.Write(BottomAreaId);
        }
    }
}
