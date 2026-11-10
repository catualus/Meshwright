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

        /// <summary>
        /// How far a candidate's near edge may sit *behind* the edge being crossed - that is, how much
        /// the two footprints may overlap in plan view and still be treated as abutting.
        ///
        /// <see cref="SharedEdge"/> compared the two faces with an absolute difference, which is
        /// symmetric and therefore said yes to a candidate lying up to <see cref="EdgeGap"/> units back
        /// underneath the area being left. That is not a shared edge at all; it is one area sitting over
        /// another, which is the ordinary arrangement wherever a walkway, a deck or an upper storey
        /// covers ground below it. The crossing then tested runs *backwards*, from a point inside the
        /// upper area to a point still underneath it, and every test in <see cref="TestCrossing"/>
        /// answers a question about the wrong piece of space: the horizontal clearance lines run through
        /// the open air above the deck, and <see cref="HasGroundBetween"/> finds the deck itself and
        /// approves of it. Only <see cref="CanFallTo"/> stands between that and a drop connection
        /// recorded straight down through a floor, and it was never meant to carry the whole weight.
        ///
        /// Six units rather than none, because two areas that ought to abut exactly do not always: the
        /// squarer, the merger and hand editing all leave seams a unit or two out, and refusing those
        /// would cost ordinary connections to buy nothing. Anything past that is one area over another.
        /// </summary>
        private const float MaxOverlap = Inset;

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

            /// <summary>
            /// The links made only at an end of their shared span (<see cref="CrossesAtEnds"/>), from, the
            /// direction and to, for <see cref="DropStale"/>: they stand on a body standing at a corner,
            /// and are not given the benefit of the doubt a link to a spot no body stands in gets.
            /// </summary>
            public readonly HashSet<(uint From, int Direction, uint To)> AtSpanEnds = [];

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
                AtSpanEnds.UnionWith(other.AtSpanEnds);
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
                    log.Add($"{name}: edges do not face each other - more than {EdgeGap:F0} units " +
                            $"apart, more than {MaxOverlap:F0} units overlapped, or no shared span");
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
                if (refusal == Refusal.Clear)
                    log.Add($"{name}:   would connect");
                else if (CrossesElsewhere(vis, from, to, bounds, other, direction, centreA, overlap, out float elsewhere,
                             out bool atEnd))
                {
                    log.Add($"{name}:   REFUSED by {refusal} at the middle, would connect further along (climb {elsewhere:F1})" +
                            (atEnd ? ", at an end of the span" : ""));
                }
                else
                    log.Add($"{name}:   REFUSED by {refusal} at the middle and everywhere along the span");
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
            => Link(nav, vis, progress, reshaped: null);

        /// <summary>
        /// <see cref="Build"/> again, for only the pairs that <paramref name="reshaped"/> can have changed
        /// the answer for: every pair with at least one of those areas in it, in both directions.
        ///
        /// Connections are built before the clip on purpose (see <see cref="AreaGenerator.ClipToGeometry"/>),
        /// and nothing re-tested them afterwards. The clip moves edges, so a pair can come out of it with
        /// a different answer: an area that ran more than <see cref="MaxOverlap"/> units under or into its
        /// neighbour is pulled back to abut it, and a crossing whose end point was buried in a wall or
        /// under a ledge is now tested from open floor. Those pairs had been tested once, on shapes that
        /// no longer exist, and refused.
        ///
        /// Only adds. A connection made before the clip is not re-tested here: the clip opening a gap where
        /// two areas used to touch is exactly what the build-first order exists to survive, and the
        /// builder's test would undo it. <see cref="DropStale"/> removes those with no way across at all,
        /// by a test of its own. A pair where neither area moved is skipped, because its geometry is what it was
        /// when it was built and the same tests give the same answer. Must run before
        /// <see cref="AreaConnectionFixer"/>, which is then free to prune what this adds like anything
        /// else.
        ///
        /// One test is stricter than the build's: across a shared span narrower than a body, a crouching
        /// body is swept through the middle crossing too, not only through the ones off it. The clip
        /// leaves exactly that shape - a strip pulled back to the wall it ran into, still touching its
        /// neighbour along a span of twenty-odd units - and the clearance lines, sixteen units either side
        /// of a middle that now sits a few units off the wall, graze its face and read clear. Without the
        /// sweep, 428 of the 743 pairs this found on the twelve benchmark maps were ones a hull checker
        /// finds no body gets through anywhere along the edge, against 1 to 3% of the build's own flat
        /// links. The build keeps the lines alone: there the middle of a narrow span is usually open
        /// ground between two strips of the sampling grid, and it is the measured baseline.
        /// </summary>
        public static Result Relink(NavFile nav, BspVisibility vis, IReadOnlySet<uint> reshaped,
            NavProgress? progress = null)
            => reshaped.Count == 0 ? new Result() : Link(nav, vis, progress, reshaped);

        /// <summary>What <see cref="DropStale"/> looked at and what it did.</summary>
        public sealed class StaleResult
        {
            /// <summary>Links made before the clip, between two areas that faced each other then, at least one of them reshaped.</summary>
            public int Tested;

            /// <summary>Of those, the ones the builder's own test now refuses on the new shapes.</summary>
            public int Refused;

            /// <summary>Of those, the ones no body gets across either, which are removed.</summary>
            public int Removed;

            /// <summary>
            /// Of those, the ones the build made only at an end of their span, removed although no body
            /// stands at both ends of any crossing on the new shapes.
            /// </summary>
            public int RemovedAtEnds;

            /// <summary>
            /// Of those, the ones removed although no body stands at both ends of any crossing, because not
            /// even a walker of no width gets across anywhere: something solid stands in the crossing
            /// itself wherever it is tried (<see cref="BodyVerdict.Walled"/>).
            /// </summary>
            public int RemovedWalled;
        }

        /// <summary>
        /// The other half of <see cref="Relink"/>: the links made before the clip that it left with no
        /// way across, removed.
        ///
        /// The clip moves edges, so a link made on the old shapes can fail the builder's own test on the
        /// new ones. That alone does not make it wrong. The build runs first precisely so that a crossing
        /// survives the clip opening a gap where two areas used to touch, and a walker still crosses where
        /// the floor runs on between them. So a link the test now refuses is put to a second one: a
        /// crouching body, swept as well as the clearance lines, tried at every point along the span the
        /// two areas now share - or, where they no longer face each other, along the span they shared
        /// before - from a unit to 32 units inside each area. A link is removed only when no body gets
        /// across anywhere and a body does stand at both ends of at least one of those crossings. Trying
        /// several depths matters because the areas the clip leaves are often strips narrower than a body
        /// against a wall: six units in, the depth the builder uses, a body sticks into the wall, while a
        /// unit in it stands half over the neighbour and crosses. The two ends of the span are tried as well,
        /// with the body centred on each and half of it past it over floor (<see cref="CrossesAtEnds"/>),
        /// and count as any other crossing does: a body that gets across there keeps the link, and one
        /// that stands at both ends of a level one there counts as standing.
        ///
        /// The second condition is what keeps this from deleting ground. Measured on the twelve benchmark
        /// maps, 10,559 of the 85,484 links made before the clip on a reshaped area fail the builder's test
        /// on the new shapes, and a body crosses about 8,000 of them. About 2,000 more lead to or from a
        /// spot no crouching body stands in at any of the depths tried: a 10 unit crate top used as a step,
        /// a strip in a corner. A hull checker flags 71% of those, nearly all because no body fits at one
        /// end, but where Valve's TF2 meshes have ground at both ends they join the two spots for 32 of 33,
        /// and removing them cut off a ledge on ctf_2fort that Valve's mesh reaches. The 505 removed are
        /// crossings blocked outright - drops through the floor over the landing, links through a wall the
        /// clip exposed. In the finished meshes the hull checker flags 330 of the 391 of them it can test,
        /// against 4% of the links kept, and where Valve's TF2 meshes have ground at both ends they leave 28
        /// of 58 unjoined, against 22 of 4,990 kept links.
        ///
        /// The exception is a link <paramref name="atSpanEnds"/> says the build made only at an end of the
        /// span (<see cref="CrossesAtEnds"/>). It was made because a body stood at both ends at a corner,
        /// not on a middle crossing that never asked, so when the clip leaves no body standing at both
        /// ends of any crossing - the corner pulled back into a strip a body no longer fits in beside the
        /// wall - there is nothing left to give it the benefit of the doubt for, and it goes too.
        ///
        /// Nor is a link given it whose crossing is shut to a walker of no width wherever it is tried
        /// (<see cref="CrossingShut"/>): a line through its middle runs into something, or it drops onto
        /// ground under the floor it leaves. The ends being too tight for a body is then not why nothing
        /// gets across. On the twelve maps that removes 180 more links and the finished
        /// meshes lose 175, 148 on the roleplay maps - window sills behind glass linked to the street, strips
        /// beside rails and between props - and a hull checker flags 159 of the 169 it can test. Valve's TF2
        /// meshes have ground at both ends of none of the 23 removed there.
        ///
        /// Links between two areas that did not face each other before the clip are left alone: they are
        /// not the builder's, but the jump-area stitcher's or someone's hand edit. Parallel over areas and
        /// safe for the same reason <see cref="Build"/> is - an iteration only removes from the area it
        /// was handed, and reads nothing but geometry - so the result does not depend on thread order.
        /// </summary>
        public static StaleResult DropStale(NavFile nav, BspVisibility vis, IReadOnlySet<uint> reshaped,
            IReadOnlyDictionary<uint, NavGeometry.Bounds> before,
            IReadOnlySet<(uint From, int Direction, uint To)>? atSpanEnds = null)
        {
            var result = new StaleResult();
            if (reshaped.Count == 0)
                return result;

            var byId = new Dictionary<uint, NavArea>(nav.Areas.Count);
            foreach (var area in nav.Areas)
                byId[area.Id] = area;

            object gate = new();

            Parallel.For(0, nav.Areas.Count, NavConcurrency.Options, () => new StaleResult(), (i, state, local) =>
            {
                var area = nav.Areas[i];
                var bounds = NavGeometry.GetBounds(area);
                var oldBounds = before.TryGetValue(area.Id, out var ob) ? ob : bounds;

                for (int direction = 0; direction < NavGeometry.DirectionCount; direction++)
                {
                    List<uint>? broken = null;

                    foreach (uint id in area.Connections[direction])
                    {
                        if (!reshaped.Contains(area.Id) && !reshaped.Contains(id))
                            continue;

                        if (!byId.TryGetValue(id, out var other))
                            continue;

                        var otherBounds = NavGeometry.GetBounds(other);
                        var otherOld = before.TryGetValue(id, out var oo) ? oo : otherBounds;

                        // Only what the builder could have made: a pair facing each other across a span
                        // it would test, on the shapes it was tested on.
                        if (!SharedEdge(oldBounds, otherOld, direction, out _, out _, out float oldOverlap) ||
                            oldOverlap < MinimumOverlap)
                        {
                            continue;
                        }

                        local.Tested++;

                        if (Accepts(vis, area, other, bounds, otherBounds, direction))
                            continue;

                        local.Refused++;

                        var verdict = BodyCrosses(vis, area, other, bounds, otherBounds, oldBounds, otherOld, direction);
                        bool atEnd = atSpanEnds is not null && atSpanEnds.Contains((area.Id, direction, id));

                        if (verdict == BodyVerdict.Crosses || verdict == BodyVerdict.NoRoom && !atEnd)
                            continue;

                        if (verdict == BodyVerdict.NoRoom)
                            local.RemovedAtEnds++;
                        else if (verdict == BodyVerdict.Walled)
                            local.RemovedWalled++;

                        (broken ??= []).Add(id);
                    }

                    if (broken is not null)
                    {
                        area.Connections[direction].RemoveAll(broken.Contains);
                        local.Removed += broken.Count;
                    }
                }

                return local;
            },
            local =>
            {
                lock (gate)
                {
                    result.Tested += local.Tested;
                    result.Refused += local.Refused;
                    result.Removed += local.Removed;
                    result.RemovedAtEnds += local.RemovedAtEnds;
                    result.RemovedWalled += local.RemovedWalled;
                }
            });

            return result;
        }

        /// <summary>
        /// The builder's own test for one directed pair, as <see cref="Build"/> applies it: the edges face
        /// each other across a span of at least <see cref="MinimumOverlap"/>, the climb at its middle is in
        /// reach, and a walker gets across at the middle or somewhere else along it.
        /// </summary>
        private static bool Accepts(BspVisibility vis, NavArea area, NavArea other,
            NavGeometry.Bounds bounds, NavGeometry.Bounds otherBounds, int direction)
        {
            if (!SharedEdge(bounds, otherBounds, direction, out float centreA, out float centreB, out float overlap) ||
                overlap < MinimumOverlap)
            {
                return false;
            }

            var (fromX, fromY) = EdgePoint(bounds, direction, centreA, -Inset);
            var (toX, toY) = EdgePoint(otherBounds, NavGeometry.Opposite(direction), centreB, -Inset);
            float fromZ = NavGeometry.SurfaceZ(area, fromX, fromY);
            float toZ = NavGeometry.SurfaceZ(other, toX, toY);

            if (!Reachable(toZ - fromZ))
                return false;

            return TestCrossing(vis, area, other, fromX, fromY, fromZ, toX, toY, toZ, out _) == Refusal.Clear ||
                   CrossesElsewhere(vis, area, other, bounds, otherBounds, direction, centreA, overlap, out _, out _);
        }

        /// <summary>How far inside each area a body crossing a stale link is tried from (<see cref="DropStale"/>).</summary>
        private static readonly float[] BodyDepths = [1f, Inset, 16f, 32f];

        /// <summary>What <see cref="BodyCrosses"/> found.</summary>
        private enum BodyVerdict
        {
            /// <summary>A body gets across somewhere.</summary>
            Crosses,

            /// <summary>A body stands at both ends somewhere, and gets across nowhere.</summary>
            Blocked,

            /// <summary>No pair of ends was found where a body stands at both, so there is nothing to judge.</summary>
            NoRoom,

            /// <summary>
            /// As <see cref="NoRoom"/>, but not even a walker of no width gets across anywhere, refused by
            /// something in the crossing itself and not by the room at its ends.
            /// </summary>
            Walled,
        }

        /// <summary>
        /// Whether a crouching body gets from one area to the other anywhere along the span they share,
        /// or along the span they shared before the clip if they no longer face each other - each end tried
        /// at every one of <see cref="BodyDepths"/> that fits inside its area, with the body swept as well
        /// as the clearance lines - and last, at the two ends of the span itself (<see cref="CrossesAtEnds"/>).
        /// In a fixed order, so the answer does not depend on thread timing.
        ///
        /// <see cref="BodyVerdict.Blocked"/> only when a body also stands at both ends of at least one of
        /// the crossings tried. Where it stands at no pair of them, the crossing is not what fails: one of
        /// the areas is too tight for a body near this edge, and refusing the crossing for that is a
        /// verdict on the area. Those are left alone (see <see cref="DropStale"/>).
        /// </summary>
        private static BodyVerdict BodyCrosses(BspVisibility vis, NavArea area, NavArea other,
            NavGeometry.Bounds bounds, NavGeometry.Bounds otherBounds,
            NavGeometry.Bounds oldBounds, NavGeometry.Bounds otherOld, int direction)
        {
            if (!SharedEdge(bounds, otherBounds, direction, out float centre, out _, out float overlap) &&
                !SharedEdge(oldBounds, otherOld, direction, out centre, out _, out overlap))
            {
                return BodyVerdict.NoRoom;
            }

            float low = centre - overlap / 2f + SpanEnd, high = centre + overlap / 2f - SpanEnd;
            float depth = Depth(bounds, direction), otherDepth = Depth(otherBounds, direction);
            var tried = new HashSet<(float, float, float, float)>();
            var reachable = new List<(float FromX, float FromY, float FromZ, float ToX, float ToY, float ToZ)>();
            bool stood = false;

            // The middle first, then the same grid as CrossesElsewhere, pinned to both ends.
            int steps = high > low ? (int)MathF.Ceiling((high - low) / SpanStep) : 0;
            for (int k = -1; k <= steps; k++)
            {
                float c = k < 0 ? centre : MathF.Min(low + k * SpanStep, high);

                foreach (float into in BodyDepths)
                {
                    if (into > 1f && into > depth - 1f)
                        continue;

                    foreach (float intoOther in BodyDepths)
                    {
                        if (intoOther > 1f && intoOther > otherDepth - 1f)
                            continue;

                        var (fromX, fromY) = EdgePoint(bounds, direction, Along(bounds, direction, c),
                            -InsetWithin(into, depth));
                        var (toX, toY) = EdgePoint(otherBounds, NavGeometry.Opposite(direction),
                            Along(otherBounds, direction, c), -InsetWithin(intoOther, otherDepth));

                        if (!tried.Add((fromX, fromY, toX, toY)))
                            continue;

                        float fromZ = NavGeometry.SurfaceZ(area, fromX, fromY);
                        float toZ = NavGeometry.SurfaceZ(other, toX, toY);

                        if (!Reachable(toZ - fromZ))
                            continue;

                        if (TestCrossing(vis, area, other, fromX, fromY, fromZ, toX, toY, toZ, out _,
                                sweepBody: true) == Refusal.Clear)
                        {
                            return BodyVerdict.Crosses;
                        }

                        reachable.Add((fromX, fromY, fromZ, toX, toY, toZ));

                        stood = stood ||
                                BodyFits(vis, fromX, fromY, GroundAt(vis, fromX, fromY, fromZ)) &&
                                BodyFits(vis, toX, toY, GroundAt(vis, toX, toY, toZ));
                    }
                }
            }

            // Then the two ends of the span themselves, with half the body past them over floor
            // (CrossesAtEnds), which count for where a body stands as well as for where it crosses.
            if (CrossesAtEnds(vis, area, other, bounds, otherBounds, direction,
                    centre - overlap / 2f, centre + overlap / 2f, BodyDepths, out _, out bool stoodAtEnd))
            {
                return BodyVerdict.Crosses;
            }

            if (stood || stoodAtEnd)
                return BodyVerdict.Blocked;

            // No body stands at both ends of anything tried, so a body's refusals say nothing about the
            // crossing. Where the crossing itself is shut at every one of them, the ends being too tight
            // for a body is not why nothing gets across (CrossingShut).
            return reachable.Count > 0 && reachable.TrueForAll(c =>
                    CrossingShut(vis, other, c.FromX, c.FromY, c.FromZ, c.ToX, c.ToY, c.ToZ))
                ? BodyVerdict.Walled
                : BodyVerdict.NoRoom;
        }

        /// <summary>
        /// Whether a crossing is shut to a walker of no width, by something in the crossing itself rather
        /// than by the room at either end: its middle clearance column, the one under the walker's middle,
        /// runs into something at one of <see cref="ClearanceHeights"/>, or it drops onto a landing that
        /// lies under the floor it is stepped off, with no lip short of it to step off and no landing past
        /// one (<see cref="CanFallTo"/>).
        ///
        /// For <see cref="DropStale"/>'s links that no body stands at both ends of, which it leaves alone
        /// because the ends being too tight for a body is not the crossing's fault. That stops being true
        /// when the crossing is shut as well, and on the roleplay maps it is the usual shape: a window sill
        /// ten to eighteen units deep behind a pane of glass, linked before the clip to the street outside
        /// across a crossing that started past the pane. The clip pulls the sill back behind the glass, and
        /// no body stands on a sill that narrow, so the link was kept with the pane in the way - once the
        /// hull sweep saw brush entities beside a body as well as under its middle, no body stood beside the
        /// glass, and 24 such drops and climbs on rp_downtown_tits_v25 and rp_downtown_meowy came back.
        ///
        /// Narrower than <see cref="TestCrossing"/> on purpose. The side columns of a crossing next to a
        /// strip narrower than a body graze the wall beside it, which is the area's shape again. A fall
        /// column that lands on something partway down is as often a ledge the walker steps off again as a
        /// slab in the way - ctf_2fort has a 171 unit drop off a battlement that a body makes by way of a
        /// block 131 units down, and a hull checker passed four of the seven such links on it and
        /// rp_downtown_tits_v25. Headroom is about the ends.
        /// </summary>
        private static bool CrossingShut(BspVisibility vis, NavArea to,
            float fromX, float fromY, float fromZ, float toX, float toY, float toZ)
        {
            // Measured from the real floor and at the height of the higher surface, as TestCrossing does.
            float fromGround = GroundAt(vis, fromX, fromY, fromZ);
            float toGround = GroundAt(vis, toX, toY, toZ);
            float high = MathF.Max(MathF.Max(fromZ, toZ), MathF.Max(fromGround, toGround));

            if (!LinesClear(vis, fromX, fromY, toX, toY, high, 0f, 0f, sideFloors: false))
                return true;

            if (toGround >= fromGround - NavConstants.StepHeight)
                return false;

            float dx = toX - fromX, dy = toY - fromY;
            float run = MathF.Sqrt(dx * dx + dy * dy);

            if (run <= 0.01f)
                return false;

            float lip = Lip(vis, fromX, fromY, fromGround, dx, dy, run, run + LandingReach);

            return run <= lip + 2f &&
                   !LandingPastLip(vis, to, fromX, fromY, fromGround, toX, toY, dx / run, dy / run, lip,
                       run + LandingReach, high, 0f, 0f, out _, out _);
        }

        /// <summary>
        /// Whether a crouching body, the full 32 units wide, stands on the floor at a point: a plate swept
        /// up from half a step to the crouching height, as the generator's crouch sampler does, and refused
        /// if it starts inside something.
        /// </summary>
        private static bool BodyFits(BspVisibility vis, float x, float y, float z)
        {
            const float Lift = NavConstants.StepHeight / 2f;
            const float Half = NavConstants.HalfHumanWidth;

            bool hit = vis.TryTraceHull(
                new BspFile.Vector3(x, y, z + Lift),
                new BspFile.Vector3(x, y, z + NavConstants.HumanCrouchHeight),
                new BspFile.Vector3(-Half, -Half, 0f), new BspFile.Vector3(Half, Half, 0f),
                BspVisibility.GenerationMask, out float fraction, out _, out bool startSolid);

            return !startSolid && (!hit || fraction >= 1f);
        }

        /// <summary>A depth into an area, kept half a unit short of its far side.</summary>
        private static float InsetWithin(float into, float depth) => MathF.Min(into, MathF.Max(depth - 0.5f, depth / 2f));

        /// <summary>An area's extent across the direction of travel.</summary>
        private static float Depth(NavGeometry.Bounds b, int direction)
            => direction is NavGeometry.North or NavGeometry.South ? b.Depth : b.Width;

        /// <summary>A position along an edge, kept a unit inside the area's own extent along it.</summary>
        private static float Along(NavGeometry.Bounds b, int direction, float c)
        {
            bool alongX = direction is NavGeometry.North or NavGeometry.South;
            float lo = alongX ? b.MinX : b.MinY, hi = alongX ? b.MaxX : b.MaxY;
            float mid = (lo + hi) / 2f;
            return Math.Clamp(c, MathF.Min(lo + SpanEnd, mid), MathF.Max(hi - SpanEnd, mid));
        }

        private static Result Link(NavFile nav, BspVisibility vis, NavProgress? progress,
            IReadOnlySet<uint>? reshaped)
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

                // On a relink, an area the clip left alone is only tested against neighbours it did move.
                bool everyCandidate = reshaped is null || reshaped.Contains(area.Id);

                for (int direction = 0; direction < NavGeometry.DirectionCount; direction++)
                {
                    var existing = new HashSet<uint>(area.Connections[direction]);

                    foreach (int j in CandidatesBeyond(index, bounds, direction))
                    {
                        if (j == i) continue;

                        var other = nav.Areas[j];
                        if (existing.Contains(other.Id))
                            continue;

                        if (!everyCandidate && !reshaped!.Contains(other.Id))
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

                        // On a relink, a body is dragged across the middle as well whenever the span is
                        // narrower than one, as it already is off the middle (CrossesElsewhere). See
                        // Relink for why the build itself does not.
                        bool sweepMiddle = reshaped is not null && overlap < 2f * NavConstants.HalfHumanWidth;

                        var refusal = TestCrossing(vis, area, other, fromX, fromY, fromZ, toX, toY, toZ,
                            out bool startedSolid, sweepBody: sweepMiddle);
                        if (refusal != Refusal.Clear &&
                            CrossesElsewhere(vis, area, other, bounds, NavGeometry.GetBounds(other), direction,
                                centreA, overlap, out float elsewhere, out bool atEnd))
                        {
                            refusal = Refusal.Clear;
                            climb = elsewhere;

                            if (atEnd)
                                local.AtSpanEnds.Add((area.Id, direction, other.Id));
                        }

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

            // Signed along the direction of travel, not as an absolute difference. North and West run
            // towards decreasing coordinates, so their forward gap is the other subtraction.
            float gap = direction is NavGeometry.North or NavGeometry.West
                ? faceA - faceB
                : faceB - faceA;

            if (gap > EdgeGap || gap < -MaxOverlap)
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

        /// <summary>How far apart the crossings tried along a shared edge are, after the middle failed.</summary>
        private const float SpanStep = 8f;

        /// <summary>
        /// Whether a walker gets across somewhere else along the shared span, the middle having failed.
        ///
        /// Only the middle of the span used to be tried. Two rooms meeting across a wall thinner than the
        /// sampling grid share one long edge, with the doorway wherever the mapper put it, so they were
        /// linked only when the door happened to sit at the centre: 296 of rp_downtown_tits_v25's 409
        /// doorways connected, 384 with this. The engine links areas from node connections along the
        /// whole edge, which is what this approximates. Tried in a fixed order, so the result does not
        /// depend on thread timing.
        ///
        /// The body's centre may be anywhere on the shared span, out to a unit from either end, with
        /// half of the body hanging past it. This used to stop a body's half-width short of each end so
        /// the whole hull stayed over the span, which on a span under 32 units left no position to try
        /// at all - and the middle is then the only crossing ever tested. A ledge 25 units deep under
        /// an overhang is that shape: a crouching body fits on it only at the lip, half of it over the
        /// drop, so the middle runs into the overhang and nothing else was tried. gm_flatgrass has
        /// them round the walls of its pit, and 22 of their areas were islands. Across the twelve
        /// benchmark maps, measured with hull sweeps along each unlinked shared edge, 93% of the edges a
        /// body could cross only off the middle were narrower than a body.
        ///
        /// Where the body does hang past the span, the space it hangs into belongs to neither area, so
        /// nothing else vouches for it and it gets a real body swept through it, not only the lines.
        ///
        /// The ends themselves, with the body centred on them, are tried last (<see cref="CrossesAtEnds"/>).
        /// </summary>
        private static bool CrossesElsewhere(BspVisibility vis, NavArea area, NavArea other,
            NavGeometry.Bounds bounds, NavGeometry.Bounds otherBounds, int direction,
            float centre, float overlap, out float climb, out bool atEnd)
        {
            climb = 0;
            atEnd = false;
            float spanLow = centre - overlap / 2f, spanHigh = centre + overlap / 2f;
            float low = spanLow + SpanEnd;
            float high = spanHigh - SpanEnd;

            // The same grid as before, a half-width in from the low end and a step apart, carried on
            // out to both ends and pinned to them, so every position tried before is still tried and a
            // crossing found before is still found. Stepped by count rather than by accumulating.
            float anchor = spanLow + NavConstants.HalfHumanWidth;
            int first = -(int)MathF.Ceiling((anchor - low) / SpanStep);

            // Runs until the position before reached the high end, so that end is tried once.
            for (int k = first; anchor + (k - 1) * SpanStep < high; k++)
            {
                float c = Math.Clamp(anchor + k * SpanStep, low, high);

                if (MathF.Abs(c - centre) < SpanStep / 2f)
                    continue;   // the middle, already tried

                var (fromX, fromY) = EdgePoint(bounds, direction, c, -Inset);
                var (toX, toY) = EdgePoint(otherBounds, NavGeometry.Opposite(direction), c, -Inset);
                float fromZ = NavGeometry.SurfaceZ(area, fromX, fromY);
                float toZ = NavGeometry.SurfaceZ(other, toX, toY);

                if (!Reachable(toZ - fromZ))
                    continue;

                bool overhangs = c - spanLow < NavConstants.HalfHumanWidth ||
                                 spanHigh - c < NavConstants.HalfHumanWidth;

                if (TestCrossing(vis, area, other, fromX, fromY, fromZ, toX, toY, toZ, out _,
                        sweepBody: overhangs) == Refusal.Clear)
                {
                    climb = toZ - fromZ;
                    return true;
                }
            }

            atEnd = CrossesAtEnds(vis, area, other, bounds, otherBounds, direction, spanLow, spanHigh, EndDepths,
                out climb, out _);
            return atEnd;
        }

        /// <summary>
        /// How deep into each area a crossing at the very end of a shared span is tried from by
        /// <see cref="CrossesElsewhere"/>: the builder's own <see cref="Inset"/>, then a unit.
        /// </summary>
        private static readonly float[] EndDepths = [Inset, SpanEnd];

        /// <summary>
        /// Whether a walker gets across at either end of the shared span itself, the body centred on the
        /// end and half of it hanging past, over whatever lies beyond: tried last, after every position on
        /// the span has failed, so it only ever adds.
        ///
        /// Every other position stops a unit short of the end (<see cref="SpanEnd"/>), and that unit is the
        /// whole corner. Two strips the clip pulls back against a wall, each narrower than a body and
        /// sharing an edge as long as they are wide, are crossed by a body that stands at their open side,
        /// half over the area they both run along; a unit further in, its far side is in the wall. On the
        /// twelve benchmark maps, of the 39,889 adjacent pairs the builder refused and left unlinked, a hull
        /// checker gets a body across 956 only with it centred on an end of the span - 737 of them level -
        /// and the builder's own test, run there, clears 570. Of the 425 links <see cref="DropStale"/>
        /// removed, 9 were the same. Each end is tried from <see cref="Inset"/> and from a unit inside each
        /// area (<paramref name="depths"/>), as the strips are often too shallow for a body six units in
        /// to clear the wall behind it.
        ///
        /// Only where the ground under the overhanging half is real floor (<see cref="OverhangSupported"/>):
        /// the half of the body past the end is over neither area, and nothing else here vouches for it.
        /// That floor is the third area the two both run along, or what it stands on; a neighbouring area
        /// is not asked for as such, as the floor is what holds a body up and the meshes leave strips of it
        /// unmeshed along walls. A crouching body has to stand at both ends (<see cref="BodyStands"/>), and
        /// it is always swept as well as the clearance lines, start-solid aware, as off the middle
        /// (<see cref="BodyCrosses(BspVisibility, float, float, float, float, float)"/>), so a corner is no
        /// way round a wall.
        ///
        /// A drop or a climb at a corner, the two floors more than a step apart, asks more. Half the body
        /// hangs over a third area whose floor belongs to one level or the other, and what the fall and
        /// headroom tests look at is the crossing's own line, not that half. So the floor under it is asked
        /// for beside the higher end only, the floor the body walks along; a body has to stand at the lower
        /// end too, beside the corner at the foot of the face; and a body is swept straight up there from
        /// where it rests to the height it is dragged across at (<see cref="BodyRises"/>) - the column it
        /// falls down or jumps up. The lower end is also tried from <see cref="ClearOfFace"/>, as at a unit
        /// or six in the body stands in the face rising from the shared edge. The climb is in reach
        /// measured on the floor as well as on the areas, and the fall and its lip are
        /// <see cref="TestCrossing"/>'s, as everywhere else. Without the foot, drops and climbs at the ends
        /// added 84 drops and 3 climbs on the twelve maps, and a hull checker flagged 22 of the drops, the
        /// shape no other test here sees: pl_upward has a 33 unit drop off a slope whose lip overhangs the
        /// floor below by 12 units, linked at the corner, where a body landing beside it is in the lip.
        /// With it, that drop is not linked - the one beside it, which lands clear of the lip, is - and
        /// the finished meshes gain 30 drops and 16 climbs a hull checker can test, and lose no link. It
        /// flags 4 of the drops and 5 of the climbs, against 30% and 32% of those kept; 4 of the 9 are one
        /// 40 unit drop and its climb back, on both RP maps, whose landing clears the wall's face by the
        /// unit the checker's own insets step over.
        ///
        /// <paramref name="stood"/> says whether a body stood at both ends of a level crossing tried here,
        /// for <see cref="DropStale"/>'s verdict. Not of a drop or a climb: counted, it turned links the
        /// pass leaves alone for want of room into links a body stood for and could not cross, and on
        /// ctf_2fort, cp_badlands, gm_construct and pl_upward the pass removed 28 more, 21 of them still
        /// missing from the finished meshes; a hull checker gets a body across 17 of the 20 it can test,
        /// 16 at the middle of the span. A drop or a climb that gets across here still keeps a link.
        /// </summary>
        private static bool CrossesAtEnds(BspVisibility vis, NavArea area, NavArea other,
            NavGeometry.Bounds bounds, NavGeometry.Bounds otherBounds, int direction,
            float spanLow, float spanHigh, float[] depths, out float climb, out bool stood)
        {
            climb = 0;
            stood = false;
            float depth = Depth(bounds, direction), otherDepth = Depth(otherBounds, direction);

            for (int end = 0; end < 2; end++)
            {
                float c = end == 0 ? spanLow : spanHigh;
                float outward = end == 0 ? -1f : 1f;

                // Where the two areas no longer face each other across this span (DropStale tries the span
                // they shared before the clip), an end one of them has since been pulled back from is not
                // on it, and there is no corner of it to stand at.
                if (!WithinAlong(bounds, direction, c) || !WithinAlong(otherBounds, direction, c))
                    continue;

                // The depths given, then ClearOfFace, which is tried only at the lower end of a drop or a
                // climb: see the summary.
                for (int i = 0; i <= depths.Length; i++)
                {
                    float into = i < depths.Length ? depths[i] : ClearOfFace;

                    if (into > 1f && into > depth - 1f)
                        continue;

                    for (int j = 0; j <= depths.Length; j++)
                    {
                        float intoOther = j < depths.Length ? depths[j] : ClearOfFace;

                        if (intoOther > 1f && intoOther > otherDepth - 1f)
                            continue;

                        var (fromX, fromY) = EdgePoint(bounds, direction, c, -InsetWithin(into, depth));
                        var (toX, toY) = EdgePoint(otherBounds, NavGeometry.Opposite(direction), c,
                            -InsetWithin(intoOther, otherDepth));
                        float fromZ = NavGeometry.SurfaceZ(area, fromX, fromY);
                        float toZ = NavGeometry.SurfaceZ(other, toX, toY);
                        float fromGround = GroundAt(vis, fromX, fromY, fromZ);
                        float toGround = GroundAt(vis, toX, toY, toZ);
                        float rise = toGround - fromGround;
                        bool level = MathF.Abs(rise) <= NavConstants.StepHeight;

                        // ClearOfFace at the lower end of a drop or a climb only: the end being left on a
                        // climb, the landing on a drop.
                        if (i == depths.Length && (level || rise < 0f) ||
                            j == depths.Length && (level || rise > 0f))
                        {
                            continue;
                        }

                        // A drop or a climb within the builder's reach, measured on the areas as everywhere
                        // else and on the floor under the corner as well.
                        if (!level && (!Reachable(toZ - fromZ) || !Reachable(rise)))
                            continue;

                        // Level, floor beside both ends and the middle; a drop or a climb, beside the
                        // higher end, whose floor is the one the body is walking along.
                        if (!OverhangSupported(vis, direction, outward, fromX, fromY, fromGround, toX, toY, toGround,
                                level ? -1 : rise < 0f ? 0 : 2))
                        {
                            continue;
                        }

                        if (!BodyStands(vis, fromX, fromY, fromGround) || !BodyStands(vis, toX, toY, toGround))
                            continue;

                        // Only on the level: see the summary.
                        stood |= level;

                        // A body falls straight down beside the corner to where it lands, or jumps straight
                        // up from where it starts, onto the height TestCrossing drags it across at.
                        if (!level)
                        {
                            bool down = rise < 0f;
                            float footX = down ? toX : fromX, footY = down ? toY : fromY;
                            float high = MathF.Max(MathF.Max(fromZ, toZ), MathF.Max(fromGround, toGround));

                            if (!BodyRises(vis, footX, footY, Rest(vis, footX, footY, down ? toGround : fromGround), high))
                                continue;
                        }

                        if (TestCrossing(vis, area, other, fromX, fromY, fromZ, toX, toY, toZ, out _,
                                sweepBody: true) == Refusal.Clear)
                        {
                            climb = toZ - fromZ;
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// How deep into the lower area the lower end of a drop or a climb at the end of a span is also tried
        /// from (<see cref="CrossesAtEnds"/>): a body's half-width and a unit, the nearest a body standing on
        /// the lower floor gets to a face that rises from the shared edge.
        /// </summary>
        private const float ClearOfFace = NavConstants.HalfHumanWidth + SpanEnd;

        /// <summary>
        /// Whether a crouching body, the full 32 units wide, goes straight up from resting on the floor at
        /// <paramref name="low"/> to <paramref name="high"/>, the height <see cref="TestCrossing"/> drags it
        /// across at, without meeting anything: the column a body falls down at the foot of a drop at the end
        /// of a span, or jumps up from at the foot of a climb (<see cref="CrossesAtEnds"/>). Lifted half a
        /// step as <see cref="BodyCrosses(BspVisibility, float, float, float, float, float)"/> is, and refused
        /// if it starts inside something.
        /// </summary>
        internal static bool BodyRises(BspVisibility vis, float x, float y, float low, float high)
        {
            const float Lift = NavConstants.StepHeight / 2f;
            const float Half = NavConstants.HalfHumanWidth;

            bool hit = vis.TryTraceHull(
                new BspFile.Vector3(x, y, low + Lift),
                new BspFile.Vector3(x, y, MathF.Max(high, low) + Lift),
                new BspFile.Vector3(-Half, -Half, 0f),
                new BspFile.Vector3(Half, Half, NavConstants.HumanCrouchHeight - Lift),
                BspVisibility.GenerationMask, out float fraction, out _, out bool startSolid);

            return !startSolid && (!hit || fraction >= 1f);
        }

        /// <summary>
        /// Whether a crouching body stands at one end of a crossing at the end of a span
        /// (<see cref="CrossesAtEnds"/>): <see cref="BodyFits"/>, from the height a body resting on the floor
        /// there comes to rest at rather than from the floor under its middle.
        ///
        /// <see cref="TestCrossing"/> sweeps the body across at the higher floor and asks only for a line's
        /// headroom at each end. At a corner half the body is over neither area, on whatever the third one
        /// is - a kerb, a stair tread, a slope - and a body rests on the highest of it, up to a step above
        /// the floor under its middle, so that is the height it has to fit from.
        /// </summary>
        internal static bool BodyStands(BspVisibility vis, float x, float y, float ground)
            => BodyFits(vis, x, y, Rest(vis, x, y, ground));

        /// <summary>
        /// The height a body resting on the floor at a point comes to rest at: the highest floor under any of
        /// it up to a step above <paramref name="ground"/>, or <paramref name="ground"/> itself if the plate
        /// lowered onto it starts inside something or finds nothing (<see cref="BodyStands"/>).
        /// </summary>
        private static float Rest(BspVisibility vis, float x, float y, float ground)
        {
            const float Half = NavConstants.HalfHumanWidth;
            const float Drop = NavConstants.StepHeight + 1f;

            bool hit = vis.TryTraceHull(
                new BspFile.Vector3(x, y, ground + NavConstants.StepHeight),
                new BspFile.Vector3(x, y, ground - 1f),
                new BspFile.Vector3(-Half, -Half, 0f), new BspFile.Vector3(Half, Half, 0f),
                BspVisibility.GroundMask, out float fraction, out _, out bool startSolid);

            float rest = !startSolid && hit ? ground + NavConstants.StepHeight - fraction * Drop : ground;
            return MathF.Max(rest, ground);
        }

        /// <summary>Whether a position along an edge lies on an area's own extent along it.</summary>
        private static bool WithinAlong(NavGeometry.Bounds b, int direction, float c)
        {
            bool alongX = direction is NavGeometry.North or NavGeometry.South;
            return c >= (alongX ? b.MinX : b.MinY) && c <= (alongX ? b.MaxX : b.MaxY);
        }

        /// <summary>Where past a span's end the floor under a body's overhanging half is probed.</summary>
        private static readonly float[] OverhangProbes =
            [NavConstants.HalfHumanWidth / 2f, NavConstants.HalfHumanWidth - 1f];

        /// <summary>
        /// Whether there is floor under the half of a body that hangs past the end of a shared span, for a
        /// crossing at that end (<see cref="CrossesAtEnds"/>): probed halfway out and at the body's edge,
        /// beside both ends of the crossing and its middle. Beside each end the probe has to find floor
        /// within a step of that end's own ground, and between them within a step of either. That is the
        /// third area the two both touch, or the floor it stands on; a ledge's corner over a drop, or a
        /// wall, is not. A probe that starts inside something finds no floor, so a wall the span ends
        /// against cannot pass for one.
        ///
        /// <paramref name="only"/>, 0 or 2, probes beside only the crossing's start or only its end: the
        /// higher end of a drop or a climb, whose floor the body walks along at the corner.
        /// </summary>
        internal static bool OverhangSupported(BspVisibility vis, int direction, float outward,
            float fromX, float fromY, float fromGround, float toX, float toY, float toGround, int only = -1)
        {
            bool alongX = direction is NavGeometry.North or NavGeometry.South;
            float low = MathF.Min(fromGround, toGround), high = MathF.Max(fromGround, toGround);
            float top = high + NavConstants.StepHeight;

            foreach (float past in OverhangProbes)
            {
                for (int k = 0; k <= 2; k++)
                {
                    if (only >= 0 && k != only)
                        continue;

                    float x = fromX + (toX - fromX) * k / 2f, y = fromY + (toY - fromY) * k / 2f;
                    if (alongX) x += outward * past; else y += outward * past;

                    if (vis.IsPointSolid(x, y, top, BspVisibility.GroundMask))
                        return false;

                    if (!StairMarker.TryFindFloor(vis, x, y, top, high - low + 2f * NavConstants.StepHeight,
                            out float floor))
                    {
                        return false;
                    }

                    // Beside each end, that end's own floor: the corner of a ledge, with the drop it
                    // overlooks a long way below, is not floor under the half of a body standing on it.
                    // Between them, either.
                    bool nearFrom = MathF.Abs(floor - fromGround) <= NavConstants.StepHeight;
                    bool nearTo = MathF.Abs(floor - toGround) <= NavConstants.StepHeight;

                    if (k == 0 ? !nearFrom : k == 2 ? !nearTo : !nearFrom && !nearTo)
                        return false;
                }
            }

            return true;
        }

        /// <summary>
        /// How close to either end of the shared span a crossing is tried: a unit in, so the trace
        /// endpoints sit on both areas rather than on the corner where their edges stop.
        /// </summary>
        private const float SpanEnd = 1f;

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
        ///
        /// <paramref name="sweepBody"/> also drags a crouching body across, for a crossing tried off
        /// the shared span's middle with half the body hanging past its end (see
        /// <see cref="CrossesElsewhere"/>).
        ///
        /// Where the lines or that sweep refuse a crossing between floors within a step of each other,
        /// a body stepping over a thin obstacle no higher than a step is tried before it is refused
        /// (<see cref="StepsOver"/>).
        /// </summary>
        private static Refusal TestCrossing(BspVisibility vis, NavArea from, NavArea to,
            float fromX, float fromY, float fromZ, float toX, float toY, float toZ,
            out bool startedSolid, bool sweepBody = false)
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

            bool clear = LinesClear(vis, fromX, fromY, toX, toY, high, sideX, sideY, sideFloors: false) ||
                         LinesClear(vis, fromX, fromY, toX, toY, high, sideX, sideY, sideFloors: true);

            if (clear && sweepBody)
                clear = BodyCrosses(vis, fromX, fromY, toX, toY, high);

            // Failing that, over a thin obstacle no higher than a step, which the lowest line runs into
            // and a walker steps onto and off again (StepsOver). Its own test is a body swept across,
            // so it stands in for the sweep above as well.
            if (!clear && !StepsOver(vis, fromX, fromY, fromGround, toX, toY, toGround))
                return Refusal.Crossing;

            // Both evaluated, not short-circuited: either end being buried is worth recording, and the
            // two traces are cheap next to the ones already done above.
            // Stepping back from the edge if the edge itself is pinched. The probe sits six units
            // inside the boundary, and at the foot of a ledge that is under whatever the ledge is
            // capped with: measured on rp_downtown_meowy, a 40 unit ledge had a kerb at its base and a
            // coping stone overhanging at 36, leaving a 30 unit slot that no 55 unit body fits in.
            // Twenty units back the same column is clear for 128 units, which is where the jump is
            // actually made from.
            //
            // This is asking the question the test was always meant to ask - whether the walker can be
            // at that end of the crossing - rather than whether it can stand in the one spot hard
            // against the obstacle. The direction to retreat comes from the crossing itself: each end
            // backs away from the other.
            float backX = 0f, backY = 0f;
            if (length > 0.01f) { backX = dx / length; backY = dy / length; }

            bool fromClear = HasRoomNear(vis, from, fromX, fromY, fromZ, -backX, -backY, out bool fromBuried);
            bool toClear = HasRoomNear(vis, to, toX, toY, toZ, backX, backY, out bool toBuried);

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
                !CanFallTo(vis, to, fromX, fromY, fromZ, toX, toY, toZ, high))
            {
                return Refusal.Fall;
            }

            return Refusal.Clear;
        }

        /// <summary>
        /// The clearance lines: three columns a body's width apart, at each of <see cref="ClearanceHeights"/>.
        ///
        /// With <paramref name="sideFloors"/> each outer column is measured from the floor under its own
        /// two ends instead of from <paramref name="high"/>, where that floor is higher - by no more than
        /// a step. It is the fallback when the plain lines fail, so it only ever adds connections.
        ///
        /// A level plane through the middle of the crossing is the wrong base wherever the ground falls
        /// away across it. A walker standing on a slope rests on its uphill side, so the outer column
        /// sixteen units up the slope is over ground well above the middle - on a 45 degree slope by the
        /// full sixteen - and the lowest line, eight units over the middle, runs straight into it. That
        /// was 2,698 of the 2,869 crossings on the finished rp_downtown_tits_v25 mesh that were refused
        /// although a hull sweep passes them at the middle - 2,581 over displacement terrain, 117 over
        /// sloped brushes - and 2,692 of 2,883 on rp_downtown_meowy. Every one was a single outer line
        /// at the lowest height and nothing else, and every one is clear with the outer columns lifted
        /// onto their own ground.
        ///
        /// No more than a step, and never measured from inside something, so a wall or a ledge higher
        /// than a walker can step onto is not a floor that lifts the line over itself. Only the outer
        /// columns, because the middle one is already measured from the floor under it.
        /// </summary>
        internal static bool LinesClear(BspVisibility vis, float fromX, float fromY, float toX, float toY,
            float high, float sideX, float sideY, bool sideFloors)
        {
            Span<float> bases = [high, high, high];

            if (sideFloors)
            {
                bool lifted = false;

                for (int side = -1; side <= 1; side += 2)
                {
                    float ox = sideX * side, oy = sideY * side;
                    float floor = MathF.Max(SideFloor(vis, fromX + ox, fromY + oy, high),
                        SideFloor(vis, toX + ox, toY + oy, high));

                    bases[side + 1] = floor;
                    lifted |= floor > high;
                }

                // Nothing to lift, so the same nine lines as the plain test, which has already failed.
                if (!lifted)
                    return false;
            }

            foreach (float height in ClearanceHeights)
            {
                for (int side = -1; side <= 1; side++)
                {
                    // No width, so the outer columns are the middle one again.
                    if (side != 0 && sideX == 0f && sideY == 0f)
                        continue;

                    float ox = sideX * side, oy = sideY * side;
                    float z = bases[side + 1] + height;

                    var a = new BspFile.Vector3(fromX + ox, fromY + oy, z);
                    var b = new BspFile.Vector3(toX + ox, toY + oy, z);

                    // Whether a body fits through the gap, not whether a bot can see through it -
                    // GenerationMask throughout, matching what Valve traces movement against.
                    //
                    // A line that starts inside a prop is refused as one starting in a brush is. The
                    // trace alone cannot say so: lying wholly inside the prop it crosses no triangle
                    // and reads clear, which passed crossings straight through a prop both ends of it
                    // stood in - 615 such lines on cp_badlands, 3,446 on rp_downtown_tits_v25.
                    if (vis.IsInsideProp(a, BspVisibility.GenerationMask) ||
                        !vis.IsLineClear(a, b, BspVisibility.GenerationMask))
                        return false;
                }
            }

            return true;
        }

        /// <summary>
        /// The floor under an outer clearance column, if it is above <paramref name="high"/> by no more
        /// than a step; otherwise <paramref name="high"/>. Searched down from a step up, and not at all
        /// when that point is inside something - a floor found from in there would be the floor under
        /// a wall, and lifting a line onto it would carry the line over the wall.
        /// </summary>
        internal static float SideFloor(BspVisibility vis, float x, float y, float high)
        {
            float top = high + NavConstants.StepHeight;

            if (vis.IsPointSolid(x, y, top, BspVisibility.GroundMask))
                return high;

            return StairMarker.TryFindFloor(vis, x, y, top, NavConstants.StepHeight, out float floor)
                ? MathF.Max(high, floor)
                : high;
        }

        /// <summary>How far back from a pinched edge to look for somewhere the walker could stand.</summary>
        private static readonly float[] SetBack = [16f, 32f];

        /// <summary>
        /// <see cref="HasRoom"/> at the edge, and failing that a little way back into the area.
        ///
        /// Only ever adds connections: the edge point is tried first and unchanged, so anything that
        /// passed before still passes. What it stops is a ledge being unreachable because the strip of
        /// ground at its foot is roofed by the ledge's own overhang.
        ///
        /// Bounded by the area's own footprint. Retreating past that would be answering for ground this
        /// crossing is not about, and on a narrow area there is nowhere to retreat to, so the edge
        /// answer stands.
        /// </summary>
        private static bool HasRoomNear(BspVisibility vis, NavArea area, float x, float y, float z,
            float backX, float backY, out bool startedSolid)
        {
            if (HasRoom(vis, x, y, z, out startedSolid))
                return true;

            var b = NavGeometry.GetBounds(area);
            float lowX = MathF.Min(b.MinX + Inset, b.MaxX), highX = MathF.Max(b.MaxX - Inset, b.MinX);
            float lowY = MathF.Min(b.MinY + Inset, b.MaxY), highY = MathF.Max(b.MaxY - Inset, b.MinY);

            foreach (float back in SetBack)
            {
                float bx = Math.Clamp(x + backX * back, MathF.Min(lowX, highX), MathF.Max(lowX, highX));
                float by = Math.Clamp(y + backY * back, MathF.Min(lowY, highY), MathF.Max(lowY, highY));

                // Clamped back onto the point we already tried, so the area has no room to give.
                if (MathF.Abs(bx - x) < 1f && MathF.Abs(by - y) < 1f)
                    break;

                if (HasRoom(vis, bx, by, GroundAt(vis, bx, by, NavGeometry.SurfaceZ(area, bx, by)), out _))
                {
                    startedSolid = false;
                    return true;
                }
            }

            return false;
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
        ///
        /// A search starting inside a brush a step over the claim answers with its own start, and one
        /// inside a prop with the floor under the prop (see <see cref="StairMarker.TryFindFloor"/>).
        /// Both are left to stand: every caller tests for a body at the ground it is given, and a body
        /// there starts in solid.
        /// </summary>
        internal static float GroundAt(BspVisibility vis, float x, float y, float claimed)
            => StairMarker.TryFindFloor(vis, x, y, claimed + NavConstants.StepHeight,
                NavConstants.StepHeight * 2f, out float ground)
                ? ground
                : claimed;

        internal static bool HasRoom(BspVisibility vis, float x, float y, float z, out bool startedSolid)
        {
            var foot = new BspFile.Vector3(x, y, z + 4f);

            // A point-in-solid test separates "there is a ceiling here" from "this point is inside the
            // floor" - and only the first is about the world. The second means the area's own surface
            // is wrong here. Asked of IsPointSolid rather than of a zero-length line, which never
            // crosses a prop's triangles: a foot inside a prop taller than a crouch read as standing
            // in the open, with the line up from it lying wholly inside the prop and so clear.
            startedSolid = vis.IsPointSolid(foot.X, foot.Y, foot.Z, BspVisibility.GenerationMask);

            return !startedSolid && vis.IsLineClear(foot,
                new BspFile.Vector3(x, y, z + NavConstants.HumanCrouchHeight),
                BspVisibility.GenerationMask);
        }

        /// <summary>
        /// Whether a crouching body, the full 32 units wide, can be dragged straight across at the
        /// height of the higher surface.
        ///
        /// The clearance lines are three columns 16 units apart at three heights, and anything that
        /// slips between them - a post, a beam over the 50 unit line - reads as clear. Across the
        /// middle of a shared span the lines are all that has ever been asked, with the body over the
        /// two areas' own ground. Off the end of the span half the body is over ground that belongs to
        /// neither area, so that space gets a body swept through it as well.
        ///
        /// Lifted half a step, as <see cref="Traversability.CanStep"/> and the crouch sampler are:
        /// anything lower is stepped over.
        ///
        /// A body that starts inside something is refused whether or not the sweep reports a hit. A box
        /// that begins in a brush and is dragged out of it never enters one, so the trace finds nothing
        /// to report and says so - with only its start-solid flag left to tell. Taking "nothing found"
        /// as clear let the body begin in a wall whenever the crossing led away from it, which is the
        /// usual shape off the end of a span: the area being left is a sliver against a wall or in a
        /// corner, too narrow for a body anywhere, and the crossing runs out of it into open ground.
        /// On the twelve benchmark maps that was 1,089 of the 11,092 links made with the body hanging
        /// past the end of a span. For 850 of them no body fits anywhere in the area being left within
        /// 64 units of the edge, and they were nearly all one-way - 743 of the 787 left in the finished
        /// meshes had no link back - because the crossing back runs into the wall and is caught.
        /// </summary>
        private static bool BodyCrosses(BspVisibility vis, float fromX, float fromY, float toX, float toY,
            float high)
        {
            const float Lift = NavConstants.StepHeight / 2f;
            const float Half = NavConstants.HalfHumanWidth;

            bool hit = vis.TryTraceHull(
                new BspFile.Vector3(fromX, fromY, high + Lift),
                new BspFile.Vector3(toX, toY, high + Lift),
                new BspFile.Vector3(-Half, -Half, 0f),
                new BspFile.Vector3(Half, Half, NavConstants.HumanCrouchHeight - Lift),
                BspVisibility.GenerationMask, out float fraction, out _, out bool startSolid);

            return !startSolid && (!hit || fraction >= 1f);
        }

        /// <summary>
        /// How deep, along the crossing, an obstacle <see cref="StepsOver"/> steps over may be: a body's
        /// width. Anything deeper holds a whole body on its top, which makes it floor rather than something
        /// in the way - the generator puts an area on it, and the builder links up onto it and down off it
        /// by ordinary steps. A link straight over it would skip that area, or, where the generator left
        /// it bare, claim a stride over ground nothing vouches for.
        /// </summary>
        private const float MaxStepOverDepth = NavConstants.HumanWidth;

        /// <summary>
        /// Whether a walker gets across by stepping over a thin obstacle between two floors - a bar, a
        /// raised threshold, a pipe, a low rail - where the clearance lines are refused because the
        /// lowest of them, eight units over the higher floor, runs into it.
        ///
        /// A player crosses anything up to <c>StepHeight</c> by stepping onto it and off again, and the
        /// lines alone read a bar ten to eighteen units high as a wall: the floors either side were never
        /// linked. Asked only once the lines have refused, so it only ever adds connections, and only
        /// where all of this holds:
        ///
        /// - Both floors are real, not a surface found from inside something (a unit above each is
        ///   open), and they are within a step of each other.
        /// - What the body passes over tops out within a step of both floors: a plate the size of the
        ///   body's footprint over the whole crossing, lowered from a step over the lower floor, starts in
        ///   nothing and lands on it. Its top has to stand above the higher floor - otherwise there is no
        ///   obstacle to step over, and the lines refused for some other reason.
        /// - The obstacle is in the crossing and thin (<see cref="MaxStepOverDepth"/>): a plate the body's
        ///   width, swept along the crossing from each end halfway up the obstacle, finds its near and far
        ///   faces no further apart than that.
        /// - A crouching body raised onto the obstacle's top - a unit clear of it, with a crouching body's
        ///   headroom above it - is swept across clear, start-solid aware as everywhere else, so a ceiling
        ///   too low over the bar, a post beside it or a prop it would start inside still refuse it.
        ///
        /// Both floors within a step of the top, not only the one being left, so the link is one a walker
        /// makes both ways: a wall twelve units above a pavement and thirty above the road beside it is a
        /// step up and a drop one way and a jump the other, and is left unlinked. So is anything taller
        /// than a step that a jump clears: Valve's TF2 meshes and the engine's gm_construct mesh link 2 of
        /// the 185 such crossings the builder refuses on those maps, and mark no area as a jump.
        ///
        /// On the twelve benchmark maps it links five sites on each RP map - a 13.5 unit rail on a prop
        /// floor, a garden wall 14 to 18 units over the terrain either side, a prop's foot grazing the
        /// body's side - one on ctf_2fort and four on pl_upward, and a hull checker flags none of the links.
        /// Where Valve's TF2 meshes or the engine's gm_construct mesh have ground at both ends of a pair the
        /// builder refused and a step move crosses, they join all nine, two directly and the rest through an
        /// area spanning the obstacle or standing on it.
        /// </summary>
        internal static bool StepsOver(BspVisibility vis, float fromX, float fromY, float fromGround,
            float toX, float toY, float toGround)
        {
            const float Half = NavConstants.HalfHumanWidth;
            const int Mask = BspVisibility.GenerationMask;

            float low = MathF.Min(fromGround, toGround), high = MathF.Max(fromGround, toGround);
            if (high - low > NavConstants.StepHeight)
                return false;

            float dx = toX - fromX, dy = toY - fromY;
            float length = MathF.Sqrt(dx * dx + dy * dy);
            if (length <= 0.01f)
                return false;

            // A floor found from inside a brush is reported where the search started, and one found from
            // inside a prop is the prop's underside (GroundAt): either has solid just above it.
            if (vis.IsPointSolid(fromX, fromY, fromGround + 1f, Mask) || vis.IsPointSolid(toX, toY, toGround + 1f, Mask))
                return false;

            // Nothing taller than a step over the lower floor may stand in the way. A line through the middle
            // at that height asks first, as it is cheap and turns away every wall, which is most refusals.
            float ceiling = low + NavConstants.StepHeight;
            if (!vis.IsLineClear(new BspFile.Vector3(fromX, fromY, ceiling), new BspFile.Vector3(toX, toY, ceiling), Mask))
                return false;

            // The top of what the body passes over: a plate covering its footprint along the whole crossing,
            // lowered from there.
            float midX = (fromX + toX) / 2f, midY = (fromY + toY) / 2f;
            float halfX = MathF.Abs(dx) / 2f + Half, halfY = MathF.Abs(dy) / 2f + Half;
            float drop = ceiling - (low - 1f);

            bool landed = vis.TryTraceHull(
                new BspFile.Vector3(midX, midY, ceiling), new BspFile.Vector3(midX, midY, low - 1f),
                new BspFile.Vector3(-halfX, -halfY, 0f), new BspFile.Vector3(halfX, halfY, 0f),
                Mask, out float fraction, out _, out bool startSolid);

            if (startSolid || !landed)
                return false;

            float top = ceiling - fraction * drop;
            if (top <= high + 1f)
                return false;

            // Its depth along the crossing, halfway up it: a plate the body's width, edge on to the crossing,
            // swept from each end until it meets a face.
            float across = Half - 0.5f;
            var plateMin = new BspFile.Vector3(-MathF.Abs(dy) / length * across, -MathF.Abs(dx) / length * across, 0f);
            var plateMax = new BspFile.Vector3(-plateMin.X, -plateMin.Y, 0f);
            float waist = (high + top) / 2f;
            var near = new BspFile.Vector3(fromX, fromY, waist);
            var far = new BspFile.Vector3(toX, toY, waist);

            if (!vis.TryTraceHull(near, far, plateMin, plateMax, Mask, out float toFace, out _, out bool nearSolid) || nearSolid ||
                !vis.TryTraceHull(far, near, plateMin, plateMax, Mask, out float backFace, out _, out bool farSolid) || farSolid)
            {
                return false;
            }

            if (length * (1f - toFace - backFace) > MaxStepOverDepth)
                return false;

            // A crouching body raised onto the top, a unit clear of it, crosses.
            bool hit = vis.TryTraceHull(
                new BspFile.Vector3(fromX, fromY, top + 1f), new BspFile.Vector3(toX, toY, top + 1f),
                new BspFile.Vector3(-Half, -Half, 0f), new BspFile.Vector3(Half, Half, NavConstants.HumanCrouchHeight - 1f),
                Mask, out float bodyFraction, out _, out bool bodySolid);

            return !bodySolid && (!hit || bodyFraction >= 1f);
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

            // A search starting inside a brush reports floor where it starts, and one inside a prop the
            // floor under it; both pass. That is the right reading here: solid between the two areas is
            // not a chasm, and whether a body gets past it is the clearance lines' question.
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
        /// Starts just past where the floor being stepped off ends (<see cref="Lip"/>) rather than at the
        /// inset point itself, which sits <see cref="Inset"/> units inside that area and therefore
        /// directly on top of its own floor - a column there would begin inside the slab being stepped
        /// off and refuse every drop on the map. The lateral offsets run parallel to the shared edge, so
        /// all three columns in a sample stand the same distance past it.
        /// </summary>
        internal static bool CanFallTo(BspVisibility vis, NavArea to,
            float fromX, float fromY, float fromZ, float toX, float toY, float toZ, float high)
        {
            float dx = toX - fromX, dy = toY - fromY;
            float run = MathF.Sqrt(dx * dx + dy * dy);

            if (run <= 0.01f)
                return false;

            float sideX = -dy / run * NavConstants.HalfHumanWidth;
            float sideY = dx / run * NavConstants.HalfHumanWidth;

            // Where along the run the first sample sits: clear of the lip being stepped off.
            //
            // If there is no room for it - the landing point is no further out than the lip - the
            // landing is underneath the floor above rather than beyond it, and a walker cannot step off
            // an edge it has not passed. The landing then moves out past the lip, if the lower area
            // reaches that far (<see cref="LandingPastLip"/>); otherwise there is no fall to make.
            //
            // This used to clamp instead, at which point every sample in the loop below collapsed onto
            // the landing point and the sweep became the single column in the wrong place that the
            // comment above says it exists to replace. Clamping is the wrong answer to "the run is too
            // short to sample": the run being too short *is* the refusal.
            float reach = LandingReach;
            float lip = Lip(vis, fromX, fromY, fromZ, dx, dy, run, run + reach);

            if (run <= lip + 2f)
            {
                if (!LandingPastLip(vis, to, fromX, fromY, fromZ, toX, toY, dx / run, dy / run, lip, run + reach,
                        high, sideX, sideY, out float landing, out toZ))
                {
                    return false;
                }

                dx = dx / run * landing;
                dy = dy / run * landing;
                run = landing;
            }

            float first = (lip + 2f) / run;

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

                    // Starting inside a brush - the slab of a floor that carries on - is blocked, and
                    // is meant to be. Starting inside a prop has to be asked: a column lying wholly in
                    // one crosses nothing and read as open air to fall through.
                    if (!vis.IsInsideProp(top, BspVisibility.GenerationMask) &&
                        vis.IsLineClear(top, bottom, BspVisibility.GenerationMask))
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
        /// How far along the run, from the inset start point, the floor being stepped off actually
        /// ends: the first point, from the upper area's own edge onward, with no floor within a step
        /// below the start, or <paramref name="limit"/> if the floor carries on that far.
        ///
        /// The fall used to be tested from two units past the upper area's edge, which assumes the
        /// floor ends where the area does. At a ledge it often does not: areas are built on a sampling
        /// grid and stop at the last sample, while the floor runs on to the lip, so the first column
        /// started inside the ledge, read as blocked, and the drop was refused. gm_flatgrass has these
        /// all round its pit, where the floor runs six and a half units past the areas along the rim,
        /// and none of the drops from them into the pit were linked. On the finished
        /// rp_downtown_tits_v25 mesh 330 of the 715 drops refused although a hull sweep passes them at
        /// the middle were the same shape.
        ///
        /// The floor carrying on all the way to the landing point is the slab the fall test exists to
        /// catch - the landing is underneath it - and leaves no fall to test.
        /// </summary>
        internal static float Lip(BspVisibility vis, float fromX, float fromY, float fromZ,
            float dx, float dy, float run, float limit)
        {
            float ux = dx / run, uy = dy / run;

            // Counted in steps rather than accumulated, so the probes land on the same points
            // whatever the run.
            for (int k = 0; Inset + k * LipStep <= limit; k++)
            {
                float along = Inset + k * LipStep;
                float x = fromX + ux * along, y = fromY + uy * along;

                // Inside a prop is floor carrying on, as inside a brush is: the probe would otherwise
                // cross nothing and call it the lip.
                var probe = new BspFile.Vector3(x, y, fromZ + 2f);

                if (!vis.IsInsideProp(probe, BspVisibility.GroundMask) &&
                    vis.IsLineClear(probe, new BspFile.Vector3(x, y, fromZ - NavConstants.StepHeight),
                        BspVisibility.GroundMask))
                {
                    return along;
                }
            }

            return limit;
        }

        /// <summary>
        /// Where a walker stepping off the lip lands, when the crossing's own landing point is not past
        /// it: a little way past the lip instead, as long as that is still inside the lower area, still
        /// below the floor being left, and has room for a body - and as long as the walk out to the lip
        /// is as clear as the crossing itself, which the clearance lines tested only as far as the
        /// original landing point.
        ///
        /// The connection pass runs before areas are clipped back out of the geometry, so a lower area
        /// can run under the ledge above it or into the wall below its lip, and the landing point six
        /// units inside it is then underneath the ledge or buried in it. Round gm_flatgrass's pit the
        /// floor of the pit runs six and a half units into the wall under each ledge, and that, with the
        /// ledge's floor running on past the area along its rim, is what kept every drop into the pit
        /// unlinked. The clip pulls the lower area back afterwards, but nothing connects it again.
        ///
        /// Not past the lower area, and not past <paramref name="limit"/>: a lip further out than that
        /// is the floor of an upper storey over the lower area, not a ledge.
        /// </summary>
        /// <remarks>
        /// The limit was 32 units past the original landing at first, the furthest a crossing's end
        /// retreats for headroom. On gm_construct that linked 384 unit drops off the edge of a slab
        /// that ran 40 units on past the upper area with nothing meshed on it - real edges, but a walk
        /// across floor no area claims, which is not what this is for. Sixteen covers the ledges.
        /// </remarks>
        private static bool LandingPastLip(BspVisibility vis, NavArea to, float fromX, float fromY, float fromZ,
            float toX, float toY, float ux, float uy, float lip, float limit, float high, float sideX, float sideY,
            out float landing, out float landingZ)
        {
            landingZ = 0;
            landing = lip + 2f + Inset;

            if (landing > limit)
                return false;

            float x = fromX + ux * landing, y = fromY + uy * landing;
            var b = NavGeometry.GetBounds(to);

            if (x < b.MinX + SpanEnd || x > b.MaxX - SpanEnd || y < b.MinY + SpanEnd || y > b.MaxY - SpanEnd)
                return false;

            if (!StairMarker.TryFindFloor(vis, x, y, NavGeometry.SurfaceZ(to, x, y) + NavConstants.StepHeight,
                    NavConstants.StepHeight * 2f, out landingZ))
            {
                return false;
            }

            if (landingZ >= fromZ - NavConstants.StepHeight || !HasRoom(vis, x, y, landingZ, out _))
                return false;

            return LinesClear(vis, toX, toY, x, y, high, sideX, sideY, sideFloors: false);
        }

        /// <summary>
        /// How far past a crossing's own landing point the lip of a drop may lie and the walker still
        /// land past it, in the lower area (<see cref="LandingPastLip"/>).
        /// </summary>
        private const float LandingReach = 16f;

        /// <summary>How far apart the probes looking for the lip of a drop are.</summary>
        private const float LipStep = 2f;

        /// <summary>
        /// How many points along the fall's horizontal run are probed. Three: just past the edge being
        /// stepped off, the landing point, and the middle. Two would leave the span between them
        /// untested, which on a crossing that can be 46 units long is where a slab edge sits.
        /// </summary>
        private const int FallSamples = 3;
    }
}
