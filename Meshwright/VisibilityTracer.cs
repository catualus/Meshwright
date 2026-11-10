using System;
using System.Collections.Generic;
using System.Threading;

namespace Meshwright
{
    /// <summary>
    /// The final visibility stage: casts rays at the pairs <see cref="VisibilityFilter"/> could not
    /// reject cheaply, and records which areas actually see each other.
    ///
    /// The test mirrors <c>CNavArea::ComputeVisibilityToMesh</c>: a ray from the source area's centre to
    /// each of the target area's four corners, both ends raised by <see cref="VisibilityFilter.EyeHeight"/>,
    /// visible if any one of them is clear. That is deliberately **not** symmetric - a small area at the
    /// foot of a slope can have all four corners visible from a large area above while the large area's
    /// centre sees none of the small one's - so both directions are evaluated separately.
    ///
    /// Results are accumulated on the lower-indexed area of each pair, which is owned by exactly one
    /// thread for the duration of its row, so the hot path needs no locking. <see cref="Symmetrise"/>
    /// redistributes them afterwards.
    /// </summary>
    public sealed class VisibilityTracer : VisibilityFilter.ICandidateSink
    {
        private readonly VisibilityFilter filter;
        private readonly BspVisibility vis;

        /// <summary>For pair (i, j) with i &lt; j: j appears here when i can see j.</summary>
        private readonly List<int>[] forward;

        /// <summary>For pair (i, j) with i &lt; j: j appears here when j can see i.</summary>
        private readonly List<int>[] backward;

        private long raysCast;
        private long visibleLinks;

        /// <summary>
        /// Where the rays go, split by which of the three stages of <see cref="Sees"/> cast them, and
        /// what each stage bought.
        ///
        /// Worth counting because the stages are wildly different bets. The centre-to-centre ray is one
        /// trace that settles both directions at once; the corner sweep is sixteen traces that only run
        /// when everything cheaper has failed, which is exactly the case where they are least likely to
        /// find anything. Whether that last stage earns its place is not answerable by reasoning about
        /// it - only by counting how many links it actually contributes for the rays it spends.
        ///
        /// What the counting says, on rp_downtown_tits_v25 at 19,577 areas: the centre ray settles
        /// 10.7 million links for 37.5 million rays, and the sweep buys 1.5 million more for 498
        /// million - 63% of all the tracing in the pass. Nearly 32 million pairs reach it and about
        /// 97.6% of them find nothing, because the work is no longer finding visibility but *proving
        /// invisibility*, and there is no early exit from that.
        ///
        /// Valve's own fast accept was tried against this and made it markedly worse: sweeping a box
        /// the size of the source area to the nearest point of the target, ahead of everything else,
        /// cost 200 seconds against 126 and removed 0.5% of rays. It resolves pairs the centre ray was
        /// already resolving in a single cheap trace, at several times the price, and leaves the
        /// invisible pairs that dominate the cost entirely untouched. A fast *accept* cannot help a
        /// workload whose cost is failures; what this pass would need is a fast reject, and the PVS is
        /// already the only cheap one available.
        ///
        /// So the PVS is now asked about every line as well as every pair (<see
        /// cref="VisibilityFilter.MaySee"/>), and the lines it rules out are not traced: 3% of the
        /// sweep on the city, where the map's clusters are large, and 10-14% on cp_badlands and
        /// ctf_2fort. What was left of the cost on the city was mostly not the world tree at all:
        /// 60% of the sweep's lines got through it and were then stopped by displacement terrain,
        /// so each line now asks first about whatever stopped the pair's last one (<see
        /// cref="BspVisibility.IsSightClear"/>). Neither changes an answer, and together they took
        /// the pass from about 275 to 184 seconds of CPU on rp_downtown_tits_v25 at 16,922 areas,
        /// and 19.3 to 15.7 on cp_badlands.
        ///
        /// Tried and left out: walking the world tree once for a whole fan of lines from one corner,
        /// answering each as its own walk would (exact, but its bookkeeping made it 0-35% slower than
        /// the lines one at a time); and dropping the corner farthest from the other area from each
        /// side of the sweep, nine lines instead of sixteen, which saved a further fifth of the pass on
        /// the city but lost 0.7% of its links.
        /// </summary>
        private long raysCentre, raysCross, raysSweep;
        private long culledCentre, culledCross, culledSweep;
        private long pairsToCross, pairsToSweep;
        private long linksCentre, linksCross, linksSweep;

        public long RaysCentre => raysCentre;
        public long RaysCross => raysCross;
        public long RaysSweep => raysSweep;

        /// <summary>
        /// Lines each stage did not trace because <see cref="VisibilityFilter.MaySee"/> already knew
        /// the answer was no. Not counted in the stage's rays, which are the traces actually cast.
        /// </summary>
        public long CulledCentre => culledCentre;
        public long CulledCross => culledCross;
        public long CulledSweep => culledSweep;
        public long PairsToCross => pairsToCross;
        public long PairsToSweep => pairsToSweep;
        public long LinksCentre => linksCentre;
        public long LinksCross => linksCross;
        public long LinksSweep => linksSweep;

        public long RaysCast => raysCast;

        /// <summary>Directed links found - a mutually visible pair counts twice.</summary>
        public long VisibleLinks => visibleLinks;

        public VisibilityTracer(VisibilityFilter filter, BspVisibility visibility, int areaCount)
        {
            this.filter = filter;
            vis = visibility;

            forward = new List<int>[areaCount];
            backward = new List<int>[areaCount];
            for (int i = 0; i < areaCount; i++)
            {
                forward[i] = [];
                backward[i] = [];
            }
        }

        public void Candidates(int areaIndex, ReadOnlySpan<int> others)
        {
            var from = filter.SightPoints(areaIndex);
            var fromCluster = filter.SightClusters(areaIndex);
            var seen = forward[areaIndex];
            var seenBy = backward[areaIndex];

            long rays = 0;
            long links = 0;
            var phase = default(PhaseCounts);

            foreach (int other in others)
            {
                var to = filter.SightPoints(other);
                Sees(from, fromCluster, to, filter.SightClusters(other),
                    out bool fromSeesTo, out bool toSeesFrom, ref rays, ref phase);

                if (fromSeesTo) { seen.Add(other); links++; }
                if (toSeesFrom) { seenBy.Add(other); links++; }
            }

            Interlocked.Add(ref raysCast, rays);
            Interlocked.Add(ref visibleLinks, links);

            Interlocked.Add(ref raysCentre, phase.RaysCentre);
            Interlocked.Add(ref raysCross, phase.RaysCross);
            Interlocked.Add(ref raysSweep, phase.RaysSweep);
            Interlocked.Add(ref culledCentre, phase.CulledCentre);
            Interlocked.Add(ref culledCross, phase.CulledCross);
            Interlocked.Add(ref culledSweep, phase.CulledSweep);
            Interlocked.Add(ref pairsToCross, phase.PairsToCross);
            Interlocked.Add(ref pairsToSweep, phase.PairsToSweep);
            Interlocked.Add(ref linksCentre, phase.LinksCentre);
            Interlocked.Add(ref linksCross, phase.LinksCross);
            Interlocked.Add(ref linksSweep, phase.LinksSweep);
        }

        /// <summary>
        /// Whether each area's sample points reach the other's - both directions at once, because most
        /// of the twenty-one rays a single direction can cast are physically the same segment as one the
        /// other direction would also cast, and <see cref="BspVisibility.IsLineClear"/> gives the same
        /// answer regardless of which end it is called from.
        ///
        /// Centre to centre is the clearest case: <c>from[centre]-to[centre]</c> and
        /// <c>to[centre]-from[centre]</c> are the same line. Less obviously, so is the entire corner
        /// sweep - <c>from[s]-to[c]</c> for every corner pair is the same set of sixteen segments as
        /// <c>to[s]-from[c]</c>, just enumerated in the opposite order. Only the centre-to-corner rays
        /// are genuinely direction-specific: <c>from[centre]-to[corner]</c> is not the same segment as
        /// anything <c>to</c>'s own sweep casts, which is exactly the asymmetric case the class comment
        /// describes - a small area at the foot of a slope, visible from a large area's corners while
        /// invisible from its centre. Tracing the shared segments once instead of twice measured a 40%
        /// cut in worst-case rays per pair (42 down to 25) and roughly a third off wall-clock time on a
        /// large map (164s to 103s on rp_downtown_meowy's full mesh).
        ///
        /// Output is not quite bit-for-bit identical to tracing both directions independently - 14
        /// links differed out of 11.2 million on that same map. <c>IsLineClear(A, B)</c> and
        /// <c>IsLineClear(A, B)</c> called with the endpoints swapped are the same query mathematically
        /// but not always bit-identical, because the BSP split-point computation takes a different
        /// floating-point rounding path depending on which end is p1; a ray that grazes a plane within
        /// float epsilon can come out clear from one order and blocked from the other. Testing each
        /// physical segment from both ends, as the un-deduplicated version did, gave those razor-edge
        /// cases two independent chances to resolve as clear; testing once gives one. Neither answer is
        /// more correct than the other for a case that close, and 0.0001% of links on the largest map
        /// this has been measured against is well inside the margin every other heuristic in this
        /// pipeline already carries.
        ///
        /// Sight points are laid out corners first, centre last.
        /// </summary>

        /// <summary>Per-worker tallies of where the rays went, folded in once per row.</summary>
        private struct PhaseCounts
        {
            public long RaysCentre, RaysCross, RaysSweep;
            public long CulledCentre, CulledCross, CulledSweep;
            public long PairsToCross, PairsToSweep;
            public long LinksCentre, LinksCross, LinksSweep;
        }

        private void Sees(ReadOnlySpan<BspFile.Vector3> from, ReadOnlySpan<short> fromCluster,
            ReadOnlySpan<BspFile.Vector3> to, ReadOnlySpan<short> toCluster,
            out bool fromSeesTo, out bool toSeesFrom, ref long rays, ref PhaseCounts phase)
        {
            const int Corners = VisibilityFilter.SightPointsPerArea - 1;

            // What stopped the last line between these two areas, which is asked about the next one
            // first (BspVisibility.IsSightClear). Started afresh for every pair: carried on from the
            // last pair in the row, a displacement that stopped its corner lines was asked first about
            // the next pair's lines too, most of which the world stops, and on gm_construct - where a
            // displacement test costs a couple of world walks - that made the whole pass 19% slower
            // rather than 4% faster.
            var lead = BspVisibility.SightBlocker.World;

            // Every line below is first put to VisibilityFilter.MaySee, which rules out the ones the
            // PVS or a sight point in solid already says are blocked. That answer is the trace's own,
            // so a line it rules out is simply not traced.
            if (!filter.MaySee(fromCluster[Corners], toCluster[Corners]))
            {
                phase.CulledCentre++;
            }
            else
            {
                rays++;
                phase.RaysCentre++;

                if (vis.IsSightClear(from[Corners], to[Corners], ref lead))
                {
                    fromSeesTo = true;
                    toSeesFrom = true;
                    phase.LinksCentre += 2;
                    return;
                }
            }

            fromSeesTo = false;
            toSeesFrom = false;
            phase.PairsToCross++;

            for (int c = 0; c < Corners; c++)
            {
                if (!filter.MaySee(fromCluster[Corners], toCluster[c]))
                {
                    phase.CulledCross++;
                    continue;
                }

                rays++;
                phase.RaysCross++;

                if (vis.IsSightClear(from[Corners], to[c], ref lead))
                {
                    fromSeesTo = true;
                    phase.LinksCross++;
                    break;
                }
            }

            for (int c = 0; c < Corners; c++)
            {
                if (!filter.MaySee(toCluster[Corners], fromCluster[c]))
                {
                    phase.CulledCross++;
                    continue;
                }

                rays++;
                phase.RaysCross++;

                if (vis.IsSightClear(to[Corners], from[c], ref lead))
                {
                    toSeesFrom = true;
                    phase.LinksCross++;
                    break;
                }
            }

            if (fromSeesTo && toSeesFrom)
                return;

            phase.PairsToSweep++;

            // Valve's collinearity skip belongs here in principle and was tried: past 1000 units,
            // `nav_potentially_visible_dot_tolerance` (0.98) lets the engine drop a sample point whose
            // bearing from its area's centre is close enough to the centre-to-centre line to be tracing
            // the same one. Measured on rp_downtown_tits_v25 it removed 1.9% of rays and moved 3,798
            // links out of 13.7 million - not worth the divergence.
            //
            // The reason it pays for Valve and not here is structural. They walk a grid across the
            // *source* area at 25-unit steps, so plenty of their sample points genuinely sit behind the
            // centre on the line to the target. Four corners spread sideways instead, nearly
            // perpendicular to the view axis, and almost none of them are ever collinear with anything.
            // The skip is only worth having alongside the grid walk it was written for.
            for (int s = 0; s < Corners; s++)
            {
                for (int c = 0; c < Corners; c++)
                {
                    if (!filter.MaySee(fromCluster[s], toCluster[c]))
                    {
                        phase.CulledSweep++;
                        continue;
                    }

                    rays++;
                    phase.RaysSweep++;

                    if (vis.IsSightClear(from[s], to[c], ref lead))
                    {
                        // Counted as what the sweep actually added, not as two links: whichever
                        // direction an earlier stage had already established was not bought here.
                        if (!fromSeesTo) phase.LinksSweep++;
                        if (!toSeesFrom) phase.LinksSweep++;

                        fromSeesTo = true;
                        toSeesFrom = true;
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Expands the pair-local lists into per-area visible sets. A counting pass sizes each array
        /// exactly, so the fill pass neither locks nor reallocates.
        /// </summary>
        public int[][] Symmetrise()
        {
            int count = forward.Length;
            var degree = new int[count];

            for (int i = 0; i < count; i++)
            {
                degree[i] += forward[i].Count;
                foreach (int j in backward[i])
                    degree[j]++;
            }

            var result = new int[count][];
            for (int i = 0; i < count; i++)
                result[i] = new int[degree[i]];

            var next = new int[count];
            for (int i = 0; i < count; i++)
            {
                foreach (int j in forward[i])
                    result[i][next[i]++] = j;

                foreach (int j in backward[i])
                    result[j][next[j]++] = i;
            }

            return result;
        }
    }
}
