using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Threading;

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

        /// <summary>
        /// Each triangle's outward normal, for triangles that are faces of a closed solid, or null when
        /// none are. A zero normal marks a triangle with no known outside. Only
        /// <see cref="TryTraceHull"/> reads it; see there.
        /// </summary>
        private BspFile.Vector3[]? outward;
        private BvhNode[] bvh = [];
        private int[] order = [];

        /// <summary>
        /// The same triangles again, as one array per component, with the two edges precomputed.
        ///
        /// This is the layout the eight-wide triangle tests want and the interleaved one cannot give
        /// them: eight lanes need eight triangles' worth of each component side by side, and reading
        /// them out of <see cref="vertices"/> would be a gather per lane and would cost more than the
        /// vector math saves. The nearest-hit trace (<see cref="NearestWide"/>) and the yes-or-no line
        /// test (<see cref="Blocks"/>) both read them; only the box sweep still works from the vertices.
        ///
        /// The edges are stored rather than recomputed because Moller-Trumbore uses them and nothing
        /// else does, so subtracting them once at build time removes six subtractions per triangle per
        /// ray. The original vertices stay: the winning triangle's normal is read off them, which
        /// happens once per trace rather than once per triangle, and reconstructing a vertex as
        /// <c>v0 + e1</c> is not bit-identical to the vertex that was read from the file.
        ///
        /// Costs one more copy of the geometry - about 6MB on a map with 165,000 triangles. Each array
        /// runs <see cref="LanePadding"/> past the last triangle.
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
        /// Takes ownership of the triangles and indexes them. The arrays are kept as given rather than
        /// copied - callers build them once and hand them over.
        ///
        /// <paramref name="triangleOutward"/>, one per triangle, gives the outward normal of each
        /// triangle that is a face of a closed solid - a prop's hull - and a zero vector for any other.
        /// Terrain leaves it out: a surface has no outside.
        /// </summary>
        public void Build(BspFile.Vector3[] triangleVertices, int[] triangleContents,
            BspFile.Vector3[]? triangleOutward = null)
        {
            vertices = triangleVertices;
            contents = triangleContents;
            outward = triangleOutward;
            identity = Interlocked.Increment(ref meshesBuilt);
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
            BuildWideTree();

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

            // Padded past the last triangle with contents no mask matches, so a leaf near the end can
            // be loaded eight lanes wide without reading off the array; see LanePadding.
            var sortedContents = new int[count + LanePadding];
            var sortedOutward = outward is null ? null : new BspFile.Vector3[count];

            for (int i = 0; i < count; i++)
            {
                int from = order[i] * 3;
                int to = i * 3;

                sortedVertices[to] = vertices[from];
                sortedVertices[to + 1] = vertices[from + 1];
                sortedVertices[to + 2] = vertices[from + 2];

                sortedContents[i] = contents[order[i]];

                if (sortedOutward is not null)
                    sortedOutward[i] = outward![order[i]];
            }

            vertices = sortedVertices;
            contents = sortedContents;
            outward = sortedOutward;
            order = [];

            BuildEdgeArrays(count);
        }

        /// <summary>
        /// How far the per-triangle arrays the wide tests read run past the last triangle: one vector
        /// less one. A leaf holds at most eight triangles but often fewer, and <see cref="Blocks"/> tests
        /// whatever a leaf holds as one eight-lane load, masking off the lanes past its end. Those lanes
        /// read the next leaf's triangles, or this padding after the last leaf, and never decide
        /// anything.
        /// </summary>
        private const int LanePadding = 7;

        /// <summary>Splits the permuted triangles into the per-component arrays the wide traces read.</summary>
        private void BuildEdgeArrays(int count)
        {
            int padded = count + LanePadding;
            v0x = new float[padded]; v0y = new float[padded]; v0z = new float[padded];
            e1x = new float[padded]; e1y = new float[padded]; e1z = new float[padded];
            e2x = new float[padded]; e2y = new float[padded]; e2z = new float[padded];

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
        /// Which part of a segment a triangle has to cross to count as hit, as a fraction of the
        /// segment: more than this along it, and no more than this past its end. A segment owns the
        /// half-open stretch <c>(SegmentEpsilon, 1 + SegmentEpsilon]</c>.
        ///
        /// The near end is left out so that a trace starting on a surface - a headroom line from a floor
        /// point, a probe from a hit point just found - does not stop on the surface it starts from.
        /// The far end is **in**, and that is the half that matters. Both ends used to be left out, so
        /// a surface lying exactly where one segment ends and the next begins was inside neither, and
        /// anything that walks a line in pieces read straight through it.
        /// <see cref="StairMarker.TryFindFloor"/> is that walk: it steps down a column four units at a
        /// time, terrain heights and the heights it starts from are both short binary fractions, so
        /// terrain landing exactly on a boundary is common rather than a coincidence - an area surface
        /// a whole two units under terrain is enough - and down a straight run of terrain it stays on
        /// the boundary at every step. The column then read as having no floor at all: cp_badlands area
        /// 210, on terrain two units above its own surface, was reported 82 units afloat.
        ///
        /// Shifting the whole stretch by the same epsilon at both ends is what makes consecutive
        /// segments share each hit exactly once rather than at least once: a surface a distance
        /// <c>s</c> past the shared point lands at <c>1 + s</c> on the first and at <c>s</c> on the
        /// second, so it is the first's when <c>s</c> is within the epsilon and the second's otherwise.
        /// That holds in exact arithmetic and on flat terrain. On a steep triangle far from the origin
        /// the hit's position along a segment carries a rounding error of a few millionths, more than
        /// the epsilon, so a walk that has to see every surface cannot rest on it alone;
        /// <see cref="StairMarker.TryFindFloor"/> also overlaps its segments.
        /// Brushes need none of this - they are solids, and a segment starting on a brush face already
        /// starts inside it - and neither does <see cref="TryTraceHull"/>, which counts a box touching a
        /// triangle at the start of a sweep as starting solid.
        ///
        /// The price is that a segment starting *inside* a prop's hull is not told so either: it
        /// crosses nothing until it leaves, and nothing at all if it never does. A caller that needs to
        /// know asks <see cref="BspVisibility.IsInsideProp"/> first; see <see cref="ConvexSolids"/>.
        /// </summary>
        private const float SegmentEpsilon = 1e-6f;

        /// <summary>The far end of the stretch a segment owns; see <see cref="SegmentEpsilon"/>.</summary>
        private const float SegmentEnd = 1f + SegmentEpsilon;


        /// <summary>
        /// Whether the segment crosses any triangle matching <paramref name="mask"/>.
        ///
        /// The hottest query in the program on a map with much terrain or many props: on
        /// rp_downtown_tits_v25 it ran 205.7 million times and was 43% of the whole run, at about 34
        /// tree nodes and 24 triangles a query. So it is written for speed in three ways, none of which
        /// changes an answer.
        ///
        /// The segment's half of the box test is worked out once (<see cref="SegmentBoxes"/>) rather
        /// than three divisions per node. Nodes are read in place rather than copied. And a leaf's
        /// triangles are tested eight at a time over the edge arrays (<see cref="LeafBlocksWide"/>),
        /// with the same operations in the same order as the one-at-a-time test, so each lane comes
        /// out bit for bit as that test would.
        ///
        /// Children are box-tested before they are pushed, and the one the segment enters first is
        /// walked first. That changes the order leaves are visited in, which is safe here and nowhere
        /// else in this class: the answer is whether *any* triangle is crossed, and that does not depend
        /// on which is found first. The traces that report a fraction or a normal keep the order they
        /// always had, because a tie between two hits at the same distance goes to the first found.
        ///
        /// The stack can never overflow - the tree is split at the median, so it is no deeper than
        /// about log2 of the triangle count, and a depth-first walk holds at most one entry per level
        /// plus one - so the guard on it is never what decides an answer, before or after.
        ///
        /// After all that, the walk itself was most of the cost. Profiled on the visibility pass, a
        /// displacement query box-tested 36 nodes on rp_downtown_tits_v25, 54 on cp_badlands and 61 on
        /// gm_construct, against three or four leaves, and the leaves' triangle tests were about a
        /// fifth of its time. And the walk was mostly finding what it had just found: 96% of those
        /// queries on the city are blocked, and in 86% the leaf that blocked the same thread's previous
        /// query blocks this one too, because the pass traces its lines in runs between neighbouring
        /// areas, over the same hill. So two more things, neither of which changes an answer either:
        ///
        /// - Each thread remembers the last <see cref="RecentCount"/> leaves that blocked a segment in
        ///   this mesh (<see cref="recentLeaves"/>) and asks them first, each with its own box test
        ///   and its triangle test, exactly as a walk asks a leaf. A remembered leaf that says yes is
        ///   one the walk would have reached and been told yes by, since a segment that passes a leaf's
        ///   box passes every box above it (<see cref="BlocksWideTree"/> has why); one that says no is
        ///   simply not the answer, and the walk follows. Answered so: 97% of the city's queries, 77%
        ///   of cp_badlands' and 74% of gm_construct's.
        /// - The walk that follows goes down the wide tree (<see cref="BlocksWideTree"/>): the same
        ///   boxes and leaves, eight boxes tested in one go, about a third as many stops.
        ///
        /// Per query, replayed from recorded visibility passes: on the city, 36.0 box tests and 3.72
        /// leaves become 2.2 box tests, 0.37 eight-box tests and 1.42 leaves; on cp_badlands, 53.8
        /// and 3.64 become 3.0, 3.05 and 2.22; on gm_construct, 61.1 and 3.21 become 3.2, 3.47 and
        /// 2.18.
        /// </summary>
        public bool Blocks(BspFile.Vector3 a, BspFile.Vector3 b, int mask)
        {
            if (bvh.Length == 0)
                return false;

            var segment = new SegmentBoxes(a, b);

            if (!segment.Hits(in bvh[0], out _))
                return false;

            if (!remembers)
                return Walk(a, b, mask, in segment) >= 0;

            ref var recent = ref (recentLeaves ??= new RecentLeaves[RecentSlots])[(int)identity & (RecentSlots - 1)];

            if (recent.Mesh != identity)
                recent = new RecentLeaves { Mesh = identity };

            Span<int> leaves = recent.Leaves;
            int remembered = recent.Count;

            for (int k = 0; k < remembered; k++)
            {
                int leaf = leaves[k];
                if (!LeafBlocks(leaf, a, b, mask, in segment))
                    continue;

                // To the front, the others moving down one to make room.
                leaves[..k].CopyTo(leaves[1..]);
                leaves[0] = leaf;
                return true;
            }

            int blocker = Walk(a, b, mask, in segment);
            if (blocker < 0)
                return false;

            // In front of the others, and the oldest forgotten if there is no room. Never one already
            // there: each of those has just been asked, and has said no.
            int kept = Math.Min(remembered, RecentCount - 1);
            leaves[..kept].CopyTo(leaves[1..]);
            leaves[0] = blocker;
            recent.Count = kept + 1;
            return true;
        }

        /// <summary>
        /// The walk for <see cref="Blocks"/>: the leaf whose triangle it found crossed, as an index into
        /// <see cref="bvh"/>, or -1 when none is.
        /// </summary>
        private int Walk(BspFile.Vector3 a, BspFile.Vector3 b, int mask, in SegmentBoxes segment)
            => wideChildren.Length != 0
                ? BlocksWideTree(a, b, mask, in segment)
                : BlocksBinaryTree(a, b, mask, in segment);

        /// <summary>
        /// Whether the segment passes through leaf <paramref name="index"/>'s box and crosses one of its
        /// triangles - the two tests every walk here puts a leaf to, on their own.
        /// </summary>
        private bool LeafBlocks(int index, BspFile.Vector3 a, BspFile.Vector3 b, int mask, in SegmentBoxes segment)
        {
            ref readonly var leaf = ref bvh[index];

            return segment.Hits(in leaf, out _) && (Vector256.IsHardwareAccelerated
                ? LeafBlocksWide(a, b, mask, leaf.First, leaf.Count)
                : LeafBlocksScalar(a, b, mask, leaf.First, leaf.Count));
        }

        // ---- the leaves that blocked last ------------------------------------------------------

        /// <summary>How many leaves a mesh remembers per thread.</summary>
        private const int RecentCount = 4;

        /// <summary>How many meshes a thread remembers leaves for at once; a power of two.</summary>
        private const int RecentSlots = 8;

        private static long meshesBuilt;

        /// <summary>
        /// This mesh as <see cref="Build"/> last left it, as far as <see cref="recentLeaves"/> is
        /// concerned: a number no other build has, so leaves remembered from another mesh, or from this
        /// one before it was rebuilt, are never taken for its own. It also picks the place a thread
        /// keeps them in; meshes built a multiple of <see cref="RecentSlots"/> apart share one and push
        /// each other's out, which costs only the shortcut.
        /// </summary>
        private long identity;

        /// <summary>Whether <see cref="Blocks"/> asks the remembered leaves first; only the tests turn it off.</summary>
        private bool remembers = true;

        /// <summary>
        /// The leaves that most recently blocked a segment, per mesh and per thread, the latest first,
        /// as indices into <see cref="bvh"/>: what <see cref="Blocks"/> asks before it walks.
        ///
        /// Per thread so it needs no lock, and keyed by <see cref="identity"/> rather than holding the
        /// mesh, so a thread that outlives a map - a map pack runs many on one pool - does not keep the
        /// map's terrain alive.
        /// </summary>
        [ThreadStatic] private static RecentLeaves[]? recentLeaves;

        private struct RecentLeaves
        {
            public long Mesh;
            public LeafList Leaves;
            public int Count;
        }

        [InlineArray(RecentCount)]
        private struct LeafList
        {
            private int first;
        }

        /// <summary>
        /// <see cref="Blocks"/>'s walk down the binary tree, one node's two children at a time, as it
        /// was before the wide tree, but for saying which leaf blocked: the walk wherever there is no
        /// <see cref="wideChildren"/> - a mesh of one leaf, or hardware without 256-bit vectors.
        /// </summary>
        private int BlocksBinaryTree(BspFile.Vector3 a, BspFile.Vector3 b, int mask, in SegmentBoxes segment)
        {
            bool wide = Vector256.IsHardwareAccelerated;

            Span<int> stack = stackalloc int[64];
            int top = 0;
            stack[top++] = 0;

            while (top > 0)
            {
                int index = stack[--top];
                ref readonly var node = ref bvh[index];

                if (node.Count != 0)
                {
                    if (wide
                            ? LeafBlocksWide(a, b, mask, node.First, node.Count)
                            : LeafBlocksScalar(a, b, mask, node.First, node.Count))
                    {
                        return index;
                    }

                    continue;
                }

                int left = index + 1;
                int right = node.Right;

                bool hitLeft = segment.Hits(in bvh[left], out float enterLeft);
                bool hitRight = segment.Hits(in bvh[right], out float enterRight);

                if (top + 2 > stack.Length)
                    continue;

                // The nearer child goes on last, so it comes off first.
                if (hitLeft && hitRight)
                {
                    bool leftFirst = enterLeft <= enterRight;
                    stack[top++] = leftFirst ? right : left;
                    stack[top++] = leftFirst ? left : right;
                }
                else if (hitLeft)
                {
                    stack[top++] = left;
                }
                else if (hitRight)
                {
                    stack[top++] = right;
                }
            }

            return -1;
        }

        // ---- the wide tree ---------------------------------------------------------------------

        /// <summary>How many children a node of the wide tree holds: one 256-bit vector of boxes.</summary>
        private const int Width = 8;

        /// <summary>Floats per wide node in <see cref="wideBounds"/>: each of six bounds for eight boxes.</summary>
        private const int WideStride = 6 * Width;

        /// <summary>
        /// The most entries <see cref="BlocksWideTree"/>'s stack can need. A tree that would need more is
        /// not given a wide form at all, and <see cref="Blocks"/> walks the binary tree instead.
        /// </summary>
        private const int WideStackLimit = 256;

        /// <summary>
        /// The binary tree again, with each node standing for a few of its levels: the boxes of up to
        /// eight of a node's descendants side by side, one array per bound, so <see cref="Blocks"/>
        /// tests eight boxes in one go rather than two at a time.
        ///
        /// **Not a new tree: the same boxes, fewer stops.** Every box here is a node of
        /// <see cref="bvh"/>, copied as it is, and every leaf is one of its leaves with the same
        /// triangle range. What the wide form leaves out is only the boxes in between - those of the
        /// nodes between a wide node and the descendants it lists - and those never decide anything;
        /// see <see cref="BlocksWideTree"/>.
        ///
        /// Built only where 256-bit vectors run in hardware, and only for a mesh of more than one leaf.
        /// </summary>
        private float[] wideBounds = [];

        /// <summary>
        /// Each wide node's eight slots: a wide node's index, or the bitwise complement of a leaf's
        /// index in <see cref="bvh"/>. An empty slot holds zero and is never read; see
        /// <see cref="wideFull"/>.
        /// </summary>
        private int[] wideChildren = [];

        /// <summary>Which of a wide node's eight slots are in use, one bit each.</summary>
        private byte[] wideFull = [];

        /// <summary>
        /// For each wide node and each of the eight octants a segment can point into, the order to try
        /// its slots in, nearest first, one slot number a byte; see <see cref="SlotOrder"/>. Only how
        /// soon a blocker is found depends on it.
        /// </summary>
        private ulong[] wideOrder = [];

        /// <summary>
        /// Folds <see cref="bvh"/> into <see cref="wideBounds"/> and its companions, or leaves them
        /// empty where the wide walk is not to be used.
        /// </summary>
        private void BuildWideTree()
        {
            wideBounds = [];
            wideChildren = [];
            wideFull = [];
            wideOrder = [];

            if (!Vector256.IsHardwareAccelerated || bvh.Length == 0 || bvh[0].Count != 0)
                return;

            var bounds = new List<float>();
            var children = new List<int>();
            var full = new List<byte>();
            var orders = new List<ulong>();

            Fold(0, bounds, children, full, orders, out int depth);

            // Each wide node taken off the stack puts at most eight back, so the stack never holds more
            // than seven per level below the root and the root's own entry.
            if ((Width - 1) * depth + 1 > WideStackLimit)
                return;

            wideBounds = bounds.ToArray();
            wideChildren = children.ToArray();
            wideFull = full.ToArray();
            wideOrder = orders.ToArray();
        }

        /// <summary>
        /// Makes a wide node of the interior node <paramref name="node"/>: starting from its two
        /// children, opens whichever listed interior node has the largest box until eight are listed or
        /// only leaves are left, then folds each interior node listed in turn. Returns the wide node's
        /// index, and in <paramref name="depth"/> how many wide levels it and the deepest below it make.
        /// </summary>
        private int Fold(int node, List<float> bounds, List<int> children, List<byte> full, List<ulong> orders,
            out int depth)
        {
            Span<int> slots = stackalloc int[Width];
            int listed = 0;
            slots[listed++] = node + 1;
            slots[listed++] = bvh[node].Right;

            while (listed < Width)
            {
                int widest = -1;
                float widestArea = -1f;

                for (int i = 0; i < listed; i++)
                {
                    ref readonly var candidate = ref bvh[slots[i]];
                    if (candidate.Count != 0)
                        continue;

                    float dx = candidate.Maxs.X - candidate.Mins.X;
                    float dy = candidate.Maxs.Y - candidate.Mins.Y;
                    float dz = candidate.Maxs.Z - candidate.Mins.Z;
                    float area = dx * dy + dy * dz + dz * dx;

                    if (area > widestArea)
                    {
                        widestArea = area;
                        widest = i;
                    }
                }

                // Only leaves left - or boxes too strange to measure, which are folded on their own
                // instead. Either way any choice here gives the same answers; it only shapes the walk.
                if (widest < 0)
                    break;

                int opened = slots[widest];
                slots[widest] = opened + 1;
                slots[listed++] = bvh[opened].Right;
            }

            int self = full.Count;
            for (int i = 0; i < WideStride; i++) bounds.Add(0f);
            for (int i = 0; i < Width; i++) children.Add(0);
            for (int i = 0; i < Width; i++) orders.Add(0);
            full.Add((byte)((1 << listed) - 1));

            for (int octant = 0; octant < Width; octant++)
                orders[self * Width + octant] = SlotOrder(node, slots[..listed], octant);

            depth = 1;

            for (int i = 0; i < listed; i++)
            {
                ref readonly var child = ref bvh[slots[i]];
                int at = self * WideStride + i;

                bounds[at] = child.Mins.X;
                bounds[at + Width] = child.Mins.Y;
                bounds[at + 2 * Width] = child.Mins.Z;
                bounds[at + 3 * Width] = child.Maxs.X;
                bounds[at + 4 * Width] = child.Maxs.Y;
                bounds[at + 5 * Width] = child.Maxs.Z;

                if (child.Count != 0)
                {
                    children[self * Width + i] = ~slots[i];
                    continue;
                }

                children[self * Width + i] = Fold(slots[i], bounds, children, full, orders, out int below);
                depth = Math.Max(depth, below + 1);
            }

            return self;
        }

        /// <summary>
        /// The order to try a wide node's slots in for a segment pointing into <paramref name="octant"/>
        /// - bit 0 set for a segment running towards smaller x, bit 1 for y, bit 2 for z - nearest
        /// first, one slot number a byte from the lowest, with the unused slots after the used ones.
        ///
        /// The binary tree split each node at the median along its box's longest axis, the smaller
        /// centres going left, so a segment running up that axis meets the left child first and one
        /// running down it the right. Walking the nodes the wide node stands for, each node's children
        /// in the order the segment meets them, lists its slots in roughly the order the segment
        /// reaches them.
        /// </summary>
        private ulong SlotOrder(int node, ReadOnlySpan<int> slots, int octant)
        {
            ulong order = 0;
            int placed = 0;
            PlaceSlots(node, slots, octant, ref order, ref placed);

            for (int unused = slots.Length; unused < Width; unused++)
                order |= (ulong)unused << (8 * placed++);

            return order;
        }

        /// <summary>One step of <see cref="SlotOrder"/>: lists the slots at or under binary node <paramref name="at"/>.</summary>
        private void PlaceSlots(int at, ReadOnlySpan<int> slots, int octant, ref ulong order, ref int placed)
        {
            int slot = slots.IndexOf(at);
            if (slot >= 0)
            {
                order |= (ulong)slot << (8 * placed++);
                return;
            }

            ref readonly var n = ref bvh[at];
            float dx = n.Maxs.X - n.Mins.X;
            float dy = n.Maxs.Y - n.Mins.Y;
            float dz = n.Maxs.Z - n.Mins.Z;
            int axis = dx >= dy && dx >= dz ? 0 : dy >= dz ? 1 : 2;

            bool downward = ((octant >> axis) & 1) != 0;
            PlaceSlots(downward ? n.Right : at + 1, slots, octant, ref order, ref placed);
            PlaceSlots(downward ? at + 1 : n.Right, slots, octant, ref order, ref placed);
        }

        /// <summary>
        /// <see cref="Blocks"/> down the wide tree: the same answer as down the binary one, for the
        /// same triangle tests, with about a third as many stops.
        ///
        /// What the binary walk answers is whether any leaf whose box the segment passes through, by
        /// <see cref="SegmentBoxes.Hits(Vector128{float}, Vector128{float}, out float)"/>, holds a
        /// triangle it crosses. It reaches every such leaf it does not stop before: a node's box is
        /// the union of its children's, and the box test cannot pass a box and fail one holding it -
        /// on a flat axis it asks whether the start lies inside, and on the others the fractions it
        /// works out from wider bounds are rounded from the same operations on numbers no further in,
        /// so they span no less (the argument <see cref="ColumnBlocks"/> rests on). So every ancestor
        /// of a leaf it passes is passed too, and the boxes in between decide nothing; this walk skips
        /// them. Each leaf is still box-tested on its own box, in <see cref="WideBoxes.Hits"/> - the
        /// same subtractions, multiplications, minimums and maximums in the same order, lane for lane,
        /// so the same answer bit for bit, NaN included - and its triangles are tested by
        /// <see cref="LeafBlocksWide"/>, the binary walk's own leaf test. The order the leaves are
        /// asked in is all that differs, and any crossing being enough, it decides nothing.
        /// </summary>
        private int BlocksWideTree(BspFile.Vector3 a, BspFile.Vector3 b, int mask, in SegmentBoxes segment)
        {
            var boxes = new WideBoxes(in segment);

            int octant = (b.X - a.X < 0f ? 1 : 0) | (b.Y - a.Y < 0f ? 2 : 0) | (b.Z - a.Z < 0f ? 4 : 0);

            Span<int> stack = stackalloc int[WideStackLimit];
            int top = 0;
            stack[top++] = 0;

            while (top > 0)
            {
                int entry = stack[--top];

                if (entry < 0)
                {
                    ref readonly var leaf = ref bvh[~entry];
                    if (LeafBlocksWide(a, b, mask, leaf.First, leaf.Count))
                        return ~entry;

                    continue;
                }

                uint passed = boxes.Hits(wideBounds, entry) & wideFull[entry];
                if (passed == 0)
                    continue;

                // Farthest first, so the nearest comes off next. Every slot is written and only those
                // whose box was passed are kept, which costs less than a branch on each.
                ulong order = wideOrder[entry * Width + octant];
                int slotsAt = entry * Width;

                for (int k = Width - 1; k >= 0; k--)
                {
                    int slot = (int)(order >> (8 * k)) & 0xFF;
                    stack[top] = wideChildren[slotsAt + slot];
                    top += (int)(passed >> slot) & 1;
                }
            }

            return -1;
        }

        /// <summary>
        /// <see cref="SegmentBoxes.Hits(Vector128{float}, Vector128{float}, out float)"/> for the eight
        /// boxes of a wide node at once, one lane a box where that test has one lane an axis.
        ///
        /// Lane for lane the same arithmetic: the same reciprocals and start, the same subtraction and
        /// multiplication per bound, the same native minimum and maximum of each pair; a flat axis
        /// swapped out for an infinite slab and its start tested against the bounds; and the three axes
        /// gathered with zero and the far end in the order that test's two shuffles gather them -
        /// x with z and y with the start, then the two together - so even a NaN falls the same way.
        /// </summary>
        private readonly struct WideBoxes
        {
            private readonly Vector256<float> ox, oy, oz;
            private readonly Vector256<float> ix, iy, iz;
            private readonly Vector256<float> slabX, slabY, slabZ;
            private readonly bool flatX, flatY, flatZ, anyFlat;

            private static readonly Vector256<float> NoEntry = Vector256.Create(float.NegativeInfinity);
            private static readonly Vector256<float> NoExit = Vector256.Create(float.PositiveInfinity);
            private static readonly Vector256<float> FarEnd = Vector256.Create(SegmentEnd);

            public WideBoxes(in SegmentBoxes segment)
            {
                var origin = segment.Origin;
                var inverse = segment.Inverse;
                var slabs = segment.Slabs;
                var flat = segment.Flat;

                ox = Vector256.Create(origin.GetElement(0));
                oy = Vector256.Create(origin.GetElement(1));
                oz = Vector256.Create(origin.GetElement(2));

                ix = Vector256.Create(inverse.GetElement(0));
                iy = Vector256.Create(inverse.GetElement(1));
                iz = Vector256.Create(inverse.GetElement(2));

                slabX = Vector256.Create(slabs.GetElement(0));
                slabY = Vector256.Create(slabs.GetElement(1));
                slabZ = Vector256.Create(slabs.GetElement(2));

                flatX = flat.AsInt32().GetElement(0) != 0;
                flatY = flat.AsInt32().GetElement(1) != 0;
                flatZ = flat.AsInt32().GetElement(2) != 0;
                anyFlat = segment.AnyFlat;
            }

            /// <summary>One bit per box of wide node <paramref name="node"/> the segment passes through.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public uint Hits(float[] bounds, int node)
            {
                ref float at = ref bounds[node * WideStride];

                var minX = Vector256.LoadUnsafe(ref at);
                var minY = Vector256.LoadUnsafe(ref at, Width);
                var minZ = Vector256.LoadUnsafe(ref at, 2 * Width);
                var maxX = Vector256.LoadUnsafe(ref at, 3 * Width);
                var maxY = Vector256.LoadUnsafe(ref at, 4 * Width);
                var maxZ = Vector256.LoadUnsafe(ref at, 5 * Width);

                var t0x = (minX - ox) * ix;
                var t1x = (maxX - ox) * ix;
                var t0y = (minY - oy) * iy;
                var t1y = (maxY - oy) * iy;
                var t0z = (minZ - oz) * iz;
                var t1z = (maxZ - oz) * iz;

                var entryX = Vector256.ConditionalSelect(slabX, Vector256.MinNative(t0x, t1x), NoEntry);
                var entryY = Vector256.ConditionalSelect(slabY, Vector256.MinNative(t0y, t1y), NoEntry);
                var entryZ = Vector256.ConditionalSelect(slabZ, Vector256.MinNative(t0z, t1z), NoEntry);
                var exitX = Vector256.ConditionalSelect(slabX, Vector256.MaxNative(t0x, t1x), NoExit);
                var exitY = Vector256.ConditionalSelect(slabY, Vector256.MaxNative(t0y, t1y), NoExit);
                var exitZ = Vector256.ConditionalSelect(slabZ, Vector256.MaxNative(t0z, t1z), NoExit);

                var entry = Vector256.MaxNative(Vector256.MaxNative(entryX, entryZ),
                    Vector256.MaxNative(entryY, Vector256<float>.Zero));
                var exit = Vector256.MinNative(Vector256.MinNative(exitX, exitZ),
                    Vector256.MinNative(exitY, FarEnd));

                var passed = Vector256.LessThanOrEqual(entry, exit);

                if (anyFlat)
                {
                    if (flatX) passed &= Vector256.GreaterThanOrEqual(ox, minX) & Vector256.LessThanOrEqual(ox, maxX);
                    if (flatY) passed &= Vector256.GreaterThanOrEqual(oy, minY) & Vector256.LessThanOrEqual(oy, maxY);
                    if (flatZ) passed &= Vector256.GreaterThanOrEqual(oz, minZ) & Vector256.LessThanOrEqual(oz, maxZ);
                }

                return passed.ExtractMostSignificantBits();
            }
        }

        /// <summary>
        /// The leaves of the tree a segment straight up or down through <paramref name="x"/>,
        /// <paramref name="y"/> with both ends between <paramref name="below"/> and
        /// <paramref name="above"/> less a margin can reach: every leaf whose box, and every ancestor's,
        /// holds the column and reaches into that stretch of it. Written to <paramref name="leaves"/>,
        /// which is grown as needed; returns how many. <see cref="ColumnBlocks"/> then answers each such
        /// segment from these alone.
        ///
        /// A segment with no length across is flat on those two axes, and <see cref="SegmentBoxes"/>
        /// asks of a flat axis only whether the start lies within the box's slab - the comparisons made
        /// here, the same way round. A box wholly above <paramref name="above"/> or wholly below
        /// <paramref name="below"/> it cannot pass for any such segment either, provided the margin the
        /// caller took is far wider than the rounding in the box test and than the far end's
        /// <see cref="SegmentEpsilon"/>: going away from such a box, the segment's fractions for it are
        /// all below zero; going towards it, all past one by at least the margin over the segment's
        /// length. <see cref="BspVisibility.ColumnMargin"/> is a thousand times what that needs. So these
        /// are all the leaves <see cref="Blocks"/> could reach for such a segment, and more: which of
        /// them it does reach depends on the heights, and that is left to <see cref="ColumnBlocks"/>.
        /// </summary>
        internal int CollectColumn(float x, float y, float below, float above, ref int[] leaves)
        {
            if (bvh.Length == 0)
                return 0;

            int found = 0;

            Span<int> stack = stackalloc int[64];
            int top = 0;
            stack[top++] = 0;

            while (top > 0)
            {
                int index = stack[--top];
                ref readonly var node = ref bvh[index];

                if (!(x >= node.Mins.X && x <= node.Maxs.X && y >= node.Mins.Y && y <= node.Maxs.Y))
                    continue;

                if (node.Mins.Z > above || node.Maxs.Z < below)
                    continue;

                if (node.Count != 0)
                {
                    if (found == leaves.Length)
                        Array.Resize(ref leaves, Math.Max(16, leaves.Length * 2));

                    leaves[found++] = index;
                    continue;
                }

                // As deep as the tree and one more, which a median split keeps far below the stack's
                // size; see Blocks.
                if (top + 2 > stack.Length)
                    continue;

                stack[top++] = node.Right;
                stack[top++] = index + 1;
            }

            return found;
        }

        /// <summary>
        /// <see cref="Blocks"/> for a segment straight up or down the column <paramref name="leaves"/>
        /// were collected for (<see cref="CollectColumn"/>): the same answer, without the walk from the
        /// root.
        ///
        /// <see cref="Blocks"/> tests the triangles of every leaf whose box the segment passes through,
        /// once it has passed through the box of every node above it, and says whether any of them is
        /// crossed. A node's box is the union of its triangles' boxes, so it holds each child's box, and
        /// the box test cannot pass a box and fail one holding it: on the flat axes it asks whether the
        /// start lies inside, and on the axis the segment runs along, the fractions it works out from a
        /// wider box's bounds are rounded from the same operations on numbers no further in, so they
        /// span no less. A leaf the segment passes through therefore has every ancestor passed through
        /// too, and the leaves reached are exactly the collected ones the segment passes through. Each
        /// is box-tested and its triangles tested as <see cref="Blocks"/> does, by the same code, so
        /// each answers the same; the order they are asked in decides nothing, any crossing being
        /// enough.
        /// </summary>
        internal bool ColumnBlocks(ReadOnlySpan<int> leaves, BspFile.Vector3 a, BspFile.Vector3 b, int mask)
        {
            if (leaves.IsEmpty)
                return false;

            var segment = new SegmentBoxes(a, b);
            bool wide = Vector256.IsHardwareAccelerated;

            foreach (int index in leaves)
            {
                ref readonly var node = ref bvh[index];

                if (!segment.Hits(in node, out _))
                    continue;

                if (wide
                        ? LeafBlocksWide(a, b, mask, node.First, node.Count)
                        : LeafBlocksScalar(a, b, mask, node.First, node.Count))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The tree as built, for the tests that hold <see cref="Blocks"/> to the walk it replaced:
        /// each node's box, its right child (the left is the next node), and its triangle range, with
        /// the triangles and their contents in the order the ranges index.
        /// </summary>
        internal (BspFile.Vector3 Mins, BspFile.Vector3 Maxs, int Right, int First, int Count)[] NodesForTests()
        {
            var nodes = new (BspFile.Vector3, BspFile.Vector3, int, int, int)[bvh.Length];
            for (int i = 0; i < bvh.Length; i++)
                nodes[i] = (bvh[i].Mins, bvh[i].Maxs, bvh[i].Right, bvh[i].First, bvh[i].Count);

            return nodes;
        }

        /// <summary>
        /// The wide tree as built, for the tests that check it is the binary tree's own boxes and leaves:
        /// each wide node's six bound arrays (<see cref="wideBounds"/>), its slots, and which are full.
        /// Empty when there is no wide tree.
        /// </summary>
        internal (float[] Bounds, int[] Children, byte[] Full) WideTreeForTests() => (wideBounds, wideChildren, wideFull);

        /// <summary>
        /// Takes away the wide tree and stops remembering blockers, as <paramref name="wide"/> and
        /// <paramref name="remember"/> say, so the tests can hold each walk - binary or wide, with or
        /// without the leaves that blocked last - to the old code.
        /// </summary>
        internal void UseForTests(bool wide, bool remember)
        {
            if (!wide)
                wideChildren = [];
            remembers = remember;
        }

        /// <inheritdoc cref="NodesForTests"/>
        internal BspFile.Vector3[] VerticesForTests() => vertices;

        /// <inheritdoc cref="NodesForTests"/>
        internal int[] ContentsForTests() => contents.AsSpan(0, TriangleCount).ToArray();

        /// <summary>The box test every walk here runs, on its own, for the tests that hold it to the one it replaced.</summary>
        internal static bool SegmentHitsBoxForTests(BspFile.Vector3 a, BspFile.Vector3 b,
            BspFile.Vector3 mins, BspFile.Vector3 maxs)
            => new SegmentBoxes(a, b).Hits(Vector128.Create(mins.X, mins.Y, mins.Z, 0f),
                Vector128.Create(maxs.X, maxs.Y, maxs.Z, 0f), out _);

        /// <summary>
        /// Whether the segment crosses any of a leaf's triangles matching the mask, one at a time. The
        /// whole of the leaf test where 256-bit vectors are not available, and the definition the wide
        /// test reproduces.
        /// </summary>
        private bool LeafBlocksScalar(BspFile.Vector3 a, BspFile.Vector3 b, int mask, int first, int count)
        {
            for (int i = first; i < first + count; i++)
            {
                if ((contents[i] & mask) == 0)
                    continue;

                int t = i * 3;
                if (SegmentHitsTriangle(a, b, vertices[t], vertices[t + 1], vertices[t + 2]))
                    return true;
            }

            return false;
        }

        /// <summary>The lane numbers, for masking off the lanes past the end of a leaf.</summary>
        private static readonly Vector256<int> LaneIndex = Vector256.Create(0, 1, 2, 3, 4, 5, 6, 7);

        /// <summary>
        /// <see cref="LeafBlocksScalar"/> eight triangles at a time: Moller-Trumbore as
        /// <see cref="TryHitTriangle"/> writes it, operation for operation, over the precomputed edges.
        ///
        /// Bit-identical lane by lane, not merely close. The edges are the same subtractions the scalar
        /// test does, done once at build time; every product, sum and quotient after that is the same
        /// operation on the same operands in the same order; and nothing is fused - a vector multiply
        /// and add are two roundings here exactly as they are in scalar code, because the JIT does not
        /// contract them into a fused multiply-add on its own. The rejections are the scalar test's
        /// comparisons negated rather than rewritten, so a NaN falls the same way in both: the scalar
        /// test rejects a hit only when a comparison says to, and so does each lane.
        ///
        /// Unlike <see cref="NearestWide"/>, which has to look at every triangle anyway, this runs for
        /// any leaf, full or not. A leaf holds four to eight triangles and is loaded as one vector
        /// regardless; the lanes past its end read the next leaf, or <see cref="LanePadding"/>, and are
        /// masked off before anything is decided. It exits as soon as no lane is left alive, as the
        /// scalar loop exits on the first hit.
        /// </summary>
        private bool LeafBlocksWide(BspFile.Vector3 a, BspFile.Vector3 b, int mask, int first, int count)
        {
            const float Epsilon = 1e-6f;
            int lanes = Vector256<float>.Count;

            var maskLanes = Vector256.Create(mask);
            var zero = Vector256<float>.Zero;
            var one = Vector256<float>.One;

            float sdx = b.X - a.X, sdy = b.Y - a.Y, sdz = b.Z - a.Z;
            var dx = Vector256.Create(sdx);
            var dy = Vector256.Create(sdy);
            var dz = Vector256.Create(sdz);

            var ax = Vector256.Create(a.X);
            var ay = Vector256.Create(a.Y);
            var az = Vector256.Create(a.Z);

            for (int i = first, end = first + count; i < end; i += lanes)
            {
                var inLeaf = Vector256.LessThan(LaneIndex, Vector256.Create(end - i));
                var wanted = Vector256.LoadUnsafe(ref contents[i]) & maskLanes;
                var live = (inLeaf & ~Vector256.Equals(wanted, Vector256<int>.Zero)).AsSingle();

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

                live &= ~Vector256.LessThan(Vector256.Abs(det), Vector256.Create(Epsilon)).AsSingle();
                if (live.Equals(zero))
                    continue;

                // A lane already dead may hold a zero determinant; whatever its quotient comes to, it
                // is masked off and decides nothing. Live lanes divide exactly as the scalar test does.
                var inverse = one / det;

                var tx = ax - Vector256.LoadUnsafe(ref v0x[i]);
                var ty = ay - Vector256.LoadUnsafe(ref v0y[i]);
                var tz = az - Vector256.LoadUnsafe(ref v0z[i]);

                var u = (tx * px + ty * py + tz * pz) * inverse;

                live &= ~(Vector256.LessThan(u, zero) | Vector256.GreaterThan(u, one)).AsSingle();
                if (live.Equals(zero))
                    continue;

                // q = t x e1
                var qx = ty * v1z - tz * v1y;
                var qy = tz * v1x - tx * v1z;
                var qz = tx * v1y - ty * v1x;

                var v = (dx * qx + dy * qy + dz * qz) * inverse;

                live &= ~(Vector256.LessThan(v, zero) | Vector256.GreaterThan(u + v, one)).AsSingle();
                if (live.Equals(zero))
                    continue;

                var at = (v2x * qx + v2y * qy + v2z * qz) * inverse;

                // The segment's own half-open stretch; see SegmentEpsilon.
                live &= ~(Vector256.LessThanOrEqual(at, Vector256.Create(SegmentEpsilon))
                          | Vector256.GreaterThan(at, Vector256.Create(SegmentEnd))).AsSingle();

                if (!live.Equals(zero))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// The slab test every tree walk here runs against node boxes, with the segment's half of it
        /// worked out once per walk, and the three axes done side by side.
        ///
        /// The test is the usual one. Along each axis the segment is inside the box's slab between two
        /// fractions, <c>(min - origin) / delta</c> and <c>(max - origin) / delta</c> taken in whichever
        /// order is smaller first; it is inside the box where it is inside all three slabs, so it hits
        /// the box when the latest entry, never before its start, comes no later than the earliest
        /// exit, never past its far end. An axis the segment barely moves along has no fractions worth
        /// dividing out, and asks only whether the start lies within the slab.
        ///
        /// It used to take the segment's two ends and work its half out afresh for every node - three
        /// subtractions, three comparisons and three divisions, on values that never change for the
        /// length of a walk - and then went axis by axis with a branch at every step. A division is the
        /// slowest arithmetic there is and those branches go whichever way the geometry says, and the
        /// profile put this test inside the most expensive query in the program. Now the reciprocals
        /// are taken once, here, and a box is one subtraction and one multiplication per bound across
        /// all three axes, a minimum and a maximum, and a single comparison at the end. A segment with
        /// a flat axis - every trace down a column, which is most of the floor walk - asks about those
        /// axes first, because that is where most boxes miss it and the old test, which reached them
        /// first, answered in two comparisons; asking after the slabs measured slower than before.
        ///
        /// Every box answers exactly as it did. The reciprocals and each fraction are the same
        /// operations on the same operands, so the same numbers. Ordering a pair by minimum and maximum
        /// rather than by a swap, and gathering the three axes at the end rather than one at a time,
        /// can only change which of two equal zeros is kept, which no comparison can see. And stopping
        /// early on the first axis that misses was never more than a shortcut, in whatever order the
        /// axes are taken: entry only rises and exit only falls as axes are added, and a flat axis
        /// either fails outright or adds nothing, so the segment misses after one axis exactly when it
        /// misses after all three. That holds for finite numbers, which a box and a segment always are:
        /// a reciprocal is taken only of a length of at least a millionth.
        /// </summary>
        private readonly struct SegmentBoxes
        {
            private readonly Vector128<float> origin;
            private readonly Vector128<float> inverse;

            /// <summary>The axes the slabs are divided out on: every axis but the flat ones and the fourth lane.</summary>
            private readonly Vector128<float> slabs;

            /// <summary>
            /// What stands in for a fraction where an axis has none: on the flat axes, a slab that
            /// constrains nothing, and in the unused fourth lane the bounds of the segment itself -
            /// so gathering the four lanes takes in where the old test started from, zero and the far
            /// end, as well.
            /// </summary>
            private static readonly Vector128<float> IdleEntry =
                Vector128.Create(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity, 0f);

            /// <inheritdoc cref="IdleEntry"/>
            private static readonly Vector128<float> IdleExit =
                Vector128.Create(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity, SegmentEnd);

            /// <summary>The flat axes, and whether there are any.</summary>
            private readonly Vector128<float> flat;
            private readonly bool anyFlat;

            public SegmentBoxes(BspFile.Vector3 a, BspFile.Vector3 b)
            {
                float dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;

                bool flatX = MathF.Abs(dx) < 1e-6f;
                bool flatY = MathF.Abs(dy) < 1e-6f;
                bool flatZ = MathF.Abs(dz) < 1e-6f;

                origin = Vector128.Create(a.X, a.Y, a.Z, 0f);

                flat = Vector128.Create(Lane(flatX), Lane(flatY), Lane(flatZ), 0).AsSingle();
                slabs = Vector128.Create(Lane(!flatX), Lane(!flatY), Lane(!flatZ), 0).AsSingle();
                anyFlat = flatX || flatY || flatZ;

                // One division for all three, each lane the same correctly rounded quotient a scalar
                // division gives. A flat axis divides by something under a millionth, or by zero, and
                // its quotient is thrown away for zero.
                var quotient = Vector128<float>.One / Vector128.Create(dx, dy, dz, 1f);
                inverse = Vector128.ConditionalSelect(slabs, quotient, Vector128<float>.Zero);

            }

            private static int Lane(bool on) => on ? -1 : 0;

            /// <summary>The segment's start, x, y and z in the first three lanes.</summary>
            public Vector128<float> Origin => origin;

            /// <summary>The reciprocal of the segment's length along each axis, zero on a flat one.</summary>
            public Vector128<float> Inverse => inverse;

            /// <summary>All bits set in the lane of each axis the segment moves along.</summary>
            public Vector128<float> Slabs => slabs;

            /// <summary>All bits set in the lane of each flat axis.</summary>
            public Vector128<float> Flat => flat;

            /// <summary>Whether any axis is flat.</summary>
            public bool AnyFlat => anyFlat;

            /// <summary>
            /// A node's mins, in the first three lanes; the fourth holds whatever follows them in the
            /// node and is never read. The node is laid out mins, maxs, then three ints, so both this
            /// and <see cref="MaxsOf"/> load sixteen bytes that lie wholly inside it.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<float> MinsOf(in BvhNode node)
                => Vector128.LoadUnsafe(ref Unsafe.As<BspFile.Vector3, float>(ref Unsafe.AsRef(in node.Mins)));

            /// <inheritdoc cref="MinsOf"/>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<float> MaxsOf(in BvhNode node)
                => Vector128.LoadUnsafe(ref Unsafe.As<BspFile.Vector3, float>(ref Unsafe.AsRef(in node.Maxs)));

            /// <summary>Whether the segment passes through a node's box, and where it enters; see <see cref="Hits(Vector128{float}, Vector128{float}, out float)"/>.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Hits(in BvhNode node, out float enter) => Hits(MinsOf(in node), MaxsOf(in node), out enter);

            /// <summary>
            /// Whether the segment passes through the box, and the fraction along it where it enters.
            /// Runs out to the same far end the triangle test does, or a flat node lying on the end point
            /// could be culled by a rounding error before its triangles were asked. Only the first three
            /// lanes of the bounds are read.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Hits(Vector128<float> mins, Vector128<float> maxs, out float enter)
            {
                if (anyFlat)
                {
                    // The start must lie inside the slab on every axis the segment does not move along -
                    // asked as the old test asked it, and first, as the old test did for a segment down a
                    // column, which most boxes miss on the first comparison.
                    var inside = Vector128.GreaterThanOrEqual(origin, mins) & Vector128.LessThanOrEqual(origin, maxs);
                    if ((flat & ~inside).ExtractMostSignificantBits() != 0)
                    {
                        enter = 0f;
                        return false;
                    }
                }

                var t0 = (mins - origin) * inverse;
                var t1 = (maxs - origin) * inverse;

                var entry = Vector128.ConditionalSelect(slabs, Vector128.MinNative(t0, t1), IdleEntry);
                var exit = Vector128.ConditionalSelect(slabs, Vector128.MaxNative(t0, t1), IdleExit);

                // Across the lanes: the latest entry and the earliest exit.
                entry = Vector128.MaxNative(entry, Vector128.Shuffle(entry, Vector128.Create(2, 3, 0, 1)));
                entry = Vector128.MaxNative(entry, Vector128.Shuffle(entry, Vector128.Create(1, 0, 3, 2)));
                exit = Vector128.MinNative(exit, Vector128.Shuffle(exit, Vector128.Create(2, 3, 0, 1)));
                exit = Vector128.MinNative(exit, Vector128.Shuffle(exit, Vector128.Create(1, 0, 3, 2)));

                enter = entry.ToScalar();
                return enter <= exit.ToScalar();
            }
        }

        /// <summary>
        /// The nearest displacement surface the segment crosses, with the triangle's normal.
        ///
        /// A displacement has no single plane to read a normal off - it is a triangulated grid - so the
        /// normal is the face normal of whichever triangle was hit first. That is the true surface
        /// orientation, which is what the walkability and stair tests need.
        ///
        /// A hit counts up to a hair past <paramref name="b"/> (see <see cref="SegmentEpsilon"/>), and
        /// is reported at <paramref name="b"/> itself, so <paramref name="fraction"/> never exceeds one.
        /// </summary>
        public bool TryTraceSurface(BspFile.Vector3 a, BspFile.Vector3 b, int mask,
            out float fraction, out BspFile.Vector3 normal)
        {
            fraction = 1f;
            normal = default;

            if (bvh.Length == 0)
                return false;

            bool found = false;

            // The same box test as Blocks, but not its order: which of two hits at the same distance
            // is reported depends on which leaf is reached first, so the walk keeps the order it has
            // always had - children pushed left then right, untested until they come off.
            var segment = new SegmentBoxes(a, b);

            Span<int> stack = stackalloc int[64];
            int top = 0;
            stack[top++] = 0;

            while (top > 0)
            {
                int index = stack[--top];
                ref readonly var node = ref bvh[index];

                if (!segment.Hits(in node, out _))
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

                // Nothing found yet bounds nothing: the hit test itself keeps to the segment's own
                // stretch, which runs past one.
                int nearest = NearestInLeaf(a, b, mask, node.First, node.Count,
                    found ? fraction : float.PositiveInfinity, out float hit);

                if (nearest >= 0)
                {
                    fraction = hit;
                    normal = TriangleNormal(vertices[nearest * 3], vertices[nearest * 3 + 1],
                        vertices[nearest * 3 + 2]);
                    found = true;
                }
            }

            // Clamped only once the nearest has been chosen, so two hits in the last epsilon still
            // compete on where they really are.
            if (found && fraction > 1f)
                fraction = 1f;

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
            var nearEnd = Vector256.Create(SegmentEpsilon);
            var farEnd = Vector256.Create(SegmentEnd);
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

                // The same bounds the scalar version applies: the segment's own half-open stretch,
                // see SegmentEpsilon.
                live2 &= Vector256.GreaterThan(at, nearEnd).AsSingle();
                live2 &= Vector256.LessThanOrEqual(at, farEnd).AsSingle();

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
        ///
        /// A box overlapping a triangle at the start is <paramref name="startSolid"/>, and is also a hit
        /// at fraction zero - with one exception. When the triangle is a face of a closed solid (see
        /// <see cref="Build"/>) and the box is moving out through it, the face is not a hit: the box is
        /// leaving the solid, not entering it, which is how a brush answers a box dragged out of it. The
        /// graze rule in <see cref="Traversability.CanStep"/> depends on that answer - a sample on a face
        /// stepping away from it - and against a prop it used to get a hit at fraction zero, which
        /// refused every such step. Terrain has no outside and keeps the hit.
        ///
        /// A box wholly inside a closed solid overlaps none of its triangles, so this does not report
        /// it; <see cref="StaticProps.TryTraceHull"/> does.
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
            // No thinner than MinHalfExtent along any axis; see there.
            var extent = new BspFile.Vector3(MathF.Max((maxs.X - mins.X) / 2f, MinHalfExtent),
                MathF.Max((maxs.Y - mins.Y) / 2f, MinHalfExtent), MathF.Max((maxs.Z - mins.Z) / 2f, MinHalfExtent));

            var from = new BspFile.Vector3(a.X + centre.X, a.Y + centre.Y, a.Z + centre.Z);
            var to = new BspFile.Vector3(b.X + centre.X, b.Y + centre.Y, b.Z + centre.Z);
            var delta = new BspFile.Vector3(b.X - a.X, b.Y - a.Y, b.Z - a.Z);

            bool found = false;

            // Visited in the order it always has been, as in TryTraceSurface: the nearest hit wins,
            // and a tie goes to whichever triangle was reached first.
            var segment = new SegmentBoxes(from, to);
            var reach = Vector128.Create(extent.X, extent.Y, extent.Z, 0f);

            Span<int> stack = stackalloc int[64];
            int top = 0;
            stack[top++] = 0;

            while (top > 0)
            {
                int index = stack[--top];
                ref readonly var node = ref bvh[index];

                // The node's box grown by the moving box's half-extents: a swept box clips a node the
                // traced centre line can miss entirely. The same subtraction and addition per axis as
                // ever, three lanes at a time.
                if (!segment.Hits(SegmentBoxes.MinsOf(in node) - reach, SegmentBoxes.MaxsOf(in node) + reach, out _))
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
                    {
                        startSolid = true;

                        // Dragged out through a face of a solid it starts across: leaving, not a hit.
                        if (outward is not null && Dot(outward[i], delta) > 0f)
                            continue;
                    }

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
        /// The least half-depth a swept box is given along each axis.
        ///
        /// A box with no depth along the direction it moves - the flat plates the body tests sweep up
        /// and down, 32 units square and no units tall - passes a triangle parallel to it in an
        /// instant: every axis the separating-axis test projects onto along that direction gives the
        /// same zero-width slab, entered and left at the same fraction. Each axis computes that
        /// fraction with its own rounding, and whenever the latest entry came out a hair after the
        /// earliest exit the plate went straight through. Whether it did depends on the coordinates,
        /// and against some faces it always did: a plate rising under a prop slab placed off the
        /// origin missed its underside at every one of 1,386 points tried, and one dropping onto it
        /// missed its top. On the benchmark maps it let crouching bodies rise into props on the two RP
        /// maps, and resting bodies drop through the top of the prop they stood on. Given a hundredth
        /// of a unit, the slabs have width enough to outlast the rounding - 12,000 random plates and
        /// prop slabs out to 30,000 units from the origin, none missed - and nothing a body test asks
        /// about is that thin. Terrain shares the sweep and gets the same.
        /// </summary>
        private const float MinHalfExtent = 0.01f;

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

        /// <summary>
        /// Moller-Trumbore, bounded to the segment rather than an infinite ray - to the half-open
        /// stretch of it described on <see cref="SegmentEpsilon"/>, so a hit on the end point counts.
        /// </summary>
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
            if (t is <= SegmentEpsilon or > SegmentEnd)
                return false;

            fraction = t;
            return true;
        }

    }
}