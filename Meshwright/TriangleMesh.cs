using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Meshwright
{
    /// <summary>
    /// A soup of triangles with a bounding-volume hierarchy over it, answering the three collision
    /// questions the generator asks: does a line cross anything, what does it hit first, and does a
    /// swept box fit.
    ///
    /// This exists because two very different things end up as the same problem. Displacement terrain is
    /// a grid deformed off a brush face; a static prop is a convex hull from a .phy placed by the map.
    /// Neither is in the BSP tree, both are triangles once reconstructed, and both need exactly these
    /// three queries - so they share one tracer rather than two that drift apart. The alternative was
    /// live for a while: displacements filtered contents once at build time while brushes re-checked the
    /// caller''s mask at trace time, and the two geometry classes quietly disagreed about what a mask
    /// meant.
    ///
    /// Contents are stored per triangle and filtered per query for the same reason. A caller tracing
    /// against MASK_BLOCKLOS and one tracing against the ground mask are asking different questions of
    /// the same geometry, and baking either answer in makes the other one wrong.
    /// </summary>
    public sealed class TriangleMesh
    {
        private BspFile.Vector3[] vertices = [];   // triangle vertices, three per triangle
        private int[] contents = [];               // one entry per triangle
        private BvhNode[] bvh = [];
        private int[] order = [];

        /// <summary>
        /// The same triangles again, as one array per component, with the two edges precomputed.
        ///
        /// This is the layout the nearest-hit trace wants and the interleaved one cannot give it. That
        /// trace has to test every triangle in every leaf it reaches - it is looking for the closest
        /// hit, so there is nothing to exit early for - which makes it the one query in this class that
        /// is pure arithmetic over a known count, and the one worth doing eight at a time. Eight lanes
        /// need eight triangles' worth of each component side by side; reading them out of
        /// <see cref="vertices"/> would be a gather per lane and would cost more than the vector math
        /// saves.
        ///
        /// The edges are stored rather than recomputed because Moller-Trumbore uses them and nothing
        /// else does, so subtracting them once at build time removes six subtractions per triangle per
        /// ray. The original vertices stay: the winning triangle's normal is read off them, which
        /// happens once per trace rather than once per triangle, and reconstructing a vertex as
        /// <c>v0 + e1</c> is not bit-identical to the vertex that was read from the file.
        ///
        /// Costs one more copy of the geometry - about 6MB on a map with 165,000 triangles.
        /// </summary>
        private float[] v0x = [], v0y = [], v0z = [];
        private float[] e1x = [], e1y = [], e1z = [];
        private float[] e2x = [], e2y = [], e2z = [];

        public int TriangleCount => vertices.Length / 3;

        private struct BvhNode
        {
            public BspFile.Vector3 Mins, Maxs;
            public int Right;        // right child index; left is always this node + 1
            public int First, Count; // triangle range when a leaf, Count 0 when interior
        }

        /// <summary>
        /// Takes ownership of the triangles and indexes them. Both arrays are kept as given rather than
        /// copied - callers build them once and hand them over.
        /// </summary>
        public void Build(BspFile.Vector3[] triangleVertices, int[] triangleContents)
        {
            vertices = triangleVertices;
            contents = triangleContents;
            BuildBvh();
        }
        /// <summary>
        /// Triangle centroids, one array per axis, and a scratch buffer of the keys currently being
        /// sorted. Built once and reused down the whole recursion.
        ///
        /// These exist for speed, and building the tree turned out to be where a map's whole geometry
        /// load went. Static props on rp_downtown_meowy looked like a 710ms cost; measuring the phases
        /// put 51ms on finding and parsing all 169 models, 11ms on placing them, and **743ms on
        /// indexing** the 48,795 triangles that came out. Nobody would have guessed that from reading
        /// the code, which is why <see cref="StaticProps.IndexMs"/> and its siblings now report it.
        ///
        /// Two things were wrong. Splitting a node sorted with a comparison delegate that recomputed a
        /// centroid from three vertices on every comparison - about five million times - so the keys are
        /// now computed once, here, and sorted as plain floats with no indirection. And it sorted at all,
        /// where a median partition is enough; see the note in <see cref="Build"/>. Together: 743ms to
        /// 218ms, and displacement terrain shares the same path and was paying it too.
        /// </summary>
        private float[] centreX = [], centreY = [], centreZ = [], keys = [];

        /// <summary>
        /// Each triangle's own bounding box, precomputed.
        ///
        /// A node's box is the union of these over its range, and every node on the path from the root
        /// recomputes it, so the range is walked once per level - about 615,000 triangle visits to index
        /// fifty thousand triangles. Doing that from the vertices meant three scattered loads and
        /// eighteen min/max operations per triangle, building two <see cref="BspFile.Vector3"/> values
        /// each time round. Reading a precomputed box is one load and six comparisons.
        ///
        /// Deliberately a struct array rather than six parallel float arrays: the six values for one
        /// triangle are always wanted together, and the index they are read at is scattered by
        /// <see cref="order"/>, so keeping them adjacent is one cache line instead of six.
        /// </summary>
        private TriangleBounds[] triangleBounds = [];

        private struct TriangleBounds
        {
            public float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        }

        private void BuildBvh()
        {
            int count = TriangleCount;
            if (count == 0) return;

            order = new int[count];
            centreX = new float[count];
            centreY = new float[count];
            centreZ = new float[count];
            keys = new float[count];

            triangleBounds = new TriangleBounds[count];

            for (int i = 0; i < count; i++)
            {
                order[i] = i;

                var a = vertices[i * 3];
                var b = vertices[i * 3 + 1];
                var c = vertices[i * 3 + 2];

                centreX[i] = (a.X + b.X + c.X) / 3f;
                centreY[i] = (a.Y + b.Y + c.Y) / 3f;
                centreZ[i] = (a.Z + b.Z + c.Z) / 3f;

                triangleBounds[i] = new TriangleBounds
                {
                    MinX = MathF.Min(a.X, MathF.Min(b.X, c.X)),
                    MinY = MathF.Min(a.Y, MathF.Min(b.Y, c.Y)),
                    MinZ = MathF.Min(a.Z, MathF.Min(b.Z, c.Z)),
                    MaxX = MathF.Max(a.X, MathF.Max(b.X, c.X)),
                    MaxY = MathF.Max(a.Y, MathF.Max(b.Y, c.Y)),
                    MaxZ = MathF.Max(a.Z, MathF.Max(b.Z, c.Z)),
                };
            }

            var nodes = new List<BvhNode>(count);
            Build(nodes, 0, count);
            bvh = nodes.ToArray();

            ApplyOrder(count);

            // Only the tree is needed to trace. The build scratch is several megabytes on a large map
            // and there is no reason to hold it for the life of the process.
            centreX = centreY = centreZ = keys = [];
            triangleBounds = [];
        }

        /// <summary>
        /// Rearranges the triangles into the order the tree visits them, so a leaf's triangles are
        /// contiguous and <see cref="order"/> stops existing.
        ///
        /// **The permutation was already being computed and then paid for on every query instead.** The
        /// build sorts an index array; the trace then walked it, and each triangle in a leaf cost a load
        /// of <c>order[i]</c> followed by two *dependent, scattered* loads - one into <c>contents</c>,
        /// one into <c>vertices</c> at <c>order[i] * 3</c>. Neighbouring triangles in a leaf are
        /// neighbours in the tree, not in memory, so each of those landed on its own cache line. Doing
        /// the shuffle once here makes the leaf loop read straight down both arrays: one cache line of
        /// vertices covers three triangles rather than a third of one.
        ///
        /// Not a change to the tree, only to where its triangles live. Node ranges already index the
        /// order array positionally, so after the permutation <c>First</c> and <c>Count</c> address the
        /// triangles directly and every trace returns exactly what it did before - which the round-trip
        /// tests and the byte-for-byte mesh comparison both check.
        /// </summary>
        private void ApplyOrder(int count)
        {
            var sortedVertices = new BspFile.Vector3[count * 3];
            var sortedContents = new int[count];

            for (int i = 0; i < count; i++)
            {
                int from = order[i] * 3;
                int to = i * 3;

                sortedVertices[to] = vertices[from];
                sortedVertices[to + 1] = vertices[from + 1];
                sortedVertices[to + 2] = vertices[from + 2];

                sortedContents[i] = contents[order[i]];
            }

            vertices = sortedVertices;
            contents = sortedContents;
            order = [];

            BuildEdgeArrays(count);
        }

        /// <summary>Splits the permuted triangles into the per-component arrays the wide trace reads.</summary>
        private void BuildEdgeArrays(int count)
        {
            v0x = new float[count]; v0y = new float[count]; v0z = new float[count];
            e1x = new float[count]; e1y = new float[count]; e1z = new float[count];
            e2x = new float[count]; e2y = new float[count]; e2z = new float[count];

            for (int i = 0; i < count; i++)
            {
                var a = vertices[i * 3];
                var b = vertices[i * 3 + 1];
                var c = vertices[i * 3 + 2];

                v0x[i] = a.X; v0y[i] = a.Y; v0z[i] = a.Z;

                e1x[i] = b.X - a.X; e1y[i] = b.Y - a.Y; e1z[i] = b.Z - a.Z;
                e2x[i] = c.X - a.X; e2y[i] = c.Y - a.Y; e2z[i] = c.Z - a.Z;
            }
        }

        private int Build(List<BvhNode> nodes, int first, int count)
        {
            const int LeafSize = 8;

            // Accumulated in locals rather than into the node. Reading and rewriting two Vector3 fields
            // per vertex is what the old loop spent its time on: the struct is not mutable in place, so
            // every one of six updates per triangle built a fresh value and stored it back.
            float mnx = float.MaxValue, mny = float.MaxValue, mnz = float.MaxValue;
            float mxx = float.MinValue, mxy = float.MinValue, mxz = float.MinValue;

            for (int i = first; i < first + count; i++)
            {
                ref var b = ref triangleBounds[order[i]];

                if (b.MinX < mnx) mnx = b.MinX;
                if (b.MinY < mny) mny = b.MinY;
                if (b.MinZ < mnz) mnz = b.MinZ;
                if (b.MaxX > mxx) mxx = b.MaxX;
                if (b.MaxY > mxy) mxy = b.MaxY;
                if (b.MaxZ > mxz) mxz = b.MaxZ;
            }

            var node = new BvhNode
            {
                Mins = new BspFile.Vector3(mnx, mny, mnz),
                Maxs = new BspFile.Vector3(mxx, mxy, mxz),
                First = first,
                Count = count,
            };

            int self = nodes.Count;
            nodes.Add(node);

            if (count <= LeafSize)
                return self;

            float dx = node.Maxs.X - node.Mins.X;
            float dy = node.Maxs.Y - node.Mins.Y;
            float dz = node.Maxs.Z - node.Mins.Z;
            int axis = dx >= dy && dx >= dz ? 0 : dy >= dz ? 1 : 2;

            var centre = axis == 0 ? centreX : axis == 1 ? centreY : centreZ;

            for (int i = first; i < first + count; i++) keys[i] = centre[order[i]];

            int half = count / 2;

            // Partitioned about the median rather than sorted. Only the split matters - which triangles
            // end up on each side - and the recursion re-partitions each half anyway, so ordering within
            // a half is work that is immediately thrown away. Selecting the median is linear where
            // sorting is n log n, and across the whole tree that turns n log squared n into n log n.
            //
            // Any partition at all yields a correct tree, because node bounds are computed from whatever
            // triangles land in the range; a bad split costs query speed, never correctness. That is
            // what makes this a safe thing to have hand-written.
            //
            // Not bit-identical to sorting, though, and it is worth saying so. Triangles with equal
            // centroids come out in a different order, so a trace that hits two coincident surfaces at
            // exactly the same distance can report the other one. On rp_downtown_meowy that moved the
            // generated mesh by one area out of 33,403; every quality measure - height error, floating
            // areas, coverage against the engine's mesh - was unchanged to the digits reported.
            SelectNth(keys, order, first, count, first + half);
            Build(nodes, first, half);
            int right = Build(nodes, first + half, count - half);

            node = nodes[self];
            node.Right = right;
            node.Count = 0;
            nodes[self] = node;

            return self;
        }

        /// <summary>
        /// Rearranges <paramref name="keys"/> and its payload so the element at <paramref name="nth"/>
        /// is the one that would be there if the range were sorted, and everything before it is no
        /// greater. Quickselect with a median-of-three pivot.
        ///
        /// The Hoare loop swaps through runs of equal keys rather than skipping them, which is what
        /// keeps a range of identical centroids - a wall of coplanar triangles, common enough in a
        /// prop - splitting down the middle instead of degenerating.
        /// </summary>
        private static void SelectNth(float[] keys, int[] items, int first, int count, int nth)
        {
            int lo = first, hi = first + count - 1;

            while (lo < hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                float a = keys[lo], b = keys[mid], c = keys[hi];
                float pivot = a < b ? (b < c ? b : a < c ? c : a) : (a < c ? a : b < c ? c : b);

                int i = lo, j = hi;

                while (i <= j)
                {
                    while (keys[i] < pivot) i++;
                    while (keys[j] > pivot) j--;

                    if (i > j) break;

                    (keys[i], keys[j]) = (keys[j], keys[i]);
                    (items[i], items[j]) = (items[j], items[i]);

                    i++; j--;
                }

                if (nth <= j) hi = j;
                else if (nth >= i) lo = i;
                else return;                 // the target already sits between the two partitions
            }
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
                    if ((contents[i] & mask) == 0)
                        continue;

                    int t = i * 3;
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

                int nearest = NearestInLeaf(a, b, mask, node.First, node.Count,
                    found ? fraction : 1f, out float hit);

                if (nearest >= 0)
                {
                    fraction = hit;
                    normal = TriangleNormal(vertices[nearest * 3], vertices[nearest * 3 + 1],
                        vertices[nearest * 3 + 2]);
                    found = true;
                }
            }

            return found;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        /// <summary>
        /// The nearest triangle in one leaf that the segment crosses closer than
        /// <paramref name="best"/>, or -1.
        ///
        /// Eight at a time where the hardware allows it. This is the only query here that suits it: the
        /// other two stop at the first blocker, so testing eight triangles when the first would have
        /// answered is work thrown away, while a nearest-hit search has to look at all of them anyway.
        /// Leaves hold eight triangles, which is one <see cref="Vector256{T}"/> exactly.
        ///
        /// The check is on <c>Vector256.IsHardwareAccelerated</c> rather than on AVX2 specifically, and
        /// it resolves to a constant when the method is compiled, so the branch costs nothing and the
        /// unused half is discarded. On hardware without it the scalar path below runs unchanged.
        /// </summary>
        private int NearestInLeaf(BspFile.Vector3 a, BspFile.Vector3 b, int mask,
            int first, int count, float best, out float hit)
        {
            return Vector256.IsHardwareAccelerated && count >= Vector256<float>.Count
                ? NearestWide(a, b, mask, first, count, best, out hit)
                : NearestScalar(a, b, mask, first, count, best, out hit);
        }

        private int NearestScalar(BspFile.Vector3 a, BspFile.Vector3 b, int mask,
            int first, int count, float best, out float hit)
        {
            int winner = -1;
            hit = best;

            for (int i = first; i < first + count; i++)
            {
                if ((contents[i] & mask) == 0)
                    continue;

                int t = i * 3;
                if (!TryHitTriangle(a, b, vertices[t], vertices[t + 1], vertices[t + 2], out float at))
                    continue;

                if (at >= hit)
                    continue;

                hit = at;
                winner = i;
            }

            return winner;
        }

        /// <summary>
        /// Moller-Trumbore over eight triangles at once.
        ///
        /// Every rejection the scalar version makes with a branch - degenerate determinant, barycentric
        /// coordinate out of range, hit behind the start or past the end - becomes a lane mask here.
        /// That is the point as much as the arithmetic width is: which triangles a leaf rejects is
        /// data-dependent and unpredictable, so the scalar loop mispredicts constantly, where this has
        /// no branches to miss.
        ///
        /// Rejected lanes are set to positive infinity so the same minimum-finding step handles them.
        /// </summary>
        private int NearestWide(BspFile.Vector3 a, BspFile.Vector3 b, int mask,
            int first, int count, float best, out float hit)
        {
            const float Epsilon = 1e-6f;
            int lanes = Vector256<float>.Count;

            var dx = Vector256.Create(b.X - a.X);
            var dy = Vector256.Create(b.Y - a.Y);
            var dz = Vector256.Create(b.Z - a.Z);

            var ax = Vector256.Create(a.X);
            var ay = Vector256.Create(a.Y);
            var az = Vector256.Create(a.Z);

            var epsilon = Vector256.Create(Epsilon);
            var zero = Vector256<float>.Zero;
            var one = Vector256<float>.One;
            var maskLanes = Vector256.Create(mask);
            var miss = Vector256.Create(float.PositiveInfinity);

            int winner = -1;
            hit = best;

            Span<float> found = stackalloc float[Vector256<float>.Count];

            int i = first;
            int end = first + count;

            for (; i + lanes <= end; i += lanes)
            {
                // Contents first: a triangle the caller is not asking about takes no further work.
                var wanted = Vector256.LoadUnsafe(ref contents[i]) & maskLanes;
                var live = ~Vector256.Equals(wanted, Vector256<int>.Zero).AsSingle();

                if (live.Equals(zero))
                    continue;

                var v1x = Vector256.LoadUnsafe(ref e1x[i]);
                var v1y = Vector256.LoadUnsafe(ref e1y[i]);
                var v1z = Vector256.LoadUnsafe(ref e1z[i]);

                var v2x = Vector256.LoadUnsafe(ref e2x[i]);
                var v2y = Vector256.LoadUnsafe(ref e2y[i]);
                var v2z = Vector256.LoadUnsafe(ref e2z[i]);

                // p = d x e2
                var px = dy * v2z - dz * v2y;
                var py = dz * v2x - dx * v2z;
                var pz = dx * v2y - dy * v2x;

                var det = v1x * px + v1y * py + v1z * pz;

                var live2 = live & ~Vector256.LessThan(Vector256.Abs(det), epsilon).AsSingle();
                if (live2.Equals(zero))
                    continue;

                // A zero determinant would divide by zero; those lanes are already dead, but the
                // division still runs, so give them something harmless to divide by.
                var safe = Vector256.ConditionalSelect(live2, det, one);
                var inverse = one / safe;

                var tx = ax - Vector256.LoadUnsafe(ref v0x[i]);
                var ty = ay - Vector256.LoadUnsafe(ref v0y[i]);
                var tz = az - Vector256.LoadUnsafe(ref v0z[i]);

                var u = (tx * px + ty * py + tz * pz) * inverse;

                live2 &= ~Vector256.LessThan(u, zero).AsSingle();
                live2 &= ~Vector256.GreaterThan(u, one).AsSingle();
                if (live2.Equals(zero))
                    continue;

                // q = t x e1
                var qx = ty * v1z - tz * v1y;
                var qy = tz * v1x - tx * v1z;
                var qz = tx * v1y - ty * v1x;

                var v = (dx * qx + dy * qy + dz * qz) * inverse;

                live2 &= ~Vector256.LessThan(v, zero).AsSingle();
                live2 &= ~Vector256.GreaterThan(u + v, one).AsSingle();
                if (live2.Equals(zero))
                    continue;

                var at = (v2x * qx + v2y * qy + v2z * qz) * inverse;

                // The same bounds the scalar version applies: strictly inside the segment.
                live2 &= Vector256.GreaterThan(at, epsilon).AsSingle();
                live2 &= Vector256.LessThan(at, one).AsSingle();

                var scored = Vector256.ConditionalSelect(live2, at, miss);

                if (Vector256.Min(scored, miss).Equals(miss))
                    continue;

                scored.CopyTo(found);

                // Lowest lane index wins a tie, which is the order the scalar loop would have taken
                // them in - so the two paths agree on a segment that grazes two coincident triangles.
                for (int lane = 0; lane < lanes; lane++)
                {
                    if (found[lane] >= hit)
                        continue;

                    hit = found[lane];
                    winner = i + lane;
                }
            }

            // Whatever is left over when a leaf is not a whole multiple of the width.
            //
            // Unreachable as things stand, and kept deliberately. A leaf holds at most LeafSize
            // triangles and that is currently eight, which is exactly one vector - so a leaf either
            // has fewer than eight and never gets here at all, or has exactly eight and leaves nothing
            // over. Changing LeafSize to anything that is not a multiple of the vector width makes this
            // live immediately, which is the situation worth being already correct for. Note that no
            // test covers it while it cannot run: deleting the tail passes the whole suite.
            if (i < end)
            {
                int tail = NearestScalar(a, b, mask, i, end - i, hit, out float tailHit);

                if (tail >= 0)
                {
                    hit = tailHit;
                    winner = tail;
                }
            }

            return winner;
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
                    if ((contents[i] & mask) == 0)
                        continue;

                    int t = i * 3;

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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Dot(BspFile.Vector3 a, BspFile.Vector3 b)
            => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool SegmentHitsTriangle(BspFile.Vector3 a, BspFile.Vector3 b,
            BspFile.Vector3 v0, BspFile.Vector3 v1, BspFile.Vector3 v2)
            => TryHitTriangle(a, b, v0, v1, v2, out _);

        /// <summary>Moller-Trumbore, bounded to the segment rather than an infinite ray.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool SegmentHitsBox(BspFile.Vector3 a, BspFile.Vector3 b,
            BspFile.Vector3 mins, BspFile.Vector3 maxs)
        {
            float tMin = 0f, tMax = 1f;

            return Slab(a.X, b.X - a.X, mins.X, maxs.X, ref tMin, ref tMax)
                && Slab(a.Y, b.Y - a.Y, mins.Y, maxs.Y, ref tMin, ref tMax)
                && Slab(a.Z, b.Z - a.Z, mins.Z, maxs.Z, ref tMin, ref tMax);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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