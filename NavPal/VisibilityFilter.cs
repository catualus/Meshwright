using System;
using System.Threading;
using System.Threading.Tasks;

namespace NavPal
{
    /// <summary>
    /// Reduces the area-pair set down to the pairs that actually need a ray traced.
    ///
    /// Staging matters here, and the order was chosen from measurement rather than intuition. The PVS
    /// prefilter alone culls only 9% of pairs on gm_construct and 21% on rp_downtown_meowy - clusters are
    /// coarse relative to nav areas (roughly three areas per cluster), so cluster visibility is a very
    /// loose bound. Distance runs first because it is a handful of flops per pair and, on a large map,
    /// removes far more: at Valve's own <c>nav_max_view_distance</c> most of the mesh is simply too far
    /// away to matter.
    ///
    /// Distance is measured between area bounding boxes, not centres, so a long area is never culled
    /// because its midpoint happens to be far off.
    /// </summary>
    public sealed class VisibilityFilter
    {
        /// <summary>
        /// Default cut-off, matching the engine's <c>nav_max_view_distance</c> as used when computing
        /// mesh visibility. Zero disables distance culling entirely.
        /// </summary>
        public const float DefaultMaxViewDistance = 6000f;

        /// <summary>Consumer of the surviving pairs. Called from several threads at once.</summary>
        public interface ICandidateSink
        {
            /// <summary>
            /// Candidate partners for one area, always with a higher index than <paramref name="areaIndex"/>.
            /// The span is scratch memory owned by the caller and is invalid once this returns.
            /// </summary>
            void Candidates(int areaIndex, ReadOnlySpan<int> others);
        }

        public sealed class Stats
        {
            public long TotalPairs;
            public long AfterDistance;
            public long AfterPvs;
            public int UnmappedAreas;
            public long ElapsedMilliseconds;

            public override string ToString() =>
                $"total {TotalPairs:N0} | after distance {AfterDistance:N0} | after PVS {AfterPvs:N0}";
        }

        private readonly int count;
        private readonly BspVisibility vis;
        private readonly float maxDistanceSquared;

        // bounds stored flat, one array per component: the inner loop touches these for every pair and
        // an array of structs would drag six unused floats through cache on each miss
        private readonly float[] minX, minY, minZ, maxX, maxY, maxZ;

        private readonly short[][] clusters;
        private readonly byte[]?[] visibleFrom;

        /// <summary>Eye-height sample points, <see cref="SightPointsPerArea"/> per area, laid out flat.</summary>
        private readonly BspFile.Vector3[] sight;

        /// <summary>Four corners then the centre - the order the tracer relies on.</summary>
        public const int SightPointsPerArea = 5;

        /// <summary>
        /// 0.75 * HumanHeight, the offset <c>CNavArea::ComputeVisibilityToMesh</c> raises both ends of
        /// its traces by. Not HalfHumanHeight - using 36 instead of 54 sits low enough to be stopped by
        /// railings and low walls the engine sees straight over.
        /// </summary>
        public const float EyeHeight = 54f;

        public int UnmappedAreas { get; }

        /// <summary>The points a ray should be cast between for this area. Corners first, centre last.</summary>
        public ReadOnlySpan<BspFile.Vector3> SightPoints(int areaIndex)
            => sight.AsSpan(areaIndex * SightPointsPerArea, SightPointsPerArea);

        public VisibilityFilter(NavFile nav, BspVisibility visibility, float maxViewDistance = DefaultMaxViewDistance)
        {
            vis = visibility;
            count = nav.Areas.Count;
            maxDistanceSquared = maxViewDistance <= 0f ? float.PositiveInfinity : maxViewDistance * maxViewDistance;

            minX = new float[count]; minY = new float[count]; minZ = new float[count];
            maxX = new float[count]; maxY = new float[count]; maxZ = new float[count];
            clusters = new short[count][];
            visibleFrom = new byte[count][];
            sight = new BspFile.Vector3[count * SightPointsPerArea];

            int unmapped = 0;

            Parallel.For(0, count, NavConcurrency.Options, i =>
            {
                var area = nav.Areas[i];

                float x0 = area.NwCorner[0], y0 = area.NwCorner[1];
                float x1 = area.SeCorner[0], y1 = area.SeCorner[1];

                minX[i] = MathF.Min(x0, x1); maxX[i] = MathF.Max(x0, x1);
                minY[i] = MathF.Min(y0, y1); maxY[i] = MathF.Max(y0, y1);

                float nw = area.NwCorner[2], ne = area.NeZ, se = area.SeCorner[2], sw = area.SwZ;
                minZ[i] = MathF.Min(MathF.Min(nw, ne), MathF.Min(se, sw));
                maxZ[i] = MathF.Max(MathF.Max(nw, ne), MathF.Max(se, sw));

                var samples = SamplePoints(area);
                clusters[i] = vis.GetClusters(samples);
                visibleFrom[i] = vis.MergeVisible(clusters[i]);

                for (int s = 0; s < SightPointsPerArea; s++)
                {
                    var p = samples[s];
                    sight[i * SightPointsPerArea + s] = new BspFile.Vector3(p.X, p.Y, p.Z + EyeHeight);
                }

                if (clusters[i].Length == 0)
                    Interlocked.Increment(ref unmapped);
            });

            UnmappedAreas = unmapped;
        }

        /// <summary>
        /// The five points an area is sampled at: its four corners pulled one unit inward, plus the
        /// centre. The inset keeps corners off the splitting planes they sit exactly on, which otherwise
        /// resolve arbitrarily to whichever side the tree descent lands on.
        /// </summary>
        private static BspFile.Vector3[] SamplePoints(NavArea area)
        {
            const float Inset = 1f;

            float x0 = area.NwCorner[0], y0 = area.NwCorner[1];
            float x1 = area.SeCorner[0], y1 = area.SeCorner[1];

            float ix = MathF.Sign(x1 - x0) * MathF.Min(Inset, MathF.Abs(x1 - x0) / 2f);
            float iy = MathF.Sign(y1 - y0) * MathF.Min(Inset, MathF.Abs(y1 - y0) / 2f);

            float nw = area.NwCorner[2], ne = area.NeZ, se = area.SeCorner[2], sw = area.SwZ;

            return
            [
                new BspFile.Vector3(x0 + ix, y0 + iy, nw),
                new BspFile.Vector3(x1 - ix, y0 + iy, ne),
                new BspFile.Vector3(x1 - ix, y1 - iy, se),
                new BspFile.Vector3(x0 + ix, y1 - iy, sw),
                new BspFile.Vector3((x0 + x1) / 2f, (y0 + y1) / 2f, (nw + ne + se + sw) / 4f),
            ];
        }

        /// <summary>Runs both stages across all cores, handing surviving pairs to the sink.</summary>
        public Stats Run(ICandidateSink? sink = null, NavProgress? progress = null)
        {
            var stats = new Stats
            {
                TotalPairs = (long)count * (count - 1) / 2,
                UnmappedAreas = UnmappedAreas,
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();

            long afterDistance = 0;
            long afterPvs = 0;

            // Counted in pairs rather than rows. Row i tests count-i-1 partners, so the first row does
            // all the work and the last does none; a bar driven by row index would race to 90% and then
            // sit there, which is exactly the impression this is meant to dispel.
            long pairsDone = 0;
            double totalPairs = Math.Max(1, stats.TotalPairs);

            Parallel.For(0, count, NavConcurrency.Options, () => new Local(count), (i, _, local) =>
            {
                int n = 0;
                float ax0 = minX[i], ay0 = minY[i], az0 = minZ[i];
                float ax1 = maxX[i], ay1 = maxY[i], az1 = maxZ[i];
                var row = visibleFrom[i];

                for (int j = i + 1; j < count; j++)
                {
                    float dx = MathF.Max(0f, MathF.Max(ax0 - maxX[j], minX[j] - ax1));
                    float dy = MathF.Max(0f, MathF.Max(ay0 - maxY[j], minY[j] - ay1));
                    float dz = MathF.Max(0f, MathF.Max(az0 - maxZ[j], minZ[j] - az1));

                    if (dx * dx + dy * dy + dz * dz > maxDistanceSquared)
                        continue;

                    local.Distance++;

                    if (!vis.SeesAny(row, clusters[j]))
                        continue;

                    local.Buffer[n++] = j;
                }

                local.Pvs += n;
                sink?.Candidates(i, local.Buffer.AsSpan(0, n));

                progress?.Report(Interlocked.Add(ref pairsDone, count - i - 1) / totalPairs);
                return local;
            }, local =>
            {
                Interlocked.Add(ref afterDistance, local.Distance);
                Interlocked.Add(ref afterPvs, local.Pvs);
            });

            sw.Stop();

            stats.AfterDistance = afterDistance;
            stats.AfterPvs = afterPvs;
            stats.ElapsedMilliseconds = sw.ElapsedMilliseconds;
            return stats;
        }

        private sealed class Local(int capacity)
        {
            public readonly int[] Buffer = new int[capacity];
            public long Distance;
            public long Pvs;
        }
    }
}
