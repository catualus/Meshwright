using System;
using System.Collections.Generic;
using System.IO;

namespace Meshwright
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

            // Other games' trigger volumes, which are not named trigger_ but are just as non-solid.
            // TF2 puts every spawn inside a func_respawnroom, so with these solid the flood started
            // from 30 spawns on ctf_2fort reached 32 cells and the map got no mesh at all.
            // func_respawnroom also covers func_respawnroomvisualizer, which is solid only to the
            // other team; Valve's own TF2 meshes run through it.
            "func_respawnroom",
            "func_regenerate",
            "func_capturezone",
            "func_nobuild",
            "func_nogrenades",
            "func_flagdetectionzone",
            "func_powerupvolume",
            "func_suggested_build",
            "func_upgradestation",
            "func_croc",
            "func_tfbot_hint",
            "func_achievement",
            "func_passtime",
            "dispenser_touch_trigger",
            "func_bomb_target",
            "func_buyzone",
            "func_hostage_rescue",
            "func_dustmotes",
        ];

        /// <summary>
        /// Whether a solid brush entity is one a player walks through as far as the mesh is concerned:
        /// what the engine's own generator passes with <c>WALK_THRU_EVERYTHING</c> (IsEntityWalkable).
        ///
        /// Doors open, breakables break and toggle brushes switch off, so Valve's nav_generate links the
        /// ground on either side of them. Treated as walls here, every room behind a brush door went
        /// unsampled on a fresh run and unconnected on a seeded one: 42 of 47 func_door doorways on
        /// rp_downtown_tits_v25, and every spawn exit on TF2. They still block sight, which is how the
        /// engine's visibility pass sees a closed door, so this applies to movement traces only.
        ///
        /// Glass (func_breakable_surf) is left solid on purpose, unlike the engine: on a roleplay map a
        /// path through a shop window is not one a bot or NPC should take.
        /// </summary>
        private static bool WalksThrough(string classname, Dictionary<string, string> entity)
        {
            if (classname.StartsWith("func_door", StringComparison.OrdinalIgnoreCase))
                return true;

            // A breakable with no health cannot be broken, and the engine treats it as a wall.
            if (classname.Equals("func_breakable", StringComparison.OrdinalIgnoreCase))
                return !entity.TryGetValue("health", out string? health) ||
                       !int.TryParse(health, out int h) || h > 0;

            // Solidity 0 toggle (the default), 1 never (handled in Blocks), 2 always. The engine walks
            // through every toggle brush. That is right for the ones map logic switches off - TF2's
            // round gates and spawn doors are func_brush toggles (cp_dustbowl's gate_2nd_spawn cut
            // 525 of 2,000 routes while it was solid) - but roleplay maps use plain func_brush walls
            // with the default solidity everywhere. A toggle with no name can never be switched off,
            // since nothing can target it, so it stays a wall; a named one, or one that starts off,
            // is walked through.
            if (classname.Equals("func_brush", StringComparison.OrdinalIgnoreCase))
            {
                string solidity = entity.TryGetValue("solidity", out string? s) ? s : "0";
                if (solidity != "0")
                    return false;

                bool startsOff = entity.TryGetValue("StartDisabled", out string? off) && off == "1";
                bool switchable = entity.TryGetValue("targetname", out string? name) && name.Length > 0;
                return startsOff || switchable;
            }

            return false;
        }

        /// <summary>
        /// Whether a brush entity a player walks through (<see cref="WalksThrough"/>) is nonetheless a
        /// floor while the map is running as it starts: a toggle func_brush that starts switched on.
        ///
        /// Walking through those is right for the question it answers - whether a body can get past
        /// one, so that the ground either side of a round gate is linked for when the gate opens. It is
        /// wrong for the other question, whether the brush is something to stand on, because until map
        /// logic switches it off it is exactly that. TF2 builds floors this way that later go away:
        /// cp_dustbowl's cap1_stairs is the only flight up to the raised yard of the first stage until
        /// the point falls, and pl_badwater's end_pit_destroy_brushes is the floor over the final pit
        /// until the bomb goes off. With both walked through, the yard had no way up and so no mesh at
        /// all, and the pit cover got none - its floor was read as the pit below. Valve's meshes cover
        /// both, and standing on these brushes took the Valve routes the mesh misses from 96 to 16 of
        /// 2,000 on cp_dustbowl and from 53 to 23 on pl_badwater.
        ///
        /// Doors and breakables are not floors in this sense: a closed door's top is in its frame, and
        /// a breakable is walked through only because it can be broken, not because it goes away.
        /// </summary>
        private static bool StandsOn(string classname, Dictionary<string, string> entity)
        {
            if (!classname.Equals("func_brush", StringComparison.OrdinalIgnoreCase))
                return false;

            string solidity = entity.TryGetValue("solidity", out string? s) ? s : "0";
            bool startsOff = entity.TryGetValue("StartDisabled", out string? off) && off == "1";
            return solidity == "0" && !startsOff;
        }

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

        /// <summary>
        /// The map's blocking brush entities. With <paramref name="forMovement"/>, leaves out the ones a
        /// player walks through (<see cref="WalksThrough"/>): that set is for the traces that decide where
        /// a body can stand and go, and the full set is for sight. With <paramref name="forGround"/> as
        /// well, keeps those of them that are a floor to stand on while the map starts
        /// (<see cref="StandsOn"/>), for the traces that look for ground.
        /// </summary>
        public static BspModels Load(string path, BspFile bsp, bool forMovement = false, bool forGround = false)
            => Load(path, count => SelectBlocking(bsp.EntityLump, count, forMovement, forGround));

        /// <summary>
        /// The map's kill volumes (<see cref="SelectKillVolumes"/>): not anything a trace stops at, but
        /// the space a player standing on the mesh must not be in. Kept as a model set of their own so
        /// the same tree walk and bounding hierarchy answer whether a point is inside one.
        /// </summary>
        public static BspModels LoadKillVolumes(string path, BspFile bsp)
            => Load(path, count => SelectKillVolumes(bsp.EntityLump, count));

        private static BspModels Load(string path,
            Func<int, List<(int Index, BspFile.Vector3 Origin)>> select)
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
            var blocking = select(raw.Length);

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
        internal static List<(int Index, BspFile.Vector3 Origin)> SelectBlocking(string entityLump, int modelCount,
            bool forMovement = false, bool forGround = false)
        {
            var result = new List<(int, BspFile.Vector3)>();

            // The quote-aware reader rather than a \{(.*?)\} regex, which cut an entity short at the
            // first brace inside a value (lua_run code, say) and read the rest as a separate entity.
            foreach (var entity in BspEntities.Parse(entityLump))
            {
                string classname = entity.TryGetValue("classname", out string? c) ? c : "";
                string model = entity.TryGetValue("model", out string? m) ? m : "";

                if (!model.StartsWith('*') || !int.TryParse(model[1..], out int index))
                    continue;

                if (index <= 0 || index >= modelCount)
                    continue;

                if (!Blocks(classname, entity))
                    continue;

                if (forMovement && WalksThrough(classname, entity) && !(forGround && StandsOn(classname, entity)))
                    continue;

                result.Add((index, ParseVector(entity.TryGetValue("origin", out string? o) ? o : "")));
            }

            return result;
        }

        private static bool Blocks(string classname, Dictionary<string, string> entity)
        {
            foreach (string prefix in NeverBlocks)
            {
                if (classname.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            // func_brush carries its own solidity: 0 toggle, 1 never, 2 always
            if (classname.Equals("func_brush", StringComparison.OrdinalIgnoreCase) &&
                entity.TryGetValue("solidity", out string? solidity) && solidity == "1")
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Damage per second at and above which a trigger_hurt counts as a death trap rather than a
        /// hazard. trigger_hurt deals its damage per second, in two half-second ticks, so this kills a
        /// 100-health player - Half-Life 2 and Garry's Mod - inside a second and most TF2 classes in two.
        /// Death pits are set far higher (pl_upward's valley is 9,999); what stays under it is the slow
        /// kind a player walks through and out of, a hot pipe or a stream, whose ground is still ground.
        /// </summary>
        internal const float LethalDamage = 100f;

        private const int TriggerAllowClients = 0x01;   // SF_TRIGGER_ALLOW_CLIENTS
        private const int TriggerAllowAll = 0x40;       // SF_TRIGGER_ALLOW_ALL

        /// <summary>
        /// The trigger_hurt volumes that kill every player who walks into them, from the moment the map
        /// starts: death pits, and the out-of-bounds ground under them. A body that can reach that
        /// ground dies there, so the flood must not mesh it, and nothing then links into it.
        ///
        /// The engine's generator does not do this - nav_generate traces with WALK_THRU_EVERYTHING and
        /// never looks at triggers - but Valve's shipped TF2 meshes do not cover that ground all the
        /// same. pl_upward has one such volume, a 7,168 by 7,168 unit box under the whole map with its
        /// top at z = -96, dealing 9,999. Valve's mesh has 8 stray areas inside it; Meshwright put 1,510
        /// there (9 million square units, over a quarter of its walkable area), and the valley floor they
        /// covered was reachable only by falling in, which is why barely two thirds of the mesh was one
        /// strongly connected piece.
        ///
        /// Which volumes count is narrow on purpose, and each rule is a volume Valve's meshes do cover:
        /// <list type="bullet">
        /// <item>Started disabled: off until map logic says otherwise. pl_badwater's kill brush between
        /// its two closing doors and pl_upward's spawn-room kill both start disabled, and Valve's meshes
        /// have an area in each. (The end-of-round pit kills on both payload maps start disabled too,
        /// with 51 and 93 Valve areas inside, though those are trigger_multiple and never count.)</item>
        /// <item>Filtered: lethal to some players and not others. TF2's spawn-room kills hurt one team
        /// (filter_activator_tfteam), and the payload cart's hurt passes only non-players. Neither
        /// makes ground anyone stands on impassable for everyone. A filter name that names nothing is
        /// no filter, as it is to the engine.</item>
        /// <item>Parented: it moves with its parent, so where it was compiled says nothing about where
        /// it kills.</item>
        /// <item>Below <see cref="LethalDamage"/>, or not touching players at all.</item>
        /// </list>
        /// </summary>
        internal static List<(int Index, BspFile.Vector3 Origin)> SelectKillVolumes(string entityLump, int modelCount)
        {
            var result = new List<(int, BspFile.Vector3)>();
            var entities = BspEntities.Parse(entityLump);

            var filters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entity in entities)
            {
                if (entity.TryGetValue("classname", out string? c) &&
                    c.StartsWith("filter_", StringComparison.OrdinalIgnoreCase) &&
                    entity.TryGetValue("targetname", out string? name) && name.Length > 0)
                {
                    filters.Add(name);
                }
            }

            foreach (var entity in entities)
            {
                string classname = entity.TryGetValue("classname", out string? c) ? c : "";
                string model = entity.TryGetValue("model", out string? m) ? m : "";

                if (!classname.Equals("trigger_hurt", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!model.StartsWith('*') || !int.TryParse(model[1..], out int index))
                    continue;

                if (index <= 0 || index >= modelCount)
                    continue;

                if (!Kills(entity, filters))
                    continue;

                result.Add((index, ParseVector(entity.TryGetValue("origin", out string? o) ? o : "")));
            }

            return result;
        }

        /// <summary>Whether a trigger_hurt kills every player who stands in it while the map starts.</summary>
        private static bool Kills(Dictionary<string, string> entity, HashSet<string> filters)
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;

            if (entity.TryGetValue("StartDisabled", out string? off) && off == "1")
                return false;

            if (entity.TryGetValue("parentname", out string? parent) && parent.Length > 0)
                return false;

            if (entity.TryGetValue("filtername", out string? filter) && filters.Contains(filter))
                return false;

            // A missing value is read as nothing: a compiled map always writes both, and guessing at a
            // default here would only ever delete ground.
            if (!entity.TryGetValue("damage", out string? damage) ||
                !float.TryParse(damage, System.Globalization.NumberStyles.Float, culture, out float perSecond) ||
                perSecond < LethalDamage)
            {
                return false;
            }

            return entity.TryGetValue("spawnflags", out string? flags) &&
                   int.TryParse(flags, System.Globalization.NumberStyles.Integer, culture, out int f) &&
                   (f & (TriggerAllowClients | TriggerAllowAll)) != 0;
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
