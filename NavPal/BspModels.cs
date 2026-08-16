using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace NavPal
{
    /// <summary>
    /// The brush entities that block line of sight - doors, walls, solid func_brush - and a bounding
    /// hierarchy for finding which of them a ray could possibly touch.
    ///
    /// Only worldspawn lives in BSP model 0. Every brush entity is its own model with its own headnode
    /// into the shared node array, so a tracer that walks model 0 alone sees straight through every
    /// closed door in the map. On rp_downtown_meowy that is 511 brush entities; on gm_construct it is 12,
    /// which is why the gm_construct comparison could never have revealed the gap.
    /// </summary>
    public sealed class BspModels
    {
        private const int LumpModels = 14;

        /// <summary>
        /// Classes whose brushes are compiled solid but are not solid at runtime, or are solid to
        /// something other than sight. Traced as written they would wall off large parts of a map.
        ///
        /// Note that materials handle themselves: glass is CONTENTS_WINDOW and grates are CONTENTS_GRATE,
        /// neither of which is in MASK_BLOCKLOS, so func_breakable_surf - 183 of meowy's entities - is
        /// correctly transparent without being named here.
        /// </summary>
        private static readonly string[] NeverBlocks =
        [
            "trigger_",             // every trigger class; compiled solid, non-solid when spawned
            "func_illusionary",
            "func_areaportal",
            "func_areaportalwindow",
            "func_occluder",
            "func_viscluster",
            "func_clip_vphysics",
            "func_vehicleclip",
            "func_ladder",
            "func_nav_",            // nav hints: blocker, avoid, prefer
            "func_precipitation",
            "func_smokevolume",
            "func_dustcloud",
            "func_instance",
            "info_",
            "env_",
            "point_",
            "filter_",
        ];

        public readonly struct Model
        {
            /// <summary>World-space bounds, i.e. the model's own bounds shifted by <see cref="Origin"/>.</summary>
            public readonly BspFile.Vector3 Mins, Maxs;

            /// <summary>
            /// Where the entity places the model. A brush entity whose geometry vbsp recentred stores its
            /// world position here, and the model's tree is expressed relative to it - so a ray has to be
            /// moved into model space before it is traced.
            /// </summary>
            public readonly BspFile.Vector3 Origin;

            public readonly int HeadNode;

            public Model(BspFile.Vector3 mins, BspFile.Vector3 maxs, BspFile.Vector3 origin, int headNode)
            {
                Mins = mins; Maxs = maxs; Origin = origin; HeadNode = headNode;
            }
        }

        private Model[] models = [];
        private BvhNode[] bvh = [];

        public int BlockingModelCount => models.Length;

        private struct BvhNode
        {
            public BspFile.Vector3 Mins, Maxs;
            public int Left;        // child index, or -1 for a leaf
            public int First, Count; // model range when a leaf
        }

        public static BspModels Load(string path, BspFile bsp)
        {
            var result = new BspModels();

            using var stream = File.OpenRead(path);
            using var r = new BinaryReader(stream);

            r.BaseStream.Seek(8, SeekOrigin.Begin);

            var offsets = new (int Offset, int Length)[BspFile.HeaderLumps];
            for (int i = 0; i < BspFile.HeaderLumps; i++)
            {
                offsets[i] = (r.ReadInt32(), r.ReadInt32());
                r.ReadInt32();
                r.ReadInt32();
            }

            var raw = ReadModels(r, offsets[LumpModels]);
            var blocking = SelectBlocking(bsp.EntityLump, raw.Length);

            var kept = new List<Model>(blocking.Count);
            foreach (var (index, origin) in blocking)
            {
                if (index <= 0 || index >= raw.Length)
                    continue;

                var m = raw[index];
                kept.Add(new Model(
                    new BspFile.Vector3(m.Mins.X + origin.X, m.Mins.Y + origin.Y, m.Mins.Z + origin.Z),
                    new BspFile.Vector3(m.Maxs.X + origin.X, m.Maxs.Y + origin.Y, m.Maxs.Z + origin.Z),
                    origin,
                    m.HeadNode));
            }

            result.models = kept.ToArray();
            result.BuildBvh();

            return result;
        }

        private static Model[] ReadModels(BinaryReader r, (int Offset, int Length) lump)
        {
            const int SizeOf = 48;
            if (lump.Length < SizeOf) return [];

            var bytes = LzmaLump.Read(r, lump.Offset, lump.Length);
            var result = new Model[bytes.Length / SizeOf];

            using var ms = new MemoryStream(bytes);
            using var lr = new BinaryReader(ms);

            for (int i = 0; i < result.Length; i++)
            {
                var mins = new BspFile.Vector3(lr.ReadSingle(), lr.ReadSingle(), lr.ReadSingle());
                var maxs = new BspFile.Vector3(lr.ReadSingle(), lr.ReadSingle(), lr.ReadSingle());
                lr.BaseStream.Seek(12, SeekOrigin.Current); // lump origin; the entity's keyvalue is what positions it
                int headNode = lr.ReadInt32();
                lr.BaseStream.Seek(8, SeekOrigin.Current); // firstface, numfaces

                result[i] = new Model(mins, maxs, default, headNode);
            }

            return result;
        }

        /// <summary>
        /// Model indices referenced by entities that block sight. Doors and moving brushes are taken in
        /// their authored position, which is where they sit when the map spawns and therefore what the
        /// engine's own visibility pass traced against.
        /// </summary>
        private static List<(int Index, BspFile.Vector3 Origin)> SelectBlocking(string entityLump, int modelCount)
        {
            var result = new List<(int, BspFile.Vector3)>();

            foreach (Match block in Regex.Matches(entityLump, @"\{(.*?)\}", RegexOptions.Singleline))
            {
                string body = block.Groups[1].Value;

                string classname = KeyValue(body, "classname");
                string model = KeyValue(body, "model");

                if (!model.StartsWith('*') || !int.TryParse(model[1..], out int index))
                    continue;

                if (index <= 0 || index >= modelCount)
                    continue;

                if (!Blocks(classname, body))
                    continue;

                result.Add((index, ParseVector(KeyValue(body, "origin"))));
            }

            return result;
        }

        private static bool Blocks(string classname, string body)
        {
            foreach (string prefix in NeverBlocks)
            {
                if (classname.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            // func_brush carries its own solidity: 0 toggle, 1 never, 2 always
            if (classname.Equals("func_brush", StringComparison.OrdinalIgnoreCase) &&
                KeyValue(body, "solidity") == "1")
            {
                return false;
            }

            return true;
        }

        private static BspFile.Vector3 ParseVector(string value)
        {
            var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return default;

            var c = System.Globalization.CultureInfo.InvariantCulture;
            return float.TryParse(parts[0], c, out float x)
                && float.TryParse(parts[1], c, out float y)
                && float.TryParse(parts[2], c, out float z)
                ? new BspFile.Vector3(x, y, z)
                : default;
        }

        private static string KeyValue(string body, string key)
        {
            var match = Regex.Match(body, $"\"{Regex.Escape(key)}\"\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private void BuildBvh()
        {
            if (models.Length == 0)
                return;

            var nodes = new List<BvhNode>(models.Length * 2);
            var order = new int[models.Length];
            for (int i = 0; i < order.Length; i++) order[i] = i;

            Build(nodes, order, 0, order.Length);

            // reorder the models so each leaf owns a contiguous run
            var sorted = new Model[models.Length];
            for (int i = 0; i < order.Length; i++) sorted[i] = models[order[i]];
            models = sorted;

            bvh = nodes.ToArray();
        }

        /// <summary>Median split on the widest axis. Returns the index of the node it appended.</summary>
        private int Build(List<BvhNode> nodes, int[] order, int first, int count)
        {
            const int LeafSize = 4;

            var node = new BvhNode
            {
                Mins = new BspFile.Vector3(float.MaxValue, float.MaxValue, float.MaxValue),
                Maxs = new BspFile.Vector3(float.MinValue, float.MinValue, float.MinValue),
                Left = -1,
                First = first,
                Count = count,
            };

            for (int i = first; i < first + count; i++)
            {
                var m = models[order[i]];
                node.Mins = Min(node.Mins, m.Mins);
                node.Maxs = Max(node.Maxs, m.Maxs);
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
                Centre(models[a], axis).CompareTo(Centre(models[b], axis))));

            int half = count / 2;
            Build(nodes, order, first, half);
            int right = Build(nodes, order, first + half, count - half);

            node = nodes[self];
            node.Left = right; // left child is always self + 1; store the right child's index
            node.Count = 0;    // marks an interior node
            nodes[self] = node;

            return self;
        }

        private static float Centre(Model m, int axis) => axis switch
        {
            0 => (m.Mins.X + m.Maxs.X) * 0.5f,
            1 => (m.Mins.Y + m.Maxs.Y) * 0.5f,
            _ => (m.Mins.Z + m.Maxs.Z) * 0.5f,
        };

        private static BspFile.Vector3 Min(BspFile.Vector3 a, BspFile.Vector3 b) =>
            new(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Min(a.Z, b.Z));

        private static BspFile.Vector3 Max(BspFile.Vector3 a, BspFile.Vector3 b) =>
            new(MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y), MathF.Max(a.Z, b.Z));

        /// <summary>
        /// Head nodes of every model whose bounds the segment could touch, written to
        /// <paramref name="heads"/> and <paramref name="origins"/>. Returns how many were written.
        ///
        /// <paramref name="truncated"/> is the half of this that was missing, and it mattered. The
        /// output buffer is fixed size, and on overflow this simply stopped gathering and returned what
        /// it had. A ray crossing more brush entities than the caller's buffer held therefore never saw
        /// the rest of them, and every trace built on this - line, surface, hull - went on to report the
        /// ray clear of geometry it had never been tested against. That is a fail-open in a tracer whose
        /// every other guard deliberately fails closed, and it is reachable: a long sight ray across a
        /// map with hundreds of doors and breakables can overlap a great many of their bounding boxes.
        ///
        /// Reporting it rather than silently sizing the buffer up lets the caller keep a small
        /// stack-allocated fast path and pay for a full-sized one only on the rays that need it.
        /// </summary>
        public int Gather(BspFile.Vector3 a, BspFile.Vector3 b, Span<int> heads,
            Span<BspFile.Vector3> origins, out bool truncated)
        {
            truncated = false;

            if (bvh.Length == 0)
                return 0;

            // Depth-first over a median-split tree, so the stack only ever holds one pending sibling per
            // level: 64 slots covers a tree of 2^63 models and cannot overflow for any real map. The
            // guard below is kept anyway, and now reports rather than dropping in silence, because a
            // bound that holds by construction is exactly the kind that stops holding unnoticed if the
            // build strategy ever changes.
            Span<int> stack = stackalloc int[64];
            int top = 0;
            stack[top++] = 0;
            int found = 0;

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
                        stack[top++] = index + 1;   // left
                        stack[top++] = node.Left;   // right
                    }
                    else
                    {
                        truncated = true;
                    }

                    continue;
                }

                for (int i = node.First; i < node.First + node.Count; i++)
                {
                    var model = models[i];
                    if (!SegmentHitsBox(a, b, model.Mins, model.Maxs))
                        continue;

                    // Flagged only once a model has actually been dropped, not merely because the
                    // buffer happens to be full - a ray whose last overlapping model exactly fills it
                    // has lost nothing and must not send the caller down the slow path.
                    if (found >= heads.Length || found >= origins.Length)
                    {
                        truncated = true;
                        break;
                    }

                    heads[found] = model.HeadNode;
                    origins[found] = model.Origin;
                    found++;
                }
            }

            return found;
        }

        /// <summary>Slab test of a finite segment against an axis-aligned box.</summary>
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
