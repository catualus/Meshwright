using System;
using System.Collections.Generic;
using System.Globalization;

namespace Meshwright
{
    /// <summary>
    /// Connects the areas a moving platform serves at each of its rest positions.
    ///
    /// No Source game does this. `nav_generate` samples the world once, in whatever state it spawned in,
    /// so a lift contributes at most the areas that happen to sit on it where it started - and nothing
    /// links the floor it is not at. Bots simply cannot use lifts.
    ///
    /// The approach is deliberately narrow. Rather than model movement, each platform is reduced to the
    /// set of heights its top surface rests at, and at each of those the areas standing on the platform
    /// are linked to the areas on solid ground beside it. That covers vertical lifts, which is what
    /// people mean by an elevator, and declines to guess about anything else.
    ///
    /// Every link is confirmed against the world before it is made, which this pass used not to do at
    /// all - it was the one place in the pipeline that added a connection on the strength of a spatial
    /// lookup alone. A landing was accepted purely because <see cref="FindLandings"/> probed a point
    /// <see cref="LandingReach"/> out from a platform edge and the index reported an area there at
    /// roughly the right height. Nothing asked what was in between. A lift running up the outside of a
    /// shaft has rooms on the far side of the shaft wall well within 40 units of its edge, and every one
    /// of them satisfied that test, so the platform was wired straight through the brickwork - the same
    /// failure <see cref="LadderBuilder.IsReachable"/> exists to prevent, and which was observed in game
    /// for ladders before it did.
    ///
    /// A stop is the height a rider stands at, which is the platform's floor rather than the top of its
    /// brush model. The two used to be taken as one, and they are not: rp_downtown_meowy's mine lift has
    /// railings, so its model reaches 32 units above the plate it carries people on, and both of its
    /// stops were looked for 32 units too high - "no nav area on the platform" at each, and not one
    /// connection, with the areas sitting on the plate the whole time.
    /// </summary>
    public static class ElevatorConnector
    {
        /// <summary>How far out from the platform edge to look for a landing.</summary>
        private const float LandingReach = 40f;

        /// <summary>Vertical slack when matching an area to a platform stop.</summary>
        internal const float StopTolerance = 24f;

        /// <summary>A platform that moves less than this is a door or a piston, not a lift.</summary>
        private const float MinimumTravel = 32f;

        /// <summary>
        /// How wide a vertical func_door has to be, both ways, to be a platform rather than a door. Room
        /// for a player with some to spare; the doors and shutters that move vertically on
        /// rp_downtown_meowy and rp_downtown_tits are 2 to 10 units deep.
        /// </summary>
        private const float MinimumLiftWidth = NavConstants.HumanWidth * 1.5f;

        public sealed class Result
        {
            public int Platforms;
            public int Stops;
            public int Connections;

            /// <summary>
            /// Rider/landing pairs that sat at the right height beside each other and still had
            /// something solid between them. Counted rather than silently dropped because a platform
            /// refusing every landing it found is the signature of a lift whose stops are being resolved
            /// at the wrong heights, and a bare "0 connections" cannot tell that apart from a lift with
            /// no mesh beside it at all.
            /// </summary>
            public int Refused;

            public readonly List<string> Notes = [];
        }

        /// <param name="Stops">The heights a rider stands at.</param>
        /// <param name="Depth">How far the platform's underside sits below the floor riders stand on.</param>
        private readonly record struct Platform(BspFile.Vector3 Mins, BspFile.Vector3 Maxs, float[] Stops,
            string Class, float Depth);

        /// <summary>
        /// Where a platform comes to rest: the centre of its floor at one stop, and how far its
        /// underside reaches below that floor.
        /// </summary>
        public readonly record struct Stop(BspFile.Vector3 Position, float Depth);

        /// <summary>
        /// Connects every platform's riders to its landings. <paramref name="vis"/> is optional only so
        /// callers with no traced geometry to hand still compile; without it no link can be checked and
        /// every one is taken on trust, which is what this pass did unconditionally before.
        /// </summary>
        public static Result Build(NavFile nav, BspFile bsp, BspVisibility? vis = null)
        {
            var result = new Result();
            var platforms = FindPlatforms(bsp, vis);
            if (platforms.Count == 0)
                return result;

            var index = new NavGeometry.Index(nav.Areas);
            result.Platforms = platforms.Count;

            foreach (var platform in platforms)
            {
                foreach (float top in platform.Stops)
                {
                    result.Stops++;

                    // Areas riding the platform at this height - or, at a stop where the platform's own
                    // geometry is not there to be sampled, standing on what it comes to rest on, which
                    // is as far below the stop as the platform's underside is below its floor.
                    var onboard = new List<int>();
                    foreach (int i in index.Overlapping(platform.Mins.X, platform.Mins.Y, platform.Maxs.X, platform.Maxs.Y))
                    {
                        var a = nav.Areas[i];
                        var b = NavGeometry.GetBounds(a);
                        float cx = (b.MinX + b.MaxX) / 2f, cy = (b.MinY + b.MaxY) / 2f;
                        float z = NavGeometry.SurfaceZ(a, cx, cy);

                        if (z <= top + StopTolerance && z >= top - platform.Depth - StopTolerance)
                            onboard.Add(i);
                    }

                    if (onboard.Count == 0)
                    {
                        // Nothing to connect: the generator never sampled the platform at this height,
                        // so no area exists to ride it. Creating one is generation rather than repair,
                        // and is out of this pass's scope - but it is the usual reason a lift stays
                        // unusable even after this runs, so it is worth saying out loud.
                        result.Notes.Add($"{platform.Class} at {platform.Mins}: no nav area on the " +
                                         $"platform at height {top:F0}");
                        continue;
                    }

                    // areas on solid ground just off each edge, at the same height
                    var landings = FindLandings(nav, index, platform, top);

                    foreach (int rider in onboard)
                    {
                        foreach (int landing in landings)
                        {
                            if (rider == landing)
                                continue;

                            if (!CanCross(vis, nav.Areas[rider], nav.Areas[landing]))
                            {
                                result.Refused++;
                                continue;
                            }

                            result.Connections += LinkBothWays(nav, rider, landing);
                        }
                    }
                }

                if (platform.Stops.Length < 2)
                    result.Notes.Add($"{platform.Class} at {platform.Mins} has only one resolved stop");
            }

            return result;
        }

        /// <summary>Areas adjacent to the platform's footprint whose surface is level with this stop.</summary>
        private static List<int> FindLandings(NavFile nav, NavGeometry.Index index, Platform platform, float top)
        {
            var found = new List<int>();

            ReadOnlySpan<(float X, float Y)> probes =
            [
                ((platform.Mins.X + platform.Maxs.X) / 2f, platform.Mins.Y - LandingReach),
                ((platform.Mins.X + platform.Maxs.X) / 2f, platform.Maxs.Y + LandingReach),
                (platform.Mins.X - LandingReach, (platform.Mins.Y + platform.Maxs.Y) / 2f),
                (platform.Maxs.X + LandingReach, (platform.Mins.Y + platform.Maxs.Y) / 2f),
            ];

            foreach (var (x, y) in probes)
            {
                int area = index.FindAt(x, y, top, StopTolerance);
                if (area >= 0 && !found.Contains(area))
                    found.Add(area);
            }

            return found;
        }

        /// <summary>
        /// Whether a rider could actually step between the platform and a landing beside it.
        ///
        /// <see cref="Traversability.CanStep"/> rather than a pair of lines, because it is already this
        /// codebase's settled answer to "can a body get from here to there": it sweeps Valve's own
        /// <c>NavTraceMins</c>/<c>NavTraceMaxs</c> box flat at half a step above the higher surface, so
        /// the whole 0..55 span is proven clear along the crossing instead of the two heights a pair of
        /// lines would happen to sample. <see cref="CornerPatcher"/> validates its synthetic connections
        /// the same way, for the same reason.
        ///
        /// The two endpoints are each footprint's nearest point to the other's centre, which for a lift
        /// is the platform edge and the facing edge of the landing - where a rider crosses, rather than
        /// the area centres, which on a large landing can sit most of a room away from the shaft.
        /// </summary>
        private static bool CanCross(BspVisibility? vis, NavArea rider, NavArea landing)
        {
            if (vis is null)
                return true;

            var a = NavGeometry.GetBounds(rider);
            var b = NavGeometry.GetBounds(landing);

            float acx = (a.MinX + a.MaxX) / 2f, acy = (a.MinY + a.MaxY) / 2f;
            float bcx = (b.MinX + b.MaxX) / 2f, bcy = (b.MinY + b.MaxY) / 2f;

            float ax = Math.Clamp(bcx, a.MinX, a.MaxX);
            float ay = Math.Clamp(bcy, a.MinY, a.MaxY);
            float bx = Math.Clamp(acx, b.MinX, b.MaxX);
            float by = Math.Clamp(acy, b.MinY, b.MaxY);

            return Traversability.CanStep(vis,
                new BspFile.Vector3(ax, ay, NavGeometry.SurfaceZ(rider, ax, ay)),
                new BspFile.Vector3(bx, by, NavGeometry.SurfaceZ(landing, bx, by)));
        }

        /// <summary>
        /// Records each area in the other's connection list, in whichever direction they lie. Returns
        /// how many links were actually new.
        /// </summary>
        private static int LinkBothWays(NavFile nav, int i, int j)
        {
            return Link(nav, i, j) + Link(nav, j, i);
        }

        private static int Link(NavFile nav, int from, int to)
        {
            var a = nav.Areas[from];
            var b = nav.Areas[to];

            var boundsA = NavGeometry.GetBounds(a);
            var boundsB = NavGeometry.GetBounds(b);

            float dx = (boundsB.MinX + boundsB.MaxX) / 2f - (boundsA.MinX + boundsA.MaxX) / 2f;
            float dy = (boundsB.MinY + boundsB.MaxY) / 2f - (boundsA.MinY + boundsA.MaxY) / 2f;

            int direction = MathF.Abs(dx) >= MathF.Abs(dy)
                ? (dx >= 0 ? NavGeometry.East : NavGeometry.West)
                : (dy >= 0 ? NavGeometry.South : NavGeometry.North);

            if (a.Connections[direction].Contains(b.Id))
                return 0;

            a.Connections[direction].Add(b.Id);
            return 1;
        }

        /// <summary>
        /// The centre of every platform's floor at every height it rests at.
        ///
        /// Exposed for <see cref="AreaGenerator"/> to seed from: a lift is a route the engine's own
        /// generator cannot take, so the floor it serves is a prime candidate for having no areas at all
        /// - which is also why this connector so often finds nothing to connect.
        /// </summary>
        public static IEnumerable<Stop> PlatformStops(BspFile bsp, BspVisibility? vis = null)
        {
            foreach (var platform in FindPlatforms(bsp, vis))
            {
                float cx = (platform.Mins.X + platform.Maxs.X) / 2f;
                float cy = (platform.Mins.Y + platform.Maxs.Y) / 2f;

                foreach (float z in platform.Stops)
                    yield return new Stop(new BspFile.Vector3(cx, cy, z), platform.Depth);
            }
        }

        /// <summary>
        /// Vertical moving platforms and the heights a rider on each comes to rest at.
        ///
        /// `func_movelinear` states its travel explicitly, so both ends are known exactly. Track trains
        /// are driven by path_track nodes, so their stops are those nodes' heights. A func_door that
        /// moves vertically is a platform when it is shaped like one (<see cref="DoorStops"/>) and has
        /// ground beside it at both ends of its travel. Anything whose movement is not essentially
        /// vertical is skipped rather than guessed at.
        ///
        /// Read from the entity lump and the models, never from what the movement traces see: a func_door
        /// is walked through by those, as the engine's generator walks through every door, and a lift
        /// built from one still has to be found here.
        /// </summary>
        private static List<Platform> FindPlatforms(BspFile bsp, BspVisibility? vis)
        {
            var result = new List<Platform>();

            var models = bsp.BrushModelBounds;
            var pathTracks = new Dictionary<string, PathNode>(StringComparer.OrdinalIgnoreCase);

            // The quote-aware reader rather than a \{(.*?)\} regex, which ended an entity at the first
            // brace inside a value and read the rest of it as another.
            var entities = BspEntities.Parse(bsp.EntityLump);

            foreach (var entity in entities)
            {
                if (!Get(entity, "classname").Equals("path_track", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (Get(entity, "targetname") is { Length: > 0 } name)
                    pathTracks[name] = new PathNode(ParseVector(Get(entity, "origin")), Get(entity, "target"));
            }

            foreach (var entity in entities)
            {
                string classname = Get(entity, "classname").ToLowerInvariant();
                string modelName = Get(entity, "model");

                if (classname is not ("func_movelinear" or "func_plat" or "func_platrot" or "func_tracktrain"
                    or "func_door"))
                {
                    continue;
                }

                if (!modelName.StartsWith('*') || !int.TryParse(modelName[1..], out int modelIndex))
                    continue;

                if ((uint)modelIndex >= (uint)models.Length)
                    continue;

                var origin = ParseVector(Get(entity, "origin"));
                var model = models[modelIndex];

                var mins = new BspFile.Vector3(model.Mins.X + origin.X, model.Mins.Y + origin.Y, model.Mins.Z + origin.Z);
                var maxs = new BspFile.Vector3(model.Maxs.X + origin.X, model.Maxs.Y + origin.Y, model.Maxs.Z + origin.Z);

                // Where a rider stands, in the position the map was built with. The top of the model is
                // only a fallback, for when there is no geometry to trace.
                float floor = StandingSurface(vis, mins, maxs) ?? maxs.Z;

                float[]? stops = classname switch
                {
                    "func_movelinear" => LinearStops(entity, floor),
                    "func_plat" or "func_platrot" => PlatStops(entity, floor),
                    "func_tracktrain" => TrackStops(entity, pathTracks, floor - origin.Z),
                    "func_door" => DoorStops(entity, mins, maxs, floor),
                    _ => null,
                };

                if (stops is null || stops.Length == 0)
                    continue;

                // A door has to show it is a lift as well as being shaped like one: something to step
                // onto at every height it stops at. A hatch or a shutter that happens to be wide and
                // low drops into a recess or a slot, with nothing beside it at the far end.
                if (classname == "func_door" && !HasLandings(vis, mins, maxs, stops))
                    continue;

                result.Add(new Platform(mins, maxs, stops, classname, floor - mins.Z));
            }

            return result;
        }

        /// <summary>
        /// The height of the floor a rider stands on, traced down through the platform where the map
        /// put it, or null when nothing walkable is found inside its bounds.
        ///
        /// Traced against the sight mask on purpose, which is the one that still sees every brush
        /// entity: the movement traces walk through doors, so a lift built from a func_door would have
        /// no floor at all to them. Five points across the footprint, taking the middle height, so a
        /// railing along an edge or a control box in the centre does not stand in for the floor.
        /// </summary>
        internal static float? StandingSurface(BspVisibility? vis, BspFile.Vector3 mins, BspFile.Vector3 maxs)
        {
            if (vis is null)
                return null;

            var heights = new List<float>(5);
            float qx = (maxs.X - mins.X) / 4f, qy = (maxs.Y - mins.Y) / 4f;
            float cx = (mins.X + maxs.X) / 2f, cy = (mins.Y + maxs.Y) / 2f;

            ReadOnlySpan<(float X, float Y)> points =
            [
                (cx, cy), (cx - qx, cy - qy), (cx + qx, cy - qy), (cx - qx, cy + qy), (cx + qx, cy + qy),
            ];

            foreach (var (x, y) in points)
            {
                // A probe that begins inside something says nothing about the floor. The trace reports
                // a start in solid as a hit right there, facing up, which this took for a floor a unit
                // above the platform's own top whenever whatever sits on the platform's top face is
                // solid: 180 of the 210 probes on ctf_2fort, 825 of 1,050 on rp_downtown_tits_v25.
                if (vis.IsPointSolid(x, y, maxs.Z + 1f, BspVisibility.MaskBlockLos))
                    continue;

                if (!vis.TryTraceSurface(new BspFile.Vector3(x, y, maxs.Z + 1f), new BspFile.Vector3(x, y, mins.Z - 1f),
                        BspVisibility.MaskBlockLos, out var hit, out var normal))
                {
                    continue;
                }

                if (normal.Z >= NavConstants.SlopeLimit && hit.Z >= mins.Z - 0.5f)
                    heights.Add(hit.Z);
            }

            if (heights.Count == 0)
                return null;

            heights.Sort();
            return heights[heights.Count / 2];
        }

        /// <summary>
        /// Whether there is ground just off the platform at every stop - floor within
        /// <see cref="StopTolerance"/> of the stop, a short way out from the middle of one of its edges.
        /// Without geometry to look at, nothing can be confirmed and the answer is no.
        /// </summary>
        internal static bool HasLandings(BspVisibility? vis, BspFile.Vector3 mins, BspFile.Vector3 maxs,
            float[] stops)
        {
            if (vis is null)
                return false;

            float cx = (mins.X + maxs.X) / 2f, cy = (mins.Y + maxs.Y) / 2f;

            foreach (float stop in stops)
            {
                bool found = false;

                foreach (float reach in (ReadOnlySpan<float>)[NavConstants.HalfHumanWidth, LandingReach])
                {
                    ReadOnlySpan<(float X, float Y)> probes =
                    [
                        (cx, mins.Y - reach), (cx, maxs.Y + reach), (mins.X - reach, cy), (maxs.X + reach, cy),
                    ];

                    foreach (var (x, y) in probes)
                    {
                        // A probe that starts inside something is beside a wall, not a landing. The
                        // floor search reports a start in solid as floor right there, a step above the
                        // stop - well inside the tolerance - so a hatch dropping into a recess found
                        // "landings" in the recess walls at the bottom of its travel, the very shape
                        // this check is here to turn away.
                        if (vis.IsPointSolid(x, y, stop + NavConstants.StepHeight, BspVisibility.GroundMask))
                            continue;

                        if (StairMarker.TryFindFloor(vis, x, y, stop + NavConstants.StepHeight,
                                NavConstants.StepHeight + StopTolerance, out float z) &&
                            MathF.Abs(z - stop) <= StopTolerance)
                        {
                            found = true;
                            break;
                        }
                    }

                    if (found)
                        break;
                }

                if (!found)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Where a vertical func_door comes to rest, if it is a platform - or null for every door,
        /// shutter and gate that merely happens to move up or down, which is nearly all of them.
        ///
        /// The stops are CBaseDoor's two positions: where the map put it, and that moved along
        /// <c>movedir</c> by its own extent in that direction less <c>lip</c>. The engine takes the
        /// extent as OBBSize - 2 because it pads every brush model by a unit each side when loading;
        /// the bounds in the models lump are the unpadded ones, so here it is the plain height.
        ///
        /// A door that starts open (spawnflag 1, or <c>spawnpos</c> 1 as newer FGDs write it) spawns
        /// at the second position and travels back to the first. The two heights are the same either
        /// way; the order says which one it rests at when the map starts, and that one is listed first.
        /// </summary>
        internal static float[]? DoorStops(IReadOnlyDictionary<string, string> entity, BspFile.Vector3 mins,
            BspFile.Vector3 maxs, float floor)
        {
            var moveDir = ParseVector(Get(entity, "movedir"));

            // movedir is pitch/yaw/roll, as for func_movelinear: pitch -90 is up, 90 down
            float pitch = moveDir.X;
            if (MathF.Abs(MathF.Abs(pitch) - 90f) > 5f)
                return null;

            if (!IsLiftShaped(mins, maxs))
                return null;

            float travel = (maxs.Z - mins.Z) - ParseFloat(Get(entity, "lip"));
            if (travel < MinimumTravel)
                return null;

            float moved = floor + (pitch < 0 ? travel : -travel);

            bool startsOpen = (ParseInt(Get(entity, "spawnflags")) & 1) != 0 || Get(entity, "spawnpos") == "1";

            return startsOpen ? [moved, floor] : [floor, moved];
        }

        /// <summary>
        /// Whether a brush model is shaped like something to stand on and ride rather than a door: wide
        /// both ways, and lower than a player is tall.
        ///
        /// A slab, in other words. The vertical func_doors on the roleplay maps measured are garage
        /// doors and shutters - 128 wide, 124 tall and 2 to 10 deep - and a box elevator car 142 tall,
        /// whose floor cannot be told from its roof from outside. A door has to be at least a doorway
        /// high to be one; a platform, railings and all, is not.
        /// </summary>
        internal static bool IsLiftShaped(BspFile.Vector3 mins, BspFile.Vector3 maxs)
        {
            float narrow = MathF.Min(maxs.X - mins.X, maxs.Y - mins.Y);
            float height = maxs.Z - mins.Z;

            return narrow >= MinimumLiftWidth && height < NavConstants.HumanHeight;
        }

        /// <summary>
        /// func_movelinear travels `movedistance` along `movedir`. Only the vertical component is of
        /// interest; a horizontal mover is a sliding door, not a lift.
        /// </summary>
        private static float[]? LinearStops(IReadOnlyDictionary<string, string> entity, float topZ)
        {
            var moveDir = ParseVector(Get(entity, "movedir"));
            float distance = ParseFloat(Get(entity, "movedistance"));
            float start = ParseFloat(Get(entity, "startposition"));

            // movedir is given as pitch/yaw/roll; pure vertical movement is pitch +/- 90
            float pitch = moveDir.X;
            if (MathF.Abs(MathF.Abs(pitch) - 90f) > 5f)
                return null;

            float travel = MathF.Abs(distance);
            if (travel < MinimumTravel)
                return null;

            // startposition is the fraction of travel the platform spawned at
            float sign = pitch < 0 ? 1f : -1f;
            float closedZ = topZ - sign * travel * start;

            return [closedZ, closedZ + sign * travel];
        }

        private static float[]? PlatStops(IReadOnlyDictionary<string, string> entity, float topZ)
        {
            float height = ParseFloat(Get(entity, "height"));
            if (MathF.Abs(height) < MinimumTravel)
                return null;

            return [topZ, topZ - MathF.Abs(height)];
        }

        /// <summary>
        /// A track train rests at its path_track nodes, so its stops are those nodes' heights. The chain
        /// is followed from the train's start node via each node's own target, which terminates either
        /// at an unnamed end or by revisiting a node on a looping track.
        /// </summary>
        private static float[]? TrackStops(IReadOnlyDictionary<string, string> entity,
            Dictionary<string, PathNode> pathTracks, float topOffset)
        {
            string current = Get(entity, "target");
            if (current.Length == 0 || pathTracks.Count == 0)
                return null;

            var heights = new List<float>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (current.Length > 0 && seen.Add(current) && pathTracks.TryGetValue(current, out var node))
            {
                float top = node.Origin.Z + topOffset;
                if (!heights.Exists(h => MathF.Abs(h - top) < 8f))
                    heights.Add(top);

                current = node.Target;
            }

            if (heights.Count < 2)
                return null;

            float span = 0;
            for (int i = 1; i < heights.Count; i++)
                span = MathF.Max(span, MathF.Abs(heights[i] - heights[0]));

            // a train that never changes height is transport, not a lift
            return span >= MinimumTravel ? heights.ToArray() : null;
        }

        private readonly record struct PathNode(BspFile.Vector3 Origin, string Target);

        private static string Get(IReadOnlyDictionary<string, string> entity, string key)
            => entity.TryGetValue(key, out string? value) ? value : string.Empty;

        private static float ParseFloat(string value)
            => float.TryParse(value, CultureInfo.InvariantCulture, out float f) ? f : 0f;

        private static int ParseInt(string value)
            => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : 0;

        private static BspFile.Vector3 ParseVector(string value)
        {
            var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return default;

            return new BspFile.Vector3(ParseFloat(parts[0]), ParseFloat(parts[1]), ParseFloat(parts[2]));
        }
    }
}
