using System;
using System.Collections.Generic;

namespace Meshwright
{
    /// <summary>
    /// Builds nav areas out of a sampled node grid, following Valve's <c>CreateNavAreasFromNodes</c>.
    ///
    /// The rule that matters has two halves: every node inside the candidate rectangle has to carry the
    /// same attributes as the others, and every one of them has to lie approximately in the plane of the
    /// north-west node's surface normal.
    ///
    /// Both halves earn their place. Same attributes keeps a crouch region from being swallowed by the
    /// standing floor around it; co-planarity is what stops one area spanning a staircase or a hillside,
    /// and it is only checkable because nodes carry normals.
    ///
    /// The payoff is the corner heights. An area's four corners are the four **corner nodes** of the
    /// rectangle, so an area sitting on a slope is genuinely sloped.
    ///
    /// **Where this deliberately parts company with Valve.** Their <c>CreateNavAreasFromNodes</c> sweeps
    /// every node looking for somewhere a 50x50 rectangle fits, then shrinks by one and sweeps again,
    /// down to a single cell. That was tried here and measured worse on every count that matters:
    /// gm_construct went from 2,285 areas to 4,433 against Valve's own 2,271, isolated areas from 49 to
    /// 276, ground coverage from 88.0% to 85.4%, and the pass from 2.7 to 22 seconds.
    ///
    /// The reason is the grid underneath, not the algorithm on top. Valve's nodes come from a walk that
    /// links each node to the one it stepped from, so a node almost always sits in a closed cell with
    /// its neighbours; the descending sweep can rely on that. These nodes come from a parallel flood
    /// that samples columns independently and links them afterwards, and 3,566 of gm_construct's 191,577
    /// end up in no closed cell at all. Placing fixed rectangles across a grid that ragged leaves
    /// slivers everywhere, where growing each seed as far as it will go absorbs them. Porting the
    /// consumer faithfully needs the producer ported first - the hull-trace sampler that guarantees the
    /// linkage - and until then this grows greedily and shapes the result afterwards.
    /// </summary>
    public static class NodeAreaBuilder
    {
        /// <summary>
        /// How far a node may sit off the starting corner's plane before the area stops growing.
        /// Valve's `offPlaneTolerance`.
        /// </summary>
        private const float OffPlaneTolerance = 5f;

        /// <summary>
        /// Longest an area may grow along one axis, in sampling steps: Valve's <c>nav_area_max_size</c>,
        /// which is 50 and is the number their own generator starts its descending sweep from.
        ///
        /// This was 52, inferred from the longest area in Valve's finished gm_construct mesh (1,275
        /// units) rather than read off the convar. Two steps does not sound like much, but it is the
        /// difference between a 1,250-unit cap and a 1,300-unit one, and a run in game found six
        /// 1,300x1,300 areas - single quads covering a quarter of a city block.
        /// </summary>
        private const int MaxSteps = 50;

        /// <summary>
        /// How far from square a rectangle may grow.
        ///
        /// Growth alternates between widening and deepening, so on open ground this never binds - the
        /// rectangle stays square by construction. It binds on the slivers, which is the point. A greedy
        /// grower working through nodes in a fixed order carves the big open shapes out first and leaves
        /// one-node-wide channels between them; unchecked, those channels run the length of whatever
        /// they are wedged between, and gm_construct produced one 4,625 units long. Those are the "long
        /// line" areas, and they are why a ladder or a doorway ends up attached to a strip spanning half
        /// the map.
        /// </summary>
        private const float MaxAspect = 4f;

        /// <summary>
        /// Thickness the first tiling pass insists on, in nodes.
        ///
        /// Four, measured. Raising it to five changed almost nothing (2,475 areas against 2,461) and
        /// lowering it cost shape steadily - at one, which is a single greedy pass, the median aspect is
        /// 4.0 and there are 4,015 areas. Four is also where the median longest side lands on Valve's
        /// own 125 units exactly.
        /// </summary>
        private const int LargestSquare = 4;

        public sealed class Result
        {
            public int AreasCreated;
            public int NodesConsumed;
            public int Rejected;
        }

        /// <summary>
        /// Consumes the grid into areas appended to <paramref name="nav"/>.
        ///
        /// Nodes are taken in a fixed order so the same grid always yields the same mesh - a compile
        /// step that produced a different result each run would be worse than useless.
        /// </summary>
        public static Result Build(NavFile nav, NavNodeGrid grid, float stepSize,
            NavProgress? progress = null)
        {
            var result = new Result();

            var ordered = new List<NavNode>(grid.Nodes);
            ordered.Sort((a, b) =>
            {
                int byX = a.Gx.CompareTo(b.Gx);
                if (byX != 0) return byX;

                int byY = a.Gy.CompareTo(b.Gy);
                return byY != 0 ? byY : a.Z.CompareTo(b.Z);
            });

            uint nextId = 1;
            foreach (var existing in nav.Areas)
                nextId = Math.Max(nextId, existing.Id + 1);

            // Chunkiest first. A single greedy pass in scan order carves the biggest rectangle it can
            // out of wherever it starts, and everything after it has to fit the L-shaped remainder -
            // which is why the areas came out elongated as a rule rather than as an exception, at a
            // median of 3.5:1 against Valve's 2.0:1. Refusing to emit anything thinner than the current
            // threshold, and lowering the threshold on each pass, lets the square shapes claim their
            // ground first and leaves only genuinely awkward corners to the thin ones.
            for (int minimum = LargestSquare; minimum >= 1; minimum--)
            {
                // Reported across all the passes together, so the bar runs once from end to end rather
                // than resetting four times.
                double passBase = (LargestSquare - minimum) / (double)LargestSquare;
                int seen = 0;

                foreach (var seed in ordered)
                {
                    progress?.Report(passBase + seen++ / (double)(ordered.Count * LargestSquare));

                    if (seed.IsCovered)
                        continue;

                    var lattice = new Lattice(seed);
                    var (width, depth) = Grow(lattice);

                    if (Math.Min(width, depth) < minimum)
                        continue;

                    var area = MakeArea(lattice, width, depth, stepSize, nextId++);

                    if (area is null)
                    {
                        // Only a failure on the last pass is a failure at all: until then the nodes are
                        // simply being left for a threshold that suits them.
                        if (minimum == 1)
                            result.Rejected++;

                        continue;
                    }

                    nav.Areas.Add(area);
                    result.AreasCreated++;

                    // claim every node the rectangle covers
                    for (int dx = 0; dx < width; dx++)
                    {
                        for (int dy = 0; dy < depth; dy++)
                        {
                            var node = lattice.At(dx, dy);
                            if (node is null) continue;

                            node.AreaIndex = nav.Areas.Count - 1;
                            result.NodesConsumed++;
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// The grid of nodes reachable from one seed, addressed by offset.
        ///
        /// Reached by walking the node links rather than by looking up grid coordinates. Looking up says
        /// "is there a sample there", which is true on the far side of a wall as readily as across an
        /// open floor; walking answers "can you get there", which is the question an area is supposed to
        /// be a claim about. Valve's <c>CreateNavAreasFromNodes</c> walks for the same reason.
        ///
        /// Results are memoised because growing tests the same offsets repeatedly, and a walk costs a
        /// step per unit of offset.
        /// </summary>
        private sealed class Lattice(NavNode seed)
        {
            private readonly Dictionary<(int, int), NavNode?> resolved = new() { [(0, 0)] = seed };
            private readonly Dictionary<(int, int), NavNode?> unfiltered = new() { [(0, 0)] = seed };

            public NavNode Seed { get; } = seed;

            private float gradX, gradY;
            private bool gradientReady;

            /// <summary>
            /// Height change per grid step in each axis, taken from the seed's own two neighbours.
            ///
            /// This is what lets an area follow a staircase. The test it feeds replaced a coplanarity
            /// check against the seed's *surface normal*, which is exactly right for a ramp - a ramp's
            /// normal tilts with it, so the next sample along lands on the plane and the area grows -
            /// and exactly wrong for stairs, where every tread is flat, every normal points straight up,
            /// and the plane through the seed is therefore horizontal. Each riser then reads as 8 to 16
            /// units off-plane against a 5 unit tolerance, so an area could span the width of a flight
            /// but never more than a step or two of its climb.
            ///
            /// The visible consequence was not fragmentation, which would have been noticed. It was that
            /// no area ever climbed far enough for the stair test's own opening gate to engage: with a
            /// total rise of one riser, all six of its probe lines report "flat, rise below step height"
            /// and decline to classify. gm_construct generated 2 stair areas where the engine's mesh has
            /// 19, and every one of the misses was a correct verdict on a wrongly shaped area.
            ///
            /// A gradient is only adopted when a second step corroborates the first, and that
            /// qualification is not a refinement - it is the difference between this working and being
            /// actively destructive. Fitted off one neighbour alone, any single threshold beside the
            /// seed - a kerb, a doorsill, the lip of a gutter - becomes the climb the whole area then
            /// expects, so the flat ground beyond it stops matching immediately and the area gives up at
            /// one node. Measured on rp_downtown_meowy, an uncorroborated fit took the mesh from 21,882
            /// areas to 26,123 against the engine's 22,058, and isolated areas from 1,472 to 2,508. A
            /// detailed map is mostly thresholds.
            /// </summary>
            private void EnsureGradient()
            {
                if (gradientReady)
                    return;

                gradientReady = true;
                gradX = FitAxis(NavGeometry.East);
                gradY = FitAxis(NavGeometry.South);
            }

            /// <summary>
            /// The per-step climb along one axis, or zero if the ground there is not consistently
            /// stepping. Zero is the answer that hands the decision back to the seed's own surface plane,
            /// which is the better model wherever it applies.
            /// </summary>
            private float FitAxis(int direction)
            {
                // Only stepped ground gets a fitted gradient, and the discriminator is Valve's own:
                // a stair tread is flat, so its normal points very nearly straight up, where a ramp's
                // normal tilts with the slope. On anything tilted the seed's plane is already the exact
                // answer and a straight-line fit is strictly worse than it, because the plane follows
                // curvature for free and the fit cannot. Measured on gm_construct's displacement
                // terrain, fitting gradients regardless of normal took the mesh from 2,371 areas to
                // 2,795 against the engine's 2,271.
                if (Seed.Normal.Z < NavConstants.StairNormal)
                    return 0f;

                var first = Seed.To[direction];
                if (first is null)
                    return 0f;

                float step = first.Z - Seed.Z;

                // Flat enough that the seed's plane already accepts it, or too tall to be a stair tread
                // at all. Either way there is no stepped gradient to fit.
                if (MathF.Abs(step) <= OffPlaneTolerance || MathF.Abs(step) > NavConstants.StepHeight)
                    return 0f;

                var second = first.To[direction];
                if (second is null)
                    return 0f;

                float next = second.Z - first.Z;

                // Corroboration means "still climbing the same way", not "climbing by exactly as much".
                // Requiring the second step to match the first within the plane tolerance assumed the
                // treads line up with the sampling grid, and real ones do not: a tread is around twelve
                // units deep against a twenty-five unit sample spacing, so consecutive samples straddle
                // two treads and pick up alternating rises - eight, sixteen, eight - purely from where
                // the grid happens to fall. Insisting they match rejected exactly the ordinary staircase
                // this is meant to follow, and left its areas one step deep.
                //
                // Both steps having the same sign and both being a plausible riser is the real signal.
                // The gradient is then their average, which is the honest description of a run the grid
                // is sampling out of phase.
                bool sameWay = (step > 0) == (next > 0);
                bool plausibleRiser = MathF.Abs(next) > OffPlaneTolerance &&
                                      MathF.Abs(next) <= NavConstants.StepHeight;

                return sameWay && plausibleRiser ? (step + next) / 2f : 0f;
            }

            /// <summary>
            /// Whether a node reached from the seed belongs in the same area as it: still free, carrying
            /// the same attributes, and on the surface the area is fitting.
            ///
            /// Which surface that is depends on what the ground is doing. On flat ground and on a smooth
            /// ramp it is the plane through the seed's own surface normal, which is exact - a ramp's
            /// normal tilts with it, so the next sample along lands on the plane for free. Only where a
            /// corroborated stepped climb has been measured does the fitted gradient take over, because
            /// that is the one case the normal cannot describe: on a staircase every tread is flat and
            /// every normal points straight up, so the plane through the seed is horizontal and each
            /// riser reads as off-plane.
            /// </summary>
            /// <summary>
            /// The height of the surface this area is fitting, at a given offset from the seed in grid
            /// steps - including offsets past the last node, which is where the far corners sit.
            ///
            /// This is what a corner height should be. The alternative, and what was there before, is to
            /// read it off whatever node happens to sit at that offset, accepting it whenever it is
            /// within a step of the last node inside the area. That node was never checked against the
            /// area's own surface, and a step is loose enough that it routinely belongs to something
            /// else: the top of a railing beside a flight of stairs, the flat ground at the foot of a
            /// slope, a kerb. The quad then tilts to meet it. A staircase comes out visibly sloping
            /// sideways across its width although every tread is level; an area on a slope sits above or
            /// below the ground it covers. Neither shows up in an area count or a coverage figure.
            ///
            /// Evaluating the fitted surface instead is self-consistent by construction: the plane the
            /// area was grown along is the plane its corners describe.
            /// </summary>
            public float HeightAt(int dx, int dy, float stepSize)
            {
                EnsureGradient();

                if (gradX != 0f || gradY != 0f)
                    return Seed.Z + gradX * dx + gradY * dy;

                // No stepped gradient, so the surface is the seed's own plane. Walking along it from the
                // seed drops by the in-plane slope; a normal lying almost flat would divide by nearly
                // nothing, so that degenerates back to level.
                var n = Seed.Normal;
                if (MathF.Abs(n.Z) < 1e-3f)
                    return Seed.Z;

                return Seed.Z - (n.X * dx * stepSize + n.Y * dy * stepSize) / n.Z;
            }

            public bool Accepts(NavNode node, int dx, int dy)
            {
                if (node.IsCovered || node.Attributes != Seed.Attributes)
                    return false;

                EnsureGradient();

                if (gradX == 0f && gradY == 0f)
                    return Seed.DistanceOffPlane(node.Position) <= OffPlaneTolerance;

                float expected = Seed.Z + gradX * dx + gradY * dy;
                return MathF.Abs(node.Z - expected) <= OffPlaneTolerance;
            }

            /// <summary>
            /// The node at an offset whether or not it belongs in this area. Used only for corner
            /// heights: an area of W by D nodes physically covers the ground out to the node one step
            /// further on, and that node is where its far edge actually sits - Valve's own areas run
            /// from node[0] to node[width] inclusive, which is why <c>TestArea</c> goes out of its way
            /// to check "the final (x=width) node" after its loop.
            ///
            /// It has to bypass <see cref="Accepts"/> to be any use. The node one step past is
            /// routinely off the seed's plane - on a staircase it is the next tread, a whole step down -
            /// and that is exactly the case whose height must be read rather than assumed.
            /// </summary>
            public NavNode? RawAt(int dx, int dy)
            {
                if (unfiltered.TryGetValue((dx, dy), out var cached))
                    return cached;

                var previous = dx == 0 ? RawAt(0, dy - 1)?.To[NavGeometry.South]
                                       : RawAt(dx - 1, dy)?.To[NavGeometry.East];

                unfiltered[(dx, dy)] = previous;
                return previous;
            }

            public NavNode? At(int dx, int dy)
            {
                if (resolved.TryGetValue((dx, dy), out var cached))
                    return cached;

                // Rows first, then along them: the seed's own column is reached by walking south, and
                // every other node by walking east from the node at the same row.
                var previous = dx == 0 ? At(0, dy - 1)?.To[NavGeometry.South]
                                       : At(dx - 1, dy)?.To[NavGeometry.East];

                var node = previous is not null && Accepts(previous, dx, dy) ? previous : null;

                resolved[(dx, dy)] = node;
                return node;
            }
        }

        /// <summary>
        /// Grows the largest rectangle of compatible nodes anchored at the seed.
        ///
        /// Extends in whichever direction still admits a full row or column, preferring the one that
        /// keeps the area squarer - long thin slivers path badly and Valve spends a whole later pass
        /// (`SquareUpAreas`) undoing them.
        /// </summary>
        private static (int Width, int Depth) Grow(Lattice lattice)
        {
            int width = 1;
            int depth = 1;

            while (true)
            {
                bool canWiden = Allowed(width + 1, depth) && ColumnFits(lattice, width, depth);
                bool canDeepen = Allowed(width, depth + 1) && RowFits(lattice, width, depth);

                if (!canWiden && !canDeepen)
                    break;

                if (canWiden && (!canDeepen || width <= depth))
                    width++;
                else
                    depth++;
            }

            return (width, depth);
        }

        /// <summary>Whether a rectangle of this shape is one the pass is willing to emit at all.</summary>
        private static bool Allowed(int width, int depth)
            => width <= MaxSteps && depth <= MaxSteps
               && Math.Max(width, depth) <= Math.Min(width, depth) * MaxAspect;

        private static bool ColumnFits(Lattice lattice, int width, int depth)
        {
            for (int dy = 0; dy < depth; dy++)
            {
                if (lattice.At(width, dy) is null)
                    return false;
            }

            return true;
        }

        private static bool RowFits(Lattice lattice, int width, int depth)
        {
            for (int dx = 0; dx < width; dx++)
            {
                if (lattice.At(dx, depth) is null)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// The height for a far corner, given the node it lands on and the last node inside the area.
        ///
        /// Takes the far node's own height only when the two are within a step of each other, so the
        /// area follows a staircase but does not tilt out over a cliff. Nodes stay linked across drops
        /// of up to <c>DeathDrop</c> - that is what makes a drop connection possible - so without this
        /// an area beside a ledge would take its far corner from the ground far below and slope down
        /// into open air. That cost 3.3 points of ground coverage when the far height was read
        /// unconditionally, against the 1.6 it gained on shape.
        /// </summary>
        private static float FarHeight(Lattice lattice, int dx, int dy, NavNode near, float stepSize)
        {
            float fitted = lattice.HeightAt(dx, dy, stepSize);

            // The fitted surface is the answer, but only where it is still describing this ground. Past
            // the last node there may be nothing at all - the area's far edge can hang over a drop - and
            // a fitted plane extrapolates happily into open air. Bounding the extrapolation to a step
            // from the last node inside the area keeps the old guard against an area tipping out over a
            // cliff, which is what made reading the far node's height unconditionally cost 3.3 points of
            // coverage when that was tried.
            float clamped = Math.Clamp(fitted, near.Z - NavConstants.StepHeight,
                near.Z + NavConstants.StepHeight);

            return clamped;
        }

        /// <summary>
        /// Turns a rectangle of nodes into an area, taking each corner height from the corresponding
        /// corner node. This is the whole point of building from nodes rather than cells.
        /// </summary>
        private static NavArea? MakeArea(Lattice lattice, int width, int depth, float stepSize, uint id)
        {
            var nw = lattice.At(0, 0);
            var ne = lattice.At(width - 1, 0);
            var sw = lattice.At(0, depth - 1);
            var se = lattice.At(width - 1, depth - 1);

            if (nw is null || ne is null || sw is null || se is null)
                return null;

            var area = new NavArea { Id = id };

            // The rectangle spans from the NW node to one step past the SE node, so a single node still
            // produces an area a step across rather than a degenerate zero-width one.
            area.NwCorner[0] = nw.Position.X;
            area.NwCorner[1] = nw.Position.Y;
            area.NwCorner[2] = nw.Z;

            area.SeCorner[0] = se.Position.X + stepSize;
            area.SeCorner[1] = se.Position.Y + stepSize;

            // Heights for the three far corners come from the nodes those corners actually land on -
            // one step past the last node the rectangle grew through - not from the last node itself.
            // Taking them from the last node is what made every area a dead flat plate: with all four
            // corners equal there is no slope to express, so a run of stairs came out as one 25-unit
            // plate per tread. Flat plates cannot merge across a step either, since the seam between two
            // of them is a 16-unit cliff rather than a shared edge, which is why a whole flight stayed a
            // row of fragments no matter how the merge was tuned.
            area.SeCorner[2] = FarHeight(lattice, width, depth, se, stepSize);
            area.NeZ = FarHeight(lattice, width, 0, ne, stepSize);
            area.SwZ = FarHeight(lattice, 0, depth, sw, stepSize);

            area.AttributeFlags = (int)lattice.Seed.Attributes;

            return area;
        }
    }
}
