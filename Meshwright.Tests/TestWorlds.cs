using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Meshwright;

namespace Meshwright.Tests
{
    /// <summary>
    /// Small worlds and maps to run the generator against without a game install.
    ///
    /// The flood, the lift finder and the ladder finder all start from a <see cref="BspFile"/> - the
    /// world's bounds, its entities, its brushes - and the only way to get one is to load a file. So
    /// this writes the few lumps those passes read and nothing else, and pairs it with a traced world
    /// built in memory.
    /// </summary>
    internal static class TestWorlds
    {
        private const int Solid = 0x1;

        /// <summary>
        /// Flat solid ground with its top face at <paramref name="top"/>, stretching well past any
        /// bounds the tests use, and open air above it.
        /// </summary>
        public static BspVisibility Ground(float top = 0f)
        {
            var planes = new[]
            {
                Plane(0, 0, 1, top),      // 0: the ground's top face, and the split above/below it
                Plane(0, 0, -1, 500),     // 1: underside, z >= -500
                Plane(-1, 0, 0, 5000),    // 2: x >= -5000
                Plane(1, 0, 0, 5000),     // 3: x <= 5000
                Plane(0, -1, 0, 5000),    // 4: y >= -5000
                Plane(0, 1, 0, 5000),     // 5: y <= 5000
            };

            // Leaf n is referenced as -(n) - 1.
            var nodes = new[] { new BspVisibility.Node { PlaneNum = 0, Child0 = -1, Child1 = -2 } };

            var leafs = new[]
            {
                new BspVisibility.Leaf { Contents = 0 },     // 0: open air
                new BspVisibility.Leaf { Contents = Solid }, // 1: the ground
            };

            var brushes = new[] { new BspFile.Brush { FirstSide = 0, NumSides = 6, Contents = Solid } };

            var sides = new[] { Side(0), Side(1), Side(2), Side(3), Side(4), Side(5) };

            return BspVisibility.FromGeometry(planes, nodes, leafs, brushes, sides, [null, [0]]);
        }

        public static BspFile.Plane Plane(float x, float y, float z, float distance)
            => new() { Normal = new BspFile.Vector3(x, y, z), Distance = distance };

        private static BspFile.BrushSide Side(int plane) => new() { PlaneNum = (ushort)plane };

        /// <summary>An axis-aligned box brush, for <see cref="WriteBsp"/>.</summary>
        public readonly record struct Box(BspFile.Vector3 Mins, BspFile.Vector3 Maxs, int Contents);

        /// <summary>
        /// Writes a BSP holding an entity lump, brush model bounds (model 0 being the world) and box
        /// brushes, and loads it back. The file is deleted again before this returns.
        /// </summary>
        public static BspFile Map(string entities, (BspFile.Vector3 Mins, BspFile.Vector3 Maxs)[] models,
            params Box[] boxes)
        {
            string path = Path.Combine(Path.GetTempPath(), $"mw_test_{Guid.NewGuid():N}.bsp");

            try
            {
                WriteBsp(path, entities, models, boxes);
                return BspFile.Load(path);
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static void WriteBsp(string path, string entities,
            (BspFile.Vector3 Mins, BspFile.Vector3 Maxs)[] models, Box[] boxes)
        {
            const int LumpEntities = 0, LumpPlanes = 1, LumpModels = 14, LumpBrushes = 18, LumpBrushSides = 19;

            var lumps = new Dictionary<int, byte[]>
            {
                [LumpEntities] = Encoding.ASCII.GetBytes(entities + "\0"),
                [LumpModels] = Bytes(w =>
                {
                    foreach (var (mins, maxs) in models)
                    {
                        Write(w, mins);
                        Write(w, maxs);
                        Write(w, default);  // origin
                        w.Write(0);         // head node
                        w.Write(0);         // first face
                        w.Write(0);         // face count
                    }
                }),
            };

            var planes = new List<BspFile.Plane>();
            lumps[LumpBrushes] = Bytes(w =>
            {
                foreach (var box in boxes)
                {
                    w.Write(planes.Count);  // first side: one side per plane, in step
                    w.Write(6);
                    w.Write(box.Contents);

                    planes.Add(Plane(1, 0, 0, box.Maxs.X));
                    planes.Add(Plane(-1, 0, 0, -box.Mins.X));
                    planes.Add(Plane(0, 1, 0, box.Maxs.Y));
                    planes.Add(Plane(0, -1, 0, -box.Mins.Y));
                    planes.Add(Plane(0, 0, 1, box.Maxs.Z));
                    planes.Add(Plane(0, 0, -1, -box.Mins.Z));
                }
            });

            lumps[LumpPlanes] = Bytes(w =>
            {
                foreach (var plane in planes)
                {
                    Write(w, plane.Normal);
                    w.Write(plane.Distance);
                    w.Write(0);
                }
            });

            lumps[LumpBrushSides] = Bytes(w =>
            {
                for (int i = 0; i < planes.Count; i++)
                {
                    w.Write((ushort)i);     // plane
                    w.Write((short)-1);     // texinfo
                    w.Write((short)-1);     // dispinfo
                    w.Write((short)0);      // bevel
                }
            });

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            bw.Write(0x50534256);           // 'VBSP'
            bw.Write(20);                   // version

            long directory = ms.Position;
            for (int i = 0; i < BspFile.HeaderLumps * 4; i++) bw.Write(0);
            bw.Write(0);                    // map revision

            foreach (var (index, data) in lumps)
            {
                long at = ms.Position;
                bw.Write(data);

                long end = ms.Position;
                ms.Seek(directory + index * 16, SeekOrigin.Begin);
                bw.Write((int)at);
                bw.Write(data.Length);
                ms.Seek(end, SeekOrigin.Begin);
            }

            File.WriteAllBytes(path, ms.ToArray());
        }

        private static byte[] Bytes(Action<BinaryWriter> write)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true)) write(w);
            return ms.ToArray();
        }

        private static void Write(BinaryWriter w, BspFile.Vector3 v)
        {
            w.Write(v.X);
            w.Write(v.Y);
            w.Write(v.Z);
        }

        /// <summary>A flat area on the ground between two corners, at one height.</summary>
        public static NavArea Area(uint id, float minX, float minY, float maxX, float maxY, float z)
        {
            var area = new NavArea { Id = id };

            area.NwCorner[0] = minX;
            area.NwCorner[1] = minY;
            area.NwCorner[2] = z;
            area.SeCorner[0] = maxX;
            area.SeCorner[1] = maxY;
            area.SeCorner[2] = z;
            area.NeZ = z;
            area.SwZ = z;

            return area;
        }
    }
}
