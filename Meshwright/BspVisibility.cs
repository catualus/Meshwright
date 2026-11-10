using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
            var vis = new BspVisibility { planes = bsp.Planes, texInfos = bsp.TexInfos };

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
            vis.BuildTraceTables();

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

        /// <summary>
        /// Surface flags per texinfo, so a trace can report what the face it landed on is made of and
        /// not merely where it is. Empty for a visibility set built by <see cref="FromGeometry"/>, which
        /// is what makes <see cref="SurfaceFlagsOf"/> answer nothing rather than crash there.
        /// </summary>
        private BspFile.TexInfo[] texInfos = [];

        private void ReadLeafBrushes(BinaryReader r, (int Offset, int Length, int Version) lump, BspFile bsp)
        {
            brushes = WithFullClip(bsp.Brushes, NavConstants.PlayerClipStopsEveryWalker);
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
        ///
        /// Plus <see cref="ContentsFullClip"/>, a brush that stops every walker the mesh is for. That
        /// is usually a brush this mask stopped already through CONTENTS_MONSTERCLIP; the exception is
        /// TF2's player clip, a wall to its bots (<see cref="NavConstants.PlayerClipStopsEveryWalker"/>).
        /// It also keeps this mask containing all of <see cref="GroundMask"/>, which is how
        /// <see cref="ModelsFor"/> tells a movement trace from a sight trace.
        /// </summary>
        public const int GenerationMask = 0x1 | 0x4000 | 0x2 | 0x20000 | ContentsGrate | ContentsFullClip;

        /// <summary>CONTENTS_MONSTERCLIP - an invisible brush that stops NPCs and nothing else.</summary>
        private const int ContentsMonsterClip = 0x20000;

        /// <summary>CONTENTS_PLAYERCLIP - an invisible brush that stops players and nothing else.</summary>
        private const int ContentsPlayerClip = 0x10000;

        /// <summary>
        /// A content bit of Meshwright's own, set at load on every brush that stops every walker the
        /// mesh is for: one carrying both CONTENTS_PLAYERCLIP and CONTENTS_MONSTERCLIP - Hammer's plain
        /// tools/toolsclip, which stops players and NPCs alike - and in TF2, whose bots are players,
        /// one carrying player clip alone (<see cref="NavConstants.PlayerClipStopsEveryWalker"/>).
        /// vbsp never writes this bit (it is past CONTENTS_HITBOX, the last one bspflags.h defines), so
        /// it means exactly that and nothing else.
        ///
        /// It exists because a mask is an "any of these bits" test and the question here is "both of
        /// them". A brush that stops one kind of walker and not the other is a barrier for the one it
        /// stops, and <see cref="GroundMask"/> explains why NPC clip on its own must not be ground. A
        /// brush that stops everybody is not a barrier: it is solid, and its top is a floor that every
        /// walker stands on, in-game, whether or not it is drawn.
        ///
        /// TF2 relies on this everywhere. Its stairs are smoothed with a clip ramp laid over the steps,
        /// and its sunken floors - 2fort's sewer, dustbowl's trenches - are often a clip slab a few
        /// units over the drawn floor. Without this bit those surfaces were neither ground nor clear:
        /// not ground because monsterclip is excluded from <see cref="GroundMask"/>, and not clear
        /// because every step under the ramp has the ramp in its headroom. The flood found nothing to
        /// stand on there at all. Measured against Valve's own meshes, 919 of the 1,128 routes
        /// ctf_2fort failed and 812 of cp_dustbowl's 1,095 broke first on exactly such a surface.
        /// </summary>
        public const int ContentsFullClip = unchecked((int)0x80000000);

        /// <summary>
        /// The brushes as the traces should see them: <paramref name="brushes"/> with
        /// <see cref="ContentsFullClip"/> added to every one that stops players and NPCs both - or,
        /// with <paramref name="playerClipStopsEveryWalker"/>, to every one that stops players, the
        /// walkers being players (see <see cref="NavConstants.PlayerClipStopsEveryWalker"/>). A copy,
        /// because the array belongs to the <see cref="BspFile"/>, which other readers share.
        /// </summary>
        internal static BspFile.Brush[] WithFullClip(BspFile.Brush[] brushes, bool playerClipStopsEveryWalker = false)
        {
            int stopsEveryWalker = playerClipStopsEveryWalker
                ? ContentsPlayerClip
                : ContentsPlayerClip | ContentsMonsterClip;

            var tagged = (BspFile.Brush[])brushes.Clone();
            for (int i = 0; i < tagged.Length; i++)
            {
                if ((tagged[i].Contents & stopsEveryWalker) == stopsEveryWalker)
                    tagged[i].Contents |= ContentsFullClip;
            }

            return tagged;
        }

        /// <summary>
        /// What counts as ground the mesh may be built on: <see cref="GenerationMask"/> without
        /// CONTENTS_MONSTERCLIP. A brush that is player clip as well as NPC clip still counts, and so in
        /// TF2 does player clip alone, through <see cref="ContentsFullClip"/>.
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

        /// <summary>
        /// <c>SURF_NODRAW</c> from bspflags.h - a face the renderer skips.
        ///
        /// A texinfo flag rather than a brush content, which is why nothing here could see it before:
        /// contents describe a volume and every nodraw brush is <c>CONTENTS_SOLID</c> like any other, so
        /// a mask can no more exclude nodraw than it can exclude brick. The flag lives on the *face*,
        /// and a trace has to report which face it hit for the question to be answerable at all.
        /// </summary>
        public const int SurfNodraw = 0x0080;

        /// <summary>Brush entities that block sight. Null until <see cref="AttachModels"/> is called.</summary>
        private BspModels? entityModels;

        /// <summary>Displacement surfaces. Null until <see cref="AttachDisplacements"/> is called.</summary>
        private BspDisplacements? displacements;

        /// <summary>Static prop collision. Null until <see cref="AttachStaticProps"/> is called.</summary>
        private StaticProps? staticProps;

        /// <summary>
        /// Brush entities that block a player's body: <see cref="entityModels"/> without doors,
        /// breakables and switched-off toggle brushes, which a player walks through. Falls back to
        /// <see cref="entityModels"/> when only one set was attached.
        /// </summary>
        private BspModels? movementModels;

        /// <summary>
        /// Brush entities that are ground: <see cref="movementModels"/> plus the walked-through toggle
        /// brushes that are switched on when the map starts, which a body passes for the purpose of
        /// linking either side of them but stands on while they are there (see
        /// <c>BspModels.StandsOn</c>). Falls back to <see cref="movementModels"/>.
        /// </summary>
        private BspModels? groundModels;

        public void AttachModels(BspModels models, BspModels? forMovement = null, BspModels? forGround = null)
        {
            entityModels = models;
            movementModels = forMovement ?? models;
            groundModels = forGround ?? movementModels;
        }

        /// <summary>
        /// The brush entities a trace against <paramref name="mask"/> should meet. Movement masks (both
        /// generation masks carry <see cref="GroundMask"/>'s windows and grates, which sight passes)
        /// get the walk-through set; sight gets every blocking entity, so a closed door still hides
        /// what is behind it. Of the two movement masks, <see cref="GroundMask"/> - the one without
        /// CONTENTS_MONSTERCLIP - is the one that asks what can be stood on rather than what stops a
        /// body, and gets the ground set.
        /// </summary>
        private BspModels? ModelsFor(int mask)
            => (mask & GroundMask) != GroundMask ? entityModels
                : (mask & ContentsMonsterClip) == 0 ? groundModels
                : movementModels;

        /// <summary>
        /// Volumes that kill a player standing in them (<c>BspModels.SelectKillVolumes</c>). Null until
        /// <see cref="AttachKillVolumes"/> is called, and never consulted by a trace: a trigger is not
        /// solid, and the ground under a death pit is still a floor something can land on.
        /// </summary>
        private BspModels? killVolumes;

        public void AttachKillVolumes(BspModels volumes) => killVolumes = volumes;

        public int KillVolumeCount => killVolumes?.BlockingModelCount ?? 0;

        /// <summary>
        /// Whether a player standing on the floor at this point would be inside a kill volume: whether
        /// the column their body fills, from just above their feet to the top of their head, enters one.
        ///
        /// The column rather than the feet alone because a trigger touches whatever its box overlaps,
        /// and a mapper's death pit is often a slab over the bottom of the pit rather than a box down
        /// into the ground. It is the line through the middle of the body rather than the full hull, so
        /// ground within half a body of a pit's edge is kept; that is ground a player stands on without
        /// falling in, and the engine's meshes cover it.
        /// </summary>
        public bool IsInKillVolume(float x, float y, float z)
        {
            if (killVolumes is null || killVolumes.BlockingModelCount == 0)
                return false;

            // Every content a brush can be listed under: what matters is that the point is inside the
            // trigger's brushes, not what they are made of. vbsp compiles them CONTENTS_SOLID.
            return BlockedByModels(killVolumes,
                new BspFile.Vector3(x, y, z + 1f),
                new BspFile.Vector3(x, y, z + NavConstants.HumanHeight),
                MaskBlockLos | GenerationMask, 0);
        }

        public void AttachDisplacements(BspDisplacements disp) => displacements = disp;

        public void AttachStaticProps(StaticProps props) => staticProps = props;

        public int BlockingModelCount => entityModels?.BlockingModelCount ?? 0;
        public int DisplacementTriangleCount => displacements?.TriangleCount ?? 0;
        public int StaticPropTriangleCount => staticProps?.TriangleCount ?? 0;

        /// <summary>The attached props, for reporting what they could and could not resolve. Null until
        /// <see cref="AttachStaticProps"/> is called.</summary>
        public StaticProps? StaticProps => staticProps;

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
        /// Static props are traced last of all, against the collision hulls read from each model's
        /// `.phy`. A prop whose model could not be found contributes nothing and is invisible to this;
        /// `meshwright props` reports how many there are.
        ///
        /// There is no start-solid flag, and the geometry classes answer a start inside them
        /// differently. A line starting inside a brush or a brush entity is blocked - it starts inside
        /// - which is the right answer for a sight line or a clearance line alike. A line starting on or
        /// inside a displacement or a prop ignores the surface it starts on (see
        /// <see cref="TriangleMesh"/>): headroom lines from a floor point depend on that, but a line
        /// lying wholly inside a prop reads clear. Sight lines can live with it - an eye inside a prop
        /// sees out only if the line never leaves the prop - but a caller asking whether a body fits
        /// has to ask <see cref="IsInsideProp"/> or <see cref="IsPointSolid"/> about the start first.
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

            if (Blocked(0, a, b, new LineQuery(a, b, mask, ignoreContents, TestedBrushes(out int ray), ray)))
                return false;

            return !BlockedPastWorld(a, b, mask, ignoreContents);
        }

        /// <summary>
        /// The half of <see cref="IsLineClear(BspFile.Vector3, BspFile.Vector3, int, int)"/> after the
        /// world tree has let the segment through: brush entities, displacements, props. Split out so the
        /// floor walk's column test (<see cref="FirstOpenStep(ReadOnlySpan{float}, float, float, int)"/>),
        /// which answers the world tree for a whole run of points at once, asks the rest exactly as a
        /// point test does.
        /// </summary>
        private bool BlockedPastWorld(BspFile.Vector3 a, BspFile.Vector3 b, int mask, int ignoreContents)
        {
            // No early return here when there are no brush entities to test. There used to be one, and
            // it skipped the displacement and prop tests below with it: on a map whose brush entities
            // are all non-blocking, every line through terrain or a prop read as clear, and the clipper
            // then deleted most of the mesh (koth_viaduct kept 762 of 6,270 areas).
            var models = ModelsFor(mask);
            if (models is not null && models.BlockingModelCount > 0 && BlockedByModels(models, a, b, mask, ignoreContents))
                return true;

            if (displacements is not null && displacements.Blocks(a, b, mask)) return true;

            return staticProps is not null && staticProps.Blocks(a, b, mask);
        }

        /// <summary>Whether any of <paramref name="models"/> blocks the segment.</summary>
        private bool BlockedByModels(BspModels models, BspFile.Vector3 a, BspFile.Vector3 b, int mask, int ignoreContents)
        {
            int gathered = Math.Min(models.BlockingModelCount, GatherBuffer);

            Span<int> heads = stackalloc int[gathered];

            Span<BspFile.Vector3> origins = stackalloc BspFile.Vector3[gathered];
            int count = models.Gather(a, b, heads, origins, out bool overflowed);

            if (overflowed)
            {
                var all = GatherAll(models, a, b);
                heads = all.Heads;
                origins = all.Origins;
                count = all.Count;
            }

            for (int i = 0; i < count; i++)
            {
                var o = origins[i];
                var start = Shift(a, o);
                var end = Shift(b, o);

                // A ray of its own: the segment is a different one in each model's space.
                if (Blocked(heads[i], start, end,
                        new LineQuery(start, end, mask, ignoreContents, TestedBrushes(out int ray), ray)))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Prepares many line tests straight down one column, between heights <paramref name="low"/>
        /// and <paramref name="high"/>, for <see cref="Column.IsClear"/> to answer each exactly as
        /// <see cref="IsLineClear(BspFile.Vector3, BspFile.Vector3, int)"/> would.
        ///
        /// The floor search (<see cref="StairMarker.TryFindFloor"/>) traces a dozen segments or more
        /// down one column, each from the root of the world tree and of both triangle trees, and nearly
        /// all of that walk is the same every time: the planes the whole column lies to one side of, the
        /// boxes the column passes outside, and the brushes beside it. The column works it out once -
        /// how far down the world tree every segment in it goes undivided (<see cref="ColumnStart"/>),
        /// which brush entities' bounds and which triangle-tree leaves it passes through within its
        /// heights, and, the first time a segment meets them, which brushes and which leaves' brush
        /// lists no segment down it can be in (<see cref="MissesColumn"/>,
        /// <see cref="LeafMissesColumn"/>) - and each segment is then asked about only what is left.
        ///
        /// A floor search on rp_downtown_tits_v25 cost 640 tree nodes and box tests and 51 brush clips,
        /// and on cp_badlands 702 and 163; through the column, 142 and 2, and 153 and 3. Replayed one
        /// thread at a time, the same searches take a quarter to a third of the time. Every answer is
        /// the line trace's, and the meshes are byte-identical.
        ///
        /// One per thread at a time: the lists live in buffers the thread keeps. A column opened while
        /// another is open takes them over, and the first then hands every line to the line trace.
        /// </summary>
        internal Column OpenColumn(float x, float y, float low, float high, int mask)
            => new(this, x, y, low, high, mask);

        /// <summary>
        /// The buffers a thread's <see cref="Column"/> keeps its lists in, kept rather than allocated
        /// per column because the floor search opens one for every point it is asked about.
        /// </summary>
        private sealed class ColumnBuffers
        {
            public int[] Displacements = new int[16], Props = new int[16], Models = new int[8];
            public readonly ColumnBrushes Brushes = new();
        }

        [ThreadStatic] private static ColumnBuffers? columnBuffers;

        /// <summary>See <see cref="OpenColumn"/>.</summary>
        internal readonly ref struct Column
        {
            private readonly BspVisibility vis;
            private readonly float x, y, low, high;
            private readonly int mask;

            /// <summary>Where every segment between <see cref="low"/> and <see cref="high"/> leaves the
            /// root's path in the world tree: a node, a leaf, or a node naming no plane.</summary>
            private readonly int start;

            /// <summary>Whether the world has a tree; without one every line is clear.</summary>
            private readonly bool tree;

            private readonly BspModels? models;
            private readonly ColumnBrushes brushes;

            /// <summary>The stamp <see cref="brushes"/> took for this column; another once a later
            /// column on the thread has taken the buffers over.</summary>
            private readonly int stamp;
            private readonly int[] modelList, displacementLeaves, propLeaves;
            private readonly int modelCount, displacementCount, propCount;

            public Column(BspVisibility vis, float x, float y, float low, float high, int mask)
            {
                this.vis = vis;
                this.x = x;
                this.y = y;
                this.low = low;
                this.high = high;
                this.mask = mask;

                var buffers = columnBuffers ??= new ColumnBuffers();

                tree = vis.nodes.Length != 0 && vis.leafs.Length != 0;
                start = tree ? vis.ColumnStart(x, y, low, high) : 0;

                brushes = buffers.Brushes;
                brushes.Open(vis.brushes.Length, vis.leafBrushes.Length, x, y, low, high);
                stamp = brushes.Stamp;

                var set = vis.ModelsFor(mask);
                models = set is not null && set.BlockingModelCount > 0 ? set : null;
                modelCount = models?.GatherColumn(x, y, ref buffers.Models) ?? 0;

                // The triangle trees are cut to the column's own heights; see ColumnMargin.
                float margin = ColumnMargin(low, high);
                float below = low - margin, above = high + margin;

                displacementCount = vis.displacements?.CollectColumn(x, y, below, above, ref buffers.Displacements) ?? 0;
                propCount = vis.staticProps?.CollectColumn(x, y, below, above, ref buffers.Props) ?? 0;

                modelList = buffers.Models;
                displacementLeaves = buffers.Displacements;
                propLeaves = buffers.Props;
            }

            /// <summary>
            /// <c>IsLineClear((x, y, from), (x, y, to), mask)</c>, to the bit. Heights outside the
            /// column's, and every line once another column has taken the thread's buffers over, are
            /// passed to <see cref="IsLineClear(BspFile.Vector3, BspFile.Vector3, int)"/> itself.
            /// </summary>
            public bool IsClear(float from, float to)
            {
                var a = new BspFile.Vector3(x, y, from);
                var b = new BspFile.Vector3(x, y, to);

                if (!(from >= low && from <= high && to >= low && to <= high) || brushes.Stamp != stamp)
                    return vis.IsLineClear(a, b, mask);

                if (!tree)
                    return true;

                // The world tree from where the column's walk divides, as IsLineClear walks it from the
                // root; see ColumnStart.
                if (vis.Blocked(start, a, b,
                        new LineQuery(a, b, mask, 0, vis.TestedBrushes(out int ray), ray, brushes)))
                    return false;

                // Then BlockedPastWorld's three, in its order: brush entities, displacements, props.
                if (models is not null && ModelsBlock(a, b))
                    return false;

                if (vis.displacements is not null
                        && vis.displacements.ColumnBlocks(displacementLeaves.AsSpan(0, displacementCount), a, b, mask))
                    return false;

                return vis.staticProps is null
                       || !vis.staticProps.ColumnBlocks(propLeaves.AsSpan(0, propCount), a, b, mask);
            }

            /// <summary><see cref="BlockedByModels"/> over the column's candidates.</summary>
            private bool ModelsBlock(BspFile.Vector3 a, BspFile.Vector3 b)
            {
                if (modelCount == 0)
                    return false;

                Span<int> heads = modelCount <= GatherBuffer ? stackalloc int[modelCount] : new int[modelCount];
                Span<BspFile.Vector3> origins = modelCount <= GatherBuffer
                    ? stackalloc BspFile.Vector3[modelCount]
                    : new BspFile.Vector3[modelCount];

                int count = models!.GatherFromColumn(modelList.AsSpan(0, modelCount), a, b, heads, origins);

                for (int i = 0; i < count; i++)
                {
                    var o = origins[i];
                    var from = Shift(a, o);
                    var to = Shift(b, o);

                    if (vis.Blocked(heads[i], from, to,
                            new LineQuery(from, to, mask, 0, vis.TestedBrushes(out int ray), ray)))
                        return true;
                }

                return false;
            }
        }

        /// <summary>
        /// How far past a column's heights a triangle-tree box may reach and still be collected
        /// (<see cref="TriangleMesh.CollectColumn"/>): a unit, a thousandth of the column's length and
        /// a sixty-thousandth of its heights. What it has to cover is the rounding in the box test and
        /// the millionth of a segment the triangle test runs on past its end - millionths of the
        /// segment's length and of the heights - so it is a thousand times what it needs.
        /// </summary>
        internal static float ColumnMargin(float low, float high)
            => 1f + (high - low) / 1024f + (MathF.Abs(low) + MathF.Abs(high)) / 65536f;

        /// <summary>
        /// The node the world-tree walk of every segment down the column at <paramref name="x"/>,
        /// <paramref name="y"/> between <paramref name="low"/> and <paramref name="high"/> reaches
        /// undivided: <see cref="Blocked"/> from here answers as <see cref="Blocked"/> from the root.
        ///
        /// Above it, the walk only ever chooses a side: both ends of such a segment lie on the same
        /// side of each plane, so it is not split and goes on with its own two ends. That rests on
        /// <see cref="PlaneDistance"/> being monotonic in height with x and y fixed - the argument made
        /// for <see cref="FirstOpenStep(ReadOnlySpan{float}, float, float, int)"/> - so a height between
        /// <paramref name="low"/> and <paramref name="high"/> is on the side both of them are on. The
        /// descent stops at the first node they part at, a leaf, or a node naming no plane, where
        /// <see cref="Blocked"/> gives up just as it would have on the way down.
        /// </summary>
        private int ColumnStart(float x, float y, float low, float high)
        {
            var top = new BspFile.Vector3(x, y, high);
            var bottom = new BspFile.Vector3(x, y, low);

            int num = 0;
            while (num >= 0)
            {
                ref readonly var node = ref traceNodes[num];

                if (node.Plane.Axis == AxisMissing)
                    break;

                float d1 = PlaneDistance(in node.Plane, in top);
                float d2 = PlaneDistance(in node.Plane, in bottom);

                if (d1 >= 0 && d2 >= 0) num = node.Child0;
                else if (d1 < 0 && d2 < 0) num = node.Child1;
                else break;
            }

            return num;
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
        private static (int[] Heads, BspFile.Vector3[] Origins, int Count) GatherAll(
            BspModels models, BspFile.Vector3 a, BspFile.Vector3 b,
            BspFile.Vector3 mins = default, BspFile.Vector3 maxs = default)
        {
            int capacity = models.BlockingModelCount;
            var heads = new int[capacity];
            var origins = new BspFile.Vector3[capacity];

            return (heads, origins, models.Gather(a, b, mins, maxs, heads, origins, out _));
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
        ///
        /// A segment starting inside a brush hits at its own start, with an upward normal, which reads
        /// exactly like a floor there; one starting inside a prop finds the face it leaves through, its
        /// normal turned upwards too. Neither is a surface anyone stands on, so a caller whose start
        /// can be buried asks <see cref="IsPointSolid"/> first.
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
            => TryTraceSurface(a, b, mask, out point, out normal, out onDisplacement, out _);

        /// <summary>
        /// Same trace, also reporting the surface flags of the face that stopped it - see
        /// <see cref="SurfNodraw"/> for the one this exists to answer.
        ///
        /// Only brush faces carry flags. A displacement or a static prop reports zero, which is correct
        /// rather than merely convenient: both are rendered geometry by construction, so "no flags" and
        /// "an ordinary drawn face" are the same answer for them.
        /// </summary>
        public bool TryTraceSurface(BspFile.Vector3 a, BspFile.Vector3 b, int mask,
            out BspFile.Vector3 point, out BspFile.Vector3 normal, out bool onDisplacement,
            out int surfaceFlags)
        {
            point = b;
            normal = new BspFile.Vector3(0, 0, 1);
            onDisplacement = false;
            surfaceFlags = 0;

            float best = float.MaxValue;
            bool found = false;

            if (nodes.Length > 0 && leafs.Length > 0)
            {
                var world = new SurfaceHit();
                if (TraceSurface(0, a, b, 0f, 1f, ref world, mask, a, b))
                {
                    best = world.Fraction;
                    normal = world.Normal;
                    surfaceFlags = world.SurfaceFlags;
                    found = true;
                }
            }

            var surfaceModels = ModelsFor(mask);
            if (surfaceModels is not null)
            {
                int gathered = Math.Min(surfaceModels.BlockingModelCount, GatherBuffer);
                Span<int> heads = stackalloc int[gathered];
                Span<BspFile.Vector3> origins = stackalloc BspFile.Vector3[gathered];
                int count = surfaceModels.Gather(a, b, heads, origins, out bool overflowed);

                if (overflowed)
                {
                    var all = GatherAll(surfaceModels, a, b);
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
                    surfaceFlags = entity.SurfaceFlags;
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
                surfaceFlags = 0;
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
                surfaceFlags = 0;
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
        /// Builds a visibility set straight from geometry, for tests.
        ///
        /// The sweep is only testable against a real BSP tree, and the bug this exists to pin down
        /// lives in how the tree is descended rather than in how a brush is clipped, so a stub that
        /// skipped the tree would not have caught it.
        /// </summary>
        internal static BspVisibility FromGeometry(BspFile.Plane[] planes, Node[] nodes, Leaf[] leafs,
            BspFile.Brush[] brushes, BspFile.BrushSide[] brushSides, int[]?[] leafBrushes,
            BspFile.TexInfo[]? texInfos = null)
        {
            var vis = new BspVisibility
            {
                planes = planes,
                nodes = nodes,
                leafs = leafs,
                brushes = WithFullClip(brushes, NavConstants.PlayerClipStopsEveryWalker),
                brushSides = brushSides,
                leafBrushes = leafBrushes,
                texInfos = texInfos ?? [],
            };

            vis.BuildTraceTables();
            return vis;
        }

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
        ///
        /// The return value is whether the box *entered* something, and is not a clearance answer on
        /// its own. A box that starts inside a brush and is swept out of it enters nothing, so this
        /// returns false and only <paramref name="startSolid"/> says the box began in solid. A caller
        /// asking whether a body fits or can pass has to refuse <paramref name="startSolid"/> whatever
        /// the return value is: <c>!startSolid &amp;&amp; (!hit || fraction &gt;= 1)</c>.
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

            var hullModels = ModelsFor(mask);
            if (hullModels is not null && hullModels.BlockingModelCount > 0)
            {
                int gathered = Math.Min(hullModels.BlockingModelCount, GatherBuffer);
                Span<int> heads = stackalloc int[gathered];
                Span<BspFile.Vector3> origins = stackalloc BspFile.Vector3[gathered];
                // Gathered by the swept box, not by the line through its origin: a brush entity the box
                // runs into beside that line is still in its way.
                int count = hullModels.Gather(a, b, mins, maxs, heads, origins, out bool overflowed);

                if (overflowed)
                {
                    var all = GatherAll(hullModels, a, b, mins, maxs);
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

                    bool entityHit = HullTrace(heads[i], ea, eb, 0f, 1f, ref entity, ref entitySolid, mask,
                        ea, eb, mins, maxs);

                    // Taken before the hit is looked at, not after. A box that starts inside a brush
                    // entity and is swept out of it never enters one, so the walk reports no hit and
                    // only this flag records where the box began - and skipping it along with the
                    // missing hit made a sweep out of a func_wall or a solid func_brush look as if it had
                    // started in open air.
                    solid |= entitySolid;

                    if (!entityHit)
                        continue;

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
            //
            // A triangle mesh reports a box that starts overlapping it as a hit at fraction zero as
            // well as start-solid, so its flag cannot go missing the way a brush entity's could. It
            // is still taken whatever the hit says, so that stays true if that ever changes.
            if (displacements is not null)
            {
                bool dispHit = displacements.TryTraceHull(a, b, mins, maxs, mask,
                    out float dispFraction, out var dispNormal, out bool dispSolid);
                solid |= dispSolid;

                if (dispHit && (!found || dispFraction < hit.Fraction))
                {
                    hit = new SurfaceHit { Fraction = dispFraction, Normal = dispNormal };
                    found = true;
                }
            }

            // Props differ from terrain in two ways, both because a prop is a closed solid rather than
            // a surface. A box wholly inside one touches none of its triangles, and is start-solid only
            // because StaticProps tests the start against the hulls themselves. And a box dragged out
            // through a face it starts across is start-solid without a hit, as it is out of a brush,
            // which is what lets Traversability.CanStep's graze rule judge it.
            if (staticProps is not null)
            {
                bool propHit = staticProps.TryTraceHull(a, b, mins, maxs, mask,
                    out float propFraction, out var propNormal, out bool propSolid);
                solid |= propSolid;

                if (propHit && (!found || propFraction < hit.Fraction))
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
                ref readonly var node = ref nodes[num];
                if ((uint)node.PlaneNum >= (uint)planes.Length)
                    return false;

                ref readonly var plane = ref planes[node.PlaneNum];

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

                // Straddles, so both halves get walked, nearest first. The two halves overlap by the
                // box offset rather than meeting at the crossing point: the box is wider than the line
                // through its middle, so it is still inside the near half a little past the plane and
                // already inside the far half a little before it. Splitting exactly at the crossing
                // leaves a slab the width of the box that neither half looks in. This is Quake's
                // CM_RecursiveHullCheck, frac and frac2.
                //
                // The parallel case is the one that mattered. A segment running along a plane has
                // d1 == d2, and there is no crossing point to split at, so Quake hands the whole
                // segment to both children. This used to collapse to t = 0, which gave the near child
                // a zero length segment starting at p1 - and a degenerate segment prunes everything,
                // so every leaf reachable only through that child went unexamined.
                //
                // That is not an edge case. Every generation sweep is horizontal, every floor and
                // ceiling plane is horizontal, and the box is tall enough to straddle the floor it is
                // walking on, so the near child was being skipped on the most ordinary trace there is.
                // A wall standing on that floor sits in the skipped child: measured on
                // rp_downtown_meowy, a 55 unit box reported a solid brick wall completely clear, the
                // link across it was allowed, and areas grew through buildings.
                float gap = d1 - d2;
                float nearEnd, farStart;

                if (MathF.Abs(gap) < 1e-6f)
                {
                    nearEnd = 1f;
                    farStart = 0f;
                }
                else
                {
                    float inverse = 1f / gap;

                    if (d1 < d2)
                    {
                        nearEnd = (d1 - offset - DistEpsilon) * inverse;
                        farStart = (d1 + offset + DistEpsilon) * inverse;
                    }
                    else
                    {
                        nearEnd = (d1 + offset + DistEpsilon) * inverse;
                        farStart = (d1 - offset - DistEpsilon) * inverse;
                    }
                }

                nearEnd = Math.Clamp(nearEnd, 0f, 1f);
                farStart = Math.Clamp(farStart, 0f, 1f);

                bool behind = d1 < d2;
                int near = behind ? node.Child1 : node.Child0;
                int far = behind ? node.Child0 : node.Child1;

                float nearFraction = startFraction + (endFraction - startFraction) * nearEnd;
                var nearPoint = Along(p1, p2, nearEnd);

                if (HullTrace(near, p1, nearPoint, startFraction, nearFraction, ref hit, ref startSolid,
                        mask, rayStart, rayEnd, mins, maxs))
                {
                    return true;
                }

                num = far;
                startFraction += (endFraction - startFraction) * farStart;
                p1 = Along(p1, p2, farStart);
            }

            int leafIndex = -num - 1;
            if ((uint)leafIndex >= (uint)leafs.Length)
                return false;

            return ClipHullToLeaf(leafIndex, rayStart, rayEnd, mins, maxs, mask, ref hit, ref startSolid);
        }

        /// <summary>A point along a segment, by fraction.</summary>
        private static BspFile.Vector3 Along(BspFile.Vector3 a, BspFile.Vector3 b, float f)
            => new(a.X + f * (b.X - a.X), a.Y + f * (b.Y - a.Y), a.Z + f * (b.Z - a.Z));

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
                ref readonly var brush = ref brushes[index];
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

                    ref readonly var side = ref brushSides[sideIndex];
                    if ((uint)side.PlaneNum >= (uint)planes.Length) { startsOutside = true; break; }

                    ref readonly var plane = ref planes[side.PlaneNum];

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

        /// <summary>A surface found in a column: how high it is, which way it faces, what it is made of.</summary>
        public readonly record struct FloorSample(float Z, BspFile.Vector3 Normal, int SurfaceFlags)
        {
            /// <summary>
            /// Whether the face is one the renderer skips.
            ///
            /// A floor nobody can see is nearly always a floor nobody is meant to stand on: the top of a
            /// brush sealed under a walkway, the inside of a structure, the base of a cavity. It is
            /// still solid, and must still stop a trace and shadow the floors beneath it - this says
            /// only that it is not ground.
            /// </summary>
            public bool Nodraw => (SurfaceFlags & SurfNodraw) != 0;
        }

        /// <summary>
        /// Whether a point is inside solid matching the mask: a brush, a brush entity, or a static prop's
        /// collision hull.
        ///
        /// This is the start-solid test the line traces do not have. A line starting inside a brush is
        /// blocked, but one starting inside a prop is not - triangles only stop a segment that crosses
        /// them past its start (see <see cref="TriangleMesh"/>), so a line lying wholly inside a prop
        /// reads as clear and one leaving it reports the face it leaves through. Callers that need to
        /// know they began inside something ask this first.
        ///
        /// The props are asked separately because a zero-length line, which is all the brush half of
        /// this is, never crosses a triangle. Before they were, every caller using this as its guard
        /// was blind to props: the floor walk below stepped 0.25 units into a prop it had landed on,
        /// was told it was in the open, and sampled the prop's underside - or the floor it stood on -
        /// as ground *inside* it, with headroom to stand wherever the prop was taller than a player.
        /// Displacements need nothing here; they are surfaces with no inside.
        /// </summary>
        public bool IsPointSolid(float x, float y, float z, int mask)
        {
            var p = new BspFile.Vector3(x, y, z);
            return !IsLineClear(p, p, mask) || IsInsideProp(p, mask);
        }

        /// <summary>
        /// Whether a point is inside a static prop's collision hull - the half of
        /// <see cref="IsPointSolid"/> a line trace cannot answer for itself.
        ///
        /// For a caller about to trace a line from <paramref name="point"/> that needs to know whether
        /// it began inside something. A line starting inside a brush is already blocked, so asking the
        /// brushes again would only cost a second walk of the tree; one starting inside a prop is not,
        /// and this is the cheap way to ask about that alone.
        /// </summary>
        public bool IsInsideProp(BspFile.Vector3 point, int mask)
            => staticProps is not null && staticProps.Contains(point, mask);

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
        /// it landed on rather than merely past its face - a prop's included, which
        /// <see cref="IsPointSolid"/> now sees.
        ///
        /// The first trace is the one exception to recording what it finds. The top of a column is
        /// usually inside the brush sealing the top of the map, and a trace starting in solid reports a
        /// hit where it starts, facing up, or the face of some brush further into the solid: either
        /// way nowhere anyone could stand, and it was every such column's first "floor", taking one of
        /// the caller's slots. gm_construct has 118 columns that fill all sixteen. The trace still
        /// runs, and the walk still carries on below what it found, so the floors after it are the
        /// ones they always were.
        ///
        /// The descent through solid is defined as stepping <see cref="SolidStep"/> at a time and
        /// asking <see cref="IsPointSolid"/> at each step, and the floors found depend on exactly where
        /// that stops: the next trace starts there, so a gap thinner than a step that the stepping
        /// skips over has to stay skipped. It is no longer paid for that way, though; see
        /// <see cref="StepOutOfSolid"/>, which gives the same answer for a fraction of the work.
        /// </summary>
        public int EnumerateFloors(float x, float y, float topZ, float bottomZ, Span<FloorSample> into)
        {
            int count = 0;
            float cursor = topZ;
            bool buried = IsPointSolid(x, y, cursor, GroundMask);

            while (count < into.Length && cursor > bottomZ)
            {
                var from = new BspFile.Vector3(x, y, cursor);
                var to = new BspFile.Vector3(x, y, bottomZ);

                if (!TryTraceSurface(from, to, GroundMask, out var point, out var normal, out _,
                        out int flags))
                    break;

                if (!buried)
                    into[count++] = new FloorSample(point.Z, normal, flags);

                // Every later trace starts where the walk below has just left solid.
                buried = false;

                // Descend through whatever was just landed on. Stepping a fixed amount below the face
                // is not enough on its own - a floor slab is usually thicker than any fixed step worth
                // taking - so keep going while still inside it.
                cursor = StepOutOfSolid(x, y, point.Z - 0.25f, bottomZ);
            }

            return count;
        }

        /// <summary>How far the floor walk steps at a time through the solid under a floor.</summary>
        internal const float SolidStep = 8f;

        /// <summary>
        /// Where the floor walk comes out of the solid it has landed in: the first of
        /// <paramref name="cursor"/>, <c>cursor - 8</c>, <c>cursor - 16</c>, ... that is not
        /// <see cref="IsPointSolid"/> against <see cref="GroundMask"/>, or the first at or below
        /// <paramref name="bottomZ"/>. Exactly
        /// <c>while (cursor &gt; bottomZ &amp;&amp; IsPointSolid(x, y, cursor, GroundMask)) cursor -= SolidStep;</c>,
        /// which is what the walk used to run, and what the tests hold this to.
        ///
        /// That loop was most of the floor walk's cost. On rp_downtown_tits_v25 it asked
        /// <see cref="IsPointSolid"/> 392 million times, 99.7% of them answered by the BSP leaf alone being
        /// solid, and 99.5% of them in runs of more than sixteen steps. Nine in ten were the 12,000
        /// units of solid between a floor spanning the map at z = 12,192 and the city under it, and
        /// most of the rest the solid under the lowest floor, down to the bottom of the world; on
        /// cp_badlands, nine in ten were the latter. Each was a full descent of the tree, about
        /// fifteen planes, to land in the same leaf as the step before.
        ///
        /// So the steps are taken in batches, and the tree is walked once per batch rather than once
        /// per step (<see cref="FirstOpenStep(ReadOnlySpan{float}, float, float, int)"/>). The steps are
        /// still produced by subtracting <see cref="SolidStep"/> again and again, as the loop did, so
        /// they are the same floats; only their answers are worked out differently. Batches start small
        /// because the common case is a floor slab, left within a step or two, and grow eightfold while
        /// the solid goes on.
        /// </summary>
        internal float StepOutOfSolid(float x, float y, float cursor, float bottomZ)
        {
            var steps = stepBuffer ??= new float[MaxStepBatch];
            int batch = FirstStepBatch;

            while (true)
            {
                int n = 0;
                while (n < batch && cursor > bottomZ)
                {
                    steps[n++] = cursor;
                    cursor -= SolidStep;
                }

                // Every step was solid, and the last left the column: where the loop would stop too.
                if (n == 0)
                    return cursor;

                int open = FirstOpenStep(steps.AsSpan(0, n), x, y, GroundMask);
                if (open < n)
                    return steps[open];

                batch = Math.Min(batch * 8, MaxStepBatch);
            }
        }

        /// <summary>The steps <see cref="StepOutOfSolid"/> takes first: about what a floor slab needs.</summary>
        private const int FirstStepBatch = 4;

        /// <summary>The most steps <see cref="StepOutOfSolid"/> takes at once - 16,384 units of solid.</summary>
        private const int MaxStepBatch = 2048;

        /// <summary>
        /// <see cref="StepOutOfSolid"/>'s batch of heights. Per thread because the walk runs on many at
        /// once, and kept rather than allocated per call because it is called for every floor found.
        /// </summary>
        [ThreadStatic]
        private static float[]? stepBuffer;

        /// <summary>
        /// The index of the first height in <paramref name="heights"/>, which must be descending, at
        /// which the point over <paramref name="x"/>, <paramref name="y"/> is not
        /// <see cref="IsPointSolid"/>; <c>heights.Length</c> if it is solid at all of them.
        ///
        /// The world tree is walked once for the whole run of points, the way a segment trace walks it,
        /// with each node splitting the run where the points cross its plane. The split has to land
        /// where a point descent would put each point, to the bit, or the answers differ - so it is not
        /// worked out from where the segment crosses the plane, but searched for among the points
        /// themselves, each tested with exactly the expression <see cref="Blocked"/> uses
        /// (<see cref="PlaneDistance"/>). Searching is sound because that expression is monotonic in
        /// height: with the column's x and y fixed it is <c>((nx*x + ny*y) + nz*z) - d</c>, or on an
        /// axial plane a single <c>z - d</c> or a constant, and every rounded multiply, add and subtract
        /// in it is monotonic in its operand, so as z falls the plane's side changes at most once along
        /// the run.
        /// Splits that fall in the same place are found by bisection, and a run that stays on one side
        /// costs two plane tests where the point descents cost one per point.
        ///
        /// A leaf whose contents block the mask answers for every point that reaches it. Anything else
        /// - a leaf whose brushes may hold some points and not others, brush entities, displacements,
        /// props - is asked point by point, in order, exactly as <see cref="IsPointSolid"/> asks it, and
        /// only until one point comes out open.
        /// </summary>
        internal int FirstOpenStep(ReadOnlySpan<float> heights, float x, float y, int mask)
        {
            if (heights.Length == 0)
                return 0;

            int found;
            if (nodes.Length == 0 || leafs.Length == 0)
            {
                // IsLineClear calls a line clear outright without a tree, props aside.
                found = -1;
                for (int k = 0; k < heights.Length && found < 0; k++)
                {
                    if (!IsInsideProp(new BspFile.Vector3(x, y, heights[k]), mask))
                        found = k;
                }
            }
            else
            {
                found = FirstOpenStep(0, heights, 0, heights.Length - 1, x, y, mask);
            }

            return found < 0 ? heights.Length : found;
        }

        /// <summary>
        /// <see cref="FirstOpenStep(ReadOnlySpan{float}, float, float, int)"/> from node
        /// <paramref name="num"/> down, for the points <paramref name="lo"/> to <paramref name="hi"/>
        /// inclusive, all of which reach it; -1 when every one of them is solid. Only the upper part of a
        /// split recurses, as in <see cref="Blocked"/>, so the stack is bounded by the tree's depth.
        /// </summary>
        private int FirstOpenStep(int num, ReadOnlySpan<float> heights, int lo, int hi, float x, float y,
            int mask)
        {
            while (num >= 0)
            {
                ref readonly var node = ref traceNodes[num];

                // Blocked gives up on a node it cannot read and calls the point not blocked by the world.
                if (node.Plane.Axis == AxisMissing)
                    return FirstOpenPastWorld(-1, heights, lo, hi, x, y, mask);

                bool first = InFront(in node, x, y, heights[lo]);
                if (InFront(in node, x, y, heights[hi]) == first)
                {
                    num = first ? node.Child0 : node.Child1;
                    continue;
                }

                // The run crosses the plane once: lo is on the first side, hi on the other. Find the
                // last point on the first side.
                int a = lo, b = hi;
                while (b - a > 1)
                {
                    int mid = a + (b - a) / 2;
                    if (InFront(in node, x, y, heights[mid]) == first)
                        a = mid;
                    else
                        b = mid;
                }

                int found = FirstOpenStep(first ? node.Child0 : node.Child1, heights, lo, a, x, y, mask);
                if (found >= 0)
                    return found;

                num = first ? node.Child1 : node.Child0;
                lo = b;
            }

            int leafIndex = -num - 1;
            if ((uint)leafIndex >= (uint)leafs.Length)
                return FirstOpenPastWorld(-1, heights, lo, hi, x, y, mask);

            if ((leafs[leafIndex].Contents & mask) != 0)
                return -1;

            return FirstOpenPastWorld(leafIndex, heights, lo, hi, x, y, mask);
        }

        /// <summary>
        /// Which side of a node's plane a point descent sends the point at this height to - by
        /// <see cref="PlaneDistance"/>, as <see cref="Blocked"/> works it out, so that it rounds the same
        /// way. <see cref="TryGetLeaf"/> takes the full dot product for every plane, and agrees: see
        /// <see cref="PlaneDistance"/> for why the axial shortcut cannot put a point on the other side.
        /// </summary>
        private static bool InFront(in TraceNode node, float x, float y, float z)
            => PlaneDistance(in node.Plane, new BspFile.Vector3(x, y, z)) >= 0;

        /// <summary>
        /// The rest of <see cref="IsPointSolid"/> for points the world tree has not already called solid,
        /// one at a time and in order: the brushes listed in their leaf (none when
        /// <paramref name="leafIndex"/> is -1, the tree having given up before a leaf), then brush
        /// entities, displacements and props. The first point none of them hold, or -1.
        /// </summary>
        private int FirstOpenPastWorld(int leafIndex, ReadOnlySpan<float> heights, int lo, int hi, float x,
            float y, int mask)
        {
            for (int k = lo; k <= hi; k++)
            {
                var p = new BspFile.Vector3(x, y, heights[k]);

                // One leaf per point, so no brush can come up twice and none are stamped.
                if (leafIndex >= 0 && HitsLeafBrush(leafIndex, new LineQuery(p, p, mask, 0, null, 0)))
                    continue;

                if (BlockedPastWorld(p, p, mask, 0) || IsInsideProp(p, mask))
                    continue;

                return k;
            }

            return -1;
        }

        private struct SurfaceHit
        {
            public float Fraction;
            public BspFile.Vector3 Normal;

            /// <summary>
            /// Surface flags of the face the segment entered through, or zero when the face is not
            /// known - a BSP split plane rather than a brush side, a leaf with no brush list, a
            /// displacement, a prop. Zero therefore means "an ordinary drawn surface as far as this can
            /// tell", which is the reading every consumer wants: an unknown face must not be treated as
            /// nodraw and thrown away.
            /// </summary>
            public int SurfaceFlags;
        }

        /// <summary>Surface flags for a brush side, or zero if it names no usable texinfo.</summary>
        private int SurfaceFlagsOf(BspFile.BrushSide side)
            => (uint)side.TexInfo < (uint)texInfos.Length ? texInfos[side.TexInfo].Flags : 0;

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
                    // plane if none of them answer. Only a brush the ray enters right here answers: the
                    // ray is already inside solid at this crossing, so anything the brushes report
                    // further on is a different surface.
                    int solidLeaf = LeafIndexAt(far, midPoint);
                    if (solidLeaf >= 0 && TryHitLeafBrush(solidLeaf, rayStart, rayEnd, mid, mid, ref hit, mask))
                        return true;

                    float sign = behind ? -1f : 1f;
                    hit.Fraction = mid;
                    hit.Normal = new BspFile.Vector3(
                        sign * plane.Normal.X, sign * plane.Normal.Y, sign * plane.Normal.Z);
                    hit.SurfaceFlags = 0;
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
            if (TryHitLeafBrush(leafIndex, rayStart, rayEnd, startFraction, endFraction, ref hit, mask))
                return true;

            if ((leafs[leafIndex].Contents & mask) != 0)
            {
                hit.Fraction = startFraction;
                hit.Normal = new BspFile.Vector3(0, 0, 1);
                hit.SurfaceFlags = 0;
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
        ///
        /// Only hits inside this leaf's own stretch of the ray, <paramref name="startFraction"/> to
        /// <paramref name="endFraction"/>, are this leaf's to report. A leaf lists every brush that
        /// touches it anywhere, and clipping the whole ray against one of those can land far past the
        /// leaf - where the ray only meets the brush after crossing other leaves, and possibly other
        /// solids. Reporting that hit here returned it ahead of anything nearer in those later leaves.
        /// On ctf_2fort a water leaf listed a sloped brush that its column only reached 24 units under
        /// the drawn floor of the sewer, so every downward trace through the water went straight past
        /// the floor; the strip it covered got no areas, and 240 of the 259 Valve routes the mesh still
        /// failed once clip brushes were ground broke there. A hit past the leaf is not lost by
        /// refusing it: the leaf the ray is in when it does meet the brush lists the brush too.
        /// </summary>
        private bool TryHitLeafBrush(int leafIndex, BspFile.Vector3 rayStart, BspFile.Vector3 rayEnd,
            float startFraction, float endFraction, ref SurfaceHit hit, int mask)
        {
            if ((uint)leafIndex >= (uint)leafBrushes.Length)
                return false;

            var list = leafBrushes[leafIndex];
            if (list is null)
                return false;

            bool found = false;
            float best = float.MaxValue;
            var bestNormal = new BspFile.Vector3(0, 0, 1);
            int bestFlags = 0;

            foreach (int index in list)
            {
                ref readonly var brush = ref brushes[index];
                if ((brush.Contents & mask) == 0)
                    continue;

                float enter = -1f;
                float exit = 1f;
                bool startsOutside = false;
                var enterNormal = new BspFile.Vector3(0, 0, 1);
                int enterFlags = 0;

                for (int i = 0; i < brush.NumSides; i++)
                {
                    int sideIndex = brush.FirstSide + i;
                    if ((uint)sideIndex >= (uint)brushSides.Length) { startsOutside = true; break; }

                    ref readonly var side = ref brushSides[sideIndex];
                    if ((uint)side.PlaneNum >= (uint)planes.Length) { startsOutside = true; break; }

                    ref readonly var plane = ref planes[side.PlaneNum];
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
                        // The entering side is the one the surface normal already comes from, so its
                        // texinfo is the face that was actually crossed rather than any face of the
                        // brush. Read here for the same reason the normal is: this is the only point
                        // that knows which side won.
                        if (f > enter) { enter = f; enterNormal = plane.Normal; enterFlags = SurfaceFlagsOf(side); }
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
                    bestFlags = 0;
                    found = true;
                    continue;
                }

                if (enter > exit || enter < 0f || enter >= 1f)
                    continue;

                // Fractions are already in the whole ray's parameter space now that the whole ray is
                // what was clipped. Ignore anything landing behind this leaf: leaves are visited front
                // to back, so a nearer intersection with that same brush would already have been found
                // in an earlier one, and accepting it here would report a hit the ray had passed.
                if (enter < startFraction - 0.0001f || enter > endFraction + 0.0001f)
                    continue;

                if (found && enter >= best)
                    continue;

                best = enter;
                bestNormal = enterNormal;
                bestFlags = enterFlags;
                found = true;
            }

            if (!found)
                return false;

            hit.Fraction = best;
            hit.Normal = bestNormal;
            hit.SurfaceFlags = bestFlags;
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
                var all = GatherAll(entityModels, a, b);
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

        /// <summary>
        /// A plane as the yes-or-no traces read it: the four numbers they use, and which unit axis the
        /// normal is, if any.
        /// </summary>
        private struct TracePlane
        {
            public float NormalX, NormalY, NormalZ, Distance;

            /// <summary>
            /// 0, 1 or 2 when the normal is exactly one along that axis and exactly zero along the
            /// other two; <see cref="AxisOblique"/> for any other plane; <see cref="AxisMissing"/> when
            /// whatever named it named a plane that does not exist, where the trace gives up.
            /// </summary>
            public int Axis;

            public TracePlane(BspFile.Plane[] planes, int index)
            {
                if ((uint)index >= (uint)planes.Length)
                {
                    this = default;
                    Axis = AxisMissing;
                    return;
                }

                ref readonly var plane = ref planes[index];

                NormalX = plane.Normal.X;
                NormalY = plane.Normal.Y;
                NormalZ = plane.Normal.Z;
                Distance = plane.Distance;
                Axis = AxisOf(plane.Normal);
            }
        }

        private const int AxisOblique = 3;
        private const int AxisMissing = -1;

        /// <summary>
        /// A tree node for the line trace, with its split plane copied in beside it.
        ///
        /// The line trace reads a node and then the plane it names, a second dependent load from a
        /// second array, on every one of tens of millions of node visits a map; this puts the plane in
        /// the same 32 bytes as the children, so a node is one load and half a cache line.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Size = 32)]
        private struct TraceNode
        {
            public TracePlane Plane;
            public int Child0, Child1;
        }

        /// <summary>The line trace's copy of the tree; see <see cref="TraceNode"/>.</summary>
        private TraceNode[] traceNodes = [];

        /// <summary>
        /// Each brush side's plane, by side - where <see cref="brushSides"/> holds only the plane's
        /// number. A brush's sides are consecutive, so clipping against one now reads straight down
        /// this array, where it used to jump from each side to its plane somewhere in
        /// <see cref="planes"/>, one scattered load per face.
        /// </summary>
        private TracePlane[] sidePlanes = [];

        /// <summary>
        /// Builds <see cref="traceNodes"/> and <see cref="sidePlanes"/> once the tree and the brushes are
        /// read. Neither is written after, so every thread tracing shares them.
        ///
        /// Which planes are axial is read off the normal itself rather than off the plane's stored
        /// type: the shortcut is only exact when the normal really is a unit axis to the bit, and a
        /// type is a claim about the normal made by whatever wrote the file.
        /// </summary>
        private void BuildTraceTables()
        {
            var built = new TraceNode[nodes.Length];

            for (int i = 0; i < nodes.Length; i++)
            {
                built[i].Plane = new TracePlane(planes, nodes[i].PlaneNum);
                built[i].Child0 = nodes[i].Child0;
                built[i].Child1 = nodes[i].Child1;
            }

            var sides = new TracePlane[brushSides.Length];
            for (int i = 0; i < brushSides.Length; i++)
                sides[i] = new TracePlane(planes, brushSides[i].PlaneNum);

            traceNodes = built;
            sidePlanes = sides;
        }

        /// <summary>Which unit axis a normal is, exactly, or <see cref="AxisOblique"/>.</summary>
        internal static int AxisOf(BspFile.Vector3 normal)
        {
            if (normal.X == 1f && normal.Y == 0f && normal.Z == 0f) return 0;
            if (normal.X == 0f && normal.Y == 1f && normal.Z == 0f) return 1;
            if (normal.X == 0f && normal.Y == 0f && normal.Z == 1f) return 2;
            return AxisOblique;
        }

        /// <summary>
        /// A point's signed distance from a plane, as the yes-or-no traces take it: the line trace's
        /// walk down the tree, its brush test, and the floor walk.
        ///
        /// Valve's shortcut: on a plane whose normal is a unit axis, the distance is that one coordinate
        /// less the plane's distance, without the dot product. It is the same number. With a normal of
        /// exactly (0, 0, 1), <c>((0*x + 0*y) + 1*z) - d</c> multiplies to zero twice and by one once,
        /// none of which rounds, and adding zeros to <c>z</c> leaves it as it was - so what reaches the
        /// subtraction is <c>z</c> itself, and the subtraction is the same one. The one thing that can
        /// differ is the sign of a zero result: the dot product of a point with a zero coordinate can
        /// come out <c>-0</c> where the shortcut gives <c>+0</c>. Nothing these traces do can tell them
        /// apart. Both are <c>&gt;= 0</c> and <c>&lt;= 0</c> and neither is <c>&lt; 0</c> or
        /// <c>&gt; 0</c>; a zero over the non-zero difference it is divided by is a zero fraction
        /// either way; in the tree a zero fraction moves no coordinate but a zero one, which is only
        /// another such sign, and in a brush it is only ever compared after a non-zero epsilon is added
        /// or taken away. So every node goes the same way, every leaf asked is the same leaf, and every
        /// brush answers the same. Taken for granted is that coordinates are finite: an infinite one
        /// would make the dot product's <c>0*x</c> a NaN that the shortcut never sees, and no map has
        /// one.
        ///
        /// Only the traces that answer yes or no use this. <see cref="TraceSurface"/>,
        /// <see cref="TryHitLeafBrush"/>, <see cref="HullTrace"/> and <see cref="ClipHullToLeaf"/> turn
        /// the same fractions into the positions written to the mesh, where a <c>-0</c> could reach a
        /// coordinate and change the file, so they keep the dot product.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float PlaneDistance(in TracePlane plane, in BspFile.Vector3 p)
        {
            if (plane.Axis < AxisOblique)
                return Unsafe.Add(ref Unsafe.AsRef(in p.X), plane.Axis) - plane.Distance;

            return plane.NormalX * p.X + plane.NormalY * p.Y + plane.NormalZ * p.Z - plane.Distance;
        }

        /// <summary>
        /// What a line trace carries down the tree: the whole ray, which every leaf's brushes are
        /// clipped against (see <see cref="HitsLeafBrush"/>), what it is tested against, and the stamp
        /// that marks the brushes it has already tested.
        /// </summary>
        private readonly ref struct LineQuery(BspFile.Vector3 start, BspFile.Vector3 end, int mask,
            int ignoreContents, int[]? tested, int ray, ColumnBrushes? column = null)
        {
            public readonly BspFile.Vector3 Start = start, End = end;
            public readonly int Mask = mask, IgnoreContents = ignoreContents;

            /// <summary>
            /// The stamp array <see cref="TestedBrushes"/> handed out, or null for a query that cannot
            /// meet a brush twice and should not pay to check.
            /// </summary>
            public readonly int[]? Tested = tested;

            /// <summary>This ray's stamp in <see cref="Tested"/>.</summary>
            public readonly int Ray = ray;

            /// <summary>
            /// For a segment down an open <see cref="Column"/>, what is known of which brushes cannot
            /// block any segment down it; null for any other ray.
            /// </summary>
            public readonly ColumnBrushes? Column = column;
        }

        /// <summary>
        /// Per brush, the last ray on this thread that tested it - Valve's <c>checkcount</c>. A detail
        /// brush is listed in every leaf it touches, and a ray crossing several of them used to clip
        /// against it once in each: on the benchmark maps 7-12% of brush tests repeated one the same
        /// ray had already made. Asking again is pointless as well as slow. A brush is clipped against
        /// the whole ray wherever it is met, so a second test is the same test with the same answer -
        /// and the trace only reaches a second one because the first said no. Skipping it changes
        /// nothing but the work.
        ///
        /// Per thread, because rays run on many at once, and shared by every map a thread traces - the
        /// tests, a batch. That needs no more than the array being long enough: a thread's rays are
        /// numbered upwards, so whatever any earlier ray left in a slot, on this map or another, is a
        /// number this ray is not. Holding no reference to a map, it keeps none alive.
        /// </summary>
        [ThreadStatic] private static int[]? testedStamps;
        [ThreadStatic] private static int testedRay;

        /// <summary>
        /// This thread's brush stamps, long enough for this map, and a fresh stamp for a new ray: one no
        /// brush carries yet. When the numbers run out the stamps are cleared and start again, which is
        /// once per two billion rays.
        /// </summary>
        private int[] TestedBrushes(out int ray)
        {
            var stamps = testedStamps;

            if (stamps is null || stamps.Length < brushes.Length)
            {
                stamps = testedStamps = new int[brushes.Length];
                testedRay = 0;
            }
            else if (testedRay == int.MaxValue)
            {
                Array.Clear(stamps);
                testedRay = 0;
            }

            ray = ++testedRay;
            return stamps;
        }

        /// <summary>
        /// Whether a segment of the ray in <paramref name="query"/>, <paramref name="p1"/> to
        /// <paramref name="p2"/>, meets blocking contents in the tree under <paramref name="num"/>:
        /// a leaf whose contents match the mask, or a brush listed in one that the whole ray enters.
        ///
        /// Three things keep this cheap, none of which changes an answer: nodes are read with their
        /// plane beside them (<see cref="TraceNode"/>), axial planes skip the dot product
        /// (<see cref="PlaneDistance"/>), and a brush is clipped at most once per ray
        /// (<see cref="testedStamps"/>).
        /// </summary>
        private bool Blocked(int num, BspFile.Vector3 p1, BspFile.Vector3 p2, in LineQuery query)
        {
            while (num >= 0)
            {
                // By reference, not by value. This is the hottest loop in the program - tens of millions
                // of node visits per map - and the array is never written during a trace.
                ref readonly var node = ref traceNodes[num];

                if (node.Plane.Axis == AxisMissing)
                    return false;

                float d1 = PlaneDistance(in node.Plane, in p1);
                float d2 = PlaneDistance(in node.Plane, in p2);

                if (d1 >= 0 && d2 >= 0) { num = node.Child0; continue; }
                if (d1 < 0 && d2 < 0) { num = node.Child1; continue; }

                // the segment straddles the plane: split it at the crossing point
                float frac = d1 / (d1 - d2);
                var mid = new BspFile.Vector3(
                    p1.X + frac * (p2.X - p1.X),
                    p1.Y + frac * (p2.Y - p1.Y),
                    p1.Z + frac * (p2.Z - p1.Z));

                bool behind = d1 < 0;
                if (Blocked(behind ? node.Child1 : node.Child0, p1, mid, in query))
                    return true;

                num = behind ? node.Child0 : node.Child1;
                p1 = mid;
            }

            int leafIndex = -num - 1;
            if ((uint)leafIndex >= (uint)leafs.Length)
                return false;

            if ((leafs[leafIndex].Contents & query.Mask) != 0)
                return true;

            return HitsLeafBrush(leafIndex, in query);
        }

        /// <summary>
        /// Whether the ray enters any brush listed in this leaf that matches the mask.
        ///
        /// Given the whole ray rather than this leaf's slice of it, for the reason spelled out on
        /// <see cref="TryHitLeafBrush"/>. Here the sub-segment version failed the opposite way round:
        /// <see cref="SegmentHitsBrush"/> reads "started inside the brush" as blocked, so a sub-segment
        /// beginning exactly on a brush face - the common case, since faces and split planes coincide -
        /// reported every such leaf as blocked whether the ray touched the brush or not. That is the
        /// same grate reading as solid to a clearance test while being invisible to a floor trace.
        ///
        /// That the whole ray is what is clipped is also what makes <see cref="testedStamps"/> exact: a
        /// brush met again in a later leaf would be asked exactly the same question.
        /// </summary>
        private bool HitsLeafBrush(int leafIndex, in LineQuery query)
        {
            if ((uint)leafIndex >= (uint)leafBrushes.Length)
                return false;

            var list = leafBrushes[leafIndex];
            if (list is null)
                return false;

            if (query.Column is not null && LeafMissesColumn(leafIndex, list, in query))
                return false;

            var tested = query.Tested;

            foreach (int index in list)
            {
                ref readonly var brush = ref brushes[index];

                // The list carries every brush either trace purpose could care about; re-check the
                // caller's own mask here so a grate blocks movement but still passes sight, and a
                // window (also generation-mask-only) never blocks either.
                if ((brush.Contents & query.Mask) == 0)
                    continue;

                if (query.IgnoreContents != 0 && (brush.Contents & query.IgnoreContents) != 0)
                    continue;

                if (tested is not null)
                {
                    if (tested[index] == query.Ray)
                        continue;

                    tested[index] = query.Ray;
                }

                if (query.Column is not null && MissesColumn(in brush, index, query.Column))
                    continue;

                if (SegmentHitsBrush(in brush, in query.Start, in query.End))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whether no segment down <paramref name="column"/> can be in the brush: whether
        /// <see cref="SegmentHitsBrush"/> answers no for every one, whatever its ends. Worked out the
        /// first time a segment down the column meets the brush, and remembered for the rest.
        ///
        /// <see cref="SegmentHitsBrush"/> answers no as soon as it reaches a side both ends of the
        /// segment lie in front of, or a side it cannot read, and nothing before that can make it answer
        /// yes. A side the column's top and bottom both lie in front of has every point between them in
        /// front of it too, the plane test being monotonic in height (see <see cref="ColumnStart"/>), so
        /// it is such a side for every segment down the column. Most brushes a column meets are like
        /// that - a detail brush listed in a leaf the column passes through, standing beside the column
        /// rather than across it - and the brush test then cost a segment most of its time.
        /// </summary>
        private bool MissesColumn(in BspFile.Brush brush, int index, ColumnBrushes column)
        {
            var marks = column.Marks;
            int mark = marks[index];

            if (mark == column.Stamp)
                return true;

            if (mark == column.Stamp + 1)
                return false;

            var top = new BspFile.Vector3(column.X, column.Y, column.High);
            var bottom = new BspFile.Vector3(column.X, column.Y, column.Low);
            bool misses = false;

            for (int i = 0; i < brush.NumSides && !misses; i++)
            {
                int sideIndex = brush.FirstSide + i;
                if ((uint)sideIndex >= (uint)sidePlanes.Length)
                {
                    misses = true;
                    break;
                }

                ref readonly var plane = ref sidePlanes[sideIndex];
                misses = plane.Axis == AxisMissing
                         || (PlaneDistance(in plane, in top) > 0 && PlaneDistance(in plane, in bottom) > 0);
            }

            marks[index] = misses ? column.Stamp : column.Stamp + 1;
            return misses;
        }

        /// <summary>
        /// Whether none of the brushes listed in a leaf can block a segment down the query's column:
        /// each is of contents the query does not ask about, or one it ignores, or
        /// <see cref="MissesColumn"/>. Worked out the first time a segment down the column reaches the
        /// leaf, and remembered for the rest, so that a leaf full of detail brushes standing beside the
        /// column is passed over in one look rather than brush by brush. The column has the one mask
        /// and nothing ignored, so the answer holds for every segment down it.
        ///
        /// Passing a leaf over leaves its brushes unstamped for the segment (<see cref="testedStamps"/>);
        /// a later leaf listing one of them tests it again and gets the same answer, no.
        /// </summary>
        private bool LeafMissesColumn(int leafIndex, int[] list, in LineQuery query)
        {
            var column = query.Column!;
            var marks = column.LeafMarks;
            int mark = marks[leafIndex];

            if (mark == column.Stamp)
                return true;

            if (mark == column.Stamp + 1)
                return false;

            bool misses = true;
            foreach (int index in list)
            {
                ref readonly var brush = ref brushes[index];

                if ((brush.Contents & query.Mask) == 0)
                    continue;

                if (query.IgnoreContents != 0 && (brush.Contents & query.IgnoreContents) != 0)
                    continue;

                if (!MissesColumn(in brush, index, column))
                {
                    misses = false;
                    break;
                }
            }

            marks[leafIndex] = misses ? column.Stamp : column.Stamp + 1;
            return misses;
        }

        /// <summary>
        /// What <see cref="MissesColumn"/> and <see cref="LeafMissesColumn"/> have worked out for the
        /// column open on this thread: per brush and per leaf, <see cref="Stamp"/> for one that cannot
        /// block a segment down the column, one more for one that may, and anything else for one not yet
        /// asked about. Each column takes two new stamps, so nothing an earlier column - on this map or
        /// another - left in a slot reads as this one's.
        /// </summary>
        private sealed class ColumnBrushes
        {
            public int[] Marks = [], LeafMarks = [];
            public int Stamp;
            public float X, Y, Low, High;

            public void Open(int brushCount, int leafCount, float x, float y, float low, float high)
            {
                if (Marks.Length < brushCount || LeafMarks.Length < leafCount)
                {
                    Marks = new int[Math.Max(brushCount, Marks.Length)];
                    LeafMarks = new int[Math.Max(leafCount, LeafMarks.Length)];
                    Stamp = 0;
                }
                else if (Stamp >= int.MaxValue - 4)
                {
                    Array.Clear(Marks);
                    Array.Clear(LeafMarks);
                    Stamp = 0;
                }

                Stamp += 2;
                X = x;
                Y = y;
                Low = low;
                High = high;
            }
        }

        /// <summary>
        /// Clips a segment against a convex brush. Each side plane either rejects the segment outright,
        /// pushes the entry fraction forward, or pulls the exit fraction back; anything left over is
        /// inside the brush.
        ///
        /// Reads the brush, its sides and their planes in place. It used to copy each of them, a side
        /// and a twenty-byte plane per face, to use five numbers out of them.
        /// </summary>
        private bool SegmentHitsBrush(in BspFile.Brush brush, in BspFile.Vector3 p1, in BspFile.Vector3 p2)
        {
            const float Epsilon = 0.03125f;

            float enter = -1f;
            float exit = 1f;
            bool startsOutside = false;

            for (int i = 0; i < brush.NumSides; i++)
            {
                int sideIndex = brush.FirstSide + i;
                if ((uint)sideIndex >= (uint)sidePlanes.Length)
                    return false;

                ref readonly var plane = ref sidePlanes[sideIndex];
                if (plane.Axis == AxisMissing)
                    return false;

                float d1 = PlaneDistance(in plane, in p1);
                float d2 = PlaneDistance(in plane, in p2);

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
