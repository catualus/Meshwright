using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Meshwright
{
    /// <summary>
    /// Adds the step, jump and drop connections between areas that the engine's generator leaves out.
    ///
    /// Two areas can share an edge in plan view and still have no connection recorded, because the
    /// generator only links areas it managed to flood-fill between during sampling. A ledge you can
    /// clearly step down from, a crate you can climb onto, a rooftop reachable from the next roof - all
    /// of these routinely end up unlinked, which is what "misses some jump-ups" looks like from inside
    /// the game.
    ///
    /// Every candidate is confirmed with a real trace before being added. A wrong connection is much
    /// worse than a missing one: a bot walks confidently into a wall or off a fatal drop, whereas a
    /// missing link only makes it take the long way round.
    /// </summary>
    public static class ConnectionBuilder
    {
        /// <summary>How far apart two edges may sit and still count as touching.</summary>
        private const float EdgeGap = 40f;

        /// <summary>Shared edge shorter than this is a corner clip, not a doorway worth linking.</summary>
        private const float MinimumOverlap = 16f;

        /// <summary>How far in from the shared edge the trace endpoints sit, so they are clear of it.</summary>
        private const float Inset = 6f;

        /// <summary>
        /// Heights above each surface at which the crossing must be clear. Ankle height alone would let
        /// a connection through a waist-high railing; head height alone would let one through a low
        /// tunnel mouth that is actually solid at the floor.
        ///
        /// The top sample was 60, above <c>HumanCrouchHeight</c>, which quietly made every crossing a
        /// *standing* crossing. That is the wrong bar for a connection. Whether a player can stand is a
        /// fact about an area, and the mesh already carries it as an area attribute for the bot to act
        /// on; whether a player can get from one area to the next is a fact about the gap between them,
        /// and ducking through a gap is still getting through it. Testing crossings at standing height
        /// refused connections through every doorway lintel, vent mouth and low tunnel on the map -
        /// including, absurdly, the ones leading into the crouch areas whose whole reason for existing
        /// is that they are too low to stand in.
        private static readonly float[] ClearanceHeights = [8f, 34f, 50f];

        public sealed class Result
        {
            public int Steps;
            public int JumpsUp;

            /// <summary>
            /// Climbs above <c>JumpHeight</c> but within <c>JumpCrouchHeight</c> - the ones a player can
            /// only make by tucking their legs up mid-jump. Counted apart from an ordinary standing jump
            /// because they are a different move with a different failure mode, and lumping them
            /// together hid the fact that the ceiling on an upward connection was a single flat number
            /// with no notion of which jump it was describing.
            /// </summary>
            public int CrouchJumpsUp;

            public int Drops;
            public int Rejected;

            // split by direction so a systematic asymmetry cannot hide behind a single total
            public int UpCandidates, UpRejectedByReach, UpRejectedByTrace;
            public int DownCandidates, DownRejectedByReach, DownRejectedByTrace;

            // and split by which test did the rejecting, so a systematic failure in one of them
            // cannot hide behind "blocked". The four are very different claims about the world and
            // they fail for unrelated reasons.
            public int RejectedByCrossing, RejectedByHeadroom, RejectedByGround, RejectedByFall;

            /// <summary>
            /// The headroom refusals split by what actually went wrong, which are opposite problems with
            /// opposite fixes. A low ceiling is the world saying no. A start point already inside solid
            /// is the *mesh* being wrong - the area claims a surface below the real floor at that point,
            /// so the trace begins buried and there was never any headroom to find.
            /// </summary>
            public int HeadroomStartSolid, HeadroomLowCeiling;

            public int Total => Steps + JumpsUp + CrouchJumpsUp + Drops;

            /// <summary>
            /// Folds a worker's tally into this one. Each thread counts into its own Result and merges
            /// once at the end, rather than every counter being an interlocked increment: there are
            /// thirteen of them and they are touched on nearly every candidate, so sharing them directly
            /// would put more traffic on the cache lines between cores than the traces save by spreading.
            /// </summary>
            public void Add(Result other)
            {
                Steps += other.Steps;
                JumpsUp += other.JumpsUp;
                CrouchJumpsUp += other.CrouchJumpsUp;
                Drops += other.Drops;
                Rejected += other.Rejected;
                UpCandidates += other.UpCandidates;
                UpRejectedByReach += other.UpRejectedByReach;
                UpRejectedByTrace += other.UpRejectedByTrace;
                DownCandidates += other.DownCandidates;
                DownRejectedByReach += other.DownRejectedByReach;
                DownRejectedByTrace += other.DownRejectedByTrace;
                RejectedByCrossing += other.RejectedByCrossing;
                RejectedByHeadroom += other.RejectedByHeadroom;
                RejectedByGround += other.RejectedByGround;
                RejectedByFall += other.RejectedByFall;
                HeadroomStartSolid += other.HeadroomStartSolid;
                HeadroomLowCeiling += other.HeadroomLowCeiling;
            }
        }

        /// <summary>Which test refused a crossing. <see cref="Clear"/> means it was accepted.</summary>
        public enum Refusal { Clear, Crossing, Headroom, Ground, Fall }

        /// <summary>
        /// Runs the connection tests for one pair of areas and describes each step, in the order the
        /// builder applies them. Diagnostic only - the builder itself does not call this.
        /// </summary>
        public static List<string> Explain(BspVisibility vis, NavArea from, NavArea to)
        {
            var log = new List<string>();

            var bounds = NavGeometry.GetBounds(from);
            var other = NavGeometry.GetBounds(to);

            for (int direction = 0; direction < NavGeometry.DirectionCount; direction++)
            {
                string name = direction switch
                {
                    NavGeometry.North => "north", NavGeometry.East => "east",
                    NavGeometry.South => "south", _ => "west",
                };

                if (from.Connections[direction].Contains(to.Id))
                {
                    log.Add($"{name}: already connected");
                    continue;
                }

                if (!SharedEdge(bounds, other, direction, out float centreA, out float centreB,
                        out float overlap))
                {
                    log.Add($"{name}: edges do not face each other within {EdgeGap:F0} units");
                    continue;
                }

                if (overlap < MinimumOverlap)
                {
                    log.Add($"{name}: shared span only {overlap:F0} units, needs {MinimumOverlap:F0}");
                    continue;
                }

                var (fromX, fromY) = EdgePoint(bounds, direction, centreA, -Inset);
                var (toX, toY) = EdgePoint(other, NavGeometry.Opposite(direction), centreB, -Inset);

                float fromZ = NavGeometry.SurfaceZ(from, fromX, fromY);
                float toZ = NavGeometry.SurfaceZ(to, toX, toY);
                float climb = toZ - fromZ;

                log.Add($"{name}: shared span {overlap:F0}, from ({fromX:F0} {fromY:F0} {fromZ:F1}) " +
                        $"to ({toX:F0} {toY:F0} {toZ:F1}), climb {climb:F1}");

                if (!Reachable(climb))
                {
                    log.Add($"{name}:   REFUSED - climb out of range " +
                            $"(-{NavConstants.DeathDrop:F0}..{NavConstants.JumpCrouchHeight:F0})");
                    continue;
                }

                var refusal = TestCrossing(vis, from, to, fromX, fromY, fromZ, toX, toY, toZ, out _);
                log.Add(refusal == Refusal.Clear
                    ? $"{name}:   would connect"
                    : $"{name}:   REFUSED by {refusal}");
            }

            return log;
        }

        /// <summary>
        /// Adds every connection the generator left out.
        ///
        /// Parallel over areas, which is safe for a reason worth stating rather than assuming: an
        /// iteration writes only to <c>nav.Areas[i].Connections</c>, the area it was handed. Everything
        /// else it touches - the spatial index, every other area's corners and bounds, the BSP - is read
        /// only for the whole pass. No two workers can therefore see the same list, and the connection
        /// graph being directed is what makes that true: adding a link from A to B does not touch B.
        /// </summary>
        public static Result Build(NavFile nav, BspVisibility vis, NavProgress? progress = null)
        {
            var result = new Result();
            var index = new NavGeometry.Index(nav.Areas);
            int done = 0;
            object gate = new();

            Parallel.For(0, nav.Areas.Count, NavConcurrency.Options, () => new Result(), (i, _, local) =>
            {
                progress?.Report(Interlocked.Increment(ref done) / (double)Math.Max(1, nav.Areas.Count));

                var area = nav.Areas[i];
                var bounds = NavGeometry.GetBounds(area);

                for (int direction = 0; direction < NavGeometry.DirectionCount; direction++)
                {
                    var existing = new HashSet<uint>(area.Connections[direction]);

                    foreach (int j in CandidatesBeyond(index, bounds, direction))
                    {
                        if (j == i) continue;

                        var other = nav.Areas[j];
                        if (existing.Contains(other.Id))
                            continue;

                        if (!SharedEdge(bounds, NavGeometry.GetBounds(other), direction,
                                out float centreA, out float centreB, out float overlap))
                            continue;

                        if (overlap < MinimumOverlap)
                            continue;

                        var (fromX, fromY) = EdgePoint(bounds, direction, centreA, -Inset);
                        var (toX, toY) = EdgePoint(NavGeometry.GetBounds(other),
                            NavGeometry.Opposite(direction), centreB, -Inset);

                        float fromZ = NavGeometry.SurfaceZ(area, fromX, fromY);
                        float toZ = NavGeometry.SurfaceZ(other, toX, toY);
                        float climb = toZ - fromZ;

                        bool upward = climb > NavConstants.StepHeight;
                        bool downward = climb < -NavConstants.StepHeight;

                        if (upward) local.UpCandidates++;
                        if (downward) local.DownCandidates++;

                        if (!Reachable(climb))
                        {
                            if (upward) local.UpRejectedByReach++;
                            if (downward) local.DownRejectedByReach++;
                            continue;
                        }

                        var refusal = TestCrossing(vis, area, other, fromX, fromY, fromZ, toX, toY, toZ,
                            out bool startedSolid);
                        if (refusal != Refusal.Clear)
                        {
                            local.Rejected++;
                            if (upward) local.UpRejectedByTrace++;
                            if (downward) local.DownRejectedByTrace++;

                            switch (refusal)
                            {
                                case Refusal.Crossing: local.RejectedByCrossing++; break;
                                case Refusal.Headroom:
                                    local.RejectedByHeadroom++;
                                    if (startedSolid) local.HeadroomStartSolid++; else local.HeadroomLowCeiling++;
                                    break;
                                case Refusal.Ground: local.RejectedByGround++; break;
                                case Refusal.Fall: local.RejectedByFall++; break;
                            }

                            continue;
                        }

                        area.Connections[direction].Add(other.Id);
                        existing.Add(other.Id);

                        if (MathF.Abs(climb) <= NavConstants.StepHeight) local.Steps++;
                        else if (climb > NavConstants.JumpHeight) local.CrouchJumpsUp++;
                        else if (climb > 0) local.JumpsUp++;
                        else local.Drops++;
                    }
                }

                return local;
            },
            local =>
            {
                lock (gate)
                    result.Add(local);
            });

            return result;
        }

        /// <summary>
        /// Whether a height change can be crossed at all. Upward is capped by a crouch jump; downward
        /// only by the drop the engine considers survivable.
        /// </summary>
        private static bool Reachable(float climb) =>
            climb >= -NavConstants.DeathDrop && climb <= NavConstants.JumpCrouchHeight;

        /// <summary>Areas in the band just beyond one edge of the footprint.</summary>
        private static IEnumerable<int> CandidatesBeyond(NavGeometry.Index index, NavGeometry.Bounds b, int direction)
            => direction switch
            {
                NavGeometry.North => index.Overlapping(b.MinX, b.MinY - EdgeGap, b.MaxX, b.MinY),
                NavGeometry.South => index.Overlapping(b.MinX, b.MaxY, b.MaxX, b.MaxY + EdgeGap),
                NavGeometry.West => index.Overlapping(b.MinX - EdgeGap, b.MinY, b.MinX, b.MaxY),
                _ => index.Overlapping(b.MaxX, b.MinY, b.MaxX + EdgeGap, b.MaxY),
            };

        /// <summary>
        /// Whether the two footprints actually abut across the given direction, and where along the
        /// shared span the crossing should be tested. The centres come back separately because the two
        /// areas' edges need not be the same length.
        /// </summary>
        private static bool SharedEdge(NavGeometry.Bounds a, NavGeometry.Bounds b, int direction,
            out float centreA, out float centreB, out float overlap)
        {
            centreA = centreB = overlap = 0;

            bool alongY = direction is NavGeometry.North or NavGeometry.South;

            // the facing edges must be within touching distance of each other
            float faceA = direction switch
            {
                NavGeometry.North => a.MinY,
                NavGeometry.South => a.MaxY,
                NavGeometry.West => a.MinX,
                _ => a.MaxX,
            };

            float faceB = direction switch
            {
                NavGeometry.North => b.MaxY,
                NavGeometry.South => b.MinY,
                NavGeometry.West => b.MaxX,
                _ => b.MinX,
            };

            if (MathF.Abs(faceA - faceB) > EdgeGap)
                return false;

            // and they must overlap along the perpendicular axis
            float lowA = alongY ? a.MinX : a.MinY;
            float highA = alongY ? a.MaxX : a.MaxY;
            float lowB = alongY ? b.MinX : b.MinY;
            float highB = alongY ? b.MaxX : b.MaxY;

            float low = MathF.Max(lowA, lowB);
            float high = MathF.Min(highA, highB);
            overlap = high - low;

            if (overlap <= 0)
                return false;

            centreA = centreB = (low + high) / 2f;
            return true;
        }

        /// <summary>
        /// A point inset from the middle of one edge. A negative <paramref name="offset"/> moves inward,
        /// which keeps trace endpoints off the boundary itself where they would land ambiguously.
        /// </summary>
        private static (float X, float Y) EdgePoint(NavGeometry.Bounds b, int direction, float centre, float offset)
            => direction switch
            {
                NavGeometry.North => (centre, b.MinY - offset),
                NavGeometry.South => (centre, b.MaxY + offset),
                NavGeometry.West => (b.MinX - offset, centre),
                _ => (b.MaxX + offset, centre),
            };

        /// <summary>
        /// Whether a walker could actually get between the two points.
        ///
        /// The horizontal tests run at the height of the *higher* surface, not each area's own. Sighting
        /// straight across from the lower surface is wrong for anything but a flat crossing: the line
        /// runs directly into the face of the step, so every jump-up gets rejected while the matching
        /// drop is accepted. That asymmetry is exactly what it looked like - 258 drops found and not one
        /// jump. Movement over a step happens in the space above it, so that is the space to test.
        ///
        /// Both ends then need vertical room to reach that level, or the connection passes under a
        /// ceiling too low to stand up in.
        /// </summary>
        private static Refusal TestCrossing(BspVisibility vis, NavArea from, NavArea to,
            float fromX, float fromY, float fromZ, float toX, float toY, float toZ,
            out bool startedSolid)
        {
            startedSolid = false;

            // Measured from the real floor, not from the areas' own surfaces.
            //
            // An area is a quad with four corner heights, so over a staircase it is a smooth ramp laid
            // across discrete treads and its interpolated surface sits *below* the step for most of each
            // tread - by up to a full step height. Every clearance test here then starts underneath the
            // tread it is supposed to be clearing, and the horizontal lines run straight into the riser.
            //
            // That is what disconnected staircases from the floors at their foot: the flight and the
            // landing sat flush at the same height, the seam was wide open, and the crossing was refused
            // anyway because it was being tested twelve units inside a step. Both ends are probed because
            // either can be the stair.
            float fromGround = GroundAt(vis, fromX, fromY, fromZ);
            float toGround = GroundAt(vis, toX, toY, toZ);

            float high = MathF.Max(MathF.Max(fromZ, toZ), MathF.Max(fromGround, toGround));
            fromZ = fromGround;
            toZ = toGround;

            // Offsets perpendicular to the crossing, spanning a player's width. A single centre line is
            // infinitely thin and slips through gaps a 32 unit wide player cannot fit through; checked
            // against real hull traces in game, centre-only let about 3% of added connections through
            // that a player is actually blocked by.
            float dx = toX - fromX, dy = toY - fromY;
            float length = MathF.Sqrt(dx * dx + dy * dy);

            float sideX = 0, sideY = 0;
            if (length > 0.01f)
            {
                sideX = -dy / length * NavConstants.HalfHumanWidth;
                sideY = dx / length * NavConstants.HalfHumanWidth;
            }

            foreach (float height in ClearanceHeights)
            {
                for (int side = -1; side <= 1; side++)
                {
                    float ox = sideX * side, oy = sideY * side;

                    var a = new BspFile.Vector3(fromX + ox, fromY + oy, high + height);
                    var b = new BspFile.Vector3(toX + ox, toY + oy, high + height);

                    // Whether a body fits through the gap, not whether a bot can see through it -
                    // GenerationMask throughout, matching what Valve traces movement against.
                    if (!vis.IsLineClear(a, b, BspVisibility.GenerationMask))
                        return Refusal.Crossing;
                }
            }

            // Both evaluated, not short-circuited: either end being buried is worth recording, and the
            // two traces are cheap next to the ones already done above.
            bool fromClear = HasRoom(vis, fromX, fromY, fromZ, out bool fromBuried);
            bool toClear = HasRoom(vis, toX, toY, toZ, out bool toBuried);

            if (!fromClear || !toClear)
            {
                startedSolid = fromBuried || toBuried;
                return Refusal.Headroom;
            }

            if (!HasGroundBetween(vis, fromX, fromY, fromZ, toX, toY, toZ))
                return Refusal.Ground;

            // Only downward: a climb has no fall path to check, and the space above a step is already
            // covered by the horizontal clearance lines.
            if (toZ < fromZ - NavConstants.StepHeight &&
                !CanFallTo(vis, fromX, fromY, fromZ, toX, toY, toZ))
            {
                return Refusal.Fall;
            }

            return Refusal.Clear;
        }

        /// <summary>
        /// Whether a body fits standing on one end of the crossing.
        ///
        /// Measured against that end's *own* surface, which sounds too obvious to be worth a comment
        /// until you see what it replaced: both ends were tested up to the height of the *higher*
        /// surface plus 60. That is a much stronger and quite different claim - that a clear column runs
        /// from the lower area all the way up to the level of the upper one - and on a drop it passes
        /// straight through the ledge being dropped from whenever that ledge overhangs its own edge,
        /// which is the ordinary shape of a ledge. It rejected 1,822 of the 2,302 refused crossings on
        /// gm_construct: 79% of every rejection, and by a wide margin the largest single cause.
        ///
        /// The space the movement actually passes through is already covered, by the horizontal
        /// clearance lines above: those run the whole crossing at three heights above the higher
        /// surface, with lateral offsets for the walker's width. This test only has to answer the
        /// remaining question, which is whether each end is somewhere a walker can be at all.
        /// </summary>
        /// Measured against <c>HumanCrouchHeight</c>, not <c>HumanHeight</c>, and deliberately not
        /// against the area's own crouch attribute either. The point being tested is inset six units
        /// from the shared edge, so on any crossing into a low opening it sits *under the lintel* - and
        /// asking a standing area for standing room at that point refuses the connection on the strength
        /// of geometry that belongs to the doorway rather than to the area. Measured on gm_construct
        /// this was 1,175 of 1,656 refusals, the largest cause left after the drop fix.
        /// <summary>
        /// The real floor at a point, falling back to the area's own claim if none is found nearby.
        ///
        /// Searched only a step either side, so this corrects the quad-versus-tread discrepancy without
        /// wandering onto a different storey through a gap beside the area.
        /// </summary>
        private static float GroundAt(BspVisibility vis, float x, float y, float claimed)
            => StairMarker.TryFindFloor(vis, x, y, claimed + NavConstants.StepHeight,
                NavConstants.StepHeight * 2f, out float ground)
                ? ground
                : claimed;

        private static bool HasRoom(BspVisibility vis, float x, float y, float z, out bool startedSolid)
        {
            var foot = new BspFile.Vector3(x, y, z + 4f);

            // A degenerate zero-length trace is a point-in-solid test, which separates "there is a
            // ceiling here" from "this point is inside the floor" - and only the first is about the
            // world. The second means the area's own surface is wrong here.
            startedSolid = !vis.IsLineClear(foot, foot, BspVisibility.GenerationMask);

            return !startedSolid && vis.IsLineClear(foot,
                new BspFile.Vector3(x, y, z + NavConstants.HumanCrouchHeight),
                BspVisibility.GenerationMask);
        }

        /// <summary>
        /// Whether there is floor along the crossing rather than open air.
        ///
        /// Clear line of sight is not enough on its own. Two rooftops forty units apart with a fatal
        /// drop between them have a perfectly clear line, and connecting them tells a bot it can simply
        /// walk across. Probing the ground at the midpoint distinguishes a gap in the mesh - which is
        /// what these connections exist to repair - from a gap in the world.
        /// </summary>
        private static bool HasGroundBetween(BspVisibility vis,
            float fromX, float fromY, float fromZ, float toX, float toY, float toZ)
        {
            float dx = toX - fromX, dy = toY - fromY;
            if (dx * dx + dy * dy <= NavConstants.StepHeight * NavConstants.StepHeight)
                return true; // the areas effectively touch; there is nothing to span

            float midX = (fromX + toX) / 2f;
            float midY = (fromY + toY) / 2f;
            float low = MathF.Min(fromZ, toZ);

            if (!StairMarker.TryFindFloor(vis, midX, midY, MathF.Max(fromZ, toZ) + 8f,
                    NavConstants.DeathDrop, out float groundZ))
            {
                return false;
            }

            return groundZ >= low - NavConstants.StepHeight;
        }

        /// <summary>
        /// Whether a walker could actually fall from the upper surface to the lower one, rather than
        /// there being a floor in the way.
        ///
        /// <see cref="HasGroundBetween"/> looks like it covers this and does not. It asks only that the
        /// ground between the two areas is not *below* the lower one - it is guarding against a
        /// connection strung across a chasm - and a solid slab high above the lower area satisfies that
        /// as comfortably as the lower area's own floor does.
        ///
        /// So a drop could be recorded straight through a ceiling. Wherever a basement runs underneath
        /// an upper storey, the two areas abut in plan view at the point the floor above ends, the
        /// horizontal clearance lines run through open air well above the slab, both ends have headroom,
        /// and the midpoint probe finds the slab and approves it. The result is a drop connection
        /// punching through solid concrete, which is exactly what it looks like in game: a nav area at
        /// ceiling height with links dropping through the floor to the room below.
        ///
        /// Swept along the whole run rather than probed at the landing point alone, and given the
        /// walker's width, for the same two reasons the horizontal clearance test above is.
        ///
        /// A single column at the landing point is in the wrong place whenever the two areas do not
        /// actually abut. <see cref="SharedEdge"/> accepts facing edges up to <see cref="EdgeGap"/>
        /// apart and the landing point sits a further <see cref="Inset"/> inside the lower area, so the
        /// one column tested could be 46 units past the edge the walker actually steps off - out beyond
        /// the end of the slab that is in the way, in open air, reporting a clean fall. And being one
        /// infinitely thin line it slips through any crack narrower than itself, exactly as the
        /// centre-only horizontal test used to.
        ///
        /// Starts just past the upper area's edge rather than at the inset point itself, which sits
        /// <see cref="Inset"/> units inside that area and therefore directly on top of its own floor -
        /// a column there would begin inside the slab being stepped off and refuse every drop on the
        /// map. The lateral offsets run parallel to the shared edge, so all three columns in a sample
        /// stand the same distance past it.
        /// </summary>
        private static bool CanFallTo(BspVisibility vis,
            float fromX, float fromY, float fromZ, float toX, float toY, float toZ)
        {
            float dx = toX - fromX, dy = toY - fromY;
            float run = MathF.Sqrt(dx * dx + dy * dy);

            float sideX = 0, sideY = 0;
            if (run > 0.01f)
            {
                sideX = -dy / run * NavConstants.HalfHumanWidth;
                sideY = dx / run * NavConstants.HalfHumanWidth;
            }

            // Where along the run the first sample sits: clear of the upper area's own edge.
            float first = run > 0.01f ? MathF.Min((Inset + 2f) / run, 1f) : 1f;

            for (int i = 0; i < FallSamples; i++)
            {
                float t = FallSamples == 1 ? 1f : first + (1f - first) * i / (FallSamples - 1);
                float x = fromX + dx * t;
                float y = fromY + dy * t;

                int sidesBlocked = 0;

                for (int side = -1; side <= 1; side++)
                {
                    // Started just under the upper surface and stopped just above the lower one, so
                    // neither floor counts as the obstruction - only something genuinely in between.
                    var top = new BspFile.Vector3(x + sideX * side, y + sideY * side, fromZ - 1f);
                    var bottom = new BspFile.Vector3(x + sideX * side, y + sideY * side, toZ + 4f);

                    if (vis.IsLineClear(top, bottom, BspVisibility.GenerationMask))
                        continue;

                    // The centre line is the fall itself, so anything in its way settles the question.
                    if (side == 0)
                        return false;

                    sidesBlocked++;
                }

                // A side column is weaker evidence and is not allowed to refuse on its own. A ledge is
                // rarely as wide as the crossing being stepped off, so one lateral column routinely
                // lands over the floor of whatever sits alongside the upper area rather than over the
                // fall - refusing on that would delete legitimate drops off the corner of every walkway.
                // Both sides blocked is a different claim: the walker does not fit through.
                if (sidesBlocked == 2)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// How many points along the fall's horizontal run are probed. Three: just past the edge being
        /// stepped off, the landing point, and the middle. Two would leave the span between them
        /// untested, which on a crossing that can be 46 units long is where a slab edge sits.
        /// </summary>
        private const int FallSamples = 3;
    }
}
