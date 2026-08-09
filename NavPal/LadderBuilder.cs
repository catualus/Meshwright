using System;
using System.Collections.Generic;
using System.Linq;

namespace NavPal
{
    /// <summary>
    /// Turns ladder brushes found in a BSP into CNavLadder records wired into a nav mesh.
    ///
    /// This is the piece no Source game outside Left 4 Dead performs at all - CNavMesh::BuildLadders is
    /// entirely #ifdef TERROR - so on Garry's Mod every brush ladder is invisible to navigation.
    /// </summary>
    public static class LadderBuilder
    {
        /// <summary>CNavLadder::LadderDirectionType. Index into NavArea.Ladders.</summary>
        private const int LadderUp = 0;
        private const int LadderDown = 1;

        /// <summary>
        /// Distances in front of the ladder face to look for floor, tried nearest-first.
        ///
        /// A single 20 unit probe misses badly on real maps: checking the skipped ladders on
        /// rp_downtown_meowy against the engine's own GetNearestNavArea showed areas sitting 40-72
        /// units from the ladder base, because generated areas do not butt up against wall geometry.
        /// Searching outward recovers those without linking areas from across the room.
        /// </summary>
        private static readonly float[] ProbeDistances = [20f, 36f, 52f, 72f];

        /// <summary>
        /// The same idea for the top of the ladder, kept shorter on purpose.
        ///
        /// The wide search above was justified by measurement at the *base* - "40-72 units from the
        /// ladder base" is the exact quote it came from - and was never separately justified for the
        /// top, just reused because the code already existed. Measured on rp_downtown_meowy's real
        /// ladders, that reuse was a real defect: 6 of 24 ladders picked up a second, distinct top area
        /// through the outermost 72-unit reach - the double-connected top exit reported in game.
        ///
        /// Dropping only the 72-unit ring halves that (6 to 3) with no regressions - every ladder that
        /// found its one true top area through the 20-52 unit rings still does. Dropping 52 as well cut
        /// the doubles further (to 1) but cost one ladder its only top connection outright, which is a
        /// worse trade: a wrong extra connection is a correctable annoyance, a ladder with no exit at all
        /// is a bot that gets stuck.
        ///
        /// Three ladders still attach to a very large area (550-875 units long) after this change,
        /// unaffected by either cutoff - a separate thing, and it is on the *base* side, matching the
        /// other report of a ladder's bottom connection being one big room rather than a tight landing.
        /// That is a mesh-shaping question - should ground right at a ladder's foot be its own small area
        /// - not a search-distance one, and is not addressed here.
        /// </summary>
        private static readonly float[] TopProbeDistances = [20f, 36f, 52f];

        /// <summary>Nominal probe distance, used where a single representative offset is needed.</summary>
        private const float ProbeDistance = 20f;

        /// <summary>
        /// Vertical slack when matching the ladder's base to the floor. Generous, because ladder
        /// brushes are sunk into geometry and the standing surface can sit well above the brush bottom.
        /// </summary>
        private const float BottomHeightTolerance = 72f;

        /// <summary>
        /// Vertical slack for areas at the top of a ladder. Deliberately tight - roughly Source's
        /// StepHeight - because the climber steps directly off onto these. With a loose tolerance,
        /// areas on other levels nearby get linked: at 72 units this connected two extra areas on
        /// gm_construct that Valve's own ladder leaves unconnected.
        /// </summary>
        private const float TopHeightTolerance = 24f;

        public sealed class Result
        {
            public int LaddersAdded;
            public int BottomConnected;
            public int TopConnected;
            public int Unresolved;

            /// <summary>Previously built ladders discarded before rebuilding.</summary>
            public int Removed;

            public readonly List<string> Warnings = [];
        }

        public static Result Build(NavFile nav, IReadOnlyList<LadderFinder.LadderBrush> brushes,
            BspVisibility? vis = null)
        {
            var result = new Result();

            // Always rebuild rather than adding to whatever is already there. On a normal compile the
            // BSP is new and the .nav beside it is left over from a previous one, so its ladders were
            // built against geometry that no longer exists. Discarding them makes the result a function
            // of the BSP, and re-running the pass a no-op.
            result.Removed = RemoveBuiltLadders(nav, brushes);

            var index = new NavGeometry.Index(nav.Areas);

            uint nextId = nav.Ladders.Count > 0 ? nav.Ladders.Max(l => l.Id) + 1 : 1;

            foreach (var brush in brushes)
            {
                // Resolve the direction sign: the climbing face points at open floor, the opposite side
                // is wall. Probe both candidates at the base and take whichever finds an area.
                var (dirA, dirB) = brush.CandidateDirections;

                var bottomA = Probe(nav, index, vis, brush.Bottom, dirA, brush.Bottom.Z, BottomHeightTolerance);
                var bottomB = Probe(nav, index, vis, brush.Bottom, dirB, brush.Bottom.Z, BottomHeightTolerance);

                uint direction;
                NavArea? bottomArea;

                if (bottomA is not null && bottomB is null) { direction = dirA; bottomArea = bottomA; }
                else if (bottomB is not null && bottomA is null) { direction = dirB; bottomArea = bottomB; }
                else if (bottomA is not null && bottomB is not null)
                {
                    // floor on both sides: prefer the one whose surface sits closest to the ladder base
                    float da = MathF.Abs(SurfaceZAt(bottomA, brush.Bottom, dirA) - brush.Bottom.Z);
                    float db = MathF.Abs(SurfaceZAt(bottomB, brush.Bottom, dirB) - brush.Bottom.Z);
                    if (da <= db) { direction = dirA; bottomArea = bottomA; } else { direction = dirB; bottomArea = bottomB; }
                }
                else
                {
                    // no floor either side - the mesh does not cover the base of this ladder
                    result.Unresolved++;
                    result.Warnings.Add($"no nav area at the base of ladder at {brush.Bottom}; skipped");
                    continue;
                }

                var ladder = new NavLadder
                {
                    Id = nextId++,
                    Width = brush.Width,
                    Length = brush.Height,
                    Direction = direction,
                };

                brush.Top.CopyTo(ladder.Top);

                // Clip the base to the floor the climber actually stands on rather than the raw brush
                // extent. Ladder brushes are routinely sunk into the floor geometry, so the brush
                // minimum sits below the walkable surface - Valve's own gm_construct ladder starts
                // 48 units above where its brush bottoms out.
                float standingZ = SurfaceZAt(bottomArea, brush.Bottom, direction);
                var basePoint = new BspFile.Vector3(brush.Bottom.X, brush.Bottom.Y,
                    MathF.Max(brush.Bottom.Z, standingZ));
                basePoint.CopyTo(ladder.Bottom);
                ladder.Length = ladder.Top[2] - ladder.Bottom[2];

                ladder.BottomAreaId = bottomArea.Id;
                bottomArea.Ladders[LadderUp].Add(ladder.Id);
                result.BottomConnected++;

                // The four areas around the top.
                //
                // m_dir is the direction the ladder face points (out from the wall, over the floor at
                // its base), so at the top the climber steps AWAY from that direction onto the ledge.
                // TopForward is therefore opposite m_dir, not along it. Verified against Valve's
                // gm_construct ladder, whose TopForward area sits on the far side from m_dir.
                var top = brush.Top;
                var forward = Probe(nav, index, vis, top, Opposite(direction), top.Z, TopHeightTolerance, TopProbeDistances);
                var behind = Probe(nav, index, vis, top, direction, top.Z, TopHeightTolerance, TopProbeDistances);
                var left = Probe(nav, index, vis, top, RotateLeft(direction), top.Z, TopHeightTolerance, TopProbeDistances);
                var right = Probe(nav, index, vis, top, RotateRight(direction), top.Z, TopHeightTolerance, TopProbeDistances);

                DeduplicateNearbyTops(top, ref forward, ref behind, ref left, ref right);

                ladder.TopForwardAreaId = Connect(forward, ladder, result);
                ladder.TopBehindAreaId = Connect(behind, ladder, result);
                ladder.TopLeftAreaId = Connect(left, ladder, result);
                ladder.TopRightAreaId = Connect(right, ladder, result);

                if (ladder.TopForwardAreaId == 0 && ladder.TopBehindAreaId == 0 &&
                    ladder.TopLeftAreaId == 0 && ladder.TopRightAreaId == 0)
                {
                    result.Warnings.Add($"ladder at {brush.Bottom} has no area at its top ({brush.Top})");
                }

                nav.Ladders.Add(ladder);
                result.LaddersAdded++;
            }

            return result;
        }

        /// <summary>
        /// How close two of the four top-direction hits have to be, centre to centre, before they are
        /// treated as the same landing rather than two distinct ones.
        ///
        /// Measured on rp_downtown_meowy's real doubled ladders: distances of 25, 62 and 100 units were
        /// every case of what turned out to be two adjacent tiles of one physical landing - one of them
        /// sharing an exact edge with the other, both far smaller than a real second destination would
        /// be. Distances of 179, 215 and 505 units were areas plausibly worth keeping distinct: bigger,
        /// and further than tiling seams tend to run. 150 sits in the gap measurement found between
        /// those two clusters.
        /// </summary>
        private const float SameLandingDistance = 150f;

        /// <summary>
        /// Collapses top-direction hits that are close enough together to be the same landing split by
        /// mesh tiling rather than two genuinely different places to step off onto - the doubled top
        /// connection reported in game. Valve's own ladder keeps up to four simultaneous top areas by
        /// design, so this only removes a candidate when another one covers essentially the same spot;
        /// two directions that land on areas far enough apart to plausibly be different destinations are
        /// left exactly as found.
        /// </summary>
        private static void DeduplicateNearbyTops(BspFile.Vector3 top,
            ref NavArea? forward, ref NavArea? behind, ref NavArea? left, ref NavArea? right)
        {
            var slots = new NavArea?[] { forward, behind, left, right };

            for (int i = 0; i < slots.Length; i++)
            {
                for (int j = i + 1; j < slots.Length; j++)
                {
                    var a = slots[i];
                    var b = slots[j];

                    if (a is null || b is null || a.Id == b.Id)
                        continue;

                    if (Distance2D(a, b) > SameLandingDistance)
                        continue;

                    // Keep whichever sits closer to the ladder itself - that is the one a climber
                    // actually steps onto - and drop the other.
                    if (DistanceToPoint(a, top) <= DistanceToPoint(b, top))
                        slots[j] = null;
                    else
                        slots[i] = null;
                }
            }

            forward = slots[0];
            behind = slots[1];
            left = slots[2];
            right = slots[3];
        }

        private static float Distance2D(NavArea a, NavArea b)
        {
            var ba = NavGeometry.GetBounds(a);
            var bb = NavGeometry.GetBounds(b);
            float ax = (ba.MinX + ba.MaxX) / 2f, ay = (ba.MinY + ba.MaxY) / 2f;
            float bx = (bb.MinX + bb.MaxX) / 2f, by = (bb.MinY + bb.MaxY) / 2f;
            float dx = ax - bx, dy = ay - by;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        private static float DistanceToPoint(NavArea a, BspFile.Vector3 point)
        {
            var b = NavGeometry.GetBounds(a);
            float cx = (b.MinX + b.MaxX) / 2f, cy = (b.MinY + b.MaxY) / 2f;
            float dx = cx - point.X, dy = cy - point.Y;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// Whether a ladder sits on a given ladder brush. Compared with a tolerance because a built
        /// ladder's base is clipped to the standing surface and so will not match the brush exactly.
        /// </summary>
        private static bool MatchesBrush(NavLadder ladder, LadderFinder.LadderBrush brush)
        {
            const float Tolerance = 24f;

            return MathF.Abs(ladder.Top[0] - brush.Top.X) <= Tolerance
                   && MathF.Abs(ladder.Top[1] - brush.Top.Y) <= Tolerance
                   && MathF.Abs(ladder.Top[2] - brush.Top.Z) <= Tolerance
                   && MathF.Abs(ladder.Bottom[0] - brush.Bottom.X) <= Tolerance
                   && MathF.Abs(ladder.Bottom[1] - brush.Bottom.Y) <= Tolerance;
        }

        /// <summary>
        /// Discards ladders this pass previously built, along with every area's reference to them, so a
        /// rebuild is not blocked by its own earlier output.
        ///
        /// Only ladders sitting on a ladder brush are removed. A hand-placed ladder somewhere the BSP
        /// has no brush is left alone, which is the difference between this and the engine's
        /// BuildLadders - that one destroys the lot.
        /// </summary>
        private static int RemoveBuiltLadders(NavFile nav, IReadOnlyList<LadderFinder.LadderBrush> brushes)
        {
            var doomed = new HashSet<uint>();

            for (int i = nav.Ladders.Count - 1; i >= 0; i--)
            {
                var ladder = nav.Ladders[i];

                bool onABrush = false;
                foreach (var brush in brushes)
                {
                    if (!MatchesBrush(ladder, brush))
                        continue;

                    onABrush = true;
                    break;
                }

                if (!onABrush)
                    continue;

                doomed.Add(ladder.Id);
                nav.Ladders.RemoveAt(i);
            }

            if (doomed.Count == 0)
                return 0;

            foreach (var area in nav.Areas)
                foreach (var list in area.Ladders)
                    list.RemoveAll(doomed.Contains);

            return doomed.Count;
        }

        /// <summary>
        /// Records the ladder on a top area and returns its id, or 0 when there is no area. Each area
        /// gets the back-reference exactly once even if several probes land in it.
        /// </summary>
        private static uint Connect(NavArea? area, NavLadder ladder, Result result)
        {
            if (area is null)
                return 0;

            if (!area.Ladders[LadderDown].Contains(ladder.Id))
            {
                area.Ladders[LadderDown].Add(ladder.Id);
                result.TopConnected++;
            }

            return area.Id;
        }

        /// <summary>
        /// Searches outward from the ladder face for the nearest area in a direction, so ladders whose
        /// surrounding areas do not reach the wall are still connected.
        /// </summary>
        private static NavArea? Probe(NavFile nav, NavGeometry.Index index, BspVisibility? vis,
            BspFile.Vector3 origin, uint dir, float referenceZ, float tolerance, float[]? distances = null)
        {
            foreach (var distance in distances ?? ProbeDistances)
            {
                var point = Offset(origin, dir, distance);
                int found = index.FindAt(point.X, point.Y, referenceZ, tolerance);

                if (found < 0)
                    continue;

                if (!IsReachable(vis, origin, point))
                    continue;

                return nav.Areas[found];
            }

            return null;
        }

        /// <summary>
        /// Whether a climber could actually step between the ladder and the probe point, or whether
        /// there is a wall in the way.
        ///
        /// Searching outward for the nearest area finds ladders their surroundings, but distance alone
        /// says nothing about what is between: a ladder run up the outside of a building has areas
        /// inside it well within reach, and without this the top of the ladder gets wired to a room on
        /// the other side of the wall. Observed in game on rp_downtown_meowy.
        /// </summary>
        private static bool IsReachable(BspVisibility? vis, BspFile.Vector3 from, BspFile.Vector3 to)
        {
            if (vis is null)
                return true;

            // Two heights: a single line along the floor grazes the lip a ladder sits behind, while one
            // at head height alone would pass over a low wall the climber cannot cross.
            foreach (float height in (ReadOnlySpan<float>)[16f, 48f])
            {
                var a = new BspFile.Vector3(from.X, from.Y, from.Z + height);
                var b = new BspFile.Vector3(to.X, to.Y, to.Z + height);

                // GenerationMask: "can the climber get across to it", not "can it be seen". Ladder
                // brushes are excluded as obstructions - `from` is the ladder's own base point, which
                // sits inside the ladder brush, so counting that brush would make every ladder report
                // itself as the wall in the way.
                if (vis.IsLineClear(a, b, BspVisibility.GenerationMask, BspVisibility.ContentsLadder))
                    return true;
            }

            return false;
        }

        /// <summary>Surface height of an area at the probe point in a given direction.</summary>
        private static float SurfaceZAt(NavArea area, BspFile.Vector3 origin, uint dir)
        {
            var point = Offset(origin, dir, ProbeDistance);
            return NavGeometry.SurfaceZ(area, point.X, point.Y);
        }

        // NavDirType: 0 north (-Y), 1 east (+X), 2 south (+Y), 3 west (-X)
        private static uint Opposite(uint dir) => (dir + 2) % 4;
        private static uint RotateLeft(uint dir) => (dir + 3) % 4;
        private static uint RotateRight(uint dir) => (dir + 1) % 4;

        private static BspFile.Vector3 Offset(BspFile.Vector3 point, uint dir, float distance) => dir switch
        {
            0 => new BspFile.Vector3(point.X, point.Y - distance, point.Z),
            1 => new BspFile.Vector3(point.X + distance, point.Y, point.Z),
            2 => new BspFile.Vector3(point.X, point.Y + distance, point.Z),
            _ => new BspFile.Vector3(point.X - distance, point.Y, point.Z),
        };

    }

    internal static class VectorExtensions
    {
        /// <summary>Copies a vector into the float[3] the nav format uses.</summary>
        public static void CopyTo(this BspFile.Vector3 v, float[] target)
        {
            target[0] = v.X;
            target[1] = v.Y;
            target[2] = v.Z;
        }
    }
}
