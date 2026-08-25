using System;
using System.Collections.Generic;
using System.IO;

namespace Meshwright
{
    /// <summary>
    /// Reads the BSP's precomputed potentially-visible-set and the tree needed to look up which leaf
    /// cluster a point falls in.
    ///
    /// This is the key to making the visibility pass tractable. Computing area-to-area visibility
    /// naively is O(n^2) raycasts - 10.3 million pairs on rp_downtown_tits_v25's analysed mesh. But
    /// vbsp has already solved the hard part: if two leaf clusters cannot see each other, no point in
    /// one can possibly see any point in the other, so the pair can be rejected without tracing
    /// anything. On a dense urban map most of the map cannot see most of the map, so this should
    /// eliminate the large majority of pairs before a single ray is cast.
    /// </summary>
    public sealed class BspVisibility
    {
        private const int LumpVisibility = 4;
        private const int LumpNodes = 5;
        private const int LumpLeafs = 10;
        private const int LumpLeafBrushes = 17; // 16 is leaffaces; they are adjacent and easy to swap

        public int ClusterCount { get; private set; }

        private Node[] nodes = [];
        private Leaf[] leafs = [];
        private BspFile.Plane[] planes = [];

        /// <summary>Decompressed PVS, one bitfield per cluster. Null when the map has no vis data.</summary>
        private byte[][]? pvs;

        public bool HasVisibilityData => pvs is not null;

        public struct Node
        {
            public const int SizeOf = 32;

            public int PlaneNum;
            public int Child0, Child1; // negative means -(leaf index) - 1

            public static Node Read(BinaryReader r)
            {
                var n = new Node
                {
                    PlaneNum = r.ReadInt32(),
                    Child0 = r.ReadInt32(),
                    Child1 = r.ReadInt32(),
                };
                r.BaseStream.Seek(6 + 6 + 2 + 2 + 2 + 2, SeekOrigin.Current); // mins/maxs/faces/area/padding
                return n;
            }
        }

        public struct Leaf
        {
            /// <summary>BSP v20 with leaf lump version 1. Version 0 carries an extra light cube.</summary>
            public const int SizeOf = 32;
            public const int SizeOfWithLightCube = 56;

            public int Contents;
            public short Cluster;
            public ushort FirstLeafBrush;
            public ushort NumLeafBrushes;

            public static Leaf Read(BinaryReader r, bool hasLightCube)
            {
                var leaf = new Leaf
                {
                    Contents = r.ReadInt32(),
                    Cluster = r.ReadInt16(),
                };

                r.BaseStream.Seek(2 + 6 + 6 + 2 + 2, SeekOrigin.Current); // area/flags, mins, maxs, leaf faces

                leaf.FirstLeafBrush = r.ReadUInt16();
                leaf.NumLeafBrushes = r.ReadUInt16();

                r.BaseStream.Seek(2, SeekOrigin.Current); // leafWaterDataID

                if (hasLightCube)
                    r.BaseStream.Seek(24, SeekOrigin.Current); // CompressedLightCube, lump version 0 only

                // trailing padding short - omitting it walks every leaf after the first off by two
                // bytes, which reads plausible-looking but entirely wrong contents and clusters
                r.BaseStream.Seek(2, SeekOrigin.Current);

                return leaf;
            }
        }

        public static BspVisibility Load(string path, BspFile bsp)
        {
            var vis = new BspVisibility { planes = bsp.Planes };

            using var stream = File.OpenRead(path);
            using var r = new BinaryReader(stream);

            // re-read the header to get lump offsets for the lumps BspFile does not expose
            r.BaseStream.Seek(0, SeekOrigin.Begin);
            r.ReadInt32(); // ident
            r.ReadInt32(); // version

            var offsets = new (int Offset, int Length, int Version)[BspFile.HeaderLumps];
            for (int i = 0; i < BspFile.HeaderLumps; i++)
            {
                offsets[i] = (r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
                r.ReadInt32();
            }

            vis.ReadNodes(r, offsets[LumpNodes]);
            vis.ReadLeafs(r, offsets[LumpLeafs]);
            vis.ReadVisibility(r, offsets[LumpVisibility]);
            vis.ReadLeafBrushes(r, offsets[LumpLeafBrushes], bsp);

            return vis;
        }

        /// <summary>
        /// Per-leaf lists of the brushes that block sight, or null where a leaf has none.
        ///
        /// This is what makes detail geometry visible to the tracer. vbsp deliberately keeps func_detail
        /// brushes out of the BSP splitting planes - that is the entire point of func_detail, it stops
        /// clutter from exploding the vis tree - so they appear only in leaf brush lists. A trace that
        /// descends nodes and stops there looks straight through every detail brush in the map. Checked
        /// against the engine on gm_construct, that accounted for 11 of 38 sampled disagreements.
        /// </summary>
        private int[]?[] leafBrushes = [];

        private BspFile.Brush[] brushes = [];
        private BspFile.BrushSide[] brushSides = [];

        private void ReadLeafBrushes(BinaryReader r, (int Offset, int Length, int Version) lump, BspFile bsp)
        {
            brushes = bsp.Brushes;
            brushSides = bsp.BrushSides;

            if (lump.Length <= 0 || leafs.Length == 0 || brushes.Length == 0)
                return;

            var bytes = LzmaLump.Read(r, lump.Offset, lump.Length);
            var map = new ushort[bytes.Length / 2];

            using (var ms = new MemoryStream(bytes))
            using (var lr = new BinaryReader(ms))
            {
                for (int i = 0; i < map.Length; i++)
                    map[i] = lr.ReadUInt16();
            }

            leafBrushes = new int[leafs.Length][];

            var scratch = new List<int>();
            for (int i = 0; i < leafs.Length; i++)
            {
                var leaf = leafs[i];
                scratch.Clear();

                for (int k = 0; k < leaf.NumLeafBrushes; k++)
                {
                    int index = leaf.FirstLeafBrush + k;
                    if (index < 0 || index >= map.Length)
                        continue;

                    int brush = map[index];
                    if (brush < 0 || brush >= brushes.Length)
                        continue;

                    // Every brush either trace purpose could care about lives in one list, and gets
                    // filtered down to the mask that actually matters at the point each consumer reads
                    // it: sight-blocking callers re-check MaskBlockLos, generation/movement callers
                    // re-check GenerationMask. A leaf whose only brush matches one but not the other
                    // would otherwise never even reach the right check.
                    if ((brushes[brush].Contents & (MaskBlockLos | GenerationMask)) != 0)
                        scratch.Add(brush);
                }

                if (scratch.Count == 0)
                    continue;

                leafBrushes[i] = scratch.ToArray();
                LeavesWithBlockingBrushes++;
            }
        }

        private void ReadNodes(BinaryReader r, (int Offset, int Length, int Version) lump)
        {
            if (lump.Length <= 0) return;

            var bytes = LzmaLump.Read(r, lump.Offset, lump.Length);
            nodes = new Node[bytes.Length / Node.SizeOf];

            using var ms = new MemoryStream(bytes);
            using var lr = new BinaryReader(ms);
            for (int i = 0; i < nodes.Length; i++)
                nodes[i] = Node.Read(lr);
        }

        private void ReadLeafs(BinaryReader r, (int Offset, int Length, int Version) lump)
        {
            if (lump.Length <= 0) return;

            var bytes = LzmaLump.Read(r, lump.Offset, lump.Length);

            // lump version 0 predates moving the light cube out of the leaf struct
            bool hasLightCube = lump.Version == 0;
            int size = hasLightCube ? Leaf.SizeOfWithLightCube : Leaf.SizeOf;

            if (bytes.Length % size != 0 && bytes.Length % Leaf.SizeOf == 0)
            {
                hasLightCube = false;
                size = Leaf.SizeOf;
            }

            leafs = new Leaf[bytes.Length / size];

            using var ms = new MemoryStream(bytes);
            using var lr = new BinaryReader(ms);
            for (int i = 0; i < leafs.Length; i++)
                leafs[i] = Leaf.Read(lr, hasLightCube);
        }

        private void ReadVisibility(BinaryReader r, (int Offset, int Length, int Version) lump)
        {
            if (lump.Length <= 0) return;

            var raw = LzmaLump.Read(r, lump.Offset, lump.Length);

            using var ms = new MemoryStream(raw);
            using var vr = new BinaryReader(ms);

            ClusterCount = vr.ReadInt32();
            if (ClusterCount <= 0) return;

            // per cluster: byte offset of its PVS, then of its PAS
            var pvsOffsets = new int[ClusterCount];
            for (int i = 0; i < ClusterCount; i++)
            {
                pvsOffsets[i] = vr.ReadInt32();
                vr.ReadInt32(); // PAS, not needed for line of sight
            }

            int rowBytes = (ClusterCount + 7) / 8;
            var rows = new byte[ClusterCount][];

            // Every row expands from its own offset into its own array, reading a shared buffer that
            // nothing writes to - so this is parallel without qualification. It is also the single
            // largest piece of BSP loading on a map with real vis data, which is every map worth
            // running this on.
            System.Threading.Tasks.Parallel.For(0, ClusterCount, NavConcurrency.Options,
                i => rows[i] = Decompress(raw, pvsOffsets[i], rowBytes));

            pvs = rows;
        }

        /// <summary>
        /// Expands Valve's run-length encoding: a zero byte is followed by a count of zero bytes to
        /// emit, anything else is literal.
        /// </summary>
        private static byte[] Decompress(byte[] source, int offset, int rowBytes)
        {
            var row = new byte[rowBytes];
            int outPos = 0;
            int inPos = offset;

            while (outPos < rowBytes && inPos < source.Length)
            {
                byte b = source[inPos++];
                if (b != 0)
                {
                    row[outPos++] = b;
                    continue;
                }

                if (inPos >= source.Length) break;

                int run = source[inPos++];
                while (run-- > 0 && outPos < rowBytes)
                    row[outPos++] = 0;
            }

            return row;
        }

        /// <summary>
        /// Cluster for a nav area's surface point, sampled at several heights.
        ///
        /// A single sample fails often: nav area corner heights sit on the floor surface, and a fixed
        /// offset can still land inside floor geometry or, on a thin ledge, punch through into the void.
        /// Sampling upward and taking the first solid answer dropped the unmapped rate substantially.
        /// Unmapped areas are treated as visible-to-everything, so they silently defeat the prefilter.
        /// </summary>
        public short GetClusterAboveSurface(BspFile.Vector3 surfacePoint)
        {
            foreach (float height in (ReadOnlySpan<float>)[8f, 24f, 40f, 64f, 4f])
            {
                short cluster = GetCluster(new BspFile.Vector3(
                    surfacePoint.X, surfacePoint.Y, surfacePoint.Z + height));

                if (cluster >= 0)
                    return cluster;
            }

            return -1;
        }

        /// <summary>
        /// The distinct clusters covering a set of sample points, with unresolvable points dropped.
        ///
        /// One sample per area is not enough: measured on gm_construct and rp_downtown_meowy, centre-only
        /// sampling left 28-31% of areas with no cluster at all, and an unmapped area defeats the filter
        /// for its entire row. Areas are quads that routinely straddle several leaves, so the honest
        /// model is a set of clusters per area, not one.
        /// </summary>
        public short[] GetClusters(ReadOnlySpan<BspFile.Vector3> surfacePoints)
        {
            Span<short> found = stackalloc short[16];
            int count = 0;

            foreach (var point in surfacePoints)
            {
                short cluster = GetClusterAboveSurface(point);
                if (cluster < 0) continue;

                bool seen = false;
                for (int i = 0; i < count; i++)
                {
                    if (found[i] != cluster) continue;
                    seen = true;
                    break;
                }

                if (!seen && count < found.Length)
                    found[count++] = cluster;
            }

            return count == 0 ? [] : found[..count].ToArray();
        }

        /// <summary>
        /// Bitwise OR of the PVS rows of several clusters - everything any of them can see. Lets a
        /// multi-cluster area be tested against another area with one bit test per target cluster
        /// instead of a full cross product. Null when there is no vis data or no cluster resolved.
        /// </summary>
        /// <summary>
        /// The union of the PVS rows for a set of clusters.
        ///
        /// Allocates a row per call and is called once per area while the visibility filter is built,
        /// which looks worth memoising on the cluster set: neighbouring areas share one constantly, and
        /// on a large map this is about twenty megabytes of short-lived arrays.
        ///
        /// Measured before writing it, and it is not worth having. Building the filter and running the
        /// whole pair funnel over a 19,577-area map takes around 300ms, against 162 seconds for the
        /// trace those pairs feed. The entire stage is 0.2% of the pass and this is a fraction of that.
        /// </summary>
        public byte[]? MergeVisible(short[] clusters)
        {
            if (pvs is null || clusters.Length == 0)
                return null;

            int rowBytes = (ClusterCount + 7) / 8;
            var merged = new byte[rowBytes];

            foreach (short cluster in clusters)
            {
                if (cluster < 0 || cluster >= ClusterCount) return null; // unknown cluster: assume all
                var row = pvs[cluster];
                for (int i = 0; i < rowBytes; i++)
                    merged[i] |= row[i];
            }

            return merged;
        }

        /// <summary>
        /// The PVS row for a single cluster, without copying it.
        ///
        /// <see cref="MergeVisible"/> allocates, which is right when several clusters have to be ORed
        /// together but wasteful for the common case of asking what one point can see. Callers must
        /// treat the result as read-only; it is the live row.
        ///
        /// Null when there is no vis data or the cluster is unknown, which every consumer already
        /// reads as "assume everything is visible".
        /// </summary>
        public byte[]? VisibleFrom(short cluster)
            => pvs is null || cluster < 0 || cluster >= ClusterCount ? null : pvs[cluster];

        /// <summary>Whether a merged row sees any of the given clusters. A null row means "assume yes".</summary>
        public bool SeesAny(byte[]? mergedRow, short[] clusters)
        {
            if (mergedRow is null || clusters.Length == 0)
                return true;

            foreach (short cluster in clusters)
            {
                if (cluster < 0 || cluster >= ClusterCount)
                    return true;

                if ((mergedRow[cluster >> 3] & (1 << (cluster & 7))) != 0)
                    return true;
            }

            return false;
        }

        public int NodeCount => nodes.Length;
        public int LeafCount => leafs.Length;
        public int LeavesWithBlockingBrushes { get; private set; }

        /// <summary>
        /// Source's MASK_BLOCKLOS: CONTENTS_SOLID | CONTENTS_BLOCKLOS | CONTENTS_MOVEABLE. The same mask
        /// the engine uses when deciding whether one nav area can see another.
        /// </summary>
        public const int MaskBlockLos = 0x1 | 0x40 | 0x4000;

        /// <summary>
        /// CONTENTS_GRATE, which the SDK describes as letting bullets and sight through while stopping
        /// anything solid. A player is a solid. Left out of <see cref="MaskBlockLos"/> on purpose -
        /// that mask is for the visibility pass, and a bot correctly sees through a grate - but a grate
        /// is real ground to stand on, and nothing that only consults MaskBlockLos can tell the two
        /// apart. Without this, a vent grate over open air reads as open air itself: the floor-finder
        /// walks straight through it looking for the next real surface, and everywhere below the grate -
        /// a sewer, a basement - gets treated as directly connected to whatever is above it.
        /// </summary>
        private const int ContentsGrate = 0x8;

        /// <summary>
        /// Source's own <c>MASK_NPCSOLID_BRUSHONLY</c> - CONTENTS_SOLID | CONTENTS_MOVEABLE |
        /// CONTENTS_WINDOW | CONTENTS_MONSTERCLIP | CONTENTS_GRATE - read directly from
        /// <c>CNavMesh::GetGenerationTraceMask()</c> in the public source, which returns exactly this.
        /// Not something inferred from a symptom: everywhere nav_generate.cpp decides what ground is or
        /// is not solid - <c>GetGroundHeight</c>, the generation hull traces, all of it - it traces
        /// against this mask, never <see cref="MaskBlockLos"/>. The two exist for different questions -
        /// can a bot see past this, and can a bot's body pass through this - and this codebase had only
        /// ever asked the first one, everywhere, including for movement.
        /// </summary>
        public const int GenerationMask = 0x1 | 0x4000 | 0x2 | 0x20000 | ContentsGrate;

        /// <summary>CONTENTS_MONSTERCLIP - an invisible brush that stops NPCs and nothing else.</summary>
        private const int ContentsMonsterClip = 0x20000;

        /// <summary>
        /// What counts as ground the mesh may be built on: <see cref="GenerationMask"/> without
        /// CONTENTS_MONSTERCLIP.
        ///
        /// The distinction the single mask was missing is between "can a body pass through this" and
        /// "can a body stand on this". Monsterclip answers yes to the first and no to the second: it is
        /// an invisible barrier a mapper puts up to keep NPCs out of somewhere, and it belongs in every
        /// obstruction test for exactly that reason - an area must not grow through one. It does not
        /// follow that its top face is a floor, and treating it as one builds the mesh on scenery that
        /// is not there.
        ///
        /// Measured on gm_construct, where the map is ringed with NPC clip: the floor finder was
        /// returning clip-brush tops as surfaces, and the flood then spread across them and built a
        /// whole grid of areas at z=240 with nothing underneath until the real ground 384 units below.
        /// Every one of them looks like solid mesh hanging in mid-air in game, and the engine's own mesh
        /// for the map has no areas there at all.
        /// </summary>
        public const int GroundMask = GenerationMask & ~ContentsMonsterClip;

        /// <summary>Brush entities that block sight. Null until <see cref="AttachModels"/> is called.</summary>
        private BspModels? entityModels;

        /// <summary>Displacement surfaces. Null until <see cref="AttachDisplacements"/> is called.</summary>
        private BspDisplacements? displacements;

        /// <summary>Static prop collision. Null until <see cref="AttachStaticProps"/> is called.</summary>
        private StaticProps? staticProps;

        public void AttachModels(BspModels models) => entityModels = models;

        public void AttachDisplacements(BspDisplacements disp) => displacements = disp;

        public void AttachStaticProps(StaticProps props) => staticProps = props;

        public int BlockingModelCount => entityModels?.BlockingModelCount ?? 0;
        public int DisplacementTriangleCount => displacements?.TriangleCount ?? 0;
        public int StaticPropTriangleCount => staticProps?.TriangleCount ?? 0;

        /// <summary>
        /// Whether a line between two points is unobstructed.
        ///
        /// This walks the same BSP tree used for cluster lookup rather than testing brushes: the tree
        /// already partitions space, so a point trace never has to intersect a single brush face. Only
        /// the near half of a split recurses; the far half continues in the loop, so stack depth is
        /// bounded by tree depth rather than by the number of splits crossed.
        ///
        /// Brush entities are only consulted once the world says the line is clear. That ordering is
        /// what makes them affordable: the overwhelming majority of traced rays are stopped by world
        /// geometry and never reach the second stage at all.
        ///
        /// Displacements are traced last and against their real triangulated surface, so terrain that
        /// bulges above the brush face it was built from stops a ray properly. They come last for the
        /// same affordability reason as brush entities - by that point the ray has already survived
        /// every piece of world and entity geometry in its path.
        ///
        /// Static props are not modelled: nothing here reads the static prop lump, so a prop that
        /// blocks a doorway in game is invisible to this.
        /// </summary>
        public bool IsLineClear(BspFile.Vector3 a, BspFile.Vector3 b) => IsLineClear(a, b, MaskBlockLos);

        /// <summary>
        /// Same query, against a caller-chosen content mask. Pass <see cref="GenerationMask"/> for
        /// anything asking whether a player's body can occupy or pass through this space - headroom,
        /// clearance between two areas, ladder reachability - rather than the sight-only default, which
        /// answers a different question and should stay reserved for it. A grate correctly fails the
        /// default (a bot sees through it) and correctly passes this one (a player cannot).
        /// </summary>
        public bool IsLineClear(BspFile.Vector3 a, BspFile.Vector3 b, int mask)
            => IsLineClear(a, b, mask, 0);

        /// <summary>
        /// CONTENTS_LADDER. Not part of <see cref="GenerationMask"/> - Valve leaves it out of
        /// MASK_NPCSOLID_BRUSHONLY too - but a ladder brush often carries other contents alongside it
        /// that are, and then the ladder blocks traces on its own account. rp_downtown_meowy's fire
        /// escapes are built from grate-textured ladder brushes (LADDER|DETAIL|TRANSLUCENT|GRATE), and a
        /// ladder's own base point is by construction *inside* that brush, so every reachability probe
        /// from the ladder to the floor beside it started solid and reported a wall. Pass this as
        /// <c>ignoreContents</c> when asking a question on a climber's behalf.
        /// </summary>
        public const int ContentsLadder = 0x20000000;

        /// <summary>
        /// Same query, ignoring brushes carrying any of <paramref name="ignoreContents"/> whatever else
        /// they are made of. For asking whether something obstructs a climber who is, right now, stood
        /// on the very brush that would otherwise be the obstruction.
        /// </summary>
        public bool IsLineClear(BspFile.Vector3 a, BspFile.Vector3 b, int mask, int ignoreContents)
        {
            if (nodes.Length == 0 || leafs.Length == 0)
                return true;

            if (Blocked(0, a, b, mask, a, b, ignoreContents))
                return false;

            if (entityModels is null || entityModels.BlockingModelCount == 0)
                return true;

            int gathered = Math.Min(entityModels.BlockingModelCount, GatherBuffer);

            Span<int> heads = stackalloc int[gathered];

            Span<BspFile.Vector3> origins = stackalloc BspFile.Vector3[gathered];
            int count = entityModels.Gather(a, b, heads, origins, out bool overflowed);

            if (overflowed)
            {
                var all = GatherAll(a, b);
                heads = all.Heads;
                origins = all.Origins;
                count = all.Count;
            }

            for (int i = 0; i < count; i++)
            {
                var o = origins[i];
                if (Blocked(heads[i], Shift(a, o), Shift(b, o), mask,
                        Shift(a, o), Shift(b, o), ignoreContents))
                    return false;
            }

            if (displacements is not null && displacements.Blocks(a, b, mask)) return false;

            return staticProps is null || !staticProps.Blocks(a, b, mask);
        }

        /// <summary>Moves a world point into a model's local space.</summary>
        private static BspFile.Vector3 Shift(BspFile.Vector3 p, BspFile.Vector3 origin)
            => new(p.X - origin.X, p.Y - origin.Y, p.Z - origin.Z);

        /// <summary>
        /// Ceiling on the stack-allocated capacity a trace gathers brush entities into before it has to
        /// fall back to the heap. Comfortably above what an ordinary ray meets, so the slow path stays
        /// rare.
        ///
        /// A ceiling, not the size actually taken. <c>stackalloc</c> is zero-initialised, so reserving
        /// the ceiling on every call means memset-ing it on every call - and these buffers sit on the
        /// hottest path in the program, one pair per ray. Reserved flat at this size, zeroing 2KB of
        /// stack per ray came to 43% of total runtime on gm_construct, comfortably more than the ray
        /// tracing it was there to support. Every call site therefore takes
        /// <c>min(BlockingModelCount, GatherBuffer)</c>: a segment cannot touch more brush entities
        /// than the map contains, so on a map with twelve of them this reserves twelve slots rather
        /// than a hundred and twenty-eight.
        /// </summary>
        private const int GatherBuffer = 128;

        /// <summary>
        /// Re-gathers into heap arrays sized to the whole model set, for the rays that overflow
        /// <see cref="GatherBuffer"/>.
        ///
        /// Sized to <c>BlockingModelCount</c> rather than doubled-and-retried because that is the hard
        /// ceiling - a segment cannot touch more models than exist - so this is guaranteed to be the
        /// last attempt rather than the first of several.
        /// </summary>
        private (int[] Heads, BspFile.Vector3[] Origins, int Count) GatherAll(
            BspFile.Vector3 a, BspFile.Vector3 b)
        {
            int capacity = entityModels!.BlockingModelCount;
            var heads = new int[capacity];
            var origins = new BspFile.Vector3[capacity];

            return (heads, origins, entityModels.Gather(a, b, heads, origins, out _));
        }

        /// <summary>
        /// The first surface a segment meets, with its outward normal.
        ///
        /// This is what separates a mesh generator from a set of heuristics. Valve reads the normal off
        /// the traced surface and decides everything from it - walkable is `normal.z >= 0.7`, stairs is
        /// `normal.z > 0.97`, steeper than walkable becomes a jump area. Without it, ground has to be
        /// guessed at by probing neighbouring columns and comparing heights, which is both slower and
        /// wrong at every edge.
        ///
        /// All three geometry classes can supply one: a brush yields the side plane the segment enters
        /// through, world geometry the BSP split plane it crosses into solid, and a displacement the
        /// face normal of the triangle hit.
        /// </summary>
        public bool TryTraceSurface(BspFile.Vector3 a, BspFile.Vector3 b,
            out BspFile.Vector3 point, out BspFile.Vector3 normal)
            => TryTraceSurface(a, b, MaskBlockLos, out point, out normal);

        /// <summary>
        /// Same trace, against a caller-chosen content mask instead of the sight-blocking default.
        /// <see cref="GenerationMask"/> is what everything that decides where a player's body can
        /// actually stand should pass here - floor height, ground normal, clearance - matching what
        /// <c>CNavMesh::GetGroundHeight</c> traces against in the public source. The default overload
        /// stays on <see cref="MaskBlockLos"/> because it is still correct for its own callers: the
        /// visibility pass, and anything else asking what a bot can see rather than where it can walk.
        /// </summary>
        public bool TryTraceSurface(BspFile.Vector3 a, BspFile.Vector3 b, int mask,
            out BspFile.Vector3 point, out BspFile.Vector3 normal)
            => TryTraceSurface(a, b, mask, out point, out normal, out _);

        /// <summary>
        /// Same trace, also reporting whether the surface found was displacement terrain.
        ///
        /// Which geometry class stopped a trace is normally none of the caller's business - ground is
        /// ground. Stairs are the exception, and Valve's <c>IsStairs</c> treats them as one: it
        /// abandons a candidate the moment a probe lands on a displacement, because terrain is never a
        /// staircase however step-like its profile happens to be. Terrain sculpted into ledges is
        /// common, and without this the profile alone cannot tell it from masonry.
        /// </summary>
        public bool TryTraceSurface(BspFile.Vector3 a, BspFile.Vector3 b, int mask,
            out BspFile.Vector3 point, out BspFile.Vector3 normal, out bool onDisplacement)
        {
            point = b;
            normal = new BspFile.Vector3(0, 0, 1);
            onDisplacement = false;

            float best = float.MaxValue;
            bool found = false;

            if (nodes.Length > 0 && leafs.Length > 0)
            {
                var world = new SurfaceHit();
                if (TraceSurface(0, a, b, 0f, 1f, ref world, mask, a, b))
                {
                    best = world.Fraction;
                    normal = world.Normal;
                    found = true;
                }
            }

            if (entityModels is not null)
            {
                int gathered = Math.Min(entityModels.BlockingModelCount, GatherBuffer);
                Span<int> heads = stackalloc int[gathered];
                Span<BspFile.Vector3> origins = stackalloc BspFile.Vector3[gathered];
                int count = entityModels.Gather(a, b, heads, origins, out bool overflowed);

                if (overflowed)
                {
                    var all = GatherAll(a, b);
                    heads = all.Heads;
                    origins = all.Origins;
                    count = all.Count;
                }

                for (int i = 0; i < count; i++)
                {
                    var o = origins[i];
                    var entity = new SurfaceHit();

                    if (!TraceSurface(heads[i], Shift(a, o), Shift(b, o), 0f, 1f, ref entity, mask,
                            Shift(a, o), Shift(b, o)))
                        continue;

                    if (found && entity.Fraction >= best)
                        continue;

                    best = entity.Fraction;
                    normal = entity.Normal;
                    found = true;
                    onDisplacement = false;
                }
            }

            if (displacements is not null &&
                displacements.TryTraceSurface(a, b, mask, out float dispFraction, out var dispNormal) &&
                (!found || dispFraction < best))
            {
                best = dispFraction;
                normal = dispNormal;
                found = true;
                onDisplacement = true;
            }

            // Props come last so that a prop standing on terrain wins the tie only when it is genuinely
            // nearer. `onDisplacement` is cleared when one does, because the callers that ask - stair
            // rejection above all - are asking about terrain specifically, and a prop is not terrain.
            if (staticProps is not null &&
                staticProps.TryTraceSurface(a, b, mask, out float propFraction, out var propNormal) &&
                (!found || propFraction < best))
            {
                best = propFraction;
                normal = propNormal;
                found = true;
                onDisplacement = false;
            }

            if (!found)
                return false;

            point = new BspFile.Vector3(
                a.X + (b.X - a.X) * best,
                a.Y + (b.Y - a.Y) * best,
                a.Z + (b.Z - a.Z) * best);

            return true;
        }

        /// <summary>
        /// Half of Valve's own <c>HumanHeight</c> - the exact constant name and value
        /// <c>CNavMesh::GetGroundHeight</c> uses in the public source, 35.5, not 36.
        /// </summary>
        internal const float HalfHumanHeight = 35.5f;

        /// <summary>
        /// Valve's <c>NavTraceMins</c>/<c>NavTraceMaxs</c>, the box every generation trace in
        /// nav_generate.cpp sweeps: 0.9 units square and <c>HumanCrouchHeight</c> tall, sitting on the
        /// trace point rather than centred on it.
        ///
        /// The width is almost nothing on purpose - this is not the player's shoulders. What the box is
        /// really for is the height: sweeping it proves the whole 0..55 span stays clear along the path,
        /// where testing a couple of separate lines only proves those two heights are clear and lets a
        /// bot walk through anything that happens to sit between them.
        /// </summary>
        public static readonly BspFile.Vector3 NavTraceMins = new(-0.45f, -0.45f, 0f);

        /// <inheritdoc cref="NavTraceMins"/>
        public static readonly BspFile.Vector3 NavTraceMaxs =
            new(0.45f, 0.45f, NavConstants.HumanCrouchHeight);

        /// <summary>Source's <c>DIST_EPSILON</c>, the nudge that keeps a sweep off the surface it stops on.</summary>
        private const float DistEpsilon = 0.03125f;

        /// <summary>
        /// Sweeps an axis-aligned box along a segment - the equivalent of <c>UTIL_TraceHull</c>, which
        /// is what every one of Valve's generation traces actually is.
        ///
        /// The brush test is Quake's <c>CM_ClipBoxToBrush</c>: each of the brush's planes is pushed
        /// outward by the box's support point along that plane's normal, which turns sweeping a box
        /// against the brush back into sweeping a point against a slightly larger one. Tree descent is
        /// widened by the same amount so no leaf the box could clip gets skipped.
        ///
        /// Covers all three geometry classes: world brushes, brush entities, and - since
        /// <see cref="BspDisplacements.TryTraceHull"/> was written - displacement terrain. It did not
        /// always. While the sweep saw brushes only it reported open air over every piece of terrain on
        /// the map, which made it useless as a general clearance test and forced everything that needed
        /// one onto thin line traces; substituting it into the standability check back then took
        /// gm_construct's isolated areas from 46 to 321, by accepting samples under displacement that no
        /// body could fit in.
        /// </summary>
        public bool TryTraceHull(BspFile.Vector3 a, BspFile.Vector3 b,
            BspFile.Vector3 mins, BspFile.Vector3 maxs, int mask,
            out float fraction, out BspFile.Vector3 normal, out bool startSolid)
        {
            fraction = 1f;
            normal = new BspFile.Vector3(0, 0, 1);
            startSolid = false;

            if (nodes.Length == 0 || leafs.Length == 0)
                return false;

            var hit = new SurfaceHit { Fraction = 1f, Normal = normal };
            bool solid = false;
            bool found = HullTrace(0, a, b, 0f, 1f, ref hit, ref solid, mask, a, b, mins, maxs);

            if (entityModels is not null && entityModels.BlockingModelCount > 0)
            {
                int gathered = Math.Min(entityModels.BlockingModelCount, GatherBuffer);
                Span<int> heads = stackalloc int[gathered];
                Span<BspFile.Vector3> origins = stackalloc BspFile.Vector3[gathered];
                int count = entityModels.Gather(a, b, heads, origins, out bool overflowed);

                if (overflowed)
                {
                    var all = GatherAll(a, b);
                    heads = all.Heads;
                    origins = all.Origins;
                    count = all.Count;
                }

                for (int i = 0; i < count; i++)
                {
                    var o = origins[i];
                    var ea = Shift(a, o);
                    var eb = Shift(b, o);
                    var entity = new SurfaceHit { Fraction = 1f, Normal = normal };
                    bool entitySolid = false;

                    if (!HullTrace(heads[i], ea, eb, 0f, 1f, ref entity, ref entitySolid, mask, ea, eb, mins, maxs))
                        continue;

                    solid |= entitySolid;
                    if (!found || entity.Fraction < hit.Fraction)
                    {
                        hit = entity;
                        found = true;
                    }
                }
            }

            // Displacements, which this used not to consult at all. Terrain is most of the ground on a
            // Source map, so without them the sweep reported open air over all of it and could not serve
            // as a general clearance test - which is exactly why every caller that needed one had to
            // fall back to an infinitely thin line and live with what a line misses.
            if (displacements is not null &&
                displacements.TryTraceHull(a, b, mins, maxs, mask,
                    out float dispFraction, out var dispNormal, out bool dispSolid))
            {
                solid |= dispSolid;

                if (!found || dispFraction < hit.Fraction)
                {
                    hit = new SurfaceHit { Fraction = dispFraction, Normal = dispNormal };
                    found = true;
                }
            }

            if (staticProps is not null &&
                staticProps.TryTraceHull(a, b, mins, maxs, mask,
                    out float propFraction, out var propNormal, out bool propSolid))
            {
                solid |= propSolid;

                if (!found || propFraction < hit.Fraction)
                {
                    hit = new SurfaceHit { Fraction = propFraction, Normal = propNormal };
                    found = true;
                }
            }

            startSolid = solid;

            if (!found)
                return false;

            fraction = Math.Clamp(hit.Fraction, 0f, 1f);
            normal = hit.Normal;
            return true;
        }

        /// <summary>Walks the tree, widened by the box, clipping the sweep against each leaf's brushes.</summary>
        private bool HullTrace(int num, BspFile.Vector3 p1, BspFile.Vector3 p2,
            float startFraction, float endFraction, ref SurfaceHit hit, ref bool startSolid, int mask,
            BspFile.Vector3 rayStart, BspFile.Vector3 rayEnd,
            BspFile.Vector3 mins, BspFile.Vector3 maxs)
        {
            while (num >= 0)
            {
                var node = nodes[num];
                if ((uint)node.PlaneNum >= (uint)planes.Length)
                    return false;

                var plane = planes[node.PlaneNum];

                // How far the box can reach either side of the traced point along this plane's normal.
                // Widening the split by it is what stops a leaf the box overlaps - but whose traced
                // point misses - being skipped. Deliberately symmetric and taken from the larger of the
                // two bounds: the signed support used in the brush test below is exact but only tells
                // you about one side, and this box sits *on* its point rather than centred on it, so a
                // one-sided widening silently misses everything above the trace line.
                float offset = MathF.Abs(plane.Normal.X) * MathF.Max(MathF.Abs(mins.X), MathF.Abs(maxs.X))
                             + MathF.Abs(plane.Normal.Y) * MathF.Max(MathF.Abs(mins.Y), MathF.Abs(maxs.Y))
                             + MathF.Abs(plane.Normal.Z) * MathF.Max(MathF.Abs(mins.Z), MathF.Abs(maxs.Z));

                float d1 = plane.Normal.X * p1.X + plane.Normal.Y * p1.Y + plane.Normal.Z * p1.Z - plane.Distance;
                float d2 = plane.Normal.X * p2.X + plane.Normal.Y * p2.Y + plane.Normal.Z * p2.Z - plane.Distance;

                if (d1 >= offset && d2 >= offset) { num = node.Child0; continue; }
                if (d1 < -offset && d2 < -offset) { num = node.Child1; continue; }

                // straddles: take both halves, nearest first
                float t = MathF.Abs(d1 - d2) < 1e-6f ? 0f : d1 / (d1 - d2);
                t = Math.Clamp(t, 0f, 1f);

                float mid = startFraction + (endFraction - startFraction) * t;
                var midPoint = new BspFile.Vector3(
                    p1.X + t * (p2.X - p1.X),
                    p1.Y + t * (p2.Y - p1.Y),
                    p1.Z + t * (p2.Z - p1.Z));

                bool behind = d1 < d2;
                int near = behind ? node.Child1 : node.Child0;
                int far = behind ? node.Child0 : node.Child1;

                if (HullTrace(near, p1, midPoint, startFraction, mid, ref hit, ref startSolid, mask,
                        rayStart, rayEnd, mins, maxs))
                {
                    return true;
                }

                num = far;
                p1 = midPoint;
                startFraction = mid;
            }

            int leafIndex = -num - 1;
            if ((uint)leafIndex >= (uint)leafs.Length)
                return false;

            return ClipHullToLeaf(leafIndex, rayStart, rayEnd, mins, maxs, mask, ref hit, ref startSolid);
        }

        /// <summary>The box's support distance along one axis of a plane normal.</summary>
        private static float Extent(float normal, float min, float max)
            => normal < 0 ? max * normal : min * normal;

        /// <summary>
        /// Quake's <c>CM_ClipBoxToBrush</c>, plane by plane: push each face out by the box's support
        /// point, then clip the segment against the enlarged convex volume.
        /// </summary>
        private bool ClipHullToLeaf(int leafIndex, BspFile.Vector3 p1, BspFile.Vector3 p2,
            BspFile.Vector3 mins, BspFile.Vector3 maxs, int mask,
            ref SurfaceHit hit, ref bool startSolid)
        {
            if ((uint)leafIndex >= (uint)leafBrushes.Length)
                return false;

            var list = leafBrushes[leafIndex];
            if (list is null)
                return false;

            bool found = false;

            foreach (int index in list)
            {
                var brush = brushes[index];
                if ((brush.Contents & mask) == 0)
                    continue;

                float enter = -1f;
                float exit = 1f;
                bool startsOutside = false;
                bool endsOutside = false;
                var enterNormal = new BspFile.Vector3(0, 0, 1);

                for (int i = 0; i < brush.NumSides; i++)
                {
                    int sideIndex = brush.FirstSide + i;
                    if ((uint)sideIndex >= (uint)brushSides.Length) { startsOutside = true; break; }

                    var side = brushSides[sideIndex];
                    if ((uint)side.PlaneNum >= (uint)planes.Length) { startsOutside = true; break; }

                    var plane = planes[side.PlaneNum];

                    // the plane, pushed out to account for the box
                    float dist = plane.Distance
                        - (Extent(plane.Normal.X, mins.X, maxs.X)
                         + Extent(plane.Normal.Y, mins.Y, maxs.Y)
                         + Extent(plane.Normal.Z, mins.Z, maxs.Z));

                    float d1 = plane.Normal.X * p1.X + plane.Normal.Y * p1.Y + plane.Normal.Z * p1.Z - dist;
                    float d2 = plane.Normal.X * p2.X + plane.Normal.Y * p2.Y + plane.Normal.Z * p2.Z - dist;

                    if (d1 > 0) startsOutside = true;
                    if (d2 > 0) endsOutside = true;

                    if (d1 > 0 && d2 > 0) { enter = 2f; break; }
                    if (d1 <= 0 && d2 <= 0) continue;

                    if (d1 > d2)
                    {
                        float f = (d1 - DistEpsilon) / (d1 - d2);
                        if (f > enter) { enter = f; enterNormal = plane.Normal; }
                    }
                    else
                    {
                        float f = (d1 + DistEpsilon) / (d1 - d2);
                        if (f < exit) exit = f;
                    }
                }

                if (!startsOutside)
                {
                    // the box is already inside this brush where the sweep begins
                    startSolid = true;
                    if (!endsOutside)
                        return true;

                    continue;
                }

                if (enter > exit || enter < 0f || enter >= 1f)
                    continue;

                if (!found || enter < hit.Fraction)
                {
                    hit.Fraction = enter;
                    hit.Normal = enterNormal;
                    found = true;
                }
            }

            return found;
        }

        /// <summary>A surface found in a column: how high it is and which way it faces.</summary>
        public readonly record struct FloorSample(float Z, BspFile.Vector3 Normal);

        /// <summary>Whether a point is inside brush contents matching the mask.</summary>
        public bool IsPointSolid(float x, float y, float z, int mask)
        {
            var p = new BspFile.Vector3(x, y, z);
            return !IsLineClear(p, p, mask);
        }

        /// <summary>
        /// Every floor surface in a vertical column, highest first.
        ///
        /// Deliberately not built on <c>CNavMesh::GetGroundHeight</c>, which this used to call in a
        /// loop. Valve has no column enumerator to copy because Valve never needs one: nav_generate's
        /// sampler walks node to node across the floor it is already standing on, so GetGroundHeight is
        /// only ever asked "where is the ground right around here", starting from a position already
        /// known to be close to it. Its retry loop exists to climb *up* out of a surface with too little
        /// headroom - the exact opposite of what enumerating downwards needs. Driving it down a column
        /// re-found the same surface forever: each restart began a few units under the last hit, landed
        /// inside that floor's own slab, and the loop dutifully climbed back out to the top of it. On
        /// rp_downtown_meowy that stopped every column at street level and left the whole sewer beneath
        /// it - 4,716 of the 5,080 areas missing against the in-game mesh - unsampled.
        ///
        /// Walking down instead is straightforward as long as each hit is followed *through* the solid
        /// it landed on rather than merely past its face.
        /// </summary>
        public int EnumerateFloors(float x, float y, float topZ, float bottomZ, Span<FloorSample> into)
        {
            const float SolidStep = 8f;

            int count = 0;
            float cursor = topZ;

            while (count < into.Length && cursor > bottomZ)
            {
                var from = new BspFile.Vector3(x, y, cursor);
                var to = new BspFile.Vector3(x, y, bottomZ);

                if (!TryTraceSurface(from, to, GroundMask, out var point, out var normal))
                    break;

                into[count++] = new FloorSample(point.Z, normal);

                // Descend through whatever was just landed on. Stepping a fixed amount below the face
                // is not enough on its own - a floor slab is usually thicker than any fixed step worth
                // taking - so keep going while still inside it.
                cursor = point.Z - 0.25f;
                while (cursor > bottomZ && IsPointSolid(x, y, cursor, GroundMask))
                    cursor -= SolidStep;
            }

            return count;
        }

        private struct SurfaceHit
        {
            public float Fraction;
            public BspFile.Vector3 Normal;
        }

        /// <summary>
        /// Walks the tree front to back recording where the segment first enters blocking contents and
        /// which plane it came through. Mirrors <see cref="Blocked"/>, but the near half must recurse
        /// with its own parametric range so the fraction stays meaningful in the original segment.
        /// </summary>
        private bool TraceSurface(int num, BspFile.Vector3 p1, BspFile.Vector3 p2,
            float startFraction, float endFraction, ref SurfaceHit hit, int mask,
            BspFile.Vector3 rayStart, BspFile.Vector3 rayEnd)
        {
            while (num >= 0)
            {
                // By reference, not by value. This is the hottest loop in the program - tens of millions
                // of node visits per map - and reading these as locals copied a 12-byte node and a
                // 20-byte plane on every one of them, to use four floats of the plane and three ints of
                // the node. The arrays are never written during a trace.
                ref readonly var node = ref nodes[num];

                if ((uint)node.PlaneNum >= (uint)planes.Length)
                    return false;

                ref readonly var plane = ref planes[node.PlaneNum];

                float d1 = plane.Normal.X * p1.X + plane.Normal.Y * p1.Y + plane.Normal.Z * p1.Z - plane.Distance;
                float d2 = plane.Normal.X * p2.X + plane.Normal.Y * p2.Y + plane.Normal.Z * p2.Z - plane.Distance;

                if (d1 >= 0 && d2 >= 0) { num = node.Child0; continue; }
                if (d1 < 0 && d2 < 0) { num = node.Child1; continue; }

                float t = d1 / (d1 - d2);
                float mid = startFraction + (endFraction - startFraction) * t;

                var midPoint = new BspFile.Vector3(
                    p1.X + t * (p2.X - p1.X),
                    p1.Y + t * (p2.Y - p1.Y),
                    p1.Z + t * (p2.Z - p1.Z));

                bool behind = d1 < 0;

                if (TraceSurface(behind ? node.Child1 : node.Child0, p1, midPoint, startFraction, mid,
                        ref hit, mask, rayStart, rayEnd))
                    return true;

                // Crossing into the far side: this plane is the surface, oriented back towards the
                // side the segment came from.
                int far = behind ? node.Child0 : node.Child1;
                if (EntersSolid(far, midPoint, mask))
                {
                    // The split plane is where the ray crossed into solid, but it is not necessarily
                    // the face it crossed through: a BSP split plane is chosen to partition space, and
                    // for something like a staircase the tree routinely splits along the flight's
                    // diagonal. Reporting that as the surface normal made every tread read as a 0.7-0.9
                    // slope instead of flat, which is exactly what stopped 14 of the 24 staircases the
                    // engine marks on rp_downtown_meowy from passing the "is this a ramp" test. Ask the
                    // brushes that actually bound the solid first, and only fall back to the split
                    // plane if none of them answer.
                    int solidLeaf = LeafIndexAt(far, midPoint);
                    if (solidLeaf >= 0 && TryHitLeafBrush(solidLeaf, rayStart, rayEnd, mid, ref hit, mask))
                        return true;

                    float sign = behind ? -1f : 1f;
                    hit.Fraction = mid;
                    hit.Normal = new BspFile.Vector3(
                        sign * plane.Normal.X, sign * plane.Normal.Y, sign * plane.Normal.Z);
                    return true;
                }

                num = far;
                p1 = midPoint;
                startFraction = mid;
            }

            int leafIndex = -num - 1;
            if ((uint)leafIndex >= (uint)leafs.Length)
                return false;

            // Brushes first even for a solid leaf: they carry the real face the ray came through, where
            // the leaf alone can only say "solid here" and used to answer with a flat (0,0,1) guess.
            if (TryHitLeafBrush(leafIndex, rayStart, rayEnd, startFraction, ref hit, mask))
                return true;

            if ((leafs[leafIndex].Contents & mask) != 0)
            {
                hit.Fraction = startFraction;
                hit.Normal = new BspFile.Vector3(0, 0, 1);
                return true;
            }

            return false;
        }

        /// <summary>The leaf a point falls in, starting the descent from an arbitrary subtree root.</summary>
        private int LeafIndexAt(int num, BspFile.Vector3 point)
        {
            while (num >= 0)
            {
                var node = nodes[num];
                if ((uint)node.PlaneNum >= (uint)planes.Length)
                    return -1;

                var plane = planes[node.PlaneNum];
                float d = plane.Normal.X * point.X + plane.Normal.Y * point.Y + plane.Normal.Z * point.Z
                          - plane.Distance;

                num = d >= 0 ? node.Child0 : node.Child1;
            }

            int leafIndex = -num - 1;
            return (uint)leafIndex < (uint)leafs.Length ? leafIndex : -1;
        }

        /// <summary>Whether the subtree immediately beyond a crossing is blocking at that point.</summary>
        private bool EntersSolid(int num, BspFile.Vector3 point, int mask)
        {
            while (num >= 0)
            {
                var node = nodes[num];
                if ((uint)node.PlaneNum >= (uint)planes.Length)
                    return false;

                var plane = planes[node.PlaneNum];
                float d = plane.Normal.X * point.X + plane.Normal.Y * point.Y + plane.Normal.Z * point.Z
                          - plane.Distance;

                num = d >= 0 ? node.Child0 : node.Child1;
            }

            int leafIndex = -num - 1;
            return (uint)leafIndex < (uint)leafs.Length && (leafs[leafIndex].Contents & mask) != 0;
        }

        /// <summary>
        /// Clips against a leaf's brushes, keeping the plane the segment enters through.
        ///
        /// Clipped against the whole original ray, not the piece of it inside this leaf, which is what
        /// Valve's own <c>CM_TraceToLeaf</c> passes its brushes (<c>trace_start</c>/<c>trace_end</c>,
        /// the full segment) rather than the sub-segment <c>CM_RecursiveHullCheck</c> is currently
        /// walking. The distinction is not cosmetic. A brush's own face is very often also a BSP split
        /// plane, so the sub-segment handed to the leaf beyond it begins *exactly* on that face - d1
        /// lands on 0, `d1 > 0` is false, `startsOutside` never becomes true, and the brush is thrown
        /// away as "the ray began inside it". Every detail brush whose top face the tree split on was
        /// therefore invisible to this trace: the see-through road grates were found in the leaf lists,
        /// carried CONTENTS_GRATE correctly, and still failed to stop a downward trace, so no floor was
        /// ever sampled on top of one and bots had no mesh to walk across.
        /// </summary>
        private bool TryHitLeafBrush(int leafIndex, BspFile.Vector3 rayStart, BspFile.Vector3 rayEnd,
            float startFraction, ref SurfaceHit hit, int mask)
        {
            if ((uint)leafIndex >= (uint)leafBrushes.Length)
                return false;

            var list = leafBrushes[leafIndex];
            if (list is null)
                return false;

            bool found = false;
            float best = float.MaxValue;
            var bestNormal = new BspFile.Vector3(0, 0, 1);

            foreach (int index in list)
            {
                var brush = brushes[index];
                if ((brush.Contents & mask) == 0)
                    continue;

                float enter = -1f;
                float exit = 1f;
                bool startsOutside = false;
                var enterNormal = new BspFile.Vector3(0, 0, 1);

                for (int i = 0; i < brush.NumSides; i++)
                {
                    int sideIndex = brush.FirstSide + i;
                    if ((uint)sideIndex >= (uint)brushSides.Length) { startsOutside = true; break; }

                    var side = brushSides[sideIndex];
                    if ((uint)side.PlaneNum >= (uint)planes.Length) { startsOutside = true; break; }

                    var plane = planes[side.PlaneNum];
                    float d1 = plane.Normal.X * rayStart.X + plane.Normal.Y * rayStart.Y
                        + plane.Normal.Z * rayStart.Z - plane.Distance;
                    float d2 = plane.Normal.X * rayEnd.X + plane.Normal.Y * rayEnd.Y
                        + plane.Normal.Z * rayEnd.Z - plane.Distance;

                    if (d1 > 0) startsOutside = true;
                    if (d1 > 0 && d2 > 0) { enter = 2f; break; }
                    if (d1 <= 0 && d2 <= 0) continue;

                    float f = d1 / (d1 - d2);

                    if (d1 > d2)
                    {
                        if (f > enter) { enter = f; enterNormal = plane.Normal; }
                    }
                    else if (f < exit)
                    {
                        exit = f;
                    }
                }

                // The ray begins inside this brush. Valve reports that through trace_t.startsolid
                // rather than as a fraction; the equivalent here is a hit at the ray's own start, which
                // is what makes the degenerate `TryTraceSurface(from, from, ...)` probe callers use as a
                // start-solid test able to see a detail brush at all.
                if (!startsOutside)
                {
                    if (found && 0f >= best)
                        continue;

                    best = 0f;
                    bestNormal = new BspFile.Vector3(0, 0, 1);
                    found = true;
                    continue;
                }

                if (enter > exit || enter < 0f || enter >= 1f)
                    continue;

                // Fractions are already in the whole ray's parameter space now that the whole ray is
                // what was clipped. Ignore anything landing behind this leaf: leaves are visited front
                // to back, so a nearer intersection with that same brush would already have been found
                // in an earlier one, and accepting it here would report a hit the ray had passed.
                if (enter < startFraction - 0.0001f)
                    continue;

                if (found && enter >= best)
                    continue;

                best = enter;
                bestNormal = enterNormal;
                found = true;
            }

            if (!found)
                return false;

            hit.Fraction = best;
            hit.Normal = bestNormal;
            return true;
        }

        /// <summary>
        /// Like <see cref="IsLineClear"/>, but reports what stopped the ray. Diagnostic only - the hot
        /// path must not pay for this.
        /// </summary>
        public bool TraceExplain(BspFile.Vector3 a, BspFile.Vector3 b, out int contents, out BspFile.Vector3 hit)
            => TraceExplain(a, b, out contents, out hit, out _);

        /// <summary>
        /// <paramref name="blockingHeadNode"/> is 0 when world geometry stopped the ray and the head node
        /// of the offending brush entity otherwise, which is the distinction that matters when the world
        /// and the entity passes disagree.
        /// </summary>
        public bool TraceExplain(BspFile.Vector3 a, BspFile.Vector3 b, out int contents,
            out BspFile.Vector3 hit, out int blockingHeadNode)
        {
            contents = 0;
            hit = b;
            blockingHeadNode = -1;

            if (nodes.Length == 0 || leafs.Length == 0)
                return true;

            if (BlockedExplain(0, a, b, ref contents, ref hit))
            {
                blockingHeadNode = 0;
                return false;
            }

            if (entityModels is null)
                return true;

            int gathered = Math.Min(entityModels.BlockingModelCount, GatherBuffer);

            Span<int> heads = stackalloc int[gathered];

            Span<BspFile.Vector3> origins = stackalloc BspFile.Vector3[gathered];
            int count = entityModels.Gather(a, b, heads, origins, out bool overflowed);

            if (overflowed)
            {
                var all = GatherAll(a, b);
                heads = all.Heads;
                origins = all.Origins;
                count = all.Count;
            }

            for (int i = 0; i < count; i++)
            {
                var o = origins[i];
                if (!BlockedExplain(heads[i], Shift(a, o), Shift(b, o), ref contents, ref hit))
                    continue;

                hit = new BspFile.Vector3(hit.X + o.X, hit.Y + o.Y, hit.Z + o.Z);
                blockingHeadNode = heads[i];
                return false;
            }

            // MaskBlockLos explicitly: this diagnostic reports what stops a sight line, and the world
            // walk above (BlockedExplain) is hardcoded to that mask too.
            //
            // Traced for the surface rather than merely asked whether it blocks, so the reported
            // position is where the terrain actually is. Reporting the ray's endpoint here - which is
            // what `hit` still held - reads as "blocked exactly where it was aiming", and that is a
            // convincing description of a self-intersection that is not happening. It cost a wrong
            // diagnosis before anyone checked it against the floor finder.
            if (displacements is not null &&
                displacements.TryTraceSurface(a, b, MaskBlockLos, out float dispFraction, out _))
            {
                hit = new BspFile.Vector3(
                    a.X + (b.X - a.X) * dispFraction,
                    a.Y + (b.Y - a.Y) * dispFraction,
                    a.Z + (b.Z - a.Z) * dispFraction);

                blockingHeadNode = -2; // displacement
                return false;
            }

            if (staticProps is not null &&
                staticProps.TryTraceSurface(a, b, MaskBlockLos, out float propFraction, out _))
            {
                hit = new BspFile.Vector3(
                    a.X + (b.X - a.X) * propFraction,
                    a.Y + (b.Y - a.Y) * propFraction,
                    a.Z + (b.Z - a.Z) * propFraction);

                blockingHeadNode = -3; // static prop
                return false;
            }

            return true;
        }

        private bool BlockedExplain(int num, BspFile.Vector3 p1, BspFile.Vector3 p2, ref int contents, ref BspFile.Vector3 hit)
        {
            while (num >= 0)
            {
                // By reference, not by value. This is the hottest loop in the program - tens of millions
                // of node visits per map - and reading these as locals copied a 12-byte node and a
                // 20-byte plane on every one of them, to use four floats of the plane and three ints of
                // the node. The arrays are never written during a trace.
                ref readonly var node = ref nodes[num];

                if ((uint)node.PlaneNum >= (uint)planes.Length)
                    return false;

                ref readonly var plane = ref planes[node.PlaneNum];

                float d1 = plane.Normal.X * p1.X + plane.Normal.Y * p1.Y + plane.Normal.Z * p1.Z - plane.Distance;
                float d2 = plane.Normal.X * p2.X + plane.Normal.Y * p2.Y + plane.Normal.Z * p2.Z - plane.Distance;

                if (d1 >= 0 && d2 >= 0) { num = node.Child0; continue; }
                if (d1 < 0 && d2 < 0) { num = node.Child1; continue; }

                float frac = d1 / (d1 - d2);
                var mid = new BspFile.Vector3(
                    p1.X + frac * (p2.X - p1.X),
                    p1.Y + frac * (p2.Y - p1.Y),
                    p1.Z + frac * (p2.Z - p1.Z));

                bool behind = d1 < 0;
                if (BlockedExplain(behind ? node.Child1 : node.Child0, p1, mid, ref contents, ref hit))
                    return true;

                num = behind ? node.Child0 : node.Child1;
                p1 = mid;
            }

            int leafIndex = -num - 1;
            if ((uint)leafIndex >= (uint)leafs.Length)
                return false;

            int c = leafs[leafIndex].Contents;
            if ((c & MaskBlockLos) == 0)
                return false;

            contents = c;
            hit = p1;
            return true;
        }

        private bool Blocked(int num, BspFile.Vector3 p1, BspFile.Vector3 p2, int mask)
            => Blocked(num, p1, p2, mask, p1, p2, 0);

        private bool Blocked(int num, BspFile.Vector3 p1, BspFile.Vector3 p2, int mask,
            BspFile.Vector3 rayStart, BspFile.Vector3 rayEnd, int ignoreContents)
        {
            while (num >= 0)
            {
                // By reference, not by value. This is the hottest loop in the program - tens of millions
                // of node visits per map - and reading these as locals copied a 12-byte node and a
                // 20-byte plane on every one of them, to use four floats of the plane and three ints of
                // the node. The arrays are never written during a trace.
                ref readonly var node = ref nodes[num];

                if ((uint)node.PlaneNum >= (uint)planes.Length)
                    return false;

                ref readonly var plane = ref planes[node.PlaneNum];

                float d1 = plane.Normal.X * p1.X + plane.Normal.Y * p1.Y + plane.Normal.Z * p1.Z - plane.Distance;
                float d2 = plane.Normal.X * p2.X + plane.Normal.Y * p2.Y + plane.Normal.Z * p2.Z - plane.Distance;

                if (d1 >= 0 && d2 >= 0) { num = node.Child0; continue; }
                if (d1 < 0 && d2 < 0) { num = node.Child1; continue; }

                // the segment straddles the plane: split it at the crossing point
                float frac = d1 / (d1 - d2);
                var mid = new BspFile.Vector3(
                    p1.X + frac * (p2.X - p1.X),
                    p1.Y + frac * (p2.Y - p1.Y),
                    p1.Z + frac * (p2.Z - p1.Z));

                bool behind = d1 < 0;
                if (Blocked(behind ? node.Child1 : node.Child0, p1, mid, mask, rayStart, rayEnd,
                        ignoreContents))
                    return true;

                num = behind ? node.Child0 : node.Child1;
                p1 = mid;
            }

            int leafIndex = -num - 1;
            if ((uint)leafIndex >= (uint)leafs.Length)
                return false;

            if ((leafs[leafIndex].Contents & mask) != 0)
                return true;

            return HitsLeafBrush(leafIndex, rayStart, rayEnd, mask, ignoreContents);
        }

        /// <summary>
        /// Whether the segment enters any brush listed in this leaf that matches the mask.
        ///
        /// Given the whole ray rather than this leaf's slice of it, for the reason spelled out on
        /// <see cref="TryHitLeafBrush"/>. Here the sub-segment version failed the opposite way round:
        /// <see cref="SegmentHitsBrush"/> reads "started inside the brush" as blocked, so a sub-segment
        /// beginning exactly on a brush face - the common case, since faces and split planes coincide -
        /// reported every such leaf as blocked whether the ray touched the brush or not. That is the
        /// same grate reading as solid to a clearance test while being invisible to a floor trace.
        /// </summary>
        private bool HitsLeafBrush(int leafIndex, BspFile.Vector3 p1, BspFile.Vector3 p2, int mask,
            int ignoreContents)
        {
            if ((uint)leafIndex >= (uint)leafBrushes.Length)
                return false;

            var list = leafBrushes[leafIndex];
            if (list is null)
                return false;

            foreach (int index in list)
            {
                // The list carries every brush either trace purpose could care about; re-check the
                // caller's own mask here so a grate blocks movement but still passes sight, and a
                // window (also generation-mask-only) never blocks either.
                if ((brushes[index].Contents & mask) == 0)
                    continue;

                if (ignoreContents != 0 && (brushes[index].Contents & ignoreContents) != 0)
                    continue;

                if (SegmentHitsBrush(brushes[index], p1, p2))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Clips a segment against a convex brush. Each side plane either rejects the segment outright,
        /// pushes the entry fraction forward, or pulls the exit fraction back; anything left over is
        /// inside the brush.
        /// </summary>
        private bool SegmentHitsBrush(BspFile.Brush brush, BspFile.Vector3 p1, BspFile.Vector3 p2)
        {
            const float Epsilon = 0.03125f;

            float enter = -1f;
            float exit = 1f;
            bool startsOutside = false;

            for (int i = 0; i < brush.NumSides; i++)
            {
                int sideIndex = brush.FirstSide + i;
                if ((uint)sideIndex >= (uint)brushSides.Length)
                    return false;

                var side = brushSides[sideIndex];
                if ((uint)side.PlaneNum >= (uint)planes.Length)
                    return false;

                var plane = planes[side.PlaneNum];
                float d1 = plane.Normal.X * p1.X + plane.Normal.Y * p1.Y + plane.Normal.Z * p1.Z - plane.Distance;
                float d2 = plane.Normal.X * p2.X + plane.Normal.Y * p2.Y + plane.Normal.Z * p2.Z - plane.Distance;

                if (d1 > 0) startsOutside = true;

                if (d1 > 0 && d2 > 0) return false; // wholly outside this face
                if (d1 <= 0 && d2 <= 0) continue;   // wholly behind it, nothing to clip

                float f = d1 / (d1 - d2);

                if (d1 > d2)
                {
                    if (f - Epsilon > enter) enter = f - Epsilon;
                }
                else
                {
                    if (f + Epsilon < exit) exit = f + Epsilon;
                }
            }

            if (!startsOutside)
                return true; // the segment starts inside the brush

            return enter < exit && enter < 1f && exit > 0f;
        }

        /// <summary>Descends the BSP tree to find the leaf containing a point, then its cluster.</summary>
        public short GetCluster(BspFile.Vector3 point) => TryGetLeaf(point, out var leaf) ? leaf.Cluster : (short)-1;

        /// <summary>Descends the BSP tree to the leaf containing a point.</summary>
        public bool TryGetLeaf(BspFile.Vector3 point, out Leaf leaf)
        {
            leaf = default;

            if (nodes.Length == 0 || leafs.Length == 0)
                return false;

            int index = 0;
            while (index >= 0)
            {
                var node = nodes[index];
                if (node.PlaneNum < 0 || node.PlaneNum >= planes.Length)
                    return false;

                var plane = planes[node.PlaneNum];
                float d = plane.Normal.X * point.X + plane.Normal.Y * point.Y + plane.Normal.Z * point.Z
                          - plane.Distance;

                index = d >= 0 ? node.Child0 : node.Child1;
            }

            int leafIndex = -index - 1;
            if (leafIndex < 0 || leafIndex >= leafs.Length)
                return false;

            leaf = leafs[leafIndex];
            return true;
        }

    }
}
