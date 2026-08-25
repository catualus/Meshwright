using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Meshwright
{
    /// <summary>
    /// The whole nav build, in the one order it is allowed to happen in.
    ///
    /// This exists because the ordering used to be written out by hand in two places - the command
    /// line's <c>build-*</c> stages, and Compile Pal's own compile step - and they drifted. Not
    /// subtly, either: the compile step never stitched jump areas, so every jump area a real compile
    /// produced stayed in the mesh un-stitched and (because <see cref="AreaClipper"/> deliberately
    /// skips them) un-clipped; it marked stairs before clipping rather than after, reporting 8 on
    /// gm_construct against the command line's 17; it never ran <see cref="NavIntegrity"/>, so meshes
    /// it wrote still carried the dangling ids the engine complains about at load; and it never graded
    /// sniper spots or built encounters at all. Every one of those was a difference between two entry
    /// points that are supposed to produce the same mesh.
    ///
    /// So the order lives here once and both callers drive it. The ordering constraints are not
    /// arbitrary and each is load-bearing:
    ///
    /// <list type="bullet">
    /// <item>Areas before ladders and connections - both attach to areas that must already exist.</item>
    /// <item>Connections before clipping. Clipping moves an edge off a wall, away from whatever used
    /// to abut it, and the connection pass has only the edges to go on.</item>
    /// <item>Stitching immediately after connecting, because the stitch needs to know what each jump
    /// area joined, which only the connection pass knows.</item>
    /// <item>Everything that reads an area's final shape - stairs, lifts, corners - after clipping.</item>
    /// <item>The reachability prune after every pass that can add a connection, and before the two
    /// that are priced per area - pruning earlier strands areas the corner patcher was about to join,
    /// pruning later spends the most expensive work in the pipeline on areas being deleted.</item>
    /// <item>Spots after movement: an encounter is a list of covered spots along a path, so it needs
    /// both the connection graph and the cover flags.</item>
    /// <item>Visibility last, and the integrity sweep after that, immediately before the write.</item>
    /// </list>
    ///
    /// Nothing here writes to the console. Callers pass a <see cref="Options.Log"/> sink, because the
    /// command line wants stdout and a host wants its own log, and a library that picks for them can
    /// only be wrong for one of them.
    /// </summary>
    public static class NavPipeline
    {
        public const string PhaseSampling = "Sampling walkable space";
        public const string PhaseLinking = "Linking samples";
        public const string PhaseAreas = "Building areas";
        public const string PhaseMerging = "Merging areas";
        public const string PhaseLadders = "Building ladders";
        public const string PhaseConnections = "Connecting areas";
        public const string PhaseClipping = "Clipping areas to geometry";
        public const string PhaseStairs = "Marking stairs";
        public const string PhaseReachability = "Checking reachability";
        public const string PhaseHiding = "Finding hiding spots";
        public const string PhaseSnipers = "Grading sniper spots";
        public const string PhaseEncounters = "Finding encounter spots";
        public const string PhaseVisibility = "Tracing area visibility";
        public const string PhaseCompress = "Compressing visibility";

        public sealed class Options
        {
            /// <summary>Find walkable ground the mesh is missing and add it. Off means finish an
            /// existing mesh rather than extend it.</summary>
            public bool GenerateAreas;

            /// <summary>Discard the loaded mesh and flood from player spawns instead of adding to it.
            /// Meaningless without <see cref="GenerateAreas"/>, and refused rather than silently
            /// obeyed in that case - see <see cref="Run"/>.</summary>
            public bool FromScratch;

            public bool Ladders = true;
            public bool Movement = true;

            /// <summary>
            /// Delete the areas nothing can reach from a player spawn, once the connection graph exists.
            ///
            /// **Off by default, on the evidence.** The intuition is that unreachable generated mesh is
            /// junk - the sampler does leave areas inside sealed voids, and it was one of those, linked
            /// to the world by a drop through a floor, that prompted this. Scored against the meshes the
            /// engine generates for the two maps there is a reference for, the intuition is wrong about
            /// the majority. Pruning every unreachable area on rp_downtown_meowy removed 1,065 of them
            /// and took coverage of the engine's own ground from 98.0% to 92.6%; capping it to groups of
            /// eight or fewer still removed 457 and still cost 2.3 points. Nearly all of it is real
            /// ground the movement pass failed to link, and the engine reaches it perfectly well.
            ///
            /// So the stranded count is *reported* on every run and acted on only when asked. Leaving a
            /// stranded area in place costs nothing at runtime - nothing can path into it, which is what
            /// stranded means - where deleting one costs the map. The bogus drop that made stranded mesh
            /// visible in the first place is fixed where it was made, in
            /// <see cref="ConnectionBuilder"/>.
            /// </summary>
            public bool PruneUnreachable;
            public bool Spots = true;
            public bool SniperSpots = true;
            public bool EncounterSpots = true;
            public bool Visibility = true;
            public bool CompressVisibility = true;

            public float MaxViewDistance = VisibilityFilter.DefaultMaxViewDistance;

            /// <summary>Where the running commentary goes. Null discards it.</summary>
            public Action<string>? Log;

            /// <summary>Null is fine; <see cref="NavProgress.None"/> is used instead.</summary>
            public NavProgress? Progress;
        }

        public sealed class Result
        {
            public AreaGenerator.Result? Areas;
            public AreaClipper.Result? Clipped;
            public LadderBuilder.Result? Ladders;
            public ConnectionBuilder.Result? Connections;
            public JumpAreaStitcher.Result? Stitched;
            public StairMarker.Result? Stairs;
            public ElevatorConnector.Result? Elevators;
            public CornerPatcher.Result? Corners;
            public AreaConnectionFixer.Result? Fixup;
            public NavReachability.PruneResult? Pruned;
            public HidingSpotFinder.Result? HidingSpots;
            public SniperSpotClassifier.Result? SniperSpots;
            public EncounterSpotBuilder.Result? Encounters;
            public NavIntegrity.Result Integrity;

            /// <summary>Things the caller should see even if it is not reading the log - a missing
            /// vis lump, an option that could not be honoured.</summary>
            public readonly List<string> Warnings = [];
        }

        /// <summary>
        /// The phases <paramref name="options"/> will actually enter, in the order they are entered,
        /// with their share of a typical run.
        ///
        /// Callers building a progress bar must use this rather than listing phases themselves. Both
        /// facts here are easy to get wrong from the outside and neither fails loudly: a phase listed
        /// out of the order it is entered makes the counter jump backwards (Compile Pal's bar used to
        /// report "[8/10]" then "[11/11]" for exactly that reason), and weights that ignore how
        /// lopsided this pipeline is park the bar early - visibility alone is around 68% of a full run.
        /// </summary>
        public static IReadOnlyList<NavProgress.Step> Plan(Options options)
        {
            var steps = new List<NavProgress.Step>();

            if (options.GenerateAreas)
            {
                steps.Add(new NavProgress.Step(PhaseSampling, 0.10));
                steps.Add(new NavProgress.Step(PhaseLinking, 0.03));
                steps.Add(new NavProgress.Step(PhaseAreas, 0.03));
                steps.Add(new NavProgress.Step(PhaseMerging, 0.01));
            }

            if (options.Ladders) steps.Add(new NavProgress.Step(PhaseLadders, 0.01));
            if (options.Movement) steps.Add(new NavProgress.Step(PhaseConnections, 0.05));
            if (options.GenerateAreas) steps.Add(new NavProgress.Step(PhaseClipping, 0.02));
            if (options.Movement) steps.Add(new NavProgress.Step(PhaseStairs, 0.02));
            if (options.Movement) steps.Add(new NavProgress.Step(PhaseReachability, 0.01));

            if (options.Spots)
            {
                steps.Add(new NavProgress.Step(PhaseHiding, 0.01));
                if (options.SniperSpots) steps.Add(new NavProgress.Step(PhaseSnipers, 0.03));
                if (options.EncounterSpots) steps.Add(new NavProgress.Step(PhaseEncounters, 0.02));
            }

            if (options.Visibility)
            {
                steps.Add(new NavProgress.Step(PhaseVisibility, 0.68));
                if (options.CompressVisibility) steps.Add(new NavProgress.Step(PhaseCompress, 0.05));
            }

            return steps;
        }

        /// <summary>
        /// Reads a BSP and everything traced against it.
        ///
        /// Overlapped rather than sequential, because loading is not a small part of a run any more.
        /// Once the passes themselves got fast, a cold spot build on gm_construct was spending roughly
        /// 590ms here against 933ms of actual work - all of it on one core while fifteen sat idle.
        ///
        /// Three of the four readers are independent, and the dependency graph says so plainly:
        /// displacements need nothing but the file, so they start before anything else; models and
        /// visibility both need the parsed lumps but never touch each other. Each opens its own handle,
        /// and everything they read from <see cref="BspFile"/> is finished being written before either
        /// is started, so none of them shares mutable state.
        ///
        /// Getting this wrong is not a loud failure, which is why every caller goes through here rather
        /// than assembling it itself: a tracer with no displacements attached silently reports open air
        /// over every piece of terrain on the map, and the run still prints plausible numbers.
        /// </summary>
        public static (BspFile Bsp, BspVisibility Vis) LoadBsp(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"no such file: {path}");

            // Depends only on the file itself, so it need not wait for the lump parse at all.
            var displacements = Task.Run(() => BspDisplacements.Load(path));

            // Likewise, and much the slower of the two: props read the prop lump, then hunt each model
            // down through the map's pakfile, the loose game directories and the VPKs, and parse a
            // collision hull out of every one. That is file system work rather than CPU work, so it
            // overlaps the rest better than it would if it were competing for cores.
            var props = Task.Run(() => StaticProps.Load(path));

            var bsp = BspFile.Load(path);

            // Both need `bsp` and neither needs the other. Visibility is much the heavier of the two -
            // it decompresses the whole PVS - so it stays on this thread and models goes to the pool.
            var models = Task.Run(() => BspModels.Load(path, bsp));
            var vis = BspVisibility.Load(path, bsp);

            vis.AttachModels(models.Result);
            vis.AttachDisplacements(displacements.Result);
            vis.AttachStaticProps(props.Result);

            return (bsp, vis);
        }

        /// <summary>
        /// Runs every enabled pass over <paramref name="nav"/>, in place.
        ///
        /// Does not save. Writing is the caller's, because a command line writing to a path it was
        /// given and a compile step swapping a file in atomically want different things, and neither
        /// wants the other's.
        /// </summary>
        public static Result Run(BspFile bsp, BspVisibility vis, NavFile nav, Options options)
        {
            var result = new Result();
            var progress = options.Progress ?? NavProgress.None;
            var log = options.Log ?? (_ => { });

            // Only meaningful alongside area generation. Honoured without it, every later pass would
            // run over an empty mesh and the run would "succeed" with nothing in it, so this refuses
            // and says why rather than silently obeying.
            if (options.FromScratch)
            {
                if (options.GenerateAreas)
                {
                    nav.Areas.Clear();
                    nav.Ladders.Clear();
                    log("Discarding the existing nav mesh - generating fresh from player spawns.");
                }
                else
                {
                    string warning = "\"Start from scratch\" has no effect without area generation " +
                                     "enabled; ignored.";
                    result.Warnings.Add(warning);
                    log(warning);
                }
            }

            NavConcurrency.ThrowIfCancelled();

            if (options.GenerateAreas)
            {
                progress.Enter(PhaseSampling);
                var areas = AreaGenerator.Generate(nav, vis, bsp, progress: progress);
                result.Areas = areas;

                log($"Areas: {areas.AreasAdded:N0} added, flooded from {areas.Seeds:N0} seeds " +
                    $"({areas.Visited:N0} cells visited)");

                foreach (string note in areas.Notes)
                    log($"       {note}");

                if (areas.AreasAdded > 0)
                {
                    result.Warnings.Add(
                        "Generated areas are experimental and unverified in game - review before shipping.");
                }
            }

            NavConcurrency.ThrowIfCancelled();

            if (options.Ladders)
            {
                progress.Enter(PhaseLadders);
                RunLadders(nav, vis, bsp, result, log);
            }

            NavConcurrency.ThrowIfCancelled();

            if (options.Movement)
            {
                progress.Enter(PhaseConnections);
                var links = ConnectionBuilder.Build(nav, vis, progress);
                result.Connections = links;

                log($"Connections: {links.Steps:N0} steps, {links.JumpsUp:N0} jumps up, " +
                    $"{links.Drops:N0} drops ({links.Rejected:N0} rejected by trace)");

                // Right after connecting, never later: the stitch needs to know what each jump area
                // joined, and only the pass above knows that.
                var stitched = JumpAreaStitcher.Stitch(nav);
                result.Stitched = stitched;

                if (stitched.JumpAreas > 0)
                {
                    log($"Jump areas: {stitched.JumpAreas:N0} stitched out, " +
                        $"{stitched.ConnectionsAdded:N0} connections bridged across them");
                }
            }

            NavConcurrency.ThrowIfCancelled();

            // After the connection graph exists, never before it - see AreaGenerator.ClipToGeometry.
            if (options.GenerateAreas)
            {
                var trimmed = AreaGenerator.ClipToGeometry(nav, vis, progress);
                result.Clipped = trimmed;

                if (trimmed.Clipped > 0)
                {
                    log($"Clipped: {trimmed.Clipped:N0} areas pulled back to geometry " +
                        $"({trimmed.Reclaimed / 1024f:N0}k sq units out of walls)" +
                        (trimmed.Discarded > 0
                            ? $", discarded {trimmed.Discarded:N0} left too narrow to walk"
                            : ""));
                }
            }

            NavConcurrency.ThrowIfCancelled();

            if (options.Movement)
                RunPostClipMovement(nav, vis, bsp, result, progress, log);

            NavConcurrency.ThrowIfCancelled();

            if (options.Spots)
                RunSpots(nav, vis, options, result, progress, log);

            NavConcurrency.ThrowIfCancelled();

            if (options.Visibility)
                RunVisibility(nav, vis, options, result, progress, log);

            progress.Finish();

            // Last thing before the caller writes. Every pass above that removes or replaces an area is
            // a chance to leave an id pointing at something that no longer exists, and nothing on this
            // side of the tooling can see one: it survives a byte-for-byte round trip and passes every
            // quality measure here. The engine notices, loudly, at load.
            result.Integrity = NavIntegrity.Prune(nav);

            if (result.Integrity.Total > 0)
            {
                log($"Pruned {result.Integrity.Total:N0} references that pointed nowhere: " +
                    $"{result.Integrity.Connections:N0} connections, " +
                    $"{result.Integrity.SelfConnections:N0} self-links, " +
                    $"{result.Integrity.Duplicates:N0} duplicates, " +
                    $"{result.Integrity.Ladders:N0} ladder, " +
                    $"{result.Integrity.LadderEndpoints:N0} ladder endpoint, " +
                    $"{result.Integrity.Visibility:N0} visibility, " +
                    $"{result.Integrity.Inherits:N0} inherit");
            }

            return result;
        }

        /// <summary>
        /// Floods the finished connection graph from the map's player spawns and reports what it could
        /// not get to - deleting it only when asked.
        ///
        /// Reported on every run rather than only when pruning, because this is the number that says
        /// whether the mesh is any good. Area counts and coverage both look healthy on a mesh whose far
        /// half no bot can enter, and until this ran as part of a normal build the only way to find that
        /// out was to know to run a diagnostic afterwards.
        /// </summary>
        private static void RunReachability(BspFile bsp, NavFile nav, Options options, Result result,
            Action<string> log)
        {
            int before = nav.Areas.Count;
            var spawns = AreaGenerator.SpawnPositions(bsp).ToList();

            if (!options.PruneUnreachable)
            {
                var analysis = NavReachability.Analyse(nav, spawns);

                if (analysis.Seeds == 0)
                {
                    log("Reachability: no player spawn resolved to an area; nothing to flood from");
                    return;
                }

                if (analysis.Unreachable == 0)
                {
                    log($"Reachability: all {before:N0} areas reachable from a player spawn");
                    return;
                }

                log($"Reachability: {analysis.Unreachable:N0} of {before:N0} areas " +
                    $"({100.0 * analysis.Unreachable / Math.Max(1, before):F1}%) cannot be reached from " +
                    $"a player spawn, in {analysis.Islands.Count:N0} groups " +
                    $"(largest {analysis.Islands[0].Size:N0})");

                log("              kept - most stranded ground is real and merely unlinked. " +
                    "`reachable` shows where; -pruneunreachable deletes the small groups.");
                return;
            }

            var pruned = NavReachability.PruneUnreachable(nav, spawns);
            result.Pruned = pruned;

            if (pruned.Refused)
            {
                string warning = $"Unreachable areas kept: {pruned.Note}.";
                result.Warnings.Add(warning);
                log($"Reachability: {warning}");
                return;
            }

            if (pruned.Removed == 0)
            {
                log(pruned.Note is null
                    ? $"Reachability: all {before:N0} areas reachable from a player spawn"
                    : $"Reachability: {pruned.Note}");
                return;
            }

            log($"Reachability: removed {pruned.Removed:N0} of {before:N0} areas nothing can reach " +
                $"({pruned.Islands:N0} groups in all, largest {pruned.LargestIsland:N0})");

            // Reported even when pruning is on. A large stranded group is real ground the movement pass
            // failed to link, which is a defect worth seeing rather than one worth deleting, and a
            // silent prune would hide exactly the cases it correctly declines to touch.
            if (pruned.Stranded > 0)
            {
                log($"              {pruned.Stranded:N0} left in place as structure rather than stray " +
                    "samples - run `reachable` to see where");
            }
        }

        private static void RunLadders(NavFile nav, BspVisibility vis, BspFile bsp, Result result,
            Action<string> log)
        {
            var brushes = LadderFinder.Find(bsp);

            if (brushes.Count == 0)
            {
                log("Ladders: none found in the BSP");
                return;
            }

            // The tracer is what stops a ladder wiring itself to whatever is nearest through a wall.
            var built = LadderBuilder.Build(nav, brushes, vis);
            result.Ladders = built;

            log($"Ladders: {built.LaddersAdded:N0} added from {brushes.Count:N0} brushes " +
                $"({built.BottomConnected:N0} bottom, {built.TopConnected:N0} top connections)");

            if (built.Unresolved > 0)
                log($"         {built.Unresolved:N0} skipped - no nav area at the base");
        }

        /// <summary>
        /// The movement passes that have to happen after clipping, because every one of them reads an
        /// area's final shape: where its edges are, which corners are isolated, what the floor under it
        /// does.
        ///
        /// Stair marking is the one that shows it. The test probes the real floor along six lines
        /// across an area, and an area still overhanging geometry it is about to be pulled back from
        /// sends those probes off the end of the flight, where they find no floor and veto it. Run
        /// before the clip it marked 8 areas on gm_construct; after it, 17.
        /// </summary>
        private static void RunPostClipMovement(NavFile nav, BspVisibility vis, BspFile bsp,
            Result result, NavProgress progress, Action<string> log)
        {
            progress.Enter(PhaseStairs);
            var stairs = StairMarker.Mark(nav, vis, progress);
            result.Stairs = stairs;
            log($"Stairs: {stairs.Marked:N0} marked, {stairs.Cleared:N0} cleared");

            var elevators = ElevatorConnector.Build(nav, bsp, vis);
            result.Elevators = elevators;

            if (elevators.Platforms > 0)
            {
                log($"Lifts: {elevators.Platforms:N0} platforms, {elevators.Connections:N0} connections " +
                    $"at {elevators.Stops:N0} stops" +
                    (elevators.Refused > 0
                        ? $" ({elevators.Refused:N0} landings refused as blocked)"
                        : ""));

                foreach (string note in elevators.Notes.Take(6))
                    log($"       {note}");
            }

            // Corner patching before the shortcut fixup, matching Valve's own FixUpGeneratedAreas
            // order: it needs the connection graph to know which corners are isolated, and it adds
            // connections the shortcut pass should then be free to prune if they turn out redundant.
            var patched = CornerPatcher.Patch(nav, vis);
            result.Corners = patched;

            if (patched.PatchesAdded > 0)
                log($"Corners: {patched.PatchesAdded:N0} corner-only touches patched");

            var fixup = AreaConnectionFixer.Fix(nav);
            result.Fixup = fixup;

            if (fixup.ShortcutsRemoved > 0)
                log($"Fixup: {fixup.ShortcutsRemoved:N0} redundant shortcuts removed");
        }

        /// <summary>
        /// Cover positions for bots, and the two gradings that read them.
        ///
        /// Worth running even where it looks optional. The engine computes hiding spots during every
        /// <c>nav_generate</c>, so a mesh without them is missing something the game expects to find -
        /// and it is cheap, tens of milliseconds on a full map. The other two are the reverse of what
        /// people assume: <c>nav_quicksave</c> defaults to 1 and both <c>ComputeSniperSpots</c> and
        /// <c>ComputeSpotEncounters</c> return immediately when it is set, so a stock in-game generate
        /// produces neither and this is strictly ahead rather than catching up.
        /// </summary>
        private static void RunSpots(NavFile nav, BspVisibility vis, Options options, Result result,
            NavProgress progress, Action<string> log)
        {
            progress.Enter(PhaseHiding);
            var spots = HidingSpotFinder.Find(nav, vis, progress);
            result.HidingSpots = spots;

            if (spots.Spots > 0)
            {
                log($"Hiding spots: {spots.Spots:N0} across {spots.AreasWithSpots:N0} areas " +
                    $"({spots.InCover:N0} in cover, {spots.Exposed:N0} exposed)");
            }

            NavConcurrency.ThrowIfCancelled();

            // Grading needs the spots to exist, so it cannot fold into the pass above.
            if (options.SniperSpots)
            {
                progress.Enter(PhaseSnipers);
                var graded = SniperSpotClassifier.Classify(nav, vis, progress);
                result.SniperSpots = graded;
                log($"Sniper spots: {graded.Ideal:N0} ideal, {graded.Good:N0} good " +
                    $"of {graded.Spots:N0} graded");
            }

            NavConcurrency.ThrowIfCancelled();

            // Last of the three: an encounter is a list of the *covered* spots seen along a path, so it
            // needs both the spots and their cover flags to exist already.
            if (options.EncounterSpots)
            {
                progress.Enter(PhaseEncounters);
                var met = EncounterSpotBuilder.Build(nav, vis, progress);
                result.Encounters = met;
                log($"Encounters: {met.Encounters:N0} across {met.AreasWithEncounters:N0} areas " +
                    $"({met.SpotOrders:N0} spot sightings)");
            }
        }

        private static void RunVisibility(NavFile nav, BspVisibility vis, Options options, Result result,
            NavProgress progress, Action<string> log)
        {
            if (!vis.HasVisibilityData)
            {
                string warning = "The BSP has no vis data, so nothing can be culled before tracing. " +
                                 "Did VVIS run?";
                result.Warnings.Add(warning);
                log($"Visibility: {warning}");
            }

            progress.Enter(PhaseVisibility);

            var filter = new VisibilityFilter(nav, vis, options.MaxViewDistance);
            var tracer = new VisibilityTracer(filter, vis, nav.Areas.Count);
            var stats = filter.Run(tracer, progress);

            log($"Visibility: {stats.TotalPairs:N0} pairs -> {stats.AfterDistance:N0} after distance " +
                $"-> {stats.AfterPvs:N0} after PVS");
            log($"            {tracer.VisibleLinks:N0} visible links from {tracer.RaysCast:N0} rays " +
                $"in {stats.ElapsedMilliseconds:N0} ms on {NavConcurrency.MaxThreads} threads");

            var visible = tracer.Symmetrise();

            foreach (var ids in visible)
                Array.Sort(ids);

            if (options.CompressVisibility)
            {
                progress.Enter(PhaseCompress);
                var compression = VisibilityCompressor.Apply(nav, visible, progress);
                log($"            compressed {compression.Compressed:N0} areas, " +
                    $"{compression.EntriesBefore:N0} -> {compression.EntriesAfter:N0} entries");
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
        }
    }
}
