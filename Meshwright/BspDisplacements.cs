using System;
using System.Collections.Generic;
using System.IO;

namespace Meshwright
{
    /// <summary>
    /// Displacement surfaces, triangulated and indexed for ray tests.
    ///
    /// Displacements are the last geometry class a BSP tracer has to handle separately. The brush face a
    /// displacement was built from stays in the tree, but the surface itself is displaced off that face
    /// by a per-vertex offset, so terrain that rises above its base brush is invisible to a trace that
    /// only knows about brushes. Measured against the engine on gm_construct, once world brushes, detail
    /// brushes and brush entities were all handled, displacements were the *only* remaining cause of
    /// disagreement - 25 of 250 sampled rays.
    /// </summary>
    public sealed class BspDisplacements
    {
        private const int LumpVertexes = 3;
        private const int LumpFaces = 7;
        private const int LumpEdges = 12;
        private const int LumpSurfEdges = 13;
        private const int LumpDispInfo = 26;
        private const int LumpDispVerts = 33;

        private const int DispInfoSize = 176;
        private const int DispVertSize = 20;
        private const int FaceSize = 56;

        private BspFile.Vector3[] vertices = [];   // triangle vertices, three per triangle
        private BvhNode[] bvh = [];
        private int[] order = [];

        /// <summary>Contents of the displacement each triangle came from, one entry per triangle.</summary>
        private int[] contents = [];

        /// <summary>
        /// Contents worth triangulating at all: the union of every mask a caller might later trace
        /// against, exactly as <c>BspVisibility.ReadLeafBrushes</c> keeps every brush either trace
        /// purpose could care about and filters at the point of use.
        ///
        /// Storing the union and filtering per query is the whole point. This used to filter by
        /// <see cref="BspVisibility.MaskBlockLos"/> once, here, and then answer every later query
        /// regardless of the mask it was asked about - so a displacement was either solid to everything
        /// or invisible to everything. The brush path has always re-checked the caller's mask at trace
        /// time; terrain silently did not, which meant the two geometry classes disagreed about what a
        /// mask means. In practice displacements carry CONTENTS_SOLID and every mask here includes it,
        /// so this corrects a real inconsistency rather than a symptom anyone has reported.
        /// </summary>
        private const int TracedContents = BspVisibility.MaskBlockLos | BspVisibility.GenerationMask;

        public int TriangleCount => vertices.Length / 3;
        public int DisplacementCount { get; private set; }

        private struct BvhNode
        {
            public BspFile.Vector3 Mins, Maxs;
            public int Right;        // right child index; left is always this node + 1
            public int First, Count; // triangle range when a leaf, Count 0 when interior
        }

        public static BspDisplacements Load(string path)
        {
            var result = new BspDisplacements();

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

            // A presence check only, not a size validation - lumps[].Length is the on-disk (possibly
            // LZMA-compressed) size, which for a compressed lump has no fixed relationship to how many
            // full DispInfoSize records the decompressed content holds. ReadDispInfos below decompresses
            // first and sizes its own array off the real byte count.
            if (lumps[LumpDispInfo].Length <= 0)
                return result;

            var worldVerts = ReadVectors(r, lumps[LumpVertexes]);
            var edges = ReadEdges(r, lumps[LumpEdges]);
            var surfEdges = ReadInts(r, lumps[LumpSurfEdges]);
            var faces = ReadFaces(r, lumps[LumpFaces]);
            var dispVerts = ReadDispVerts(r, lumps[LumpDispVerts]);
            var infos = ReadDispInfos(r, lumps[LumpDispInfo]);

            result.DisplacementCount = infos.Length;
            result.Build(infos, faces, edges, surfEdges, worldVerts, dispVerts);

            return result;
        }

        private readonly record struct DispInfo(BspFile.Vector3 StartPosition, int VertStart, int Power, int Contents, int MapFace);
        private readonly record struct DispVert(BspFile.Vector3 Vector, float Distance);
        private readonly record struct Face(int FirstEdge, int NumEdges, int DispInfo);

        private static BspFile.Vector3[] ReadVectors(BinaryReader r, (int Offset, int Length) lump)
        {
            var bytes = LzmaLump.Read(r, lump.Offset, lump.Length);
            var result = new BspFile.Vector3[bytes.Length / 12];

            using var ms = new MemoryStream(bytes);
            using var lr = new BinaryReader(ms);
            for (int i = 0; i < result.Length; i++)
                result[i] = new BspFile.Vector3(lr.ReadSingle(), lr.ReadSingle(), lr.ReadSingle());
            return result;
        }

        private static (ushort A, ushort B)[] ReadEdges(BinaryReader r, (int Offset, int Length) lump)
        {
            var bytes = LzmaLump.Read(r, lump.Offset, lump.Length);
            var result = new (ushort, ushort)[bytes.Length / 4];

            using var ms = new MemoryStream(bytes);
            using var lr = new BinaryReader(ms);
            for (int i = 0; i < result.Length; i++)
                result[i] = (lr.ReadUInt16(), lr.ReadUInt16());
            return result;
        }

        private static int[] ReadInts(BinaryReader r, (int Offset, int Length) lump)
        {
            var bytes = LzmaLump.Read(r, lump.Offset, lump.Length);
            var result = new int[bytes.Length / 4];

            using var ms = new MemoryStream(bytes);
            using var lr = new BinaryReader(ms);
            for (int i = 0; i < result.Length; i++)
                result[i] = lr.ReadInt32();
            return result;
        }

        private static Face[] ReadFaces(BinaryReader r, (int Offset, int Length) lump)
        {
            var bytes = LzmaLump.Read(r, lump.Offset, lump.Length);
            var result = new Face[bytes.Length / FaceSize];

            using var ms = new MemoryStream(bytes);
            using var lr = new BinaryReader(ms);
            for (int i = 0; i < result.Length; i++)
            {
                lr.BaseStream.Seek(i * FaceSize + 4, SeekOrigin.Begin);
                int firstEdge = lr.ReadInt32();
                short numEdges = lr.ReadInt16();
                lr.ReadInt16(); // texinfo
                short dispInfo = lr.ReadInt16();
                result[i] = new Face(firstEdge, numEdges, dispInfo);
            }
            return result;
        }

        private static DispVert[] ReadDispVerts(BinaryReader r, (int Offset, int Length) lump)
        {
            var bytes = LzmaLump.Read(r, lump.Offset, lump.Length);
            var result = new DispVert[bytes.Length / DispVertSize];

            using var ms = new MemoryStream(bytes);
            using var lr = new BinaryReader(ms);
            for (int i = 0; i < result.Length; i++)
            {
                var v = new BspFile.Vector3(lr.ReadSingle(), lr.ReadSingle(), lr.ReadSingle());
                float dist = lr.ReadSingle();
                lr.ReadSingle(); // alpha
                result[i] = new DispVert(v, dist);
            }
            return result;
        }

        private static DispInfo[] ReadDispInfos(BinaryReader r, (int Offset, int Length) lump)
        {
            var bytes = LzmaLump.Read(r, lump.Offset, lump.Length);
            var result = new DispInfo[bytes.Length / DispInfoSize];

            using var ms = new MemoryStream(bytes);
            using var lr = new BinaryReader(ms);
            for (int i = 0; i < result.Length; i++)
            {
                lr.BaseStream.Seek(i * DispInfoSize, SeekOrigin.Begin);

                var start = new BspFile.Vector3(lr.ReadSingle(), lr.ReadSingle(), lr.ReadSingle());
                int vertStart = lr.ReadInt32();
                lr.ReadInt32();              // triangle tag start
                int power = lr.ReadInt32();
                lr.ReadInt32();              // minimum tesselation
                lr.ReadSingle();             // smoothing angle
                int contents = lr.ReadInt32();
                int mapFace = lr.ReadUInt16();

                result[i] = new DispInfo(start, vertStart, power, contents, mapFace);
            }
            return result;
        }

        private void Build(DispInfo[] infos, Face[] faces, (ushort A, ushort B)[] edges,
            int[] surfEdges, BspFile.Vector3[] worldVerts, DispVert[] dispVerts)
        {
            var tris = new List<BspFile.Vector3>();
            var triContents = new List<int>();

            foreach (var info in infos)
            {
                if ((info.Contents & TracedContents) == 0)
                    continue;

                if ((uint)info.MapFace >= (uint)faces.Length)
                    continue;

                var corners = FaceCorners(faces[info.MapFace], edges, surfEdges, worldVerts);
                if (corners is null)
                    continue;

                Rotate(corners, info.StartPosition);

                int size = 1 << info.Power;      // cells per side
                int stride = size + 1;           // vertices per side
                var grid = new BspFile.Vector3[stride * stride];

                for (int i = 0; i < stride; i++)
                {
                    float tx = i / (float)size;
                    var left = Lerp(corners[0], corners[1], tx);
                    var right = Lerp(corners[3], corners[2], tx);

                    for (int j = 0; j < stride; j++)
                    {
                        float ty = j / (float)size;
                        var p = Lerp(left, right, ty);

                        int index = info.VertStart + i * stride + j;
                        if ((uint)index < (uint)dispVerts.Length)
                        {
                            var dv = dispVerts[index];
                            p = new BspFile.Vector3(
                                p.X + dv.Vector.X * dv.Distance,
                                p.Y + dv.Vector.Y * dv.Distance,
                                p.Z + dv.Vector.Z * dv.Distance);
                        }

                        grid[i * stride + j] = p;
                    }
                }

                for (int i = 0; i < size; i++)
                {
                    for (int j = 0; j < size; j++)
                    {
                        var a = grid[i * stride + j];
                        var b = grid[i * stride + j + 1];
                        var c = grid[(i + 1) * stride + j + 1];
                        var d = grid[(i + 1) * stride + j];

                        tris.Add(a); tris.Add(b); tris.Add(c);
                        tris.Add(a); tris.Add(c); tris.Add(d);

                        // One entry per triangle, not per vertex - the traces index by triangle.
                        triContents.Add(info.Contents);
                        triContents.Add(info.Contents);
                    }
                }
            }

            vertices = tris.ToArray();
            contents = triContents.ToArray();
            BuildBvh();
        }

        /// <summary>
        /// The four corners of a displacement's base face, in winding order. Displacements are always
        /// built on quads, so anything else is not one and is skipped.
        /// </summary>
        private static BspFile.Vector3[]? FaceCorners(Face face, (ushort A, ushort B)[] edges,
            int[] surfEdges, BspFile.Vector3[] worldVerts)
        {
            if (face.NumEdges != 4)
                return null;

            var corners = new BspFile.Vector3[4];

            for (int i = 0; i < 4; i++)
            {
                int se = face.FirstEdge + i;
                if ((uint)se >= (uint)surfEdges.Length)
                    return null;

                int value = surfEdges[se];
                int edge = Math.Abs(value);
                if ((uint)edge >= (uint)edges.Length)
                    return null;

                // a negative surfedge means the edge is walked backwards
                int vertex = value >= 0 ? edges[edge].A : edges[edge].B;
                if ((uint)vertex >= (uint)worldVerts.Length)
                    return null;

                corners[i] = worldVerts[vertex];
            }

            return corners;
        }

        /// <summary>
        /// Rotates the corners so the one nearest the displacement's recorded start position comes
        /// first. vbsp stores the grid relative to that corner, and getting it wrong mirrors or rotates
        /// the whole surface rather than failing outright.
        /// </summary>
        private static void Rotate(BspFile.Vector3[] corners, BspFile.Vector3 start)
        {
            int nearest = 0;
            float best = float.MaxValue;

            for (int i = 0; i < 4; i++)
            {
                float dx = corners[i].X - start.X;
                float dy = corners[i].Y - start.Y;
                float dz = corners[i].Z - start.Z;
                float d = dx * dx + dy * dy + dz * dz;

                if (d >= best) continue;
                best = d;
                nearest = i;
            }

            if (nearest == 0) return;

            var copy = (BspFile.Vector3[])corners.Clone();
            for (int i = 0; i < 4; i++)
                corners[i] = copy[(i + nearest) % 4];
        }

        private static BspFile.Vector3 Lerp(BspFile.Vector3 a, BspFile.Vector3 b, float t) =>
            new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);

        private void BuildBvh()
        {
            int count = TriangleCount;
            if (count == 0) return;

            order = new int[count];
            for (int i = 0; i < count; i++) order[i] = i;

            var nodes = new List<BvhNode>(count);
            Build(nodes, 0, count);
            bvh = nodes.ToArray();
        }

        private int Build(List<BvhNode> nodes, int first, int count)
        {
            const int LeafSize = 8;

            var node = new BvhNode
            {
                Mins = new BspFile.Vector3(float.MaxValue, float.MaxValue, float.MaxValue),
                Maxs = new BspFile.Vector3(float.MinValue, float.MinValue, float.MinValue),
                First = first,
                Count = count,
            };

            for (int i = first; i < first + count; i++)
            {
                for (int k = 0; k < 3; k++)
                {
                    var v = vertices[order[i] * 3 + k];
                    node.Mins = new BspFile.Vector3(MathF.Min(node.Mins.X, v.X), MathF.Min(node.Mins.Y, v.Y), MathF.Min(node.Mins.Z, v.Z));
                    node.Maxs = new BspFile.Vector3(MathF.Max(node.Maxs.X, v.X), MathF.Max(node.Maxs.Y, v.Y), MathF.Max(node.Maxs.Z, v.Z));
                }
            }

            int self = nodes.Count;
            nodes.Add(node);

            if (count <= LeafSize)
                return self;

            float dx = node.Maxs.X - node.Mins.X;
            float dy = node.Maxs.Y - node.Mins.Y;
            float dz = node.Maxs.Z - node.Mins.Z;
            int axis = dx >= dy && dx >= dz ? 0 : dy >= dz ? 1 : 2;

            Array.Sort(order, first, count, Comparer<int>.Create((a, b) =>
                Centre(a, axis).CompareTo(Centre(b, axis))));

            int half = count / 2;
            Build(nodes, first, half);
            int right = Build(nodes, first + half, count - half);

            node = nodes[self];
            node.Right = right;
            node.Count = 0;
            nodes[self] = node;

            return self;
        }

        private float Centre(int triangle, int axis)
        {
            float sum = 0;
            for (int k = 0; k < 3; k++)
            {
                var v = vertices[triangle * 3 + k];
                sum += axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
            }
            return sum / 3f;
        }

        /// <summary>
        /// Whether the segment crosses any displacement surface matching <paramref name="mask"/>.
        /// </summary>
        public bool Blocks(BspFile.Vector3 a, BspFile.Vector3 b, int mask)
        {
            if (bvh.Length == 0)
                return false;

            Span<int> stack = stackalloc int[64];
            int top = 0;
            stack[top++] = 0;

            while (top > 0)
            {
                int index = stack[--top];
                var node = bvh[index];

                if (!SegmentHitsBox(a, b, node.Mins, node.Maxs))
                    continue;

                if (node.Count == 0)
                {
                    if (top + 2 <= stack.Length)
                    {
                        stack[top++] = index + 1;
                        stack[top++] = node.Right;
                    }
                    continue;
                }

                for (int i = node.First; i < node.First + node.Count; i++)
                {
                    if ((contents[order[i]] & mask) == 0)
                        continue;

                    int t = order[i] * 3;
                    if (SegmentHitsTriangle(a, b, vertices[t], vertices[t + 1], vertices[t + 2]))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The nearest displacement surface the segment crosses, with the triangle's normal.
        ///
        /// A displacement has no single plane to read a normal off - it is a triangulated grid - so the
        /// normal is the face normal of whichever triangle was hit first. That is the true surface
        /// orientation, which is what the walkability and stair tests need.
        /// </summary>
        public bool TryTraceSurface(BspFile.Vector3 a, BspFile.Vector3 b, int mask,
            out float fraction, out BspFile.Vector3 normal)
        {
            fraction = 1f;
            normal = default;

            if (bvh.Length == 0)
                return false;

            bool found = false;

            Span<int> stack = stackalloc int[64];
            int top = 0;
            stack[top++] = 0;

            while (top > 0)
            {
                int index = stack[--top];
                var node = bvh[index];

                if (!SegmentHitsBox(a, b, node.Mins, node.Maxs))
                    continue;

                if (node.Count == 0)
                {
                    if (top + 2 <= stack.Length)
                    {
                        stack[top++] = index + 1;
                        stack[top++] = node.Right;
                    }
                    continue;
                }

                for (int i = node.First; i < node.First + node.Count; i++)
                {
                    if ((contents[order[i]] & mask) == 0)
                        continue;

                    int t = order[i] * 3;
                    if (!TryHitTriangle(a, b, vertices[t], vertices[t + 1], vertices[t + 2], out float hit))
                        continue;

                    if (found && hit >= fraction)
                        continue;

                    fraction = hit;
                    normal = TriangleNormal(vertices[t], vertices[t + 1], vertices[t + 2]);
                    found = true;
                }
            }

            return found;
        }

        private static BspFile.Vector3 TriangleNormal(BspFile.Vector3 v0, BspFile.Vector3 v1, BspFile.Vector3 v2)
        {
            float e1x = v1.X - v0.X, e1y = v1.Y - v0.Y, e1z = v1.Z - v0.Z;
            float e2x = v2.X - v0.X, e2y = v2.Y - v0.Y, e2z = v2.Z - v0.Z;

            float nx = e1y * e2z - e1z * e2y;
            float ny = e1z * e2x - e1x * e2z;
            float nz = e1x * e2y - e1y * e2x;

            float length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (length < 1e-6f)
                return new BspFile.Vector3(0, 0, 1);

            // Face upward: a ground normal is wanted, and triangle winding is not relied on here.
            float sign = nz < 0 ? -1f : 1f;
            return new BspFile.Vector3(sign * nx / length, sign * ny / length, sign * nz / length);
        }

        /// <summary>
        /// Sweeps an axis-aligned box along a segment against the displacement surface.
        ///
        /// This is the half of collision that was missing. Everything that asks whether a *body* fits
        /// somewhere - as opposed to whether a sight line is clear - wants a swept box, and the box
        /// sweep in <see cref="BspVisibility"/> only ever consulted brushes. On terrain it therefore
        /// reported open air, which is why every clearance test in the generator had to fall back to an
        /// infinitely thin line and accept what that misses.
        ///
        /// The box does not have to be centred on the traced point - Valve's <c>NavTraceMins/Maxs</c>
        /// sits *on* it, 0.9 units square and 55 tall - so the sweep is re-expressed as a centred box by
        /// moving the ray to the box's centre, and the half-extents are what the triangles get inflated
        /// by.
        /// </summary>
        public bool TryTraceHull(BspFile.Vector3 a, BspFile.Vector3 b,
            BspFile.Vector3 mins, BspFile.Vector3 maxs, int mask,
            out float fraction, out BspFile.Vector3 normal, out bool startSolid)
        {
            fraction = 1f;
            normal = new BspFile.Vector3(0, 0, 1);
            startSolid = false;

            if (bvh.Length == 0)
                return false;

            // Centre the box and carry the offset on the ray instead.
            var centre = new BspFile.Vector3((mins.X + maxs.X) / 2f, (mins.Y + maxs.Y) / 2f,
                (mins.Z + maxs.Z) / 2f);
            var extent = new BspFile.Vector3((maxs.X - mins.X) / 2f, (maxs.Y - mins.Y) / 2f,
                (maxs.Z - mins.Z) / 2f);

            var from = new BspFile.Vector3(a.X + centre.X, a.Y + centre.Y, a.Z + centre.Z);
            var to = new BspFile.Vector3(b.X + centre.X, b.Y + centre.Y, b.Z + centre.Z);

            bool found = false;

            Span<int> stack = stackalloc int[64];
            int top = 0;
            stack[top++] = 0;

            while (top > 0)
            {
                int index = stack[--top];
                var node = bvh[index];

                // The node's box grown by the moving box's half-extents: a swept box clips a node the
                // traced centre line can miss entirely.
                var grown = (new BspFile.Vector3(node.Mins.X - extent.X, node.Mins.Y - extent.Y, node.Mins.Z - extent.Z),
                             new BspFile.Vector3(node.Maxs.X + extent.X, node.Maxs.Y + extent.Y, node.Maxs.Z + extent.Z));

                if (!SegmentHitsBox(from, to, grown.Item1, grown.Item2))
                    continue;

                if (node.Count == 0)
                {
                    if (top + 2 <= stack.Length)
                    {
                        stack[top++] = index + 1;
                        stack[top++] = node.Right;
                    }
                    continue;
                }

                for (int i = node.First; i < node.First + node.Count; i++)
                {
                    if ((contents[order[i]] & mask) == 0)
                        continue;

                    int t = order[i] * 3;

                    if (!SweepBoxAgainstTriangle(from, to, extent,
                            vertices[t], vertices[t + 1], vertices[t + 2],
                            out float hit, out var hitNormal, out bool solid))
                    {
                        continue;
                    }

                    if (solid)
                        startSolid = true;

                    if (found && hit >= fraction)
                        continue;

                    fraction = hit;
                    normal = hitNormal;
                    found = true;
                }
            }

            return found;
        }

        /// <summary>
        /// Sweeps a centred box along a segment against one triangle, by the separating axis theorem.
        ///
        /// The set of positions where a box overlaps a triangle is their Minkowski sum, which is convex,
        /// so a moving point against it is an ordinary ray-versus-convex-volume clip: for each candidate
        /// axis, project the triangle onto it, widen that interval by how far the box reaches along the
        /// same axis, and intersect the resulting slabs. Whichever slab the ray enters last is the face
        /// it hits, and that slab's axis is the surface normal.
        ///
        /// The axes are the full SAT set rather than just the triangle's plane - the plane alone, which
        /// is the tempting shortcut, treats the triangle as infinite and reports hits out past its
        /// edges. Face normals of the box catch the box's own sides, and the nine edge-cross-edge axes
        /// catch the case where an edge of the box slides past an edge of the triangle without either
        /// face being the separating one.
        /// </summary>
        private static bool SweepBoxAgainstTriangle(BspFile.Vector3 from, BspFile.Vector3 to,
            BspFile.Vector3 extent, BspFile.Vector3 v0, BspFile.Vector3 v1, BspFile.Vector3 v2,
            out float fraction, out BspFile.Vector3 normal, out bool startSolid)
        {
            fraction = 1f;
            normal = new BspFile.Vector3(0, 0, 1);
            startSolid = false;

            var delta = new BspFile.Vector3(to.X - from.X, to.Y - from.Y, to.Z - from.Z);

            Span<BspFile.Vector3> axes = stackalloc BspFile.Vector3[13];
            int count = 0;

            axes[count++] = TriangleNormal(v0, v1, v2);
            axes[count++] = new BspFile.Vector3(1, 0, 0);
            axes[count++] = new BspFile.Vector3(0, 1, 0);
            axes[count++] = new BspFile.Vector3(0, 0, 1);

            Span<BspFile.Vector3> edges =
            [
                new(v1.X - v0.X, v1.Y - v0.Y, v1.Z - v0.Z),
                new(v2.X - v1.X, v2.Y - v1.Y, v2.Z - v1.Z),
                new(v0.X - v2.X, v0.Y - v2.Y, v0.Z - v2.Z),
            ];

            for (int e = 0; e < 3; e++)
            {
                for (int axis = 0; axis < 3; axis++)
                {
                    var unit = axis == 0 ? new BspFile.Vector3(1, 0, 0)
                             : axis == 1 ? new BspFile.Vector3(0, 1, 0)
                                         : new BspFile.Vector3(0, 0, 1);

                    axes[count++] = new BspFile.Vector3(
                        edges[e].Y * unit.Z - edges[e].Z * unit.Y,
                        edges[e].Z * unit.X - edges[e].X * unit.Z,
                        edges[e].X * unit.Y - edges[e].Y * unit.X);
                }
            }

            float enter = 0f, exit = 1f;
            var enterNormal = new BspFile.Vector3(0, 0, 1);
            bool haveEnter = false;

            for (int i = 0; i < count; i++)
            {
                var n = axes[i];

                float lengthSquared = n.X * n.X + n.Y * n.Y + n.Z * n.Z;
                if (lengthSquared < 1e-12f)
                    continue;   // degenerate axis: parallel edges, or a sliver triangle

                float d0 = Dot(n, v0), d1 = Dot(n, v1), d2 = Dot(n, v2);
                float low = MathF.Min(d0, MathF.Min(d1, d2));
                float high = MathF.Max(d0, MathF.Max(d1, d2));

                // How far the box reaches along this axis - its support, which is what inflating the
                // triangle by the box amounts to on this axis.
                float reach = MathF.Abs(n.X) * extent.X + MathF.Abs(n.Y) * extent.Y + MathF.Abs(n.Z) * extent.Z;
                low -= reach;
                high += reach;

                float start = Dot(n, from);
                float travel = Dot(n, delta);

                if (MathF.Abs(travel) < 1e-9f)
                {
                    // No movement along this axis: either it separates for the whole sweep or never.
                    if (start < low || start > high)
                        return false;

                    continue;
                }

                float toLow = (low - start) / travel;
                float toHigh = (high - start) / travel;

                BspFile.Vector3 face;
                if (toLow > toHigh)
                {
                    (toLow, toHigh) = (toHigh, toLow);
                    face = n;               // entering through the high side
                }
                else
                {
                    face = new BspFile.Vector3(-n.X, -n.Y, -n.Z);   // entering through the low side
                }

                if (toLow > enter)
                {
                    enter = toLow;
                    enterNormal = face;
                    haveEnter = true;
                }

                if (toHigh < exit)
                    exit = toHigh;

                if (enter > exit)
                    return false;
            }

            if (enter > exit || enter >= 1f)
                return false;

            // Overlapping before it has moved anywhere is the caller's "start solid", not a hit at
            // fraction zero: there is no surface in front to stop against.
            if (!haveEnter || enter <= 0f)
            {
                startSolid = true;
                fraction = 0f;
                normal = new BspFile.Vector3(0, 0, 1);
                return true;
            }

            fraction = enter;

            float length = MathF.Sqrt(enterNormal.X * enterNormal.X + enterNormal.Y * enterNormal.Y +
                                      enterNormal.Z * enterNormal.Z);
            normal = length < 1e-9f
                ? new BspFile.Vector3(0, 0, 1)
                : new BspFile.Vector3(enterNormal.X / length, enterNormal.Y / length, enterNormal.Z / length);

            return true;
        }

        private static float Dot(BspFile.Vector3 a, BspFile.Vector3 b)
            => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        private static bool SegmentHitsTriangle(BspFile.Vector3 a, BspFile.Vector3 b,
            BspFile.Vector3 v0, BspFile.Vector3 v1, BspFile.Vector3 v2)
            => TryHitTriangle(a, b, v0, v1, v2, out _);

        /// <summary>Moller-Trumbore, bounded to the segment rather than an infinite ray.</summary>
        private static bool TryHitTriangle(BspFile.Vector3 a, BspFile.Vector3 b,
            BspFile.Vector3 v0, BspFile.Vector3 v1, BspFile.Vector3 v2, out float fraction)
        {
            fraction = 1f;
            const float Epsilon = 1e-6f;

            float dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;

            float e1x = v1.X - v0.X, e1y = v1.Y - v0.Y, e1z = v1.Z - v0.Z;
            float e2x = v2.X - v0.X, e2y = v2.Y - v0.Y, e2z = v2.Z - v0.Z;

            float px = dy * e2z - dz * e2y;
            float py = dz * e2x - dx * e2z;
            float pz = dx * e2y - dy * e2x;

            float det = e1x * px + e1y * py + e1z * pz;
            if (MathF.Abs(det) < Epsilon)
                return false; // parallel; a grazing hit is not worth chasing

            float inverse = 1f / det;

            float tx = a.X - v0.X, ty = a.Y - v0.Y, tz = a.Z - v0.Z;
            float u = (tx * px + ty * py + tz * pz) * inverse;
            if (u < 0f || u > 1f) return false;

            float qx = ty * e1z - tz * e1y;
            float qy = tz * e1x - tx * e1z;
            float qz = tx * e1y - ty * e1x;

            float v = (dx * qx + dy * qy + dz * qz) * inverse;
            if (v < 0f || u + v > 1f) return false;

            float t = (e2x * qx + e2y * qy + e2z * qz) * inverse;
            if (t is <= Epsilon or >= 1f)
                return false;

            fraction = t;
            return true;
        }

        private static bool SegmentHitsBox(BspFile.Vector3 a, BspFile.Vector3 b,
            BspFile.Vector3 mins, BspFile.Vector3 maxs)
        {
            float tMin = 0f, tMax = 1f;

            return Slab(a.X, b.X - a.X, mins.X, maxs.X, ref tMin, ref tMax)
                && Slab(a.Y, b.Y - a.Y, mins.Y, maxs.Y, ref tMin, ref tMax)
                && Slab(a.Z, b.Z - a.Z, mins.Z, maxs.Z, ref tMin, ref tMax);
        }

        private static bool Slab(float origin, float delta, float min, float max, ref float tMin, ref float tMax)
        {
            if (MathF.Abs(delta) < 1e-6f)
                return origin >= min && origin <= max;

            float inverse = 1f / delta;
            float t0 = (min - origin) * inverse;
            float t1 = (max - origin) * inverse;

            if (t0 > t1) (t0, t1) = (t1, t0);

            tMin = MathF.Max(tMin, t0);
            tMax = MathF.Min(tMax, t1);

            return tMin <= tMax;
        }
    }
}
