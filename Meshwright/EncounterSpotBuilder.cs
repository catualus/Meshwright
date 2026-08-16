using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Meshwright
{
    /// <summary>
    /// Records which hiding spots a bot walks past on its way across an area - Valve's
    /// <c>CNavArea::ComputeSpotEncounters</c> and the <c>AddSpotEncounters</c> it drives.
    ///
    /// An encounter is a claim about one specific movement: entering this area from a given neighbour
    /// and leaving toward another, these are the covered positions that come into view along the way,
    /// in the order you meet them. Bots use it to know where to look as they move rather than only
    /// where to stand, which is why a mesh without encounters makes them walk corridors staring
    /// straight ahead.
    ///
    /// **The engine does not do this by default.** <c>nav_quicksave</c> defaults to 1 and
    /// <c>ComputeSpotEncounters</c> returns immediately when it is set, so a stock <c>nav_generate</c>
    /// writes none - a freshly generated gm_construct has 267 hiding spots and zero encounters.
    /// Getting them from the engine needs <c>nav_quicksave 0</c> and then <c>nav_analyze</c>, which is
    /// the slow path the convar exists to skip.
    ///
    /// Only spots that are actually in cover are considered. Valve's reasoning is in their own comment:
    /// an exposed spot is out in the open and easily seen, so noting that you can see it tells a bot
    /// nothing it did not already know.
    /// </summary>
    public static class EncounterSpotBuilder
    {
        /// <summary>How far along the path the eye moves between samples. Valve's <c>stepSize</c>.</summary>
        private const float StepSize = 25f;

        /// <summary>Beyond this a spot is too far to be worth noting. Valve's <c>seeSpotRange</c>.</summary>
        private const float SeeSpotRange = 2000f;

        /// <summary>
        /// How square-on a spot has to be to count, as a dot product against the direction of travel.
        ///
        /// This is cos(45°), and the test keeps spots whose bearing is *between* 45° and 135° off the
        /// path - that is, beside you rather than ahead or behind. The intent, per the SDK's own
        /// reasoning about this test, is to capture the ones that open up to the side partway through
        /// the crossing: those are the moment worth warning a bot about, where something straight ahead
        /// was already in plain view before the move began and tells it nothing new.
        /// </summary>
        private const float PerpendicularDot = 0.7071f;

        public sealed class Result
        {
            public int Encounters;
            public int AreasWithEncounters;
            public long SpotOrders;
            public long Rays;
        }

        public static Result Build(NavFile nav, BspVisibility vis, NavProgress? progress = null)
        {
            var result = new Result();

            var byId = new Dictionary<uint, NavArea>(nav.Areas.Count);
            foreach (var area in nav.Areas)
                byId[area.Id] = area;

            // Every spot with cover, flattened once for the whole mesh. Valve walks its global spot
            // list per encounter; the list does not change, so it is built here instead.
            var coverSpots = new List<(uint Id, float X, float Y, float Z)>();
            foreach (var area in nav.Areas)
            {
                foreach (var spot in area.HidingSpots)
                {
                    if ((spot.Flags & (byte)HidingSpot.SpotFlags.InCover) != 0)
                        coverSpots.Add((spot.Id, spot.Position[0], spot.Position[1], spot.Position[2]));
                }
            }

            var spots = coverSpots.ToArray();
            if (spots.Length == 0)
                return result;

            int done = 0;
            object gate = new();

            // Parallel over areas, which is safe for the same reason the connection pass is: an
            // iteration writes only to the Encounters list of the area it was handed, and everything
            // else it reads - other areas' corners, the spot list, the BSP - is read-only for the pass.
            //
            // Handed out one area at a time rather than in contiguous ranges. An area produces one
            // encounter per ordered pair of its connections, so a junction with eight of them does
            // fifty-six while a corridor tile does two - and because areas are stored in spatial order,
            // a static range split hands one thread a dense region and another an empty one. Measured
            // on gm_construct, range splitting scaled 3.09x on sixteen threads against 5.67x for the
            // whole pass at eight once both phases were handed out per item.
            Parallel.ForEach(Partitioner.Create(0, nav.Areas.Count, 1), NavConcurrency.Options,
                () => new Result(), (range, _, local) =>
            {
            for (int i = range.Item1; i < range.Item2; i++)
            {
                progress?.Report(Interlocked.Increment(ref done) / (double)Math.Max(1, nav.Areas.Count));

                var area = nav.Areas[i];
                area.Encounters.Clear();

                // Scratch reused across every encounter this area produces, rather than reallocated:
                // an area with eight connections generates fifty-six of them. Holds the indices of the
                // spots still worth testing, so it never needs clearing between encounters - each one
                // refills it from scratch.
                var active = new int[spots.Length];

                for (int fromDir = 0; fromDir < NavGeometry.DirectionCount; fromDir++)
                {
                    var fromList = area.Connections[fromDir];

                    for (int fromIndex = 0; fromIndex < fromList.Count; fromIndex++)
                    {
                        if (!byId.TryGetValue(fromList[fromIndex], out var from))
                            continue;

                        for (int toDir = 0; toDir < NavGeometry.DirectionCount; toDir++)
                        {
                            var toList = area.Connections[toDir];

                            for (int toIndex = 0; toIndex < toList.Count; toIndex++)
                            {
                                // The same connection, not merely the same area. Valve compares the
                                // connection records themselves, so one neighbour reachable in two
                                // different directions legitimately produces a pair with itself.
                                if (fromDir == toDir && fromIndex == toIndex)
                                    continue;

                                if (!byId.TryGetValue(toList[toIndex], out var to))
                                    continue;

                                var encounter = BuildEncounter(vis, area, from, fromDir, to, toDir,
                                    spots, active, local);

                                area.Encounters.Add(encounter);
                                local.Encounters++;
                                local.SpotOrders += encounter.Spots.Count;
                            }
                        }
                    }
                }

                if (area.Encounters.Count > 0)
                    local.AreasWithEncounters++;
            }

                return local;
            },
            local =>
            {
                lock (gate)
                {
                    result.Encounters += local.Encounters;
                    result.AreasWithEncounters += local.AreasWithEncounters;
                    result.SpotOrders += local.SpotOrders;
                    result.Rays += local.Rays;
                }
            });

            return result;
        }

        /// <summary>
        /// Walks the path across one area from the portal it is entered by to the portal it is left by,
        /// collecting the covered spots that come into view along the run.
        /// </summary>
        private static SpotEncounter BuildEncounter(BspVisibility vis, NavArea area,
            NavArea from, int fromDir, NavArea to, int toDir,
            (uint Id, float X, float Y, float Z)[] spots, int[] active, Result local)
        {
            var encounter = new SpotEncounter
            {
                FromAreaId = from.Id,
                FromDirection = (byte)fromDir,
                ToAreaId = to.Id,
                ToDirection = (byte)toDir,
            };

            var (fromX, fromY) = Portal(area, from, fromDir);
            var (toX, toY) = Portal(area, to, toDir);

            // Height comes from the *neighbour's* surface at the portal, not this area's. The portal
            // sits on the shared edge, so both describe it, and this is what Valve reads.
            float fromZ = NavGeometry.SurfaceZ(from, fromX, fromY) + NavConstants.HumanEyeHeight;
            float toZ = NavGeometry.SurfaceZ(to, toX, toY) + NavConstants.HumanEyeHeight;

            float dx = toX - fromX, dy = toY - fromY, dz = toZ - fromZ;
            float length = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

            if (length < 1e-4f)
                return encounter;

            float dirX = dx / length, dirY = dy / length, dirZ = dz / length;

            // Spots that could possibly come into range at some point on this path, gathered once.
            //
            // A spot outside the path's bounding box grown by SeeSpotRange is further than that from
            // every point on the path, so it can never be in range at any step: Euclidean distance
            // within R implies each axis is within R, and the contrapositive is what makes this an
            // exact filter rather than a heuristic. Doing it once here replaces a distance test per
            // spot per step.
            float loX = MathF.Min(fromX, toX) - SeeSpotRange, hiX = MathF.Max(fromX, toX) + SeeSpotRange;
            float loY = MathF.Min(fromY, toY) - SeeSpotRange, hiY = MathF.Max(fromY, toY) + SeeSpotRange;
            float loZ = MathF.Min(fromZ, toZ) - SeeSpotRange, hiZ = MathF.Max(fromZ, toZ) + SeeSpotRange;

            int activeCount = 0;
            for (int s = 0; s < spots.Length; s++)
            {
                var spot = spots[s];
                float eyeZOfSpot = spot.Z + NavConstants.HumanEyeHeight;

                if (spot.X < loX || spot.X > hiX || spot.Y < loY || spot.Y > hiY ||
                    eyeZOfSpot < loZ || eyeZOfSpot > hiZ)
                {
                    continue;
                }

                active[activeCount++] = s;
            }

            bool done = false;

            for (float along = 0f; !done; along += StepSize)
            {
                if (along >= length)
                {
                    along = length;
                    done = true;
                }

                // Everything reachable from this path has already been accounted for; the remaining
                // steps can only re-confirm it.
                if (activeCount == 0)
                    break;

                float eyeX = fromX + along * dirX;
                float eyeY = fromY + along * dirY;
                float eyeZ = fromZ + along * dirZ;

                // Walked and compacted in one pass. A spot is recorded at most once per encounter, at
                // the first point along the path where it is both in range and visible - so once seen
                // it is dropped from the working set instead of being re-tested at every later step,
                // which is what the flag array this replaced was doing. Survivors are written back in
                // ascending order, so spots recorded at the same step keep the order they had before
                // and the mesh is unchanged byte for byte.
                //
                // Worth being straight about the payoff: on gm_construct this measured 1.03x, near
                // enough nothing. The scan it removes looked expensive - 159 million flag checks
                // against 8.2 million rays - but a predictable byte load and branch costs about a
                // cycle, so the whole scan was a low single-digit percentage and the rays were always
                // the real cost.
                //
                // It is kept for how it scales rather than for that number. The old form was O(every
                // cover spot on the map) per step; this is O(spots the path could reach), and
                // gm_construct has only 125 cover spots to begin with. On a mesh with thousands the
                // gap is the whole point - though that is reasoning, not something measured here.
                int keep = 0;

                for (int k = 0; k < activeCount; k++)
                {
                    int s = active[k];
                    var spot = spots[s];

                    float sx = spot.X - eyeX;
                    float sy = spot.Y - eyeY;
                    float sz = spot.Z + NavConstants.HumanEyeHeight - eyeZ;

                    float distanceSquared = sx * sx + sy * sy + sz * sz;
                    if (distanceSquared > SeeSpotRange * SeeSpotRange)
                    {
                        active[keep++] = s;
                        continue;
                    }

                    local.Rays++;

                    // Aimed at the spot's own middle rather than its eye - Valve traces to
                    // HalfHumanHeight above it while measuring the bearing to eye height.
                    if (!vis.IsLineClear(
                            new BspFile.Vector3(eyeX, eyeY, eyeZ),
                            new BspFile.Vector3(spot.X, spot.Y, spot.Z + NavConstants.HalfHumanHeight),
                            BspVisibility.GenerationMask))
                    {
                        // In range but not visible yet, so it stays a candidate for later steps.
                        active[keep++] = s;
                        continue;
                    }

                    float distance = MathF.Sqrt(distanceSquared);
                    if (distance > 1e-4f)
                    {
                        float dot = (dirX * sx + dirY * sy + dirZ * sz) / distance;

                        // Beside the path rather than along it, and not already in view when the move
                        // began - a spot visible from the very start was not something you walked past.
                        if (dot < PerpendicularDot && dot > -PerpendicularDot && along > 0f)
                        {
                            encounter.Spots.Add((spot.Id, (byte)Math.Clamp(
                                (int)MathF.Round(255f * along / length), 0, 255)));
                        }
                    }

                    // Seen, whether or not it was recorded - so it is not written back, and drops out
                    // of the working set for every remaining step.
                }

                activeCount = keep;
            }

            return encounter;
        }

        /// <summary>
        /// The midpoint of the edge two areas share, on this area's boundary - Valve's
        /// <c>ComputePortal</c>.
        ///
        /// The overlap is clamped back into this area's own extent before the midpoint is taken, which
        /// matters because a connection does not guarantee the two footprints overlap: a drop connects
        /// areas that may only face each other across a gap, and without the clamp the portal would sit
        /// outside the area the path is supposed to cross.
        /// </summary>
        private static (float X, float Y) Portal(NavArea area, NavArea other, int direction)
        {
            var a = NavGeometry.GetBounds(area);
            var b = NavGeometry.GetBounds(other);

            if (direction is NavGeometry.North or NavGeometry.South)
            {
                float y = direction == NavGeometry.North ? a.MinY : a.MaxY;

                float left = Math.Clamp(MathF.Max(a.MinX, b.MinX), a.MinX, a.MaxX);
                float right = Math.Clamp(MathF.Min(a.MaxX, b.MaxX), a.MinX, a.MaxX);

                return ((left + right) / 2f, y);
            }

            float x = direction == NavGeometry.West ? a.MinX : a.MaxX;

            float top = Math.Clamp(MathF.Max(a.MinY, b.MinY), a.MinY, a.MaxY);
            float bottom = Math.Clamp(MathF.Min(a.MaxY, b.MaxY), a.MinY, a.MaxY);

            return (x, (top + bottom) / 2f);
        }
    }
}
