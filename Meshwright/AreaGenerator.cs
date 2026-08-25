using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Meshwright
{
    /// <summary>
    /// Generates nav areas for walkable ground the existing mesh does not cover.
    ///
    /// This is the one gap the other passes cannot close. Ladders, connections and visibility all refine
    /// a mesh that already exists; a rooftop the generator's flood fill never reached has no areas at
    /// all, and nothing can be connected to nothing.
    ///
    /// **Reachability is the whole problem.** An earlier version sampled the world on a grid and kept
    /// everything walkable. On gm_construct that called 3.11 million of 3.16 million sampled points
    /// walkable and produced 87,537 areas, essentially all isolated - because the top of every wall,
    /// the inside of the skybox and every out-of-bounds ledge passes a headroom-and-slope test. Valve's
    /// generator never has this problem because it floods outward from player spawns rather than
    /// sampling everywhere.
    ///
    /// So this floods too, but from seeds chosen to reach what the engine misses: the existing mesh
    /// (so the flood can spread off the edges of what is already known and cross it freely), the top of
    /// every ladder brush, and every resting height of every lift. Those last two are exactly the
    /// routes the engine's own fill cannot take, which is why the surfaces beyond them are missing.
    /// </summary>
    public static class AreaGenerator
    {
        /// <summary>
        /// Grid spacing, matching the engine's own GenerationStepSize. Sampling finer would find detail
        /// the area representation cannot express anyway - areas are axis-aligned quads.
        /// </summary>
        public const float StepSize = 25f;

        /// <summary>Vertical clearance a sample needs to be stood in.</summary>
        private const float RequiredHeadroom = NavConstants.HumanHeight;

        /// <summary>
        /// How much room a sample has above it, and so whether a player can be there at all.
        ///
        /// The middle case is the one that did not exist before. Sampling asked a yes/no question against
        /// the full standing height, so every vent, crawlspace, under-stair gap and low tunnel came back
        /// "no" and was discarded outright - not merely left unmarked, but treated as somewhere a player
        /// cannot go, which also stops the flood passing through and cuts off whatever lies beyond.
        /// Valve's own gm_construct mesh marks 13 areas <c>NAV_MESH_CROUCH</c>; this produced none,
        /// because nothing below 71 units of headroom was ever sampled in the first place.
        /// </summary>
        public enum Clearance
        {
            /// <summary>No room for a player, or ground too broken to stand on.</summary>
            None,

            /// <summary>Room to crouch but not to stand: <c>HumanCrouchHeight</c>, not <c>HumanHeight</c>.</summary>
            Crouch,

            /// <summary>Full standing room.</summary>
            Stand,
        }

        /// <summary>A sample this close in height to an existing area's surface is already covered.</summary>
        private const float CoveredTolerance = 24f;

        /// <summary>Ceiling on the flood, so a leak into the void cannot run away with the machine.</summary>
        private const int MaxVisited = 3_000_000;

        public sealed class Result
        {
            public int Seeds;
            public int Visited;
            public int Uncovered;
            /// <summary>
            /// Areas this run created, and the size of the mesh afterwards.
            ///
            /// Two numbers because one was doing the work of both and getting it wrong. A single field
            /// held <c>nav.Areas.Count</c> - the total - and every caller printed it as "added", so a
            /// run over an existing 2,271-area mesh that found 1,540 new ones reported 3,811 added. The
            /// distinction matters most exactly where the old number was least true: on a seeded run,
            /// which is the normal way to use this.
            /// </summary>
            public int Added;

            /// <inheritdoc cref="Added"/>
            public int Total;

            /// <summary>
            /// The lowest id this run handed out. Everything at or above it was created here;
            /// everything below was in the mesh before the run started.
            ///
            /// Carried on the result because the passes that need it do not all run inside
            /// <see cref="Generate"/> - <see cref="ClipToGeometry"/> is deliberately deferred until
            /// after the connection graph exists - and it is only knowable before the first new area is
            /// made.
            /// </summary>
            public uint FirstGeneratedId;

            public readonly List<string> Notes = [];
        }

        private readonly record struct Cell(int Gx, int Gy, float Z);

        /// <summary>
        /// Pulls area boundaries back out of the geometry they overhang.
        ///
        /// Deliberately not part of <see cref="Generate"/>, and deliberately run after connections have
        /// been built. It is the only pass that moves an edge to somewhere no node sits, and everything
        /// that decides whether two areas are neighbours works off those edges: merging matches them
        /// exactly, and connecting needs them to still abut. Clipping first leaves gaps where areas used
        /// to touch, and the connection pass then has nothing to join. Measured on rp_downtown_meowy,
        /// clipping before connecting cost 144 areas that ended up with no connections at all and about
        /// 1,600 connections overall - a whole sewer room among them, four same-height neighbours sitting
        /// a few units off its edge with nothing linking them.
        /// </summary>
        /// <param name="firstGeneratedId">
        /// <see cref="Result.FirstGeneratedId"/> from the <see cref="Generate"/> call this is finishing.
        /// Required rather than defaulted, because a caller that forgets it does not get a worse mesh -
        /// it gets somebody's hand-edited areas pulled about and their slivers deleted, silently.
        /// </param>
        public static AreaClipper.Result ClipToGeometry(NavFile nav, BspVisibility vis,
            uint firstGeneratedId, NavProgress? progress = null)
        {
            progress?.Enter(NavPipeline.PhaseClipping);
            return AreaClipper.Clip(nav, vis, StepSize, progress, firstGeneratedId);
        }

        public static Result Generate(NavFile nav, BspVisibility vis, BspFile bsp, NavFile? reference = null,
            bool squareUp = true, NavProgress? progress = null)
        {
            var result = new Result();

            // Established before the first early return, not alongside the areas it describes. Every
            // pass downstream reads it to tell what this run made from what it was handed, and a run
            // that bails out here has made nothing - so leaving it at zero would tell the clipper that
            // the entire loaded mesh was fair game, on a map it could not even sample.
            foreach (var existing in nav.Areas)
                result.FirstGeneratedId = Math.Max(result.FirstGeneratedId, existing.Id + 1);

            // Counted here rather than at the end, for the same reason FirstGeneratedId is: by the time
            // the passes below have run there is no way to tell what was already present.
            int existingAreas = nav.Areas.Count;

            if (!TryGetWorldBounds(bsp, out var mins, out var maxs))
            {
                result.Notes.Add("BSP has no world model bounds; cannot sample");
                return result;
            }

            var world = new World(vis, mins, maxs);
            var index = new NavGeometry.Index(nav.Areas);

            // Level-synchronous BFS: expand the whole frontier at once across every core, then swap in
            // what it produced. Ordinary BFS is sequential, and nearly all the cost here is the floor
            // probing each cell triggers - many traces per column - so the frontier is where the time
            // goes and where the parallelism belongs.
            var visited = new System.Collections.Concurrent.ConcurrentDictionary<(int, int, int), byte>();
            var accepted = new System.Collections.Concurrent.ConcurrentBag<Cell>();

            var frontier = new List<Cell>();
            foreach (var seed in Seeds(nav, bsp, world, result))
            {
                if (visited.TryAdd(Key(seed), 0))
                    frontier.Add(seed);
            }

            result.Seeds = frontier.Count;
            int visitedCount = 0;
            int uncovered = 0;

            while (frontier.Count > 0 && visited.Count < MaxVisited)
            {
                var next = new System.Collections.Concurrent.ConcurrentBag<Cell>();

                Parallel.ForEach(frontier, NavConcurrency.Options, cell =>
                {
                    Interlocked.Increment(ref visitedCount);

                    foreach (var neighbour in Neighbours(world, cell))
                    {
                        if (!visited.TryAdd(Key(neighbour), 0))
                            continue;

                        next.Add(neighbour);

                        // Cells already represented still propagate - the flood has to be able to cross
                        // existing mesh to reach the far side of it - but they contribute no new area.
                        float x = mins.X + neighbour.Gx * StepSize;
                        float y = mins.Y + neighbour.Gy * StepSize;

                        if (index.FindAt(x, y, neighbour.Z, CoveredTolerance) < 0)
                        {
                            accepted.Add(neighbour);
                            Interlocked.Increment(ref uncovered);
                        }
                    }
                });

                frontier = [.. next];

                // No denominator exists here - the flood finds out how much ground there is by walking
                // it - so this reports what it has found rather than inventing a percentage.
                progress?.Counted(visitedCount);
            }

            result.Visited = visitedCount;
            result.Uncovered = uncovered;

            if (visited.Count >= MaxVisited)
                result.Notes.Add($"flood hit the {MaxVisited:N0} cell ceiling; output may be incomplete");

            // Sorted so the node grid, and therefore the mesh, is identical run to run: a ConcurrentBag
            // returns its contents in whatever order threads happened to add them.
            var ordered = accepted.ToList();
            ordered.Sort((a, b) =>
            {
                int byX = a.Gx.CompareTo(b.Gx);
                if (byX != 0) return byX;

                int byY = a.Gy.CompareTo(b.Gy);
                return byY != 0 ? byY : a.Z.CompareTo(b.Z);
            });

            // Accepted cells become nodes carrying the ground normal already sampled for them, and
            // areas are built from those - so corner heights come from real sampled points and an area
            // on a slope is actually sloped.
            var grid = new NavNodeGrid();
            foreach (var cell in ordered)
            {
                foreach (var surface in world.SurfacesIn(cell.Gx, cell.Gy))
                {
                    if (MathF.Abs(surface.Position.Z - cell.Z) > NavNodeGrid.HeightGranularity)
                        continue;

                    var node = grid.Add(cell.Gx, cell.Gy, surface.Position, surface.Normal);

                    // Ground too steep to stand on is not thrown away; it becomes a jump area, exactly
                    // as MarkJumpAreas does. Discarding it was why coverage fell when areas started
                    // being built from nodes - the steep ground simply vanished instead of turning into
                    // the connection across it. NoMerge keeps these from being absorbed by the walkable
                    // areas around them, and the differing attributes stop them sharing an area at all.
                    if (!node.IsWalkable)
                    {
                        node.Attributes = NavAttributes.Jump | NavAttributes.NoMerge;
                    }
                    else if (world.ClearanceAt(cell.Gx, cell.Gy, cell.Z) == Clearance.Crouch)
                    {
                        // Nothing further is needed to keep these out of the standing areas around
                        // them: NodeAreaBuilder.Accepts already refuses to grow an area across nodes
                        // whose attributes differ from the seed's, which is Valve's own rule - every node
                        // inside a candidate area has to share the same attributes. That gate was
                        // written and then had nothing to gate on, because no node ever carried an
                        // attribute a walkable neighbour lacked.
                        node.Attributes = NavAttributes.Crouch;
                    }

                    break;
                }
            }

            int crouchNodes = 0;
            foreach (var node in grid.Nodes)
            {
                if ((node.Attributes & NavAttributes.Crouch) != 0)
                    crouchNodes++;
            }

            if (crouchNodes > 0)
                result.Notes.Add($"crouch: {crouchNodes:N0} nodes");

            progress?.Enter(NavPipeline.PhaseLinking);
            var (linksMade, linksRefused) = LinkNodes(grid, vis, progress);
            result.Notes.Add($"links {linksMade:N0} made, {linksRefused:N0} refused as blocked");

            progress?.Enter(NavPipeline.PhaseAreas);
            var built = NodeAreaBuilder.Build(nav, grid, StepSize, progress);
            result.Notes.Add($"nodes {grid.Nodes.Count:N0}, consumed {built.NodesConsumed:N0}, " +
                             $"rejected {built.Rejected:N0}");

            // Merge, then square up - in that order, and as a pair. Growing rectangles out of a grid
            // leaves seams wherever a row happened to stop; merging closes them, and only then is
            // splitting long areas a tidy-up rather than pure fragmentation. Squaring up alone measured
            // worse than doing nothing.
            //
            // Both are handed the id this run started at, and both only touch areas at or above it.
            // Neither is a pass over "the mesh" - they are the second and third steps of turning a node
            // grid into areas, and a mesh that was already there did not come from this node grid.
            progress?.Enter(NavPipeline.PhaseMerging);
            var merged = AreaMerger.Merge(nav, vis, result.FirstGeneratedId);
            if (merged.Merges > 0)
            {
                result.Notes.Add($"merged {merged.Merges:N0} areas in {merged.Passes:N0} passes; " +
                                 $"left unmerged: {merged.NoPartner:N0} no partner presenting the same span, " +
                                 $"{merged.HeightMismatch:N0} step at the seam, " +
                                 $"{merged.NotCoplanar:N0} gradient break at the seam, " +
                                 $"{merged.TooBig:N0} too big");
            }

            if (squareUp)
            {
                var squared = AreaSquarer.SquareUp(nav, result.FirstGeneratedId);
                if (squared.Split > 0)
                    result.Notes.Add($"split {squared.Split:N0} long areas into {squared.Split + squared.Created:N0}");
            }

            result.Total = nav.Areas.Count;
            result.Added = nav.Areas.Count - existingAreas;

            if (reference is not null)
                Classify(reference, nav, world, visited, [.. ordered.Select(Key)], result);

            return result;
        }

        /// <summary>
        /// Explains every reference area the generated mesh fails to cover.
        ///
        /// A coverage percentage says how much is missing but not why, and the causes need opposite
        /// fixes: ground the flood never reached is a movement-limit problem, ground it rejected is a
        /// walkability problem, and ground it accepted but did not emit is a merge problem. Guessing
        /// between them is how the earlier versions of this pass went wrong.
        /// </summary>
        private static void Classify(NavFile reference, NavFile generated, World world,
            System.Collections.Concurrent.ConcurrentDictionary<(int, int, int), byte> visited,
            HashSet<(int, int, int)> accepted, Result result)
        {
            var generatedIndex = new NavGeometry.Index(generated.Areas);
            var reasons = new SortedDictionary<string, int>();
            var samples = new Dictionary<string, List<string>>();
            BspFile.Vector3 at = default;

            void Note(string reason)
            {
                reasons[reason] = reasons.GetValueOrDefault(reason) + 1;

                if (!samples.TryGetValue(reason, out var where))
                    samples[reason] = where = [];

                if (where.Count < 5)
                    where.Add($"({at.X:F0} {at.Y:F0} {at.Z:F0})");
            }

            foreach (var area in reference.Areas)
            {
                var b = NavGeometry.GetBounds(area);
                float cx = (b.MinX + b.MaxX) / 2f;
                float cy = (b.MinY + b.MaxY) / 2f;
                float cz = NavGeometry.SurfaceZ(area, cx, cy);
                at = new BspFile.Vector3(cx, cy, cz);

                if (generatedIndex.FindAt(cx, cy, cz, 48f) >= 0)
                    continue; // covered, nothing to explain

                if (!world.TryCell(cx, cy, cz, out var cell))
                {
                    Note("outside the sampled bounds");
                    continue;
                }

                float? floor = null;
                foreach (float z in world.FloorsIn(cell.Gx, cell.Gy))
                {
                    if (MathF.Abs(z - cz) > 48f) continue;
                    floor = z;
                    break;
                }

                if (floor is null)
                {
                    Note("no floor found in that column");
                    continue;
                }

                var key = Key(new Cell(cell.Gx, cell.Gy, floor.Value));

                if (!world.IsStandable(cell.Gx, cell.Gy, floor.Value))
                    Note("floor found but not standable");
                else if (!visited.ContainsKey(key))
                    Note("standable but the flood never reached it");
                else if (!accepted.Contains(key))
                    Note("reached, but treated as already covered");
                else
                    Note("accepted, then dropped by the merge");
            }

            foreach (var (reason, count) in reasons)
            {
                result.Notes.Add($"miss: {reason} - {count:N0}");

                // A count says how much is missing; a position says where to go and look. Without these
                // the only way to act on the number is to guess at causes, which is how two plausible
                // theories about the largest category turned out to be wrong.
                if (samples.TryGetValue(reason, out var where))
                    result.Notes.Add($"      e.g. {string.Join("  ", where)}");
            }
        }


        /// <summary>
        /// Wires each node to the neighbour it can actually reach in each direction.
        ///
        /// One-way on purpose, matching Valve: a node can link down to a ledge below without the ledge
        /// linking back up, because the drop is traversable and the climb is not. Collapsing that would
        /// lose the difference between a step and a fall.
        ///
        /// Every link is confirmed against the world before it is made. These links are the only thing
        /// areas are allowed to grow along, so a link is a promise that the ground is continuous - and
        /// an unchecked one across a wall is precisely how a floor area came to run on into the wall
        /// behind it, and how a doorway came to be swallowed by the room on either side instead of
        /// narrowing to the opening.
        /// </summary>
        private static (int Made, int Refused) LinkNodes(NavNodeGrid grid, BspVisibility vis,
            NavProgress? progress)
        {
            int made = 0, refused = 0, done = 0;
            double total = Math.Max(1, grid.Nodes.Count);

            ReadOnlySpan<(int Dx, int Dy, int Direction)> steps =
            [
                (0, -1, NavGeometry.North),
                (1, 0, NavGeometry.East),
                (0, 1, NavGeometry.South),
                (-1, 0, NavGeometry.West),
            ];

            // The trace dominates, and every node is independent of every other, so this is the same
            // level-synchronous shape as the flood: read-only shared grid, one writer per node.
            var directions = steps.ToArray();

            Parallel.ForEach(grid.Nodes, NavConcurrency.Options, node =>
            {
                foreach (var (dx, dy, direction) in directions)
                {
                    NavNode? best = null;
                    float bestClimb = float.MaxValue;

                    foreach (var candidate in grid.At(node.Gx + dx, node.Gy + dy))
                    {
                        float climb = candidate.Z - node.Z;

                        if (climb > NavConstants.JumpCrouchHeight || climb < -NavConstants.DeathDrop)
                            continue;

                        float cost = MathF.Abs(climb);
                        if (cost >= bestClimb) continue;

                        if (!Traversability.CanStep(vis, node.Position, candidate.Position))
                        {
                            Interlocked.Increment(ref refused);
                            continue;
                        }

                        bestClimb = cost;
                        best = candidate;
                    }

                    if (best is not null)
                    {
                        node.ConnectTo(direction, best);
                        Interlocked.Increment(ref made);
                    }
                }

                progress?.Report(Interlocked.Increment(ref done) / total);
            });

            return (made, refused);
        }

        /// <summary>Height is quantised so two probes of the same surface hash to the same cell.</summary>
        private static (int, int, int) Key(Cell c) => (c.Gx, c.Gy, (int)MathF.Round(c.Z / 8f));

        /// <summary>
        /// Where the flood starts.
        ///
        /// The existing mesh is included so the fill can spread off its edges and travel across it.
        /// Ladder tops and lift stops are the interesting ones: they are precisely the routes the
        /// engine's own generator will not take, so the ground beyond them is what went missing.
        /// </summary>
        private static IEnumerable<Cell> Seeds(NavFile nav, BspFile bsp, World world, Result result)
        {
            // Player spawns, which is what the engine itself floods from. These are the only seeds that
            // do not presuppose a mesh, so they are what lets this run against an empty one.
            int spawns = 0;
            foreach (var origin in SpawnPoints(bsp))
            {
                if (world.TryCell(origin.X, origin.Y, origin.Z, out var spawn))
                {
                    spawns++;
                    yield return spawn;
                }
            }

            foreach (var area in nav.Areas)
            {
                var b = NavGeometry.GetBounds(area);
                float cx = (b.MinX + b.MaxX) / 2f;
                float cy = (b.MinY + b.MaxY) / 2f;

                if (world.TryCell(cx, cy, NavGeometry.SurfaceZ(area, cx, cy), out var cell))
                    yield return cell;
            }

            int ladders = 0;
            foreach (var brush in LadderFinder.Find(bsp))
            {
                // just off the climbing face at the top, which is where a climber steps off
                foreach (var (dx, dy) in Offsets(StepSize))
                {
                    if (world.TryCell(brush.Top.X + dx, brush.Top.Y + dy, brush.Top.Z, out var cell))
                    {
                        ladders++;
                        yield return cell;
                    }
                }
            }

            int lifts = 0;
            foreach (var stop in ElevatorConnector.PlatformStops(bsp))
            {
                if (world.TryCell(stop.X, stop.Y, stop.Z, out var cell))
                {
                    lifts++;
                    yield return cell;
                }
            }

            result.Notes.Add($"seeded from {spawns} player spawns, {nav.Areas.Count:N0} existing areas, " +
                             $"{ladders} ladder tops, {lifts} lift stops");
        }

        /// <summary>
        /// Where players enter the map. A spawn is guaranteed to be somewhere a person can stand, which
        /// makes it the one seed that needs nothing to already exist.
        /// </summary>
        public static IEnumerable<BspFile.Vector3> SpawnPositions(BspFile bsp) => SpawnPoints(bsp);

        private static IEnumerable<BspFile.Vector3> SpawnPoints(BspFile bsp)
        {
            foreach (System.Text.RegularExpressions.Match block in
                     System.Text.RegularExpressions.Regex.Matches(bsp.EntityLump, @"\{(.*?)\}",
                         System.Text.RegularExpressions.RegexOptions.Singleline))
            {
                string body = block.Groups[1].Value;

                var classname = System.Text.RegularExpressions.Regex.Match(body, "\"classname\"\\s*\"([^\"]*)\"");
                if (!classname.Success || !classname.Groups[1].Value.StartsWith("info_player", StringComparison.OrdinalIgnoreCase))
                    continue;

                var origin = System.Text.RegularExpressions.Regex.Match(body, "\"origin\"\\s*\"([^\"]*)\"");
                if (!origin.Success)
                    continue;

                var parts = origin.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;

                var c = System.Globalization.CultureInfo.InvariantCulture;
                if (float.TryParse(parts[0], c, out float x) &&
                    float.TryParse(parts[1], c, out float y) &&
                    float.TryParse(parts[2], c, out float z))
                {
                    yield return new BspFile.Vector3(x, y, z);
                }
            }
        }

        private static IEnumerable<(float, float)> Offsets(float d)
        {
            yield return (d, 0);
            yield return (-d, 0);
            yield return (0, d);
            yield return (0, -d);
        }

        /// <summary>
        /// Cells a walker could move to from here: the four grid neighbours, at whichever storey is
        /// within reach. Reach is asymmetric on purpose - up is capped by a crouch jump, down by the
        /// drop the engine treats as survivable.
        /// </summary>
        private static IEnumerable<Cell> Neighbours(World world, Cell cell)
        {
            // Four-way only. Diagonal movement was tried and made no difference whatsoever - identical
            // visited count, byte-identical output - because anywhere a diagonal step reaches, the two
            // orthogonal steps already reached. Not worth the extra work per cell.
            foreach (var (dgx, dgy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int gx = cell.Gx + dgx;
                int gy = cell.Gy + dgy;

                foreach (float z in world.FloorsIn(gx, gy))
                {
                    float climb = z - cell.Z;
                    if (climb > NavConstants.JumpCrouchHeight || climb < -NavConstants.DeathDrop)
                        continue;

                    if (!world.IsStandable(gx, gy, z))
                        continue;

                    // And that no other floor in the same column stands between here and there.
                    //
                    // Being within falling distance is not the same as being able to get there. A column
                    // routinely holds several floors, and stepping sideways off one of them does not let
                    // a walker pass through the ones underneath - but the flood was free to pick any of
                    // them, so it fell through solid ground into whatever was below.
                    //
                    // Terrain is where it showed worst. A displacement is a sheet raised off the brush
                    // face it was built from, and the space between the two is air, so under every hill
                    // on the map there is a sealed cavity with a real floor at the bottom. On
                    // gm_construct the grass sits at about z=+129 and that floor at z=-156, comfortably
                    // inside falling range. The flood stepped off the hillside, dropped through the sheet
                    // it had been standing on a moment earlier, and carried on sampling the cavity
                    // underneath. Nothing about the result looked wrong to any check: that floor is real,
                    // it is simply on the other side of the ground.
                    //
                    // Deliberately geometric rather than traced. An earlier attempt fired a ray down the
                    // destination column and missed this entirely, because it started just below the
                    // height being left - which in that column is already underneath the sheet, so the
                    // sheet was never in the way. The floors are already known here; asking whether one
                    // of them sits in between needs no trace at all, and so costs nothing.
                    if (Shadowed(world, gx, gy, z, cell.Z))
                        continue;

                    yield return new Cell(gx, gy, z);
                }
            }
        }

        /// <summary>
        /// Whether another floor in the same column lies between the walker and the one being considered.
        ///
        /// The blocker has to be meaningfully above the candidate - more than a step - or the treads of a
        /// staircase would shadow each other. It also has to be at or below the height being left, plus a
        /// step: a floor above the walker's head is a ceiling, not something they would land on the far
        /// side of.
        /// </summary>
        private static bool Shadowed(World world, int gx, int gy, float candidate, float from)
        {
            float ceiling = from + NavConstants.StepHeight;
            float floor = candidate + NavConstants.StepHeight;

            foreach (float other in world.FloorsIn(gx, gy))
            {
                if (other > floor && other <= ceiling)
                    return true;
            }

            return false;
        }

        private static bool TryGetWorldBounds(BspFile bsp, out BspFile.Vector3 mins, out BspFile.Vector3 maxs)
        {
            mins = default;
            maxs = default;

            if (bsp.BrushModelBounds.Length == 0)
                return false;

            var world = bsp.BrushModelBounds[0];
            mins = world.Mins;
            maxs = world.Maxs;
            return maxs.X > mins.X && maxs.Y > mins.Y && maxs.Z > mins.Z;
        }

        /// <summary>
        /// The sampled world: grid coordinates, floor heights per column, and standability. Results are
        /// cached because the flood revisits the same column from several directions, and a column costs
        /// a full set of traces to resolve.
        /// </summary>
        private sealed class World(BspVisibility vis, BspFile.Vector3 mins, BspFile.Vector3 maxs)
        {
            // Concurrent rather than lock-guarded: the parallel frontier hammers these from every core,
            // and a lock around the whole dictionary would serialise exactly the work being spread out.
            // A duplicate computation on a race is harmless - the answer is the same either way.
            private readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), Surface[]> surfaces = new();
            private readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int, int), Clearance> clearance = new();

            public int Columns { get; } = (int)MathF.Ceiling((maxs.X - mins.X) / StepSize);
            public int Rows { get; } = (int)MathF.Ceiling((maxs.Y - mins.Y) / StepSize);

            public bool TryCell(float x, float y, float z, out Cell cell)
            {
                int gx = (int)MathF.Round((x - mins.X) / StepSize);
                int gy = (int)MathF.Round((y - mins.Y) / StepSize);
                cell = new Cell(gx, gy, z);

                return gx >= 0 && gy >= 0 && gx < Columns && gy < Rows;
            }

            public float[] FloorsIn(int gx, int gy)
            {
                var surfaces = SurfacesIn(gx, gy);
                var heights = new float[surfaces.Length];

                for (int i = 0; i < surfaces.Length; i++)
                    heights[i] = surfaces[i].Position.Z;

                return heights;
            }

            /// <summary>
            /// Every floor in a column with the normal of each, resolved in the same pass that finds it.
            ///
            /// Computing the normal here rather than as a separate query is the difference between it
            /// being free and costing a second full trace per sample - which, measured, was a nine-fold
            /// slowdown for less coverage.
            /// </summary>
            public Surface[] SurfacesIn(int gx, int gy)
            {
                if (gx < 0 || gy < 0 || gx >= Columns || gy >= Rows)
                    return [];

                if (surfaces.TryGetValue((gx, gy), out var cached))
                    return cached;

                float x = mins.X + gx * StepSize;
                float y = mins.Y + gy * StepSize;

                return surfaces.GetOrAdd((gx, gy), FindSurfaces(x, y).ToArray());
            }

            /// <summary>A sampled floor: where it is and which way it faces.</summary>
            public readonly record struct Surface(BspFile.Vector3 Position, BspFile.Vector3 Normal);

            /// <summary>
            /// Every floor surface in a column, top down - a road grate over a sewer being the case
            /// that motivated getting this right. The walk itself lives in
            /// <see cref="BspVisibility.EnumerateFloors"/>, which documents why it is not built out of
            /// Valve's <c>GetGroundHeight</c> despite that being the obvious-looking reuse.
            /// </summary>
            private List<Surface> FindSurfaces(float x, float y)
            {
                const int MaxFloors = 16;

                Span<BspVisibility.FloorSample> samples = stackalloc BspVisibility.FloorSample[MaxFloors];
                int count = vis.EnumerateFloors(x, y, maxs.Z, mins.Z, samples);

                var found = new List<Surface>(count);
                for (int i = 0; i < count; i++)
                {
                    found.Add(new Surface(
                        new BspFile.Vector3(x, y, samples[i].Z), samples[i].Normal));
                }

                return found;
            }

            /// <summary>
            /// Whether a person fits standing here, on ground level enough to stand on.
            ///
            /// Both halves matter. Headroom alone lets the flood climb the face of a wall, because a
            /// vertical surface has all the clearance in the world above it; the local slope test is
            /// what keeps it on ground rather than on geometry that merely has sky above it.
            /// </summary>
            /// <summary>Whether a player can occupy this sample at all, standing or crouched.</summary>
            public bool IsStandable(int gx, int gy, float z) => ClearanceAt(gx, gy, z) != Clearance.None;

            /// <inheritdoc cref="Clearance"/>
            public Clearance ClearanceAt(int gx, int gy, float z)
            {
                var key = (gx, gy, (int)MathF.Round(z / 8f));

                if (clearance.TryGetValue(key, out var cached))
                    return cached;

                float x = mins.X + gx * StepSize;
                float y = mins.Y + gy * StepSize;

                // GenerationMask, not the sight-only default: headroom is asking whether a player's
                // body fits here, and a grate blocks a body while correctly not blocking a bot's sight.
                // Kept as a line rather than the generation hull, against expectation. Swapping this
                // for a zero-length sweep of NavTraceMins/Maxs - which is what Valve's
                // FindGroundForNode checks - measured clearly worse: gm_construct's isolated areas went
                // 46 -> 321, rp_downtown_meowy's 2,033 -> 3,390, coverage 98.1% -> 97.7% and stairs 4
                // -> 0. It became *more* permissive, not less, and fragmented the mesh.
                //
                // That was originally blamed on the hull sweep not consulting displacements. It was not
                // the reason, or not the whole one: displacement sweeping now exists
                // (BspDisplacements.TryTraceHull, validated to agree with a line trace on 99.99% of
                // 20,000 random segments), the experiment was repeated, and it still fails - neutral on
                // gm_construct but taking rp_downtown_meowy from 20,921 areas to 28,195 and its isolated
                // areas from 967 to 3,746.
                //
                // The actual reason is that this is a *point* question and a sweep is a segment
                // algorithm. Asking for clearance at one spot means a zero-length sweep, and a segment
                // of no length is degenerate for the brush clipper the same way a zero-extent box is
                // degenerate against a triangle: entry and exit coincide and the overlap frequently
                // registers as nothing at all. It fails open, which is why the mesh grew rather than
                // shrank. A hull is the right tool for "can a body get from here to there", which is
                // what Traversability.CanStep asks and now gets a correct answer to; it is the wrong
                // tool for "is there room right here" until the degenerate case is handled properly.
                // Headroom first, then the ground test, because the order is what it costs. The headroom
                // line is one trace; IsLevelEnough is four floor probes, each of which is a descending
                // sweep. A sample with no room above it at all is the common rejection, so paying one
                // trace to rule it out before paying four is worth the slightly awkward shape.
                bool stand = vis.IsLineClear(
                    new BspFile.Vector3(x, y, z + 2f),
                    new BspFile.Vector3(x, y, z + RequiredHeadroom),
                    BspVisibility.GenerationMask);

                bool crouch = stand || vis.IsLineClear(
                    new BspFile.Vector3(x, y, z + 2f),
                    new BspFile.Vector3(x, y, z + NavConstants.HumanCrouchHeight),
                    BspVisibility.GenerationMask);

                // Two gates, and which one applies depends on how much room there is.
                //
                // Standing ground is judged by the surface normal alone - `nav_slope_limit`, Valve's own
                // rule, read off the normal that finding the floor already produced. That is both more
                // correct than inferring the slope from four neighbouring heights and very much cheaper:
                // the neighbour probe costs four descending sweeps per sample, and dropping it for the
                // common case took generation on gm_construct from 4.7s to 3.3s while *improving* how
                // closely areas sit on the ground (median error 0.7 -> 0.3).
                //
                // Crouch-height ground keeps the old neighbour probe as well. The normal test on its own
                // is too permissive here, because a low ceiling is exactly what you find over the ragged
                // little nooks the neighbour probe exists to reject - under a prop, behind a pipe, in the
                // gap between two crates. With the normal test alone gm_construct produced 431 crouch
                // areas against the 13 in the engine's own mesh. It is only paid on samples that already
                // failed the standing test, which is a small minority, so the speed-up survives.
                bool walkable = IsWalkableGround(gx, gy, z)
                                && (stand || IsLevelEnough(x, y, z));

                var result = !crouch || !walkable
                    ? Clearance.None
                    : stand ? Clearance.Stand : Clearance.Crouch;

                return clearance.GetOrAdd(key, result);
            }

            /// <summary>
            /// Whether the ground here is shallow enough to walk on, read straight off the surface
            /// normal the way `nav_slope_limit` does.
            ///
            /// This replaces probing four neighbouring columns and comparing heights, which was an
            /// inference about the surface rather than a measurement of it - wrong at every edge,
            /// several traces more expensive, and with a threshold that had to be tuned by hand.
            /// Checked against Valve's own gm_construct mesh, 97.4% of its areas sit on ground passing
            /// this test, which is what an engine-made mesh should look like.
            /// </summary>
            private bool IsWalkableGround(int gx, int gy, float z)
            {
                foreach (var surface in SurfacesIn(gx, gy))
                {
                    if (MathF.Abs(surface.Position.Z - z) > NavNodeGrid.HeightGranularity)
                        continue;

                    return surface.Normal.Z >= NavConstants.SlopeLimit;
                }

                return false;
            }

            /// <summary>
            /// Whether the surface immediately around a point is close to level. Probes a short way out
            /// on each axis - far enough to catch a slope, near enough to stay on the same surface.
            /// </summary>
            private bool IsLevelEnough(float x, float y, float z)
            {
                // Shrunk from 10 after measuring on a real map with narrow interior stairs
                // (rp_downtown_meowy): a probe reaching 10 units out from a sample on one tread lands
                // past its edge and into the riser of the next step often enough to cost real coverage -
                // 6 recovered 7.6% more areas there with no cost to shape or solid overlap (aspect p90
                // unchanged, solid-touching areas 4.1% -> 3.9%). Neutral on gm_construct's displacement
                // terrain, the map the original 10 was tuned against - 2,468 areas against 2,475, the
                // same handful of standability misses either way. This does not fully explain the
                // fragmented stair coverage reported in game; it is a measured improvement, not a fix
                // for that specific complaint.
                const float Reach = 6f;
                const float Tolerance = NavConstants.StepHeight;

                // An edge is normal ground, not a disqualification. Requiring floor on all four sides
                // rejects every ledge, walkway and stair lip - and because a rejected cell also stops
                // the flood passing through it, that walls off whatever lies beyond. Measured on
                // gm_construct, 214 of 258 missed areas were standable ground the flood simply could
                // not reach. Only a cell with nothing around it at all is a pillar rather than an edge.
                // A single outlier neighbour used to veto the whole point outright, which was
                // inconsistent with the tolerance missing floor already gets above - and it landed
                // hardest exactly where a staircase most needs to connect: the top and bottom, where a
                // flight meets a landing. Traced one such case to ground on rp_downtown_meowy: the tread
                // surfaces themselves were already accepted correctly (flat, all four neighbours within
                // a couple of units), but the landing at the top was rejected because one of its four
                // probes - six units off, at a decorative overlap sitting on the same footprint - landed
                // 72 units higher, and Source's own stairs routinely put an unrelated feature that close
                // without it meaning anything about the tread underneath.
                //
                // Tolerating up to two mismatched neighbours (not just missing ones) recovered most of
                // what tolerating one did on the same map - stairs correctly marked 25 to 67, 19.6% more
                // area coverage overall - for a solid-overlap cost on gm_construct of 0.07% to 0.25% of
                // sampled footprint. That is a real cost, not a free change, but it is still under a
                // third of what solid overlap measured before this session's clipping work (0.71%), and
                // the map that motivated it made the coverage loss the more visible problem in practice.
                const int MaxMismatch = 2;

                int missing = 0;
                int mismatched = 0;

                foreach (var (dx, dy) in Offsets(Reach))
                {
                    if (!StairMarker.TryFindFloor(vis, x + dx, y + dy, z + RequiredHeadroom,
                            RequiredHeadroom + Tolerance * 2f, out float nz))
                    {
                        missing++;
                        continue;
                    }

                    if (MathF.Abs(nz - z) > Tolerance)
                        mismatched++;
                }

                return missing < 4 && mismatched <= MaxMismatch;
            }
        }

        public sealed class ReachReport
        {
            public bool StartHasFloor;
            public bool StartStandable;
            public int LocalComponentSize;
            public float MinZ = float.MaxValue, MaxZ = float.MinValue;
            public readonly List<string> DeadEnds = [];
            public readonly List<string> Notes = [];

            /// <summary>
            /// The single biggest one-way step found on the shortest path back to the lowest point
            /// reached - a climb this search could descend through (up to 200 units at once) but which
            /// an upward flood starting from ground could not, since it may only climb 58 at a time.
            /// A value over that cap is the concrete answer to "why didn't the flood reach this": not a
            /// bug, a real cliff, and whatever is on the other side needs a ladder or a lift, not a
            /// bigger climb allowance.
            /// </summary>
            public float LargestOneWayDrop;
            public string? LargestOneWayDropAt;
        }

        /// <summary>
        /// Floods locally from one point, using the exact same rules the real generator does, and reports
        /// the shape of what it finds - answering "is this point cut off, and if so where" for a miss
        /// the coverage numbers alone cannot explain.
        ///
        /// Bounded to a box around the start rather than the whole map: this is meant to be read, not to
        /// re-run the entire flood, and a miss that is reachable at all is reachable within a few hundred
        /// units of itself or it would not read as one connected structure in game either.
        /// </summary>
        public static ReachReport DiagnoseReach(BspVisibility vis, BspFile bsp, BspFile.Vector3 start,
            float radius = 800f)
        {
            var report = new ReachReport();

            if (!TryGetWorldBounds(bsp, out var mins, out var maxs))
            {
                report.Notes.Add("BSP has no world model bounds");
                return report;
            }

            var world = new World(vis, mins, maxs);

            if (!world.TryCell(start.X, start.Y, start.Z, out var startCell))
            {
                report.Notes.Add("start point is outside the sampled bounds");
                return report;
            }

            bool foundFloor = false;
            float floorZ = start.Z;
            foreach (float z in world.FloorsIn(startCell.Gx, startCell.Gy))
            {
                if (MathF.Abs(z - start.Z) > 48f) continue;
                foundFloor = true;
                floorZ = z;
                break;
            }

            report.StartHasFloor = foundFloor;
            if (!foundFloor)
            {
                report.Notes.Add("no floor sampled within 48 units of the given Z at this column");
                return report;
            }

            startCell = new Cell(startCell.Gx, startCell.Gy, floorZ);
            report.StartStandable = world.IsStandable(startCell.Gx, startCell.Gy, floorZ);

            int radiusCells = (int)(radius / StepSize);
            var visited = new HashSet<(int, int, int)>();
            var parent = new Dictionary<(int, int, int), Cell>();
            var frontier = new Queue<Cell>();

            visited.Add(Key(startCell));
            frontier.Enqueue(startCell);

            int deadEndsLogged = 0;
            Cell lowest = startCell;

            while (frontier.Count > 0)
            {
                var cell = frontier.Dequeue();

                report.MinZ = MathF.Min(report.MinZ, cell.Z);
                report.MaxZ = MathF.Max(report.MaxZ, cell.Z);
                if (cell.Z < lowest.Z) lowest = cell;

                bool anyNeighbour = false;

                foreach (var neighbour in Neighbours(world, cell))
                {
                    if (Math.Abs(neighbour.Gx - startCell.Gx) > radiusCells ||
                        Math.Abs(neighbour.Gy - startCell.Gy) > radiusCells)
                        continue;

                    anyNeighbour = true;

                    if (visited.Add(Key(neighbour)))
                    {
                        parent[Key(neighbour)] = cell;
                        frontier.Enqueue(neighbour);
                    }
                }

                if (!anyNeighbour && deadEndsLogged < 10)
                {
                    deadEndsLogged++;
                    report.DeadEnds.Add(
                        $"({mins.X + cell.Gx * StepSize:F0} {mins.Y + cell.Gy * StepSize:F0} {cell.Z:F0}) " +
                        "- no standable, in-reach neighbour in any direction");
                }
            }

            report.LocalComponentSize = visited.Count;

            // Walk back from the lowest point reached to the start, over the same edges the search
            // actually used, and report the sharpest single step - the one an upward flood would have
            // had to climb in one 25-unit stride to get this far by the same route.
            var at = lowest;
            while (parent.TryGetValue(Key(at), out var back))
            {
                float drop = at.Z - back.Z;
                if (MathF.Abs(drop) > report.LargestOneWayDrop)
                {
                    report.LargestOneWayDrop = MathF.Abs(drop);
                    report.LargestOneWayDropAt =
                        $"({mins.X + back.Gx * StepSize:F0} {mins.Y + back.Gy * StepSize:F0} {back.Z:F0}) " +
                        $"-> ({mins.X + at.Gx * StepSize:F0} {mins.Y + at.Gy * StepSize:F0} {at.Z:F0})  " +
                        $"({drop:F0} units over one {StepSize:F0} unit step)";
                }

                at = back;
            }

            return report;
        }
    }
}
