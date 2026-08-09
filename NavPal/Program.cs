using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NavPal
{
    /// <summary>
    /// Command line entry point. Compile Pal invokes this as a compile step, the same way it shells
    /// out to vbsp/bspzip, but it is deliberately usable standalone so the nav work can be tested
    /// without launching the GUI or the game.
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Usage();
                return 1;
            }

            try
            {
                // Global, not per-command: every pass in every subcommand reads NavConcurrency.MaxThreads
                // at the point it starts, so setting it once here before dispatch is enough to cover all
                // of them. Parsed ahead of the switch rather than left for FlagValue inside each command,
                // since not every command routes through that helper and this needs to apply uniformly.
                if (TryGetThreadCount(args, out int threads))
                    NavConcurrency.MaxThreads = threads;

                return args[0].ToLowerInvariant() switch
                {
                    "verify" => Verify(args),
                    "info" => Info(args),
                    "bsp" => BspInfo(args),
                    "ladders" => FindLadders(args),
                    "build-ladders" => BuildLadders(args),
                    "vis-stats" => VisStats(args),
                    "vis-debug" => VisDebug(args),
                    "vis-trace" => VisTrace(args),
                    "vis-compare" => VisCompare(args),
                    "vis-why" => VisWhy(args),
                    "build-visibility" => BuildVisibility(args),
                    "vis-count" => VisCount(args),
                    "probe" => Probe(args),
                    "sample-rays" => SampleRays(args),
                    "stairs" => Stairs(args),
                    "build-movement" => BuildMovement(args),
                    "diff-connections" => DiffConnections(args),
                    "build-areas" => BuildAreas(args),
                    "compare-areas" => CompareAreas(args),
                    "normals" => Normals(args),
                    "shape" => Shape(args),
                    "fit" => Fit(args),
                    "floors" => Floors(args),
                    "spots" => Spots(args),
                    "build-spots" => BuildSpots(args),
                    "hull-check" => HullCheck(args),
                    "area" => AreaInfo(args),
                    "why-not-connected" => WhyNotConnected(args),
                    "compare-spots" => CompareSpots(args),
                    "fix-connections" => FixConnections(args),
                    "reach" => Reach(args),
                    _ => UnknownCommand(args[0]),
                };
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 1;
            }
        }

        /// <summary>
        /// Reads a global "-threads N" flag, wherever it appears in the argument list. Not part of any
        /// one command's own flag parsing because it has to apply before the command even starts.
        /// </summary>
        private static bool TryGetThreadCount(string[] args, out int threads)
        {
            threads = 0;
            int i = Array.FindIndex(args, a => a.Equals("-threads", StringComparison.OrdinalIgnoreCase));

            if (i < 0 || i + 1 >= args.Length)
                return false;

            if (!int.TryParse(args[i + 1], out threads) || threads < 1)
            {
                throw new ArgumentException(
                    $"-threads expects a positive integer, got '{args[i + 1]}'");
            }

            return true;
        }

        private static void Usage()
        {
            Console.WriteLine("navpal - Source engine navigation mesh tool");
            Console.WriteLine();
            Console.WriteLine("  global flags (any command):");
            Console.WriteLine("  -threads N              Cap parallel work at N threads (default: every core)");
            Console.WriteLine();
            Console.WriteLine("  build passes (each writes a new .nav; chain them in this order):");
            Console.WriteLine("  navpal build-ladders    <file.bsp> <file.nav> [-o out.nav]");
            Console.WriteLine("      Add nav ladders for the BSP's ladder brushes. The engine builds none");
            Console.WriteLine("      outside Left 4 Dead, so brush ladders are otherwise invisible to bots.");
            Console.WriteLine("  navpal build-areas      <file.bsp> <file.nav> [-o out.nav] [-noconnect]");
            Console.WriteLine("                          [-scratch] [-reference known-good.nav]");
            Console.WriteLine("      Flood the world for walkable ground the mesh does not cover - rooftops,");
            Console.WriteLine("      lift platforms - add areas for it, and connect them in.");
            Console.WriteLine("      -scratch   discard the mesh and generate from player spawns alone");
            Console.WriteLine("      -reference score the result against a known-good mesh and explain each miss");
            Console.WriteLine("  navpal build-movement   <file.bsp> <file.nav> [-o out.nav]");
            Console.WriteLine("      Mark stairs, add step/jump/drop connections, connect lift stops.");
            Console.WriteLine("  navpal build-visibility <file.bsp> <file.nav> [-o out.nav] [-d dist] [-nocompress]");
            Console.WriteLine("      Compute area-to-area visibility offline, across all cores.");
            Console.WriteLine();
            Console.WriteLine("  inspection:");
            Console.WriteLine("  navpal info    <file.nav>                  Summarise a nav mesh");
            Console.WriteLine("  navpal verify  <file.nav>                  Round-trip and diff byte-for-byte");
            Console.WriteLine("  navpal bsp     <file.bsp>                  Summarise BSP geometry lumps");
            Console.WriteLine("  navpal ladders <file.bsp>                  List ladder brushes in a BSP");
            Console.WriteLine();
            Console.WriteLine("  diagnostics:");
            Console.WriteLine("  navpal vis-stats        <file.bsp> <file.nav> [dist]   Pair-set funnel only");
            Console.WriteLine("  navpal vis-trace        <file.bsp> <file.nav> [dist]   Full pipeline, no write");
            Console.WriteLine("  navpal vis-compare      <file.bsp> <analysed.nav>      Score vs an analysed mesh");
            Console.WriteLine("  navpal vis-why          <file.bsp> <file.nav> <id> [id]  What blocks a pair");
            Console.WriteLine("  navpal vis-debug        <file.bsp> <file.nav>          Cluster lookup failures");
            Console.WriteLine("  navpal vis-count        <file.nav> <id> [id...]        Resolved visible counts");
            Console.WriteLine("  navpal sample-rays      <file.bsp> <file.nav> <n> [seed]  Sight lines + verdicts");
            Console.WriteLine("  navpal probe            <file.bsp> x1 y1 z1 x2 y2 z2   Trace one segment");
            Console.WriteLine("  navpal stairs           <file.bsp> <file.nav>          Score stair detection");
            Console.WriteLine("  navpal diff-connections <before.nav> <after.nav> <n>   Added links as endpoints");
            Console.WriteLine("  navpal compare-areas    <reference.nav> <candidate.nav>  Ground coverage scoring");
            Console.WriteLine("  navpal shape            <file.bsp> <file.nav>          Area shape + solid overlap");
            Console.WriteLine("  navpal fit              <file.bsp> <file.nav>          Areas floating above ground or over air");
            Console.WriteLine("  navpal fix-connections  <file.nav> [-o out.nav]        Redundant-shortcut count");
            Console.WriteLine("  navpal reach            <file.bsp> x y z [-radius u]   Explain a coverage miss");
        }

        /// <summary>
        /// Adds CNavLadder records for every ladder brush in the BSP and wires them to nav areas.
        /// Writes to a new file by default rather than in place - nav meshes are expensive to rebuild.
        /// </summary>
        private static int BuildLadders(string[] args)
        {
            if (args.Length < 3)
                throw new ArgumentException("expected: build-ladders <file.bsp> <file.nav> [-o out.nav]");

            string bspPath = args[1];
            string navPath = args[2];

            if (!File.Exists(bspPath)) throw new FileNotFoundException($"no such file: {bspPath}");
            if (!File.Exists(navPath)) throw new FileNotFoundException($"no such file: {navPath}");

            int outFlag = Array.FindIndex(args, a => a.Equals("-o", StringComparison.OrdinalIgnoreCase));
            string outPath = outFlag >= 0 && outFlag + 1 < args.Length
                ? args[outFlag + 1]
                : Path.Combine(Path.GetDirectoryName(navPath) ?? ".",
                    Path.GetFileNameWithoutExtension(navPath) + ".ladders.nav");

            // The tracer is what stops a ladder wiring itself to whatever happens to be nearest through
            // a wall; without it the probe only knows about distance.
            var (bsp, vis) = LoadBsp(bspPath);
            var brushes = LadderFinder.Find(bsp);
            var nav = NavFile.Load(navPath);

            Console.WriteLine($"bsp   {Path.GetFileName(bspPath)}: {brushes.Count:N0} ladder brushes");
            Console.WriteLine($"nav   {Path.GetFileName(navPath)}: {nav.Areas.Count:N0} areas, {nav.Ladders.Count:N0} existing ladders");

            var result = LadderBuilder.Build(nav, brushes, vis);

            Console.WriteLine($"      added {result.LaddersAdded:N0} ladders");
            Console.WriteLine($"      bottom connections {result.BottomConnected:N0}, top connections {result.TopConnected:N0}");
            if (result.Unresolved > 0)
                Console.WriteLine($"      skipped {result.Unresolved:N0} (no nav area at base)");

            foreach (var warning in result.Warnings.Take(10))
                Console.WriteLine($"      warn: {warning}");
            if (result.Warnings.Count > 10)
                Console.WriteLine($"      ... and {result.Warnings.Count - 10:N0} more warnings");

            ReportLadderAttachment(nav);

            nav.Save(outPath);
            Console.WriteLine($"out   {outPath}  ({new FileInfo(outPath).Length:N0} bytes)");

            // reload what we just wrote: proves the file is still structurally valid
            var reloaded = NavFile.Load(outPath);
            long ladderRefs = reloaded.Areas.Sum(a => a.Ladders.Sum(l => (long)l.Count));
            Console.WriteLine($"check reloaded OK: {reloaded.Areas.Count:N0} areas, {reloaded.Ladders.Count:N0} ladders, {ladderRefs:N0} area references");

            return 0;
        }

        /// <summary>
        /// Summarises what each ladder ended up attached to.
        ///
        /// Two things go wrong in ways a total count cannot show. A ladder can land on an area that
        /// happens to be enormous, in which case a bot heading for the ladder is really heading for a
        /// strip that stretches out of sight; and it can pick up several distinct areas at one end,
        /// which draws as a fan of connections going nowhere sensible. Both are visible in game long
        /// before they are visible in a "ladders added: 24" line.
        /// </summary>
        private static void ReportLadderAttachment(NavFile nav)
        {
            if (nav.Ladders.Count == 0)
                return;

            var byId = new Dictionary<uint, NavArea>();
            foreach (var area in nav.Areas)
                byId[area.Id] = area;

            var topCounts = new SortedDictionary<int, int>();
            var sizes = new List<float>();
            var oversized = new List<string>();

            foreach (var ladder in nav.Ladders)
            {
                var tops = new HashSet<uint>();
                foreach (uint id in (ReadOnlySpan<uint>)[ladder.TopForwardAreaId, ladder.TopBehindAreaId,
                             ladder.TopLeftAreaId, ladder.TopRightAreaId])
                {
                    if (id != 0) tops.Add(id);
                }

                topCounts[tops.Count] = topCounts.GetValueOrDefault(tops.Count) + 1;

                var attached = new List<uint>(tops);
                if (ladder.BottomAreaId != 0) attached.Add(ladder.BottomAreaId);

                foreach (uint id in attached)
                {
                    if (!byId.TryGetValue(id, out var area))
                        continue;

                    var b = NavGeometry.GetBounds(area);
                    float longest = MathF.Max(b.Width, b.Depth);
                    sizes.Add(longest);

                    if (longest > 512f && oversized.Count < 6)
                    {
                        oversized.Add($"ladder at ({ladder.Bottom[0]:F0} {ladder.Bottom[1]:F0} " +
                                      $"{ladder.Bottom[2]:F0}) -> area {longest:F0} units long");
                    }
                }
            }

            sizes.Sort();

            Console.WriteLine($"      top areas per ladder: " +
                              string.Join(", ", topCounts.Select(p => $"{p.Value}x{p.Key}")));

            if (sizes.Count > 0)
            {
                Console.WriteLine($"      attached area longest side: median {sizes[sizes.Count / 2]:F0}, " +
                                  $"max {sizes[^1]:F0}");
            }

            foreach (string note in oversized)
                Console.WriteLine($"      big: {note}");
        }

        private static string RequireBsp(string[] args)
        {
            if (args.Length < 2)
                throw new ArgumentException("expected a path to a .bsp file");
            if (!File.Exists(args[1]))
                throw new FileNotFoundException($"no such file: {args[1]}");
            return args[1];
        }

        private static int BspInfo(string[] args)
        {
            string path = RequireBsp(args);
            var bsp = BspFile.Load(path);

            Console.WriteLine($"file            {Path.GetFileName(path)}  ({new FileInfo(path).Length:N0} bytes)");
            Console.WriteLine($"version         {bsp.Version}  (map revision {bsp.MapRevision})");
            Console.WriteLine($"planes          {bsp.Planes.Length:N0}");
            Console.WriteLine($"texinfos        {bsp.TexInfos.Length:N0}");
            Console.WriteLine($"texdatas        {bsp.TexDatas.Length:N0}");
            Console.WriteLine($"materials       {bsp.MaterialNames.Length:N0}");
            Console.WriteLine($"brushes         {bsp.Brushes.Length:N0}");
            Console.WriteLine($"brushsides      {bsp.BrushSides.Length:N0}");
            Console.WriteLine($"entity lump     {bsp.EntityLump.Length:N0} chars");
            return 0;
        }

        /// <summary>
        /// Locates ladder brushes by material. The engine builds no nav ladders at all outside Left 4
        /// Dead (CNavMesh::BuildLadders is entirely #ifdef TERROR), and it only ever looks for
        /// func_simpleladder entities - so brush-based ladders are invisible to it regardless.
        /// </summary>
        private static int FindLadders(string[] args)
        {
            string path = RequireBsp(args);
            var bsp = BspFile.Load(path);

            var found = LadderFinder.Find(bsp);

            int angled = found.Count(l => l.IsAngled);
            Console.WriteLine($"{Path.GetFileName(path)}: {found.Count:N0} ladder brushes ({angled} angled)");

            foreach (var l in found)
            {
                Console.WriteLine($"  bottom={l.Bottom} top={l.Top} h={l.Height,5:F0} w={l.Width,3:F0} " +
                                  $"axis={l.AxisName}{(l.IsAngled ? " ANGLED" : "")}");
            }

            return 0;
        }

        /// <summary>
        /// Measures how far the cheap stages cut the area-pair set down before any ray is traced.
        /// Everything after this is raycasting, so the surviving count is the budget the tracer has to
        /// live within - worth knowing on a real map before building around it.
        /// </summary>
        private static int VisStats(string[] args)
        {
            if (args.Length < 3)
                throw new ArgumentException("expected: vis-stats <file.bsp> <file.nav> [maxViewDistance]");

            string bspPath = args[1];
            string navPath = args[2];

            float maxViewDistance = VisibilityFilter.DefaultMaxViewDistance;
            if (args.Length > 3 && !float.TryParse(args[3], System.Globalization.CultureInfo.InvariantCulture, out maxViewDistance))
                throw new ArgumentException($"'{args[3]}' is not a distance");

            var (bsp, vis) = LoadBsp(bspPath);
            var nav = NavFile.Load(navPath);

            Console.WriteLine($"bsp        {Path.GetFileName(bspPath)}");
            Console.WriteLine($"clusters   {vis.ClusterCount:N0}  (vis data: {(vis.HasVisibilityData ? "present" : "MISSING")})");
            Console.WriteLine($"areas      {nav.Areas.Count:N0}");
            Console.WriteLine($"max view   {(maxViewDistance <= 0 ? "unlimited" : maxViewDistance.ToString("N0"))}");

            if (!vis.HasVisibilityData)
                Console.WriteLine("note: no PVS in this BSP - only the distance stage will cull");

            var filter = new VisibilityFilter(nav, vis, maxViewDistance);
            var stats = filter.Run();

            Console.WriteLine();
            Console.WriteLine($"unmapped areas   {stats.UnmappedAreas:N0}  ({100.0 * stats.UnmappedAreas / nav.Areas.Count:F1}%)");
            Console.WriteLine($"total pairs      {stats.TotalPairs:N0}");
            Report("after distance", stats.AfterDistance, stats.TotalPairs);
            Report("after PVS", stats.AfterPvs, stats.TotalPairs);
            Console.WriteLine($"took             {stats.ElapsedMilliseconds:N0} ms on {NavConcurrency.MaxThreads} threads");

            return 0;

            static void Report(string label, long survivors, long total)
            {
                Console.WriteLine($"{label,-16} {survivors:N0}  ({100.0 * survivors / total:F1}% survive, " +
                                  $"{100.0 * (total - survivors) / total:F1}% culled)");
            }
        }

        /// <summary>
        /// Scores computed visibility against an already-analysed nav mesh.
        ///
        /// Only areas that store a complete list are compared. Valve compresses the mesh by having most
        /// areas inherit a neighbour's set and store a delta against it, and the delta semantics are not
        /// worth reverse-engineering to grade a tracer - the areas that own their full list are a large
        /// enough and unbiased enough sample.
        /// </summary>
        private static int VisCompare(string[] args)
        {
            if (args.Length < 3)
                throw new ArgumentException("expected: vis-compare <file.bsp> <analysed.nav> [maxViewDistance]");

            float maxViewDistance = VisibilityFilter.DefaultMaxViewDistance;
            if (args.Length > 3 && !float.TryParse(args[3], System.Globalization.CultureInfo.InvariantCulture, out maxViewDistance))
                throw new ArgumentException($"'{args[3]}' is not a distance");

            var (bsp, vis) = LoadBsp(args[1]);
            var nav = NavFile.Load(args[2]);

            if (!nav.IsAnalyzed)
                Console.WriteLine("warning: reference mesh is not marked analysed; visibility may be absent");

            var filter = new VisibilityFilter(nav, vis, maxViewDistance);
            var tracer = new VisibilityTracer(filter, vis, nav.Areas.Count);
            filter.Run(tracer);
            var mine = tracer.Symmetrise();

            var indexOf = new Dictionary<uint, int>(nav.Areas.Count);
            for (int i = 0; i < nav.Areas.Count; i++)
                indexOf[nav.Areas[i].Id] = i;

            long agree = 0, missed = 0, extra = 0;
            int compared = 0;
            var worst = new List<(uint Id, int Missed, int Reference, int Index)>();

            for (int i = 0; i < nav.Areas.Count; i++)
            {
                var area = nav.Areas[i];
                if (area.InheritVisibilityFrom != 0 || area.VisibleAreas.Count == 0)
                    continue;

                var reference = new HashSet<int>();
                foreach (var v in area.VisibleAreas)
                {
                    if (indexOf.TryGetValue(v.AreaId, out int idx))
                        reference.Add(idx);
                }

                var computed = new HashSet<int>(mine[i]);

                int hit = 0;
                foreach (int r in reference)
                    if (computed.Contains(r)) hit++;

                agree += hit;
                missed += reference.Count - hit;
                extra += computed.Count - hit;
                compared++;

                if (reference.Count - hit > 0)
                    worst.Add((area.Id, reference.Count - hit, reference.Count, i));
            }

            if (compared == 0)
            {
                Console.WriteLine("no areas store a complete visible list - nothing to compare against");
                return 1;
            }

            long referenceTotal = agree + missed;

            Console.WriteLine($"areas compared   {compared:N0} of {nav.Areas.Count:N0} (the rest inherit)");
            Console.WriteLine($"reference links  {referenceTotal:N0}");
            Console.WriteLine($"  found          {agree:N0}  ({100.0 * agree / referenceTotal:F2}% recall)");
            Console.WriteLine($"  missed         {missed:N0}  ({100.0 * missed / referenceTotal:F2}%)");
            Console.WriteLine($"extra links      {extra:N0}  ({100.0 * agree / Math.Max(1, agree + extra):F2}% precision)");

            worst.Sort((a, b) => b.Missed.CompareTo(a.Missed));
            foreach (var (id, m, r, idx) in worst.Take(10))
            {
                var eye = filter.SightPoints(idx)[^1];
                Console.WriteLine($"  worst: area {id,-7} missed {m,-6} of {r,-6} eye {eye}");
            }

            return 0;
        }

        /// <summary>
        /// Runs the whole visibility pipeline - distance, PVS, then raycasting - and reports the funnel.
        /// Nothing is written; this is the measurement that decides whether the tracing budget is real.
        /// </summary>
        private static int VisTrace(string[] args)
        {
            if (args.Length < 3)
                throw new ArgumentException("expected: vis-trace <file.bsp> <file.nav> [maxViewDistance]");

            float maxViewDistance = VisibilityFilter.DefaultMaxViewDistance;
            if (args.Length > 3 && !float.TryParse(args[3], System.Globalization.CultureInfo.InvariantCulture, out maxViewDistance))
                throw new ArgumentException($"'{args[3]}' is not a distance");

            var (bsp, vis) = LoadBsp(args[1]);
            var nav = NavFile.Load(args[2]);

            Console.WriteLine($"bsp        {Path.GetFileName(args[1])}");
            Console.WriteLine($"areas      {nav.Areas.Count:N0}   nodes {vis.NodeCount:N0}   leafs {vis.LeafCount:N0}   blocking brush entities {vis.BlockingModelCount:N0}   leaves with blocking brushes {vis.LeavesWithBlockingBrushes:N0}   displacement triangles {vis.DisplacementTriangleCount:N0}");

            var filter = new VisibilityFilter(nav, vis, maxViewDistance);
            var tracer = new VisibilityTracer(filter, vis, nav.Areas.Count);
            var stats = filter.Run(tracer);

            Console.WriteLine();
            Console.WriteLine($"total pairs      {stats.TotalPairs:N0}");
            Console.WriteLine($"after distance   {stats.AfterDistance:N0}  ({100.0 * stats.AfterDistance / stats.TotalPairs:F1}%)");
            Console.WriteLine($"after PVS        {stats.AfterPvs:N0}  ({100.0 * stats.AfterPvs / stats.TotalPairs:F1}%)");
            Console.WriteLine($"after raycast    {tracer.VisibleLinks:N0}  ({100.0 * tracer.VisibleLinks / stats.TotalPairs:F1}%)");
            Console.WriteLine($"rays cast        {tracer.RaysCast:N0}  ({(double)tracer.RaysCast / Math.Max(1, stats.AfterPvs):F2} per traced pair)");
            Console.WriteLine($"took             {stats.ElapsedMilliseconds:N0} ms on {NavConcurrency.MaxThreads} threads");

            return 0;
        }

        /// <summary>
        /// Explains why areas fail to resolve to a leaf cluster. An unmapped area is treated as
        /// visible to everything, so each one weakens the prefilter for its whole row - the failures
        /// are worth understanding rather than tolerating.
        /// </summary>
        private static int VisDebug(string[] args)
        {
            if (args.Length < 3)
                throw new ArgumentException("expected: vis-debug <file.bsp> <file.nav>");

            var (bsp, vis) = LoadBsp(args[1]);
            var nav = NavFile.Load(args[2]);

            Console.WriteLine($"nodes {vis.NodeCount:N0}  leafs {vis.LeafCount:N0}  clusters {vis.ClusterCount:N0}");

            int solid = 0, emptyNoCluster = 0, offTree = 0, mapped = 0;
            int shown = 0;

            foreach (var area in nav.Areas)
            {
                var centre = new BspFile.Vector3(
                    (area.NwCorner[0] + area.SeCorner[0]) / 2f,
                    (area.NwCorner[1] + area.SeCorner[1]) / 2f,
                    (area.NwCorner[2] + area.SeCorner[2]) / 2f);

                if (vis.GetClusterAboveSurface(centre) >= 0) { mapped++; continue; }

                // classify the failure using a single sample well clear of the floor
                var probe = new BspFile.Vector3(centre.X, centre.Y, centre.Z + 24f);
                if (!vis.TryGetLeaf(probe, out var leaf)) { offTree++; continue; }

                if (leaf.Contents != 0) solid++; else emptyNoCluster++;

                if (shown++ < 10)
                    Console.WriteLine($"  area {area.Id,-7} at {centre}  contents 0x{leaf.Contents:X}  cluster {leaf.Cluster}");
            }

            Console.WriteLine();
            Console.WriteLine($"mapped             {mapped:N0}");
            Console.WriteLine($"solid leaf         {solid:N0}");
            Console.WriteLine($"empty, cluster -1  {emptyNoCluster:N0}");
            Console.WriteLine($"fell off the tree  {offTree:N0}");

            // An eye point buried in world geometry blocks every ray it is the source of, so it costs
            // the area its entire visible set rather than a few links.
            var filter = new VisibilityFilter(nav, vis, VisibilityFilter.DefaultMaxViewDistance);
            int allBuried = 0, centreBuried = 0;

            for (int i = 0; i < nav.Areas.Count; i++)
            {
                var points = filter.SightPoints(i);
                int buried = 0;

                foreach (var p in points)
                    if (!vis.IsLineClear(p, p, BspVisibility.GenerationMask)) buried++;

                if (buried == points.Length) allBuried++;
                if (!vis.IsLineClear(points[^1], points[^1], BspVisibility.GenerationMask)) centreBuried++;
            }

            Console.WriteLine();
            Console.WriteLine($"eye point in solid: centre {centreBuried:N0}, all five {allBuried:N0} " +
                              $"of {nav.Areas.Count:N0} areas");

            return 0;
        }


        /// <summary>
        /// Traces the sight lines between two areas and reports what stops each one. This is how the
        /// tracer's disagreements with an analysed mesh get diagnosed - a percentage tells you there is
        /// a problem, a blocked ray tells you what it is.
        /// </summary>
        private static int VisWhy(string[] args)
        {
            if (args.Length < 4)
                throw new ArgumentException("expected: vis-why <file.bsp> <file.nav> <fromAreaId> [toAreaId]");

            var (bsp, vis) = LoadBsp(args[1]);
            var nav = NavFile.Load(args[2]);

            uint fromId = uint.Parse(args[3]);
            int from = nav.Areas.FindIndex(a => a.Id == fromId);
            if (from < 0) throw new ArgumentException($"no area with id {fromId}");

            var filter = new VisibilityFilter(nav, vis, 0f);
            var indexOf = new Dictionary<uint, int>(nav.Areas.Count);
            for (int i = 0; i < nav.Areas.Count; i++) indexOf[nav.Areas[i].Id] = i;

            var targets = new List<int>();
            if (args.Length > 4)
            {
                uint toId = uint.Parse(args[4]);
                if (!indexOf.TryGetValue(toId, out int to)) throw new ArgumentException($"no area with id {toId}");
                targets.Add(to);
            }
            else
            {
                // sample the reference's own claims so the report covers real disagreements
                foreach (var v in nav.Areas[from].VisibleAreas.Take(6))
                    if (indexOf.TryGetValue(v.AreaId, out int to)) targets.Add(to);
            }

            var eye = filter.SightPoints(from)[^1];
            Console.WriteLine($"from area {fromId} eye {eye}");
            Console.WriteLine($"reference lists {nav.Areas[from].VisibleAreas.Count:N0} visible areas, " +
                              $"inherits from {nav.Areas[from].InheritVisibilityFrom}");
            Console.WriteLine();

            foreach (int to in targets)
            {
                Console.WriteLine($"  to area {nav.Areas[to].Id}:");
                var corners = filter.SightPoints(to);

                for (int c = 0; c < VisibilityFilter.SightPointsPerArea - 1; c++)
                {
                    bool clear = vis.TraceExplain(eye, corners[c], out int contents, out var hit, out int head);
                    string by = head switch { 0 => "world", -2 => "displacement", _ => $"entity model headnode {head}" };
                    Console.WriteLine(clear
                        ? $"    corner {c} {corners[c]}  CLEAR"
                        : $"    corner {c} {corners[c]}  blocked by {by} at {hit} contents 0x{contents:X}");
                }
            }

            return 0;
        }


        /// <summary>
        /// Computes area-to-area visibility and writes it into the mesh, the offline equivalent of the
        /// engine's nav_analyze visibility pass.
        ///
        /// Writes to a new file by default: a mesh takes long enough to produce that overwriting one in
        /// place on a tool that is still being tuned is not a trade worth making.
        /// </summary>
        private static int BuildVisibility(string[] args)
        {
            if (args.Length < 3)
                throw new ArgumentException("expected: build-visibility <file.bsp> <file.nav> [-o out.nav] [-d maxViewDistance]");

            string bspPath = args[1];
            string navPath = args[2];

            if (!File.Exists(bspPath)) throw new FileNotFoundException($"no such file: {bspPath}");
            if (!File.Exists(navPath)) throw new FileNotFoundException($"no such file: {navPath}");

            string outPath = FlagValue(args, "-o")
                ?? Path.Combine(Path.GetDirectoryName(navPath) ?? ".",
                    Path.GetFileNameWithoutExtension(navPath) + ".vis.nav");

            float maxViewDistance = VisibilityFilter.DefaultMaxViewDistance;
            if (FlagValue(args, "-d") is { } d &&
                !float.TryParse(d, System.Globalization.CultureInfo.InvariantCulture, out maxViewDistance))
                throw new ArgumentException($"'{d}' is not a distance");

            var (bsp, vis) = LoadBsp(bspPath);
            var nav = NavFile.Load(navPath);

            Console.WriteLine($"bsp   {Path.GetFileName(bspPath)}");
            Console.WriteLine($"nav   {Path.GetFileName(navPath)}: {nav.Areas.Count:N0} areas");

            if (!vis.HasVisibilityData)
                Console.WriteLine("warn  BSP has no vis data; the PVS stage will not cull anything");

            var filter = new VisibilityFilter(nav, vis, maxViewDistance);
            var tracer = new VisibilityTracer(filter, vis, nav.Areas.Count);

            // The long one. On a large map this is minutes of raycasting, and it is the phase most
            // worth showing a bar for.
            var tracing = new ConsoleProgress(new NavProgress.Step("Tracing area visibility", 1.0));
            tracing.Progress.Enter("Tracing area visibility");

            var stats = filter.Run(tracer, tracing.Progress);

            tracing.Progress.Finish();
            tracing.Dispose();

            var visible = tracer.Symmetrise();

            Console.WriteLine($"      {stats.TotalPairs:N0} pairs -> {stats.AfterDistance:N0} after distance " +
                              $"-> {stats.AfterPvs:N0} after PVS");
            Console.WriteLine($"      {tracer.VisibleLinks:N0} visible links from {tracer.RaysCast:N0} rays " +
                              $"in {stats.ElapsedMilliseconds:N0} ms on {NavConcurrency.MaxThreads} threads");

            foreach (var ids in visible)
                Array.Sort(ids);

            bool compress = !args.Contains("-nocompress", StringComparer.OrdinalIgnoreCase);

            if (compress)
            {
                var packing = new ConsoleProgress(new NavProgress.Step("Compressing visibility", 1.0));
                packing.Progress.Enter("Compressing visibility");

                var compression = VisibilityCompressor.Apply(nav, visible, packing.Progress);

                packing.Progress.Finish();
                packing.Dispose();

                Console.WriteLine($"      compressed {compression.Compressed:N0} areas: " +
                                  $"{compression.EntriesBefore:N0} -> {compression.EntriesAfter:N0} entries " +
                                  $"({100.0 * compression.Ratio:F1}% of uncompressed)");
            }
            else
            {
                for (int i = 0; i < nav.Areas.Count; i++)
                {
                    var area = nav.Areas[i];
                    area.VisibleAreas.Clear();
                    area.InheritVisibilityFrom = 0;

                    foreach (int j in visible[i])
                        area.VisibleAreas.Add(new VisibleArea { AreaId = nav.Areas[j].Id, Attributes = 1 });
                }
            }

            nav.IsAnalyzed = true;
            nav.Save(outPath);

            var written = new FileInfo(outPath);
            Console.WriteLine($"out   {outPath}  ({written.Length:N0} bytes)");

            var reloaded = NavFile.Load(outPath);
            long links = reloaded.Areas.Sum(a => (long)a.VisibleAreas.Count);
            Console.WriteLine($"check reloaded OK: {reloaded.Areas.Count:N0} areas, {links:N0} stored entries, analysed={reloaded.IsAnalyzed}");

            // Compression is only safe if it is lossless, and that is cheap enough to prove on every
            // run rather than trust: resolve the deltas back out and diff against what was computed.
            var resolved = VisibilityCompressor.Resolve(reloaded);
            long mismatched = 0;

            for (int i = 0; i < reloaded.Areas.Count; i++)
            {
                var expected = new HashSet<uint>();
                foreach (int j in visible[i])
                    expected.Add(nav.Areas[j].Id);

                if (!resolved[i].SetEquals(expected))
                    mismatched++;
            }

            Console.WriteLine(mismatched == 0
                ? $"check visibility round-trips exactly for all {reloaded.Areas.Count:N0} areas"
                : $"FAIL: {mismatched:N0} areas resolve to a different visible set than was computed");

            return mismatched == 0 ? 0 : 1;
        }

        /// <summary>
        /// Loads a BSP with everything the passes need attached to it.
        ///
        /// Every command wants the same four lines, and sixteen copies of them is sixteen chances to
        /// forget one. Forgetting is not a loud failure either: a tracer with no displacements attached
        /// silently reports open air over every piece of terrain on the map, and the command still runs
        /// and still prints plausible numbers.
        /// </summary>
        private static (BspFile Bsp, BspVisibility Vis) LoadBsp(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"no such file: {path}");

            var bsp = BspFile.Load(path);
            var vis = BspVisibility.Load(path, bsp);

            vis.AttachModels(BspModels.Load(path, bsp));
            vis.AttachDisplacements(BspDisplacements.Load(path));

            return (bsp, vis);
        }

        private static string? FlagValue(string[] args, string flag)
        {
            int i = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }


        /// <summary>
        /// Resolves the stored visibility deltas and prints how many areas each named area can see.
        /// Exists to be cross-checked against the engine's own answer: the delta encoding was inferred
        /// from Valve's data, so the only proof it is right is the game agreeing area by area.
        /// </summary>
        private static int VisCount(string[] args)
        {
            if (args.Length < 3)
                throw new ArgumentException("expected: vis-count <file.nav> <areaId> [areaId...]");

            var nav = NavFile.Load(args[1]);
            var resolved = VisibilityCompressor.Resolve(nav);

            for (int a = 2; a < args.Length; a++)
            {
                uint id = uint.Parse(args[a]);
                int index = nav.Areas.FindIndex(x => x.Id == id);

                if (index < 0)
                {
                    Console.WriteLine($"area {id}: not present");
                    continue;
                }

                var area = nav.Areas[index];
                Console.WriteLine($"area {id}: sees {resolved[index].Count:N0}  " +
                                  $"(stores {area.VisibleAreas.Count:N0} entries, inherits from {area.InheritVisibilityFrom})");
            }

            return 0;
        }


        /// <summary>
        /// Traces one segment and says what stopped it. The smallest possible test of the tracer, which
        /// is exactly what is wanted when the question is "does this door block anything at all".
        /// </summary>
        private static int Probe(string[] args)
        {
            if (args.Length < 8)
                throw new ArgumentException("expected: probe <file.bsp> x1 y1 z1 x2 y2 z2");

            var c = System.Globalization.CultureInfo.InvariantCulture;
            var a = new BspFile.Vector3(float.Parse(args[2], c), float.Parse(args[3], c), float.Parse(args[4], c));
            var b = new BspFile.Vector3(float.Parse(args[5], c), float.Parse(args[6], c), float.Parse(args[7], c));

            var (_, vis) = LoadBsp(args[1]);

            // All three masks, because which question is being asked matters more than it looks and the
            // answers differ on exactly the surfaces that decide where a body can go. A grate, a window
            // and an NPC clip brush stop none of a bullet, a bot's line of sight or a player - but the
            // generation mask stops on all three. Reporting one mask alone reads "clear" for geometry
            // the generator is busy refusing to cross, which is a good way to spend an afternoon looking
            // for a bug that is not there. Seeing them side by side names the culprit immediately: only
            // the generation column blocking means grate, window or clip; ground blocking too means it
            // is real world geometry.
            bool sight = vis.IsLineClear(a, b, BspVisibility.MaskBlockLos);
            bool generation = vis.IsLineClear(a, b, BspVisibility.GenerationMask);
            bool ground = vis.IsLineClear(a, b, BspVisibility.GroundMask);

            Console.WriteLine($"{a} -> {b}");
            Console.WriteLine($"  sight       {(sight ? "CLEAR" : "blocked")}   (solid|blocklos|moveable)");
            Console.WriteLine($"  generation  {(generation ? "CLEAR" : "blocked")}   (+ window, monsterclip, grate)");
            Console.WriteLine($"  ground      {(ground ? "CLEAR" : "blocked")}   (generation without monsterclip)");

            if (!generation && ground)
                Console.WriteLine("  -> the only thing stopping this is NPC clip");

            if (!vis.TraceExplain(a, b, out int contents, out var hit, out int head))
            {
                Console.WriteLine($"  sight hit   {(head switch { 0 => "world", -2 => "displacement", _ => $"entity headnode {head}" })} " +
                                  $"at {hit} contents 0x{contents:X}");
            }

            return 0;
        }


        /// <summary>
        /// Emits a deterministic sample of area-to-area sight lines with this tracer's verdict on each.
        ///
        /// The point is cross-validation against the engine itself: the same coordinates can be fed to
        /// util.TraceLine in game and the answers compared directly. Scoring against an analysed mesh
        /// only ever shows agreement with whatever produced that mesh, which may be stale or may have
        /// been generated under different settings; the engine's live trace is the real authority.
        /// </summary>
        private static int SampleRays(string[] args)
        {
            if (args.Length < 4)
                throw new ArgumentException("expected: sample-rays <file.bsp> <file.nav> <count> [seed]");

            var (bsp, vis) = LoadBsp(args[1]);
            var nav = NavFile.Load(args[2]);

            int count = int.Parse(args[3]);
            int seed = args.Length > 4 ? int.Parse(args[4]) : 1234;

            var filter = new VisibilityFilter(nav, vis, VisibilityFilter.DefaultMaxViewDistance);
            var random = new Random(seed);

            for (int n = 0; n < count; n++)
            {
                int i = random.Next(nav.Areas.Count);
                int j = random.Next(nav.Areas.Count);
                if (i == j) { n--; continue; }

                var a = filter.SightPoints(i)[^1];
                var b = filter.SightPoints(j)[^1];

                bool clear = vis.IsLineClear(a, b);

                Console.WriteLine($"{a.X:F2} {a.Y:F2} {a.Z:F2} {b.X:F2} {b.Y:F2} {b.Z:F2} " +
                                  $"{(clear ? 1 : 0)}");
            }

            return 0;
        }


        /// <summary>
        /// Scores stair detection against whatever the mesh already has marked.
        ///
        /// On gm_construct that reference is authoritative: running the engine's own nav_check_stairs
        /// reproduces the shipped mesh's 19 marked areas exactly, so those 19 are Valve's algorithm's
        /// answer and not a stale artefact.
        /// </summary>
        private static int Stairs(string[] args)
        {
            if (args.Length < 3)
                throw new ArgumentException("expected: stairs <file.bsp> <file.nav>");

            var (bsp, vis) = LoadBsp(args[1]);
            var nav = NavFile.Load(args[2]);

            var reference = new HashSet<uint>();
            foreach (var area in nav.Areas)
            {
                if (((NavAttributes)area.AttributeFlags & NavAttributes.Stairs) != 0)
                    reference.Add(area.Id);
            }

            var mine = new HashSet<uint>();
            var features = new StairMarker.Features[nav.Areas.Count];

            System.Threading.Tasks.Parallel.For(0, nav.Areas.Count, NavConcurrency.Options,
                i => features[i] = StairMarker.Analyse(nav.Areas[i], vis));

            for (int i = 0; i < nav.Areas.Count; i++)
            {
                if (features[i].IsStairs)
                    mine.Add(nav.Areas[i].Id);
            }

            int hit = 0;
            foreach (uint id in reference)
                if (mine.Contains(id)) hit++;

            // -at explains one place rather than scoring the map. When a generated mesh and an
            // engine-made one disagree about a staircase, the useful question is which of the six probe
            // lines vetoed it and why, and that is only answerable one area at a time.
            if (FlagValue(args, "-at") is { } atX)
            {
                var c = System.Globalization.CultureInfo.InvariantCulture;
                int i = Array.FindIndex(args, a => a.Equals("-at", StringComparison.OrdinalIgnoreCase));
                float px = float.Parse(atX, c);
                float py = float.Parse(args[i + 2], c);
                float pz = float.Parse(args[i + 3], c);

                var index = new NavGeometry.Index(nav.Areas);
                int found = index.FindAt(px, py, pz, 64f);

                if (found < 0)
                {
                    Console.WriteLine($"no area within 64 units of ({px:F0} {py:F0} {pz:F0})");
                    return 1;
                }

                var target = nav.Areas[found];
                var tb = NavGeometry.GetBounds(target);
                var explain = new StairMarker.Explanation();
                StairMarker.TestStairs(target, vis, explain);

                Console.WriteLine($"area {target.Id} at ({px:F0} {py:F0} {pz:F0})");
                Console.WriteLine($"  footprint  {tb.Width:F0} x {tb.Depth:F0}  " +
                                  $"({tb.Width / NavConstants.GenerationStepSize:F1} x " +
                                  $"{tb.Depth / NavConstants.GenerationStepSize:F1} steps)");
                Console.WriteLine($"  corners    nw={target.NwCorner[2]:F1} ne={target.NeZ:F1} " +
                                  $"se={target.SeCorner[2]:F1} sw={target.SwZ:F1}");
                Console.WriteLine($"  too small  {explain.TooSmall}");
                Console.WriteLine($"  coplanar   {!explain.NotCoplanar}");

                foreach (string line in explain.Lines)
                    Console.WriteLine($"  {line}");

                Console.WriteLine($"  verdict    {(explain.Verdict ? "STAIRS" : "not stairs")}");
                return 0;
            }

            // Sizes, not just a count. A stair area that comes out one sampling step across is rejected
            // by TestStairs before any geometry is probed - "don't bother with stairs on small areas" -
            // so when a generated mesh and an engine-made one disagree about a staircase, the first
            // thing worth knowing is whether they even agree about how big the areas over it are.
            Console.WriteLine("reference-marked areas (footprint, in sampling steps of " +
                              $"{NavConstants.GenerationStepSize:F0}):");
            foreach (var area in nav.Areas)
            {
                if (((NavAttributes)area.AttributeFlags & NavAttributes.Stairs) == 0)
                    continue;

                var b = NavGeometry.GetBounds(area);
                Console.WriteLine($"  area {area.Id,-7} {b.Width,6:F0} x {b.Depth,6:F0}  " +
                                  $"({b.Width / NavConstants.GenerationStepSize:F1} x " +
                                  $"{b.Depth / NavConstants.GenerationStepSize:F1} steps)  " +
                                  $"at ({(b.MinX + b.MaxX) / 2:F0} {(b.MinY + b.MaxY) / 2:F0} " +
                                  $"{NavGeometry.SurfaceZ(area, (b.MinX + b.MaxX) / 2, (b.MinY + b.MaxY) / 2):F0})");
            }

            Console.WriteLine();
            Console.WriteLine($"reference marked {reference.Count:N0} areas");
            Console.WriteLine($"detector marked  {mine.Count:N0} areas");
            Console.WriteLine($"  agree          {hit:N0}");
            Console.WriteLine($"  missed         {reference.Count - hit:N0}");
            Console.WriteLine($"  extra          {mine.Count - hit:N0}");

            Console.WriteLine();
            Console.WriteLine("areas the two disagree on:");
            for (int i = 0; i < nav.Areas.Count; i++)
            {
                uint id = nav.Areas[i].Id;
                bool inRef = reference.Contains(id);
                bool inMine = mine.Contains(id);
                if (inRef == inMine) continue;

                var f = features[i];
                var a = nav.Areas[i];
                Console.WriteLine($"  {(inRef ? "reference only" : "detector only ")} area {id,-7} " +
                                  $"at ({(a.NwCorner[0] + a.SeCorner[0]) / 2:F0} " +
                                  $"{(a.NwCorner[1] + a.SeCorner[1]) / 2:F0} {a.NwCorner[2]:F0})  " +
                                  $"run={f.Run,6:F0} rise={f.Rise,6:F1} slope={f.Slope,5:F2} " +
                                  $"residual={f.Residual,5:F2} risers={f.Risers,3} maxRiser={f.MaxRiser,5:F1}");
            }

            return 0;
        }


        /// <summary>
        /// The movement pass: marks stairs and adds the step, jump and drop connections the generator
        /// left out. Both need the same traced geometry, so they run together and write once.
        /// </summary>
        private static int BuildMovement(string[] args)
        {
            if (args.Length < 3)
                throw new ArgumentException("expected: build-movement <file.bsp> <file.nav> [-o out.nav]");

            string bspPath = args[1];
            string navPath = args[2];

            if (!File.Exists(bspPath)) throw new FileNotFoundException($"no such file: {bspPath}");
            if (!File.Exists(navPath)) throw new FileNotFoundException($"no such file: {navPath}");

            string outPath = FlagValue(args, "-o")
                ?? Path.Combine(Path.GetDirectoryName(navPath) ?? ".",
                    Path.GetFileNameWithoutExtension(navPath) + ".movement.nav");

            var (bsp, vis) = LoadBsp(bspPath);
            var nav = NavFile.Load(navPath);

            long before = nav.Areas.Sum(a => a.Connections.Sum(c => (long)c.Count));
            int isolatedBefore = nav.Areas.Count(a => a.Connections.All(c => c.Count == 0));

            Console.WriteLine($"bsp   {Path.GetFileName(bspPath)}");
            Console.WriteLine($"nav   {Path.GetFileName(navPath)}: {nav.Areas.Count:N0} areas, " +
                              $"{before:N0} connections, {isolatedBefore:N0} isolated");

            var sw = System.Diagnostics.Stopwatch.StartNew();

            var moving = new ConsoleProgress(
                new NavProgress.Step("Connecting areas", 0.65),
                new NavProgress.Step("Marking stairs", 0.35));

            moving.Progress.Enter("Connecting areas");
            var links = ConnectionBuilder.Build(nav, vis, moving.Progress);

            Console.WriteLine($"      connections: {links.Steps:N0} steps, {links.JumpsUp:N0} jumps up, " +
                              $"{links.CrouchJumpsUp:N0} crouch jumps up, " +
                              $"{links.Drops:N0} drops  ({links.Rejected:N0} rejected by trace)");
            Console.WriteLine($"        up:   {links.UpCandidates:N0} candidates, " +
                              $"{links.UpRejectedByReach:N0} too high, {links.UpRejectedByTrace:N0} blocked");
            Console.WriteLine($"        down: {links.DownCandidates:N0} candidates, " +
                              $"{links.DownRejectedByReach:N0} too far, {links.DownRejectedByTrace:N0} blocked");
            Console.WriteLine($"        blocked by: {links.RejectedByCrossing:N0} crossing, " +
                              $"{links.RejectedByHeadroom:N0} headroom, {links.RejectedByGround:N0} no ground between, " +
                              $"{links.RejectedByFall:N0} floor in the way of the drop");
            Console.WriteLine($"        headroom:   {links.HeadroomLowCeiling:N0} low ceiling, " +
                              $"{links.HeadroomStartSolid:N0} start point buried (area surface below the real floor)");

            // Right after connections, not before: stitching needs to know what each jump area joined.
            var stitched = JumpAreaStitcher.Stitch(nav);
            if (stitched.JumpAreas > 0)
            {
                Console.WriteLine($"      jump areas: {stitched.JumpAreas:N0} stitched out, " +
                                  $"{stitched.ConnectionsAdded:N0} connections bridged across them");
            }

            // Stairs last of the three, as in CreateNavAreasFromNodes: the jump areas above are steep
            // stair-shaped fragments that exist only to be deleted, and testing them before the stitch
            // marks a pile of them.
            moving.Progress.Enter("Marking stairs");
            var stairs = StairMarker.Mark(nav, vis, moving.Progress);

            moving.Progress.Finish();
            moving.Dispose();

            Console.WriteLine($"      stairs: marked {stairs.Marked:N0}, cleared {stairs.Cleared:N0}");

            var elevators = ElevatorConnector.Build(nav, bsp);
            Console.WriteLine($"      elevators: {elevators.Platforms:N0} platforms, " +
                              $"{elevators.Connections:N0} connections at {elevators.Stops:N0} stops");
            foreach (string note in elevators.Notes.Take(6))
                Console.WriteLine($"        note: {note}");

            // Corner patching before the shortcut fixup, matching Valve's own FixUpGeneratedAreas order:
            // it needs the connection graph above to know which corners are isolated, and it adds new
            // connections the shortcut pass should then be free to prune if they turn out redundant.
            var patched = CornerPatcher.Patch(nav, vis);
            Console.WriteLine($"      corners: {patched.IsolatedCorners:N0} isolated -> " +
                              $"{patched.CandidateFound:N0} had a diagonal neighbour -> " +
                              $"{patched.CornerMatched:N0} touched exactly -> " +
                              $"{patched.TraceCleared:N0} cleared by trace -> " +
                              $"{patched.PatchesAdded:N0} patched");

            var fixup = AreaConnectionFixer.Fix(nav);
            Console.WriteLine($"      fixup: {fixup.ShortcutsRemoved:N0} redundant shortcuts removed");

            sw.Stop();

            long after = nav.Areas.Sum(a => a.Connections.Sum(c => (long)c.Count));
            int isolatedAfter = nav.Areas.Count(a => a.Connections.All(c => c.Count == 0));

            Console.WriteLine($"      {before:N0} -> {after:N0} connections, " +
                              $"{isolatedBefore:N0} -> {isolatedAfter:N0} isolated, in {sw.ElapsedMilliseconds:N0} ms");

            nav.Save(outPath);
            Console.WriteLine($"out   {outPath}  ({new FileInfo(outPath).Length:N0} bytes)");

            var reloaded = NavFile.Load(outPath);
            long reloadedLinks = reloaded.Areas.Sum(a => a.Connections.Sum(c => (long)c.Count));
            Console.WriteLine($"check reloaded OK: {reloaded.Areas.Count:N0} areas, {reloadedLinks:N0} connections");

            return reloadedLinks == after ? 0 : 1;
        }


        /// <summary>
        /// Emits the connections one mesh has that another does not, as endpoint pairs.
        ///
        /// Exists so added links can be checked with a real player hull in game. The builder validates
        /// with line traces, which are infinitely thin - a line slips through a gap a 32 unit wide
        /// player cannot, so line clearance alone is not proof that a connection is walkable.
        /// </summary>
        private static int DiffConnections(string[] args)
        {
            if (args.Length < 4)
                throw new ArgumentException("expected: diff-connections <before.nav> <after.nav> <count> [seed]");

            var before = NavFile.Load(args[1]);
            var after = NavFile.Load(args[2]);
            int count = int.Parse(args[3]);
            int seed = args.Length > 4 ? int.Parse(args[4]) : 99;

            var had = new HashSet<(uint, uint)>();
            foreach (var area in before.Areas)
                foreach (var list in area.Connections)
                    foreach (uint id in list)
                        had.Add((area.Id, id));

            var byId = new Dictionary<uint, NavArea>(after.Areas.Count);
            foreach (var area in after.Areas)
                byId[area.Id] = area;

            var added = new List<(NavArea From, NavArea To)>();
            foreach (var area in after.Areas)
            {
                foreach (var list in area.Connections)
                {
                    foreach (uint id in list)
                    {
                        if (had.Contains((area.Id, id)) || !byId.TryGetValue(id, out var target))
                            continue;

                        added.Add((area, target));
                    }
                }
            }

            Console.Error.WriteLine($"{added.Count:N0} connections added");

            var random = new Random(seed);
            for (int n = 0; n < Math.Min(count, added.Count); n++)
            {
                var (from, to) = added[random.Next(added.Count)];

                var fb = NavGeometry.GetBounds(from);
                var tb = NavGeometry.GetBounds(to);

                float fx = (fb.MinX + fb.MaxX) / 2f, fy = (fb.MinY + fb.MaxY) / 2f;
                float tx = (tb.MinX + tb.MaxX) / 2f, ty = (tb.MinY + tb.MaxY) / 2f;

                float fz = NavGeometry.SurfaceZ(from, fx, fy);
                float tz = NavGeometry.SurfaceZ(to, tx, ty);

                Console.WriteLine($"{fx:F2} {fy:F2} {fz:F2} {tx:F2} {ty:F2} {tz:F2}");
            }

            return 0;
        }

        /// <summary>
        /// Samples the world for walkable ground the mesh does not cover and adds areas for it, then
        /// links them in. Reports the funnel rather than just a total: how much of the world was
        /// walkable, and how much of that the mesh was already missing, are separate facts.
        /// </summary>
        private static int BuildAreas(string[] args)
        {
            if (args.Length < 3)
                throw new ArgumentException("expected: build-areas <file.bsp> <file.nav> [-o out.nav] [-noconnect]");

            string bspPath = args[1];
            string navPath = args[2];

            if (!File.Exists(bspPath)) throw new FileNotFoundException($"no such file: {bspPath}");
            if (!File.Exists(navPath)) throw new FileNotFoundException($"no such file: {navPath}");

            string outPath = FlagValue(args, "-o")
                ?? Path.Combine(Path.GetDirectoryName(navPath) ?? ".",
                    Path.GetFileNameWithoutExtension(navPath) + ".areas.nav");

            var (bsp, vis) = LoadBsp(bspPath);
            var nav = NavFile.Load(navPath);

            Console.WriteLine($"bsp   {Path.GetFileName(bspPath)}");
            Console.WriteLine($"nav   {Path.GetFileName(navPath)}: {nav.Areas.Count:N0} areas");

            // -scratch throws the mesh away and generates from player spawns alone. That is the mode
            // that answers whether this is a generator or only a finisher, and on a map with a known
            // good mesh it can be scored against it.
            if (args.Contains("-scratch", StringComparer.OrdinalIgnoreCase))
            {
                nav.Areas.Clear();
                nav.Ladders.Clear();
                Console.WriteLine("      -scratch: discarded the existing mesh, seeding from spawns only");
            }

            // -reference scores the result against a known-good mesh and explains every miss, which is
            // the only way to tell a movement-limit problem from a walkability or merge one.
            NavFile? reference = FlagValue(args, "-reference") is { } referencePath
                ? NavFile.Load(referencePath)
                : null;

            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Weights are rough shares of a typical run rather than anything derived: sampling dominates
            // on an empty mesh, and the passes after it are cheap by comparison.
            using var bar = new ConsoleProgress(
                new NavProgress.Step("Sampling walkable space", 0.55),
                new NavProgress.Step("Linking samples", 0.15),
                new NavProgress.Step("Building areas", 0.15),
                new NavProgress.Step("Merging areas", 0.05));

            bar.Progress.Enter("Sampling walkable space");
            var result = AreaGenerator.Generate(nav, vis, bsp, reference, progress: bar.Progress);
            bar.Progress.Finish();
            bar.Dispose();

            sw.Stop();

            Console.WriteLine($"      flooded from {result.Seeds:N0} seeds, visited {result.Visited:N0} cells, " +
                              $"{result.Uncovered:N0} not covered by the mesh");
            Console.WriteLine($"      added {result.AreasAdded:N0} areas in {sw.ElapsedMilliseconds:N0} ms " +
                              $"on {NavConcurrency.MaxThreads} threads");

            foreach (string note in result.Notes)
                Console.WriteLine($"      note: {note}");

            if (!args.Contains("-noconnect", StringComparer.OrdinalIgnoreCase) && result.AreasAdded > 0)
            {
                using var connecting = new ConsoleProgress(new NavProgress.Step("Connecting areas", 1.0));
                connecting.Progress.Enter("Connecting areas");

                var links = ConnectionBuilder.Build(nav, vis, connecting.Progress);
                connecting.Progress.Finish();
                connecting.Dispose();
                Console.WriteLine($"      connections: {links.Steps:N0} steps, {links.JumpsUp:N0} jumps up, " +
                                  $"{links.CrouchJumpsUp:N0} crouch jumps up, " +
                                  $"{links.Drops:N0} drops  ({links.Rejected:N0} rejected by trace)");

                // After connecting, not before: the stitch needs to know what each jump area joined.
                var stitched = JumpAreaStitcher.Stitch(nav);
                if (stitched.JumpAreas > 0)
                {
                    Console.WriteLine($"      jump areas: {stitched.JumpAreas:N0} stitched out, " +
                                      $"{stitched.ConnectionsAdded:N0} connections bridged across them");
                }
            }

            if (result.AreasAdded > 0)
            {
                var trimmed = AreaGenerator.ClipToGeometry(nav, vis);
                if (trimmed.Clipped > 0)
                {
                    Console.WriteLine($"      clipped {trimmed.Clipped:N0} areas back to geometry " +
                                      $"({trimmed.Reclaimed / 1024f:N0}k sq units out of walls)" +
                                      (trimmed.Discarded > 0
                                          ? $", discarded {trimmed.Discarded:N0} left too narrow to walk"
                                          : ""));
                }
            }

            nav.Save(outPath);
            Console.WriteLine($"out   {outPath}  ({new FileInfo(outPath).Length:N0} bytes)");

            var reloaded = NavFile.Load(outPath);
            int isolated = reloaded.Areas.Count(a => a.Connections.All(c => c.Count == 0));
            Console.WriteLine($"check reloaded OK: {reloaded.Areas.Count:N0} areas, {isolated:N0} isolated");

            return 0;
        }

        /// <summary>
        /// Scores one mesh's ground coverage against another's.
        ///
        /// The question a generator has to answer is not "how many areas" but "does it cover the ground
        /// the reference covers, and does it claim ground the reference does not". Comparing area counts
        /// says nothing - one big area and twenty small ones can cover the same floor.
        /// </summary>
        private static int CompareAreas(string[] args)
        {
            if (args.Length < 3)
                throw new ArgumentException("expected: compare-areas <reference.nav> <candidate.nav>");

            var reference = NavFile.Load(args[1]);
            var candidate = NavFile.Load(args[2]);

            Console.WriteLine($"reference {Path.GetFileName(args[1])}: {reference.Areas.Count:N0} areas");
            Console.WriteLine($"candidate {Path.GetFileName(args[2])}: {candidate.Areas.Count:N0} areas");

            const float Tolerance = 48f;

            var referenceIndex = new NavGeometry.Index(reference.Areas);
            var candidateIndex = new NavGeometry.Index(candidate.Areas);

            int covered = CountCovered(reference.Areas, candidateIndex, Tolerance);
            int extra = candidate.Areas.Count - CountCovered(candidate.Areas, referenceIndex, Tolerance);

            Console.WriteLine();
            Console.WriteLine($"reference ground the candidate covers   {covered:N0} of {reference.Areas.Count:N0}  " +
                              $"({100.0 * covered / Math.Max(1, reference.Areas.Count):F1}%)");
            Console.WriteLine($"candidate areas on ground the reference does not cover  {extra:N0}  " +
                              $"({100.0 * extra / Math.Max(1, candidate.Areas.Count):F1}%)");

            return 0;
        }

        /// <summary>How many areas have their centre inside some area of the other mesh.</summary>
        private static int CountCovered(IReadOnlyList<NavArea> areas, NavGeometry.Index other, float tolerance)
        {
            int found = 0;

            foreach (var area in areas)
            {
                var b = NavGeometry.GetBounds(area);
                float cx = (b.MinX + b.MaxX) / 2f;
                float cy = (b.MinY + b.MaxY) / 2f;

                if (other.FindAt(cx, cy, NavGeometry.SurfaceZ(area, cx, cy), tolerance) >= 0)
                    found++;
            }

            return found;
        }

        /// <summary>
        /// Traces straight down under each area of a mesh and reports the surface normals found.
        ///
        /// A sanity check before anything is built on them: an engine-made mesh sits on walkable ground
        /// by definition, so nearly every normal under it should pass Valve's own slope limit. If they
        /// do not, the normals are wrong and every test derived from them would be too.
        /// </summary>
        private static int Normals(string[] args)
        {
            if (args.Length < 3)
                throw new ArgumentException("expected: normals <file.bsp> <file.nav>");

            var (bsp, vis) = LoadBsp(args[1]);
            var nav = NavFile.Load(args[2]);

            int walkable = 0, tooSteep = 0, stairFlat = 0, noSurface = 0;
            var samples = new List<string>();

            foreach (var area in nav.Areas)
            {
                var b = NavGeometry.GetBounds(area);
                float cx = (b.MinX + b.MaxX) / 2f;
                float cy = (b.MinY + b.MaxY) / 2f;
                float cz = NavGeometry.SurfaceZ(area, cx, cy);

                var from = new BspFile.Vector3(cx, cy, cz + 16f);
                var to = new BspFile.Vector3(cx, cy, cz - 32f);

                if (!vis.TryTraceSurface(from, to, BspVisibility.GenerationMask, out _, out var normal))
                {
                    noSurface++;
                    continue;
                }

                if (normal.Z >= NavConstants.SlopeLimit) walkable++; else tooSteep++;
                if (normal.Z > NavConstants.StairNormal) stairFlat++;

                if (samples.Count < 6)
                    samples.Add($"{normal.Z:F3}");
            }

            int total = nav.Areas.Count;
            Console.WriteLine($"areas                 {total:N0}");
            Console.WriteLine($"  walkable (z >= {NavConstants.SlopeLimit})  {walkable:N0}  ({100.0 * walkable / Math.Max(1, total):F1}%)");
            Console.WriteLine($"  too steep            {tooSteep:N0}  ({100.0 * tooSteep / Math.Max(1, total):F1}%)");
            Console.WriteLine($"  flat (z > {NavConstants.StairNormal})       {stairFlat:N0}  ({100.0 * stairFlat / Math.Max(1, total):F1}%)");
            Console.WriteLine($"  no surface found     {noSurface:N0}");
            Console.WriteLine($"  sample normal.z      {string.Join(", ", samples)}");

            return 0;
        }

        /// <summary>
        /// Measures the two things that make a mesh look wrong in game rather than score badly on paper:
        /// how much of each area is buried in solid geometry, and how far from square its areas are.
        ///
        /// Coverage scoring cannot see either. An area that runs a step past the wall it ends at still
        /// has its centre over real floor, so it counts as covered while a player watching a bot walk
        /// into the wall can see perfectly well that it is not.
        /// </summary>
        /// <summary>
        /// Measures how far each area's claimed surface sits from the real floor underneath it.
        ///
        /// This is the failure <see cref="Shape"/> cannot see. Shape asks whether an area is buried
        /// inside geometry, which catches a quad that has grown into a wall; it says nothing about a
        /// quad that hovers in the air above the ramp it is supposed to describe, or one that overhangs
        /// a roof edge with nothing under it at all. Both look fine in every count the generator
        /// reports - right number of areas, right number of connections, correctly not isolated - and
        /// both are immediately obvious the moment anyone looks at the mesh in game.
        ///
        /// Reported against a reference mesh rather than in the abstract, because no mesh has zero
        /// error: areas are flat-ish quads and real ground is not, so some deviation is the
        /// representation working as intended rather than a defect. What matters is how it compares to
        /// what the engine's own generator accepts on the same map.
        /// </summary>
        /// <summary>
        /// Lists every surface the sampler finds in one vertical column, and cross-checks each against
        /// the independent floor finder the rest of the codebase uses.
        ///
        /// The two are different code paths asking the same question - EnumerateFloors walks the column
        /// with TryTraceSurface, TryFindFloor steps down it with IsLineClear - and when they disagree,
        /// areas get built on surfaces that nothing downstream can find again. Printing them side by
        /// side is the only way to see that, because every count in the generator is computed from the
        /// first one alone and therefore looks perfectly healthy.
        /// </summary>
        private static int Floors(string[] args)
        {
            if (args.Length < 4)
                throw new ArgumentException("expected: floors <file.bsp> x y");

            var c = System.Globalization.CultureInfo.InvariantCulture;
            float x = float.Parse(args[2], c);
            float y = float.Parse(args[3], c);

            var (bsp, vis) = LoadBsp(args[1]);

            var world = bsp.BrushModelBounds[0];

            Span<BspVisibility.FloorSample> samples = stackalloc BspVisibility.FloorSample[32];
            int count = vis.EnumerateFloors(x, y, world.Maxs.Z, world.Mins.Z, samples);

            Console.WriteLine($"column ({x:F0} {y:F0})  world z {world.Mins.Z:F0}..{world.Maxs.Z:F0}");
            Console.WriteLine($"EnumerateFloors found {count} surface(s):");

            for (int i = 0; i < count; i++)
            {
                var s = samples[i];

                bool alsoFound = StairMarker.TryFindFloor(vis, x, y, s.Z + 8f, 64f, out float viaLine);
                string agreement = alsoFound && MathF.Abs(viaLine - s.Z) <= 2f
                    ? "agrees"
                    : alsoFound ? $"DISAGREES (line finder says {viaLine:F1})"
                                : "DISAGREES (line finder finds nothing here)";

                Console.WriteLine($"  z {s.Z,9:F1}  normal ({s.Normal.X,6:F2} {s.Normal.Y,6:F2} " +
                                  $"{s.Normal.Z,6:F2})  walkable {s.Normal.Z >= NavConstants.SlopeLimit,-5}  {agreement}");
            }

            return 0;
        }

        /// <summary>
        /// Walks the connection tests for one specific pair of areas and reports where they part company.
        ///
        /// Built after too long spent reasoning about why a staircase would not link to the floor at its
        /// foot. Every individual condition looked satisfied on paper - same height, shared edge, ample
        /// overlap, a clear trace across the seam - and the connection still was not there, which means
        /// the reasoning was wrong somewhere rather than the world being odd. Asking the code to say
        /// which test it fails settles that in one run instead of several.
        /// </summary>
        private static int WhyNotConnected(string[] args)
        {
            if (args.Length < 5)
                throw new ArgumentException("expected: why-not-connected <file.bsp> <file.nav> <fromId> <toId>");

            var (_, vis) = LoadBsp(args[1]);
            var nav = NavFile.Load(args[2]);

            uint fromId = uint.Parse(args[3]);
            uint toId = uint.Parse(args[4]);

            var from = nav.Areas.Find(a => a.Id == fromId);
            var to = nav.Areas.Find(a => a.Id == toId);

            if (from is null || to is null)
            {
                Console.WriteLine($"missing area: {(from is null ? fromId : toId)}");
                return 1;
            }

            var report = ConnectionBuilder.Explain(vis, from, to);

            Console.WriteLine($"from {fromId} -> {toId}");
            foreach (string line in report)
                Console.WriteLine($"  {line}");

            return 0;
        }

        /// <summary>
        /// Everything about one area, by id, cross-checked against the world underneath it.
        ///
        /// The id is what the in-game editors show, so this is the bridge between "that one looks wrong"
        /// and something measurable. Reporting the area's own claim beside what the world says at the
        /// same points is the whole purpose: an area can be the right size, correctly connected and
        /// perfectly ordinary in every count, and still be describing a surface that is not there.
        /// </summary>
        private static int AreaInfo(string[] args)
        {
            if (args.Length < 4)
                throw new ArgumentException("expected: area <file.bsp> <file.nav> <id>");

            var (_, vis) = LoadBsp(args[1]);
            var nav = NavFile.Load(args[2]);
            uint id = uint.Parse(args[3]);

            var area = nav.Areas.Find(a => a.Id == id);
            if (area is null)
            {
                Console.WriteLine($"no area with id {id}");
                return 1;
            }

            var b = NavGeometry.GetBounds(area);

            Console.WriteLine($"area {id}");
            Console.WriteLine($"  bounds     x {b.MinX:F0}..{b.MaxX:F0}  y {b.MinY:F0}..{b.MaxY:F0}" +
                              $"   ({b.Width:F0} x {b.Depth:F0})");
            Console.WriteLine($"  corners    nw {area.NwCorner[2]:F1}  ne {area.NeZ:F1}  " +
                              $"se {area.SeCorner[2]:F1}  sw {area.SwZ:F1}");
            Console.WriteLine($"  attributes {(NavAttributes)area.AttributeFlags}");
            Console.WriteLine($"  connections {string.Join(", ", area.Connections.Select((l, d) => $"{Name(d)}={l.Count}"))}");
            Console.WriteLine($"  hiding spots {area.HidingSpots.Count}");

            Console.WriteLine();
            Console.WriteLine("  what the world says under this area (5x5 lattice):");

            const int Lattice = 5;
            int air = 0, buried = 0;

            for (int j = 0; j < Lattice; j++)
            {
                var row = new System.Text.StringBuilder("   ");

                for (int i = 0; i < Lattice; i++)
                {
                    float x = b.MinX + (i + 0.5f) / Lattice * b.Width;
                    float y = b.MinY + (j + 0.5f) / Lattice * b.Depth;
                    float claimed = NavGeometry.SurfaceZ(area, x, y);

                    bool inSolid = vis.IsPointSolid(x, y, claimed + NavConstants.StepHeight,
                        BspVisibility.GenerationMask);

                    if (!StairMarker.TryFindFloor(vis, x, y, claimed + NavConstants.StepHeight, 512f,
                            out float ground))
                    {
                        air++;
                        row.Append("    air");
                        continue;
                    }

                    if (inSolid) buried++;

                    row.Append($"{ground - claimed,7:F0}");
                }

                Console.WriteLine(row.ToString());
            }

            Console.WriteLine("   (numbers are floor height minus the area's claim; 0 is correct)");
            Console.WriteLine($"   samples over open air {air}, samples buried in solid {buried}");

            return 0;

            static string Name(int d) => d switch
            {
                NavGeometry.North => "N", NavGeometry.East => "E",
                NavGeometry.South => "S", _ => "W",
            };
        }

        /// <summary>
        /// Checks the swept-box trace against the line trace on real geometry.
        ///
        /// Swept-box collision is fiddly enough that "it compiles and the mesh looks about the same" is
        /// not evidence it works. The check that does not depend on knowing the right answer: a box with
        /// no size is a point, so sweeping a zero-extent box has to agree with tracing a line along the
        /// same segment - every time, on whatever geometry the map happens to have. Disagreement is a
        /// bug in the sweep, and it can be found without a single hand-computed expected value.
        ///
        /// The second half reports how much the sweep now stops on that a line does not, which is the
        /// thing it was written for: a body is wider than a sight line and should be blocked more often,
        /// never less.
        /// </summary>
        private static int HullCheck(string[] args)
        {
            if (args.Length < 2)
                throw new ArgumentException("expected: hull-check <file.bsp> [samples] [seed]");

            var (bsp, vis) = LoadBsp(args[1]);

            int samples = args.Length > 2 ? int.Parse(args[2]) : 20000;
            int seed = args.Length > 3 ? int.Parse(args[3]) : 20260807;

            if (bsp.BrushModelBounds.Length == 0)
                throw new InvalidOperationException("BSP has no world bounds");

            var world = bsp.BrushModelBounds[0];
            var random = new Random(seed);

            // Deliberately tiny rather than actually zero. A zero-extent box has no thickness along a
            // triangle's own normal, so the sweep enters and leaves that surface at the same instant and
            // whether it registers at all is decided by floating-point noise - the comparison would be
            // measuring rounding rather than the algorithm. A hundredth of a unit is far below anything
            // the mesh cares about and makes it stable.
            var zero = new BspFile.Vector3(-0.01f, -0.01f, -0.01f);
            var zeroMax = new BspFile.Vector3(0.01f, 0.01f, 0.01f);

            int agree = 0, pointHitLineMissed = 0, lineHitPointMissed = 0;
            int bodyBlockedLineClear = 0, lineBlockedBodyClear = 0;
            float worstFractionGap = 0;

            int rejected = 0;

            for (int i = 0; i < samples; i++)
            {
                var a = RandomPoint(random, world);

                // Only start in open space. A point trace beginning inside solid is reported blocked by
                // the leaf's contents alone, whereas a box sweep has nothing to clip unless that leaf
                // also lists brushes - and brushes are the correct source for a box, which is what
                // Source itself uses. Most of a world's bounding box is solid, so sampling it uniformly
                // compares the two traces almost entirely on the one case where they are meant to
                // differ, and says nothing about the case that matters.
                if (vis.IsPointSolid(a.X, a.Y, a.Z, BspVisibility.GenerationMask))
                {
                    rejected++;
                    i--;

                    if (rejected > samples * 100L)
                        throw new InvalidOperationException("too little open space in this map to sample");

                    continue;
                }

                // Short segments: long ones almost always hit something, which tests nothing.
                var b = new BspFile.Vector3(
                    a.X + (float)(random.NextDouble() - 0.5) * 256f,
                    a.Y + (float)(random.NextDouble() - 0.5) * 256f,
                    a.Z + (float)(random.NextDouble() - 0.5) * 256f);

                bool lineHit = vis.TryTraceSurface(a, b, BspVisibility.GenerationMask, out var linePoint, out _);

                bool pointHit = vis.TryTraceHull(a, b, zero, zeroMax, BspVisibility.GenerationMask,
                    out float pointFraction, out _, out bool pointSolid);

                if (lineHit == (pointHit && !pointSolid))
                {
                    agree++;

                    if (lineHit && pointHit)
                    {
                        float dx = linePoint.X - a.X, dy = linePoint.Y - a.Y, dz = linePoint.Z - a.Z;
                        float lineFraction = MathF.Sqrt(dx * dx + dy * dy + dz * dz) /
                            MathF.Max(1e-6f, Distance(a, b));

                        worstFractionGap = MathF.Max(worstFractionGap, MathF.Abs(lineFraction - pointFraction));
                    }
                }
                else if (pointHit && !lineHit)
                {
                    pointHitLineMissed++;
                }
                else
                {
                    lineHitPointMissed++;
                }

                // And the real question: a player-sized body against the same segment.
                bool bodyHit = vis.TryTraceHull(a, b, BspVisibility.NavTraceMins, BspVisibility.NavTraceMaxs,
                    BspVisibility.GenerationMask, out _, out _, out _);

                if (bodyHit && !lineHit) bodyBlockedLineClear++;
                if (lineHit && !bodyHit) lineBlockedBodyClear++;
            }

            Console.WriteLine($"bsp              {Path.GetFileName(args[1])}");
            Console.WriteLine($"displacements    {vis.DisplacementTriangleCount:N0} triangles");
            Console.WriteLine($"samples          {samples:N0}  ({rejected:N0} start points rejected as inside solid)");
            Console.WriteLine();
            Console.WriteLine("zero-extent box against a line (these must agree):");
            Console.WriteLine($"  agree                  {agree:N0}  ({100.0 * agree / samples:F2}%)");
            Console.WriteLine($"  box hit, line missed   {pointHitLineMissed:N0}");
            Console.WriteLine($"  line hit, box missed   {lineHitPointMissed:N0}");
            Console.WriteLine($"  worst fraction gap     {worstFractionGap:F4}");
            Console.WriteLine();
            Console.WriteLine("player-sized box against a line (the box should stop more, never less):");
            Console.WriteLine($"  body blocked, line clear   {bodyBlockedLineClear:N0}");
            Console.WriteLine($"  line blocked, body clear   {lineBlockedBodyClear:N0}  " +
                              "(expected to be small: a grazing line through a corner the box misses)");

            return lineHitPointMissed + pointHitLineMissed == 0 ? 0 : 1;

            static BspFile.Vector3 RandomPoint(Random r, BspFile.BrushModel w) => new(
                w.Mins.X + (float)r.NextDouble() * (w.Maxs.X - w.Mins.X),
                w.Mins.Y + (float)r.NextDouble() * (w.Maxs.Y - w.Mins.Y),
                w.Mins.Z + (float)r.NextDouble() * (w.Maxs.Z - w.Mins.Z));

            static float Distance(BspFile.Vector3 a, BspFile.Vector3 b)
            {
                float dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
                return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            }
        }

        /// <summary>
        /// Scores computed hiding spots against an engine-made mesh, by position.
        ///
        /// Counting them is not enough. Two meshes can carry the same number of spots and put them in
        /// entirely different corners, and the corner is the whole content of a hiding spot - the
        /// position is the answer, the count is just how many answers there are. Matching by proximity
        /// says how many of the engine's actual choices were reproduced.
        ///
        /// Most useful run with both meshes built on the same areas: point it at an engine mesh that has
        /// had spots recomputed, and any disagreement is the selection rule rather than a difference in
        /// where the areas happen to be.
        /// </summary>
        private static int CompareSpots(string[] args)
        {
            if (args.Length < 3)
                throw new ArgumentException("expected: compare-spots <reference.nav> <candidate.nav> [tolerance]");

            var reference = NavFile.Load(args[1]);
            var candidate = NavFile.Load(args[2]);

            float tolerance = args.Length > 3
                ? float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture)
                : 4f;

            var referenceSpots = new List<(float X, float Y, float Z, byte Flags)>();
            var candidateSpots = new List<(float X, float Y, float Z, byte Flags)>();

            foreach (var area in reference.Areas)
                foreach (var s in area.HidingSpots)
                    referenceSpots.Add((s.Position[0], s.Position[1], s.Position[2], s.Flags));

            foreach (var area in candidate.Areas)
                foreach (var s in area.HidingSpots)
                    candidateSpots.Add((s.Position[0], s.Position[1], s.Position[2], s.Flags));

            // Greedy nearest matching. The spots are far enough apart relative to the tolerance that
            // anything cleverer would return the same answer.
            var taken = new bool[candidateSpots.Count];
            int matched = 0, flagsAgree = 0;
            var misses = new List<string>();

            foreach (var r in referenceSpots)
            {
                int best = -1;
                float bestDistance = tolerance;

                for (int i = 0; i < candidateSpots.Count; i++)
                {
                    if (taken[i]) continue;

                    var c = candidateSpots[i];
                    float dx = c.X - r.X, dy = c.Y - r.Y, dz = c.Z - r.Z;
                    float distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

                    if (distance > bestDistance) continue;

                    bestDistance = distance;
                    best = i;
                }

                if (best < 0)
                {
                    if (misses.Count < 8)
                        misses.Add($"({r.X:F0} {r.Y:F0} {r.Z:F0}) flags 0x{r.Flags:X2}");

                    continue;
                }

                taken[best] = true;
                matched++;

                if (candidateSpots[best].Flags == r.Flags)
                    flagsAgree++;
            }

            int extra = candidateSpots.Count - matched;

            Console.WriteLine($"reference spots  {referenceSpots.Count:N0}");
            Console.WriteLine($"candidate spots  {candidateSpots.Count:N0}");
            Console.WriteLine($"matched          {matched:N0}  " +
                              $"({100.0 * matched / Math.Max(1, referenceSpots.Count):F1}% recall, " +
                              $"{100.0 * matched / Math.Max(1, candidateSpots.Count):F1}% precision, " +
                              $"within {tolerance:F0} units)");
            Console.WriteLine($"missed           {referenceSpots.Count - matched:N0}");
            Console.WriteLine($"extra            {extra:N0}");
            Console.WriteLine($"flags agree      {flagsAgree:N0} of {matched:N0} matched " +
                              $"({100.0 * flagsAgree / Math.Max(1, matched):F1}%)");

            if (misses.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("reference spots with no match nearby:");
                foreach (string m in misses)
                    Console.WriteLine($"  {m}");
            }

            return 0;
        }

        /// <summary>
        /// Adds hiding spots to a mesh - the cover positions bots fall back to, which the engine
        /// computes during nav_generate and nothing here produced until now.
        /// </summary>
        private static int BuildSpots(string[] args)
        {
            if (args.Length < 3)
                throw new ArgumentException("expected: build-spots <file.bsp> <file.nav> [-o out.nav]");

            string bspPath = args[1];
            string navPath = args[2];

            if (!File.Exists(navPath)) throw new FileNotFoundException($"no such file: {navPath}");

            string outPath = FlagValue(args, "-o")
                ?? Path.Combine(Path.GetDirectoryName(navPath) ?? ".",
                    Path.GetFileNameWithoutExtension(navPath) + ".spots.nav");

            var (_, vis) = LoadBsp(bspPath);
            var nav = NavFile.Load(navPath);

            Console.WriteLine($"bsp   {Path.GetFileName(bspPath)}");
            Console.WriteLine($"nav   {Path.GetFileName(navPath)}: {nav.Areas.Count:N0} areas");

            var sw = System.Diagnostics.Stopwatch.StartNew();

            using var bar = new ConsoleProgress(new NavProgress.Step("Finding hiding spots", 1.0));
            bar.Progress.Enter("Finding hiding spots");
            var result = HidingSpotFinder.Find(nav, vis, bar.Progress);
            bar.Progress.Finish();
            bar.Dispose();

            sw.Stop();

            Console.WriteLine($"      {result.Spots:N0} hiding spots across {result.AreasWithSpots:N0} areas " +
                              $"({result.InCover:N0} in cover, {result.Exposed:N0} exposed) " +
                              $"in {sw.ElapsedMilliseconds:N0} ms");

            nav.Save(outPath);
            Console.WriteLine($"out   {outPath}  ({new FileInfo(outPath).Length:N0} bytes)");

            var reloaded = NavFile.Load(outPath);
            long spots = reloaded.Areas.Sum(a => (long)a.HidingSpots.Count);
            Console.WriteLine($"check reloaded OK: {reloaded.Areas.Count:N0} areas, {spots:N0} hiding spots");

            return spots == result.Spots ? 0 : 1;
        }

        /// <summary>
        /// Describes the hiding spots in a mesh, in terms of where they sit inside their own area.
        ///
        /// Written to learn the rule from an engine-made mesh rather than guess at it. The `.nav` format
        /// stores a hiding spot as a bare world position with a flag byte, which says nothing about how
        /// the generator chose it; expressing each one as an offset from the nearest corner of its area,
        /// and looking at how that offset is distributed, is what turns a list of coordinates back into
        /// the rule that produced them.
        /// </summary>
        private static int Spots(string[] args)
        {
            if (args.Length < 2)
                throw new ArgumentException("expected: spots <file.nav>");

            var nav = NavFile.Load(args[1]);

            long total = 0;
            var byFlag = new SortedDictionary<int, int>();
            var perArea = new SortedDictionary<int, int>();
            var cornerOffsets = new List<float>();
            var heightOffsets = new List<float>();
            var samples = new List<string>();

            foreach (var area in nav.Areas)
            {
                perArea[area.HidingSpots.Count] = perArea.GetValueOrDefault(area.HidingSpots.Count) + 1;
                total += area.HidingSpots.Count;

                var b = NavGeometry.GetBounds(area);

                foreach (var spot in area.HidingSpots)
                {
                    byFlag[spot.Flags] = byFlag.GetValueOrDefault(spot.Flags) + 1;

                    // Distance to the nearest corner in plan view: if the generator places spots at
                    // corners, this clusters tightly on one value and that value is the inset.
                    float best = float.MaxValue;
                    foreach (var (cx, cy) in (ReadOnlySpan<(float, float)>)
                             [(b.MinX, b.MinY), (b.MaxX, b.MinY), (b.MinX, b.MaxY), (b.MaxX, b.MaxY)])
                    {
                        float dx = spot.Position[0] - cx, dy = spot.Position[1] - cy;
                        best = MathF.Min(best, MathF.Sqrt(dx * dx + dy * dy));
                    }

                    cornerOffsets.Add(best);
                    heightOffsets.Add(spot.Position[2] -
                                      NavGeometry.SurfaceZ(area, spot.Position[0], spot.Position[1]));

                    if (samples.Count < 6)
                    {
                        samples.Add($"area {area.Id,-7} spot at ({spot.Position[0]:F0} " +
                                    $"{spot.Position[1]:F0} {spot.Position[2]:F0})  " +
                                    $"corner offset {best:F1}  flags 0x{spot.Flags:X2}  " +
                                    $"area {b.Width:F0}x{b.Depth:F0}");
                    }
                }
            }

            cornerOffsets.Sort();
            heightOffsets.Sort();

            Console.WriteLine($"areas            {nav.Areas.Count:N0}");
            Console.WriteLine($"hiding spots     {total:N0}");
            Console.WriteLine($"areas with spots {nav.Areas.Count(a => a.HidingSpots.Count > 0):N0}");

            Console.WriteLine();
            Console.WriteLine("spots per area:  " +
                              string.Join(", ", perArea.Where(p => p.Key > 0).Select(p => $"{p.Value}x{p.Key}")));

            Console.WriteLine("flags:           " + string.Join(", ", byFlag.Select(p =>
                $"0x{p.Key:X2}({DescribeFlags((byte)p.Key)})={p.Value}")));

            if (cornerOffsets.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"offset from nearest corner:  min {cornerOffsets[0]:F1}  " +
                                  $"median {cornerOffsets[cornerOffsets.Count / 2]:F1}  " +
                                  $"p90 {cornerOffsets[(int)(cornerOffsets.Count * 0.9)]:F1}  " +
                                  $"max {cornerOffsets[^1]:F1}");
                Console.WriteLine($"height above area surface:   min {heightOffsets[0]:F1}  " +
                                  $"median {heightOffsets[heightOffsets.Count / 2]:F1}  " +
                                  $"max {heightOffsets[^1]:F1}");
            }

            Console.WriteLine();
            foreach (string s in samples)
                Console.WriteLine($"  {s}");

            long encounters = nav.Areas.Sum(a => (long)a.Encounters.Count);
            Console.WriteLine();
            Console.WriteLine($"encounters       {encounters:N0} across " +
                              $"{nav.Areas.Count(a => a.Encounters.Count > 0):N0} areas");

            return 0;

            static string DescribeFlags(byte flags)
            {
                var f = (HidingSpot.SpotFlags)flags;
                var parts = new List<string>();
                if ((f & HidingSpot.SpotFlags.InCover) != 0) parts.Add("cover");
                if ((f & HidingSpot.SpotFlags.GoodSniperSpot) != 0) parts.Add("good-sniper");
                if ((f & HidingSpot.SpotFlags.IdealSniperSpot) != 0) parts.Add("ideal-sniper");
                if ((f & HidingSpot.SpotFlags.Exposed) != 0) parts.Add("exposed");
                return parts.Count == 0 ? "none" : string.Join("|", parts);
            }
        }

        private static int Fit(string[] args)
        {
            if (args.Length < 3)
                throw new ArgumentException("expected: fit <file.bsp> <file.nav> [-tolerance u]");

            var (bsp, vis) = LoadBsp(args[1]);
            var nav = NavFile.Load(args[2]);

            float tolerance = FlagValue(args, "-tolerance") is { } t
                ? float.Parse(t, System.Globalization.CultureInfo.InvariantCulture)
                : NavConstants.StepHeight;

            const int Lattice = 5;

            // How far below the claimed surface to keep looking before calling it open air. Generous:
            // the question is whether there is any floor at all, and a legitimate area over a stairwell
            // can sit a long way above the next thing down.
            const float SearchDepth = 512f;

            int floatingAreas = 0, airAreas = 0;
            long floatingSamples = 0, airSamples = 0, totalSamples = 0;
            double errorSum = 0;
            var errors = new List<float>();
            var worstFloating = new List<(float Error, string Where)>();
            var worstAir = new List<(float Fraction, string Where)>();

            foreach (var area in nav.Areas)
            {
                var b = NavGeometry.GetBounds(area);
                int floating = 0, air = 0, taken = 0;
                float worst = 0;
                string worstAt = "";

                for (int i = 0; i < Lattice; i++)
                {
                    for (int j = 0; j < Lattice; j++)
                    {
                        float u = (i + 0.5f) / Lattice;
                        float v = (j + 0.5f) / Lattice;
                        float x = b.MinX + u * b.Width;
                        float y = b.MinY + v * b.Depth;
                        float claimed = NavGeometry.SurfaceZ(area, x, y);

                        taken++;

                        // Start the search a little above the claimed surface so a quad sitting a hair
                        // under the real floor still finds it rather than reporting the storey below.
                        if (!StairMarker.TryFindFloor(vis, x, y, claimed + NavConstants.StepHeight,
                                SearchDepth, out float actual))
                        {
                            air++;
                            continue;
                        }

                        float error = claimed - actual;
                        errors.Add(MathF.Abs(error));
                        errorSum += MathF.Abs(error);

                        // Only a positive error is "floating". A negative one means the traced floor is
                        // above the claimed surface, which is the buried case Shape already reports.
                        if (error > tolerance)
                        {
                            floating++;

                            if (error > worst)
                            {
                                worst = error;
                                worstAt = $"({x:F0} {y:F0}) claims {claimed:F0}, floor {actual:F0}";
                            }
                        }
                    }
                }

                totalSamples += taken;
                floatingSamples += floating;
                airSamples += air;

                float cx = (b.MinX + b.MaxX) / 2f, cy = (b.MinY + b.MaxY) / 2f;
                string where = $"area {area.Id,-7} at ({cx:F0} {cy:F0} " +
                               $"{NavGeometry.SurfaceZ(area, cx, cy):F0})  {b.Width:F0}x{b.Depth:F0}";

                if (floating > 0)
                {
                    floatingAreas++;
                    worstFloating.Add((worst, $"{where}  worst {worstAt}"));
                }

                if (air > 0)
                {
                    airAreas++;
                    worstAir.Add((air / (float)taken, where));
                }
            }

            errors.Sort();

            Console.WriteLine($"areas            {nav.Areas.Count:N0}   samples {totalSamples:N0} " +
                              $"({Lattice}x{Lattice} per area)");
            Console.WriteLine($"tolerance        {tolerance:F0} units");
            Console.WriteLine();
            Console.WriteLine($"height error     mean {errorSum / Math.Max(1, errors.Count):F1}   " +
                              $"median {(errors.Count > 0 ? errors[errors.Count / 2] : 0):F1}   " +
                              $"p90 {(errors.Count > 0 ? errors[(int)(errors.Count * 0.9)] : 0):F1}   " +
                              $"max {(errors.Count > 0 ? errors[^1] : 0):F1}");
            Console.WriteLine($"floating         {floatingAreas:N0} areas ({100.0 * floatingAreas / Math.Max(1, nav.Areas.Count):F1}%), " +
                              $"{floatingSamples:N0} samples ({100.0 * floatingSamples / Math.Max(1, totalSamples):F1}%)");
            Console.WriteLine($"over open air    {airAreas:N0} areas ({100.0 * airAreas / Math.Max(1, nav.Areas.Count):F1}%), " +
                              $"{airSamples:N0} samples ({100.0 * airSamples / Math.Max(1, totalSamples):F1}%)");

            worstFloating.Sort((a, b2) => b2.Error.CompareTo(a.Error));
            worstAir.Sort((a, b2) => b2.Fraction.CompareTo(a.Fraction));

            Console.WriteLine();
            Console.WriteLine("worst floating:");
            foreach (var (error, where) in worstFloating.Take(10))
                Console.WriteLine($"  {error,6:F0} above floor   {where}");

            Console.WriteLine();
            Console.WriteLine("worst over air:");
            foreach (var (fraction, where) in worstAir.Take(10))
                Console.WriteLine($"  {100 * fraction,5:F0}% no floor     {where}");

            return 0;
        }

        private static int Shape(string[] args)
        {
            if (args.Length < 3)
                throw new ArgumentException("expected: shape <file.bsp> <file.nav>");

            var (bsp, vis) = LoadBsp(args[1]);
            var nav = NavFile.Load(args[2]);

            // A 7x7 lattice inset half a sample from the edges, so the outermost ring sits just inside
            // the boundary being judged rather than exactly on it, where a coplanar wall face would
            // register as solid for every mesh ever made.
            const int Lattice = 7;

            int buriedAreas = 0;
            long buriedSamples = 0, totalSamples = 0;
            var worstOffenders = new List<(float Fraction, string Where)>();

            foreach (var area in nav.Areas)
            {
                var b = NavGeometry.GetBounds(area);
                int inside = 0, taken = 0;

                for (int i = 0; i < Lattice; i++)
                {
                    for (int j = 0; j < Lattice; j++)
                    {
                        float u = (i + 0.5f) / Lattice;
                        float v = (j + 0.5f) / Lattice;
                        float x = b.MinX + u * b.Width;
                        float y = b.MinY + v * b.Depth;
                        float z = NavGeometry.SurfaceZ(area, x, y);

                        taken++;

                        // Knee height. Low enough that a doorway's floor still reads as open, high
                        // enough to clear the surface itself and anything sitting flush on it.
                        var point = new BspFile.Vector3(x, y, z + 18f);
                        if (!vis.IsLineClear(point, point, BspVisibility.GenerationMask))
                            inside++;
                    }
                }

                totalSamples += taken;
                buriedSamples += inside;

                if (inside == 0)
                    continue;

                buriedAreas++;

                float fraction = inside / (float)taken;
                float cx = (b.MinX + b.MaxX) / 2f, cy = (b.MinY + b.MaxY) / 2f;
                worstOffenders.Add((fraction,
                    $"x {b.MinX:F0}..{b.MaxX:F0}  y {b.MinY:F0}..{b.MaxY:F0}  " +
                    $"z {NavGeometry.SurfaceZ(area, cx, cy):F0}"));
            }

            var aspects = new List<float>();
            var longest = new List<float>();

            foreach (var area in nav.Areas)
            {
                var b = NavGeometry.GetBounds(area);
                float lo = MathF.Min(b.Width, b.Depth);
                float hi = MathF.Max(b.Width, b.Depth);

                longest.Add(hi);
                aspects.Add(lo < 0.01f ? 999f : hi / lo);
            }

            aspects.Sort();
            longest.Sort();

            static float Percentile(List<float> sorted, double p)
                => sorted.Count == 0 ? 0f : sorted[Math.Clamp((int)(sorted.Count * p), 0, sorted.Count - 1)];

            int total = Math.Max(1, nav.Areas.Count);

            Console.WriteLine($"areas                  {nav.Areas.Count:N0}");
            Console.WriteLine($"areas touching solid   {buriedAreas:N0}  ({100.0 * buriedAreas / total:F1}%)");
            Console.WriteLine($"footprint in solid     {100.0 * buriedSamples / Math.Max(1, totalSamples):F2}% of {totalSamples:N0} samples");
            Console.WriteLine($"aspect   median {Percentile(aspects, 0.5):F1}   p90 {Percentile(aspects, 0.9):F1}   max {(aspects.Count > 0 ? aspects[^1] : 0):F1}");
            Console.WriteLine($"longest  median {Percentile(longest, 0.5):F0}   p90 {Percentile(longest, 0.9):F0}   max {(longest.Count > 0 ? longest[^1] : 0):F0}");

            worstOffenders.Sort((a, b) => b.Fraction.CompareTo(a.Fraction));
            foreach (var (fraction, where) in worstOffenders.Take(6))
                Console.WriteLine($"  worst  {100 * fraction,5:F0}% buried at {where}");

            return 0;
        }

        /// <summary>
        /// Draws a live progress line for a run, the way the engine's own nav_generate does.
        ///
        /// Redraws in place on a terminal and falls back to occasional plain lines when the output is
        /// piped to a file, where a few thousand carriage returns would be unreadable rather than
        /// animated. Disposing tidies up after the last redraw so the next thing printed does not land
        /// on top of the bar.
        /// </summary>
        private sealed class ConsoleProgress : IDisposable
        {
            private const int BarCells = 24;

            /// <summary>
            /// Redraw in place, or print occasional lines.
            ///
            /// Auto-detected from whether the output is a terminal, because a bar redrawn a few thousand
            /// times is unreadable in a log file. <c>NAVPAL_PROGRESS</c> overrides it - "live", "plain"
            /// or "off" - for the cases detection gets wrong, which is anything running the tool behind
            /// a wrapper that owns the console.
            /// </summary>
            private readonly bool live = Environment.GetEnvironmentVariable("NAVPAL_PROGRESS")?.ToLowerInvariant()
                switch
                {
                    "live" => true,
                    "plain" or "off" => false,
                    _ => !Console.IsOutputRedirected,
                };

            private readonly bool silent =
                string.Equals(Environment.GetEnvironmentVariable("NAVPAL_PROGRESS"), "off",
                    StringComparison.OrdinalIgnoreCase);

            private string lastPhase = "";
            private TimeSpan lastLine = TimeSpan.MinValue;
            private int written;

            public NavProgress Progress { get; }

            public ConsoleProgress(params NavProgress.Step[] steps)
            {
                Progress = new NavProgress(Render, steps);
            }

            private void Render(NavProgress.Update u)
            {
                if (silent)
                    return;

                bool newPhase = !string.Equals(u.Phase, lastPhase, StringComparison.Ordinal);

                if (!live)
                {
                    // Piped: one line per phase, plus a heartbeat so a long phase still shows life.
                    if (!newPhase && (u.Elapsed - lastLine).TotalSeconds < 5)
                        return;

                    lastPhase = u.Phase;
                    lastLine = u.Elapsed;
                    Console.WriteLine($"      [{u.Index}/{u.Total}] {u.Phase} - {Detail(u)} ({Clock(u.Elapsed)})");
                    return;
                }

                lastPhase = u.Phase;

                string line = $"      [{u.Index}/{u.Total}] {Truncate(u.Phase, 28),-28} {Detail(u)}  {Clock(u.Elapsed)}";

                Console.Write('\r');
                Console.Write(line.PadRight(written));
                written = Math.Max(written, line.Length);
            }

            private static string Detail(NavProgress.Update u) => u.Fraction is { } f
                ? $"[{new string('#', (int)(f * BarCells)).PadRight(BarCells, '.')}] {f * 100,3:F0}%"
                : $"{u.Count,12:N0} cells";

            private static string Clock(TimeSpan t) => t.TotalMinutes >= 1
                ? $"{(int)t.TotalMinutes}m{t.Seconds:00}s"
                : $"{t.TotalSeconds:F1}s";

            private static string Truncate(string s, int max)
                => s.Length <= max ? s : s[..(max - 1)] + "…";

            /// <summary>
            /// Idempotent: callers dispose explicitly to close the bar before printing results, and the
            /// <c>using</c> that owns it disposes again on the way out.
            /// </summary>
            public void Dispose()
            {
                if (!live || written == 0)
                    return;

                written = 0;
                Console.WriteLine();
            }
        }

        /// <summary>
        /// Runs the redundant-shortcut fixup on a mesh's existing connections in isolation, with no
        /// ConnectionBuilder pass first. Isolated so the count can be checked against a mesh's own
        /// as-shipped graph rather than against connections this tool just added itself.
        /// </summary>
        private static int FixConnections(string[] args)
        {
            if (args.Length < 2)
                throw new ArgumentException("expected: fix-connections <file.nav> [-o out.nav]");

            var nav = NavFile.Load(args[1]);
            long before = nav.Areas.Sum(a => a.Connections.Sum(c => (long)c.Count));

            var result = AreaConnectionFixer.Fix(nav);

            long after = nav.Areas.Sum(a => a.Connections.Sum(c => (long)c.Count));
            Console.WriteLine($"nav   {Path.GetFileName(args[1])}: {nav.Areas.Count:N0} areas, {before:N0} connections");
            Console.WriteLine($"      removed {result.ShortcutsRemoved:N0} redundant shortcuts -> {after:N0} connections");

            if (FlagValue(args, "-o") is { } outPath)
            {
                nav.Save(outPath);
                Console.WriteLine($"out   {outPath}");
            }

            return 0;
        }

        /// <summary>
        /// Explains a single coverage miss: is the point standable at all, and if it locally connects to
        /// a real chunk of ground, where does that local flood stop. Built to tell a genuine gap - a
        /// point cut off from everywhere else at any climb the generator allows - from a bug where a
        /// perfectly walkable path exists but some rule along it rejects one link.
        /// </summary>
        private static int Reach(string[] args)
        {
            if (args.Length < 5)
                throw new ArgumentException("expected: reach <file.bsp> x y z [-radius units]");

            var (bsp, vis) = LoadBsp(args[1]);

            var c = System.Globalization.CultureInfo.InvariantCulture;
            var point = new BspFile.Vector3(
                float.Parse(args[2], c), float.Parse(args[3], c), float.Parse(args[4], c));

            float radius = FlagValue(args, "-radius") is { } r ? float.Parse(r, c) : 800f;

            var report = AreaGenerator.DiagnoseReach(vis, bsp, point, radius);

            Console.WriteLine($"start            {point}");
            Console.WriteLine($"has floor        {report.StartHasFloor}");
            Console.WriteLine($"standable        {report.StartStandable}");
            Console.WriteLine($"local component  {report.LocalComponentSize:N0} cells within {radius:F0} units, " +
                              $"z {report.MinZ:F0}..{report.MaxZ:F0}");

            if (report.LargestOneWayDropAt is not null)
            {
                string verdict = report.LargestOneWayDrop > NavConstants.JumpCrouchHeight
                    ? " <- exceeds the 58-unit climb limit; an upward flood cannot cross this in one step"
                    : "";
                Console.WriteLine($"steepest step on the path down  {report.LargestOneWayDrop:F0} units " +
                                  $"at {report.LargestOneWayDropAt}{verdict}");
            }

            foreach (string note in report.Notes)
                Console.WriteLine($"note: {note}");

            if (report.DeadEnds.Count > 0)
            {
                Console.WriteLine("dead ends (cells with no reachable neighbour within radius):");
                foreach (string deadEnd in report.DeadEnds)
                    Console.WriteLine($"  {deadEnd}");
            }

            return 0;
        }

        private static int UnknownCommand(string command)
        {
            Console.Error.WriteLine($"error: unknown command '{command}'");
            Usage();
            return 1;
        }

        private static string RequirePath(string[] args)
        {
            if (args.Length < 2)
                throw new ArgumentException("expected a path to a .nav file");

            if (!File.Exists(args[1]))
                throw new FileNotFoundException($"no such file: {args[1]}");

            return args[1];
        }

        private static int Info(string[] args)
        {
            string path = RequirePath(args);
            var nav = NavFile.Load(path);

            long connections = nav.Areas.Sum(a => a.Connections.Sum(c => (long)c.Count));
            long hidingSpots = nav.Areas.Sum(a => (long)a.HidingSpots.Count);
            long visiblePairs = nav.Areas.Sum(a => (long)a.VisibleAreas.Count);
            int isolated = nav.Areas.Count(a => a.Connections.All(c => c.Count == 0));
            long ladderRefs = nav.Areas.Sum(a => a.Ladders.Sum(l => (long)l.Count));

            Console.WriteLine($"file            {Path.GetFileName(path)}  ({new FileInfo(path).Length:N0} bytes)");
            Console.WriteLine($"version         {nav.Version} (sub {nav.SubVersion})");
            Console.WriteLine($"bsp size        {nav.BspSize:N0}");
            Console.WriteLine($"analyzed        {nav.IsAnalyzed}");
            Console.WriteLine($"places          {nav.Places.Count}");
            Console.WriteLine($"areas           {nav.Areas.Count:N0}");
            Console.WriteLine($"connections     {connections:N0}  (avg {(nav.Areas.Count > 0 ? connections / (double)nav.Areas.Count : 0):F2} per area)");
            Console.WriteLine($"isolated areas  {isolated:N0}");
            Console.WriteLine($"hiding spots    {hidingSpots:N0}");

            var attributeCounts = new SortedDictionary<NavAttributes, int>();
            foreach (var area in nav.Areas)
            {
                var flags = (NavAttributes)area.AttributeFlags;
                foreach (NavAttributes bit in Enum.GetValues<NavAttributes>())
                {
                    if (bit != NavAttributes.None && flags.HasFlag(bit))
                        attributeCounts[bit] = attributeCounts.GetValueOrDefault(bit) + 1;
                }
            }

            Console.WriteLine(attributeCounts.Count == 0
                ? "attributes      none set on any area"
                : $"attributes      {string.Join(", ", attributeCounts.Select(kv => $"{kv.Key}={kv.Value:N0}"))}");

            Console.WriteLine($"visible pairs   {visiblePairs:N0}");

            if (visiblePairs > 0)
            {
                // The attributes byte and the inherit id together encode Valve's delta compression.
                // Their exact meaning decides whether a compressed mesh can be written safely, so the
                // raw distribution is worth surfacing rather than assuming.
                var attributes = new SortedDictionary<byte, long>();
                int inheriting = 0;
                int inheritingWithOwnList = 0;

                foreach (var area in nav.Areas)
                {
                    foreach (var v in area.VisibleAreas)
                        attributes[v.Attributes] = attributes.GetValueOrDefault(v.Attributes) + 1;

                    if (area.InheritVisibilityFrom == 0) continue;

                    inheriting++;
                    if (area.VisibleAreas.Count > 0) inheritingWithOwnList++;
                }

                Console.WriteLine($"  attributes    {string.Join(", ", attributes.Select(kv => $"0x{kv.Key:X2}={kv.Value:N0}"))}");
                Console.WriteLine($"  inheriting    {inheriting:N0} areas ({inheritingWithOwnList:N0} also carry their own entries)");
            }

            Console.WriteLine($"ladders         {nav.Ladders.Count:N0}  ({ladderRefs:N0} area references)");
            Console.WriteLine($"trailing bytes  {nav.TrailingData.Length:N0}");

            foreach (var l in nav.Ladders)
            {
                Console.WriteLine($"  ladder #{l.Id} dir={l.Direction} w={l.Width:F1} len={l.Length:F1} " +
                                  $"bottom=({l.Bottom[0]:F1} {l.Bottom[1]:F1} {l.Bottom[2]:F1}) " +
                                  $"top=({l.Top[0]:F1} {l.Top[1]:F1} {l.Top[2]:F1}) " +
                                  $"areas fwd={l.TopForwardAreaId} l={l.TopLeftAreaId} r={l.TopRightAreaId} " +
                                  $"behind={l.TopBehindAreaId} bottom={l.BottomAreaId}");
            }

            return 0;
        }

        /// <summary>
        /// Reads the file, writes it back out from the parsed model, and compares byte-for-byte.
        ///
        /// This is the gate for trusting the parser at all: the area record contains fixed-size arrays
        /// whose lengths come from engine constants, and a wrong length shifts every subsequent byte
        /// without throwing. A clean round-trip on a real mesh is what proves the layout is right.
        /// </summary>
        private static int Verify(string[] args)
        {
            string path = RequirePath(args);

            var original = File.ReadAllBytes(path);
            var nav = NavFile.Load(path);

            using var buffer = new MemoryStream();
            using (var writer = new BinaryWriter(buffer, System.Text.Encoding.ASCII, leaveOpen: true))
                nav.Write(writer);

            var rewritten = buffer.ToArray();

            Console.WriteLine($"{Path.GetFileName(path)}: {nav.Areas.Count:N0} areas, {nav.Ladders.Count:N0} ladders");
            Console.WriteLine($"  original  {original.Length:N0} bytes");
            Console.WriteLine($"  rewritten {rewritten.Length:N0} bytes");

            if (original.Length != rewritten.Length)
            {
                Console.Error.WriteLine($"  FAIL: length differs by {rewritten.Length - original.Length:N0} bytes");
                ReportFirstDifference(original, rewritten);
                return 1;
            }

            int mismatch = -1;
            for (int i = 0; i < original.Length; i++)
            {
                if (original[i] != rewritten[i]) { mismatch = i; break; }
            }

            if (mismatch >= 0)
            {
                Console.Error.WriteLine($"  FAIL: first differing byte at offset {mismatch:N0} (0x{mismatch:X})");
                ReportFirstDifference(original, rewritten);
                return 1;
            }

            Console.WriteLine("  PASS: byte-for-byte identical");
            return 0;
        }

        private static void ReportFirstDifference(byte[] a, byte[] b)
        {
            int limit = Math.Min(a.Length, b.Length);
            for (int i = 0; i < limit; i++)
            {
                if (a[i] == b[i]) continue;

                int start = Math.Max(0, i - 8);
                int count = Math.Min(24, limit - start);
                Console.Error.WriteLine($"  offset 0x{i:X}:");
                Console.Error.WriteLine($"    original  {Convert.ToHexString(a, start, count)}");
                Console.Error.WriteLine($"    rewritten {Convert.ToHexString(b, start, count)}");
                return;
            }

            Console.Error.WriteLine("  (common prefix identical; files differ only in length)");
        }
    }
}
