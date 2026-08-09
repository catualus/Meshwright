using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NavPal
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
    /// </summary>
    public static class ElevatorConnector
    {
        /// <summary>How far out from the platform edge to look for a landing.</summary>
        private const float LandingReach = 40f;

        /// <summary>Vertical slack when matching an area to a platform stop.</summary>
        private const float StopTolerance = 24f;

        /// <summary>A platform that moves less than this is a door or a piston, not a lift.</summary>
        private const float MinimumTravel = 32f;

        public sealed class Result
        {
            public int Platforms;
            public int Stops;
            public int Connections;
            public readonly List<string> Notes = [];
        }

        private readonly record struct Platform(BspFile.Vector3 Mins, BspFile.Vector3 Maxs, float[] Stops, string Class);

        public static Result Build(NavFile nav, BspFile bsp)
        {
            var result = new Result();
            var platforms = FindPlatforms(bsp);
            if (platforms.Count == 0)
                return result;

            var index = new NavGeometry.Index(nav.Areas);
            result.Platforms = platforms.Count;

            foreach (var platform in platforms)
            {
                foreach (float top in platform.Stops)
                {
                    result.Stops++;

                    // areas riding the platform at this height
                    var onboard = new List<int>();
                    foreach (int i in index.Overlapping(platform.Mins.X, platform.Mins.Y, platform.Maxs.X, platform.Maxs.Y))
                    {
                        var a = nav.Areas[i];
                        var b = NavGeometry.GetBounds(a);
                        float cx = (b.MinX + b.MaxX) / 2f, cy = (b.MinY + b.MaxY) / 2f;

                        if (MathF.Abs(NavGeometry.SurfaceZ(a, cx, cy) - top) <= StopTolerance)
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
        /// Vertical moving platforms and the heights their top surface comes to rest at.
        ///
        /// `func_movelinear` states its travel explicitly, so both ends are known exactly. Track trains
        /// are driven by path_track nodes, so their stops are those nodes' heights. Anything whose
        /// movement is not essentially vertical is skipped rather than guessed at.
        /// </summary>
        /// <summary>
        /// The centre of every platform at every height it rests at.
        ///
        /// Exposed for <see cref="AreaGenerator"/> to seed from: a lift is a route the engine's own
        /// generator cannot take, so the floor it serves is a prime candidate for having no areas at all
        /// - which is also why this connector so often finds nothing to connect.
        /// </summary>
        public static IEnumerable<BspFile.Vector3> PlatformStops(BspFile bsp)
        {
            foreach (var platform in FindPlatforms(bsp))
            {
                float cx = (platform.Mins.X + platform.Maxs.X) / 2f;
                float cy = (platform.Mins.Y + platform.Maxs.Y) / 2f;

                foreach (float z in platform.Stops)
                    yield return new BspFile.Vector3(cx, cy, z);
            }
        }

        private static List<Platform> FindPlatforms(BspFile bsp)
        {
            var result = new List<Platform>();

            var models = bsp.BrushModelBounds;
            var pathTracks = new Dictionary<string, PathNode>(StringComparer.OrdinalIgnoreCase);

            foreach (var entity in Entities(bsp.EntityLump))
            {
                if (!Get(entity, "classname").Equals("path_track", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (Get(entity, "targetname") is { Length: > 0 } name)
                    pathTracks[name] = new PathNode(ParseVector(Get(entity, "origin")), Get(entity, "target"));
            }

            foreach (var entity in Entities(bsp.EntityLump))
            {
                string classname = Get(entity, "classname");
                string modelName = Get(entity, "model");

                if (!modelName.StartsWith('*') || !int.TryParse(modelName[1..], out int modelIndex))
                    continue;

                if ((uint)modelIndex >= (uint)models.Length)
                    continue;

                var origin = ParseVector(Get(entity, "origin"));
                var model = models[modelIndex];

                var mins = new BspFile.Vector3(model.Mins.X + origin.X, model.Mins.Y + origin.Y, model.Mins.Z + origin.Z);
                var maxs = new BspFile.Vector3(model.Maxs.X + origin.X, model.Maxs.Y + origin.Y, model.Maxs.Z + origin.Z);

                float[]? stops = classname.ToLowerInvariant() switch
                {
                    "func_movelinear" => LinearStops(entity, maxs.Z),
                    "func_plat" or "func_platrot" => PlatStops(entity, maxs.Z),
                    "func_tracktrain" => TrackStops(entity, pathTracks, maxs.Z - origin.Z),
                    _ => null,
                };

                if (stops is null || stops.Length == 0)
                    continue;

                result.Add(new Platform(mins, maxs, stops, classname));
            }

            return result;
        }

        /// <summary>
        /// func_movelinear travels `movedistance` along `movedir`. Only the vertical component is of
        /// interest; a horizontal mover is a sliding door, not a lift.
        /// </summary>
        private static float[]? LinearStops(Dictionary<string, string> entity, float topZ)
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

        private static float[]? PlatStops(Dictionary<string, string> entity, float topZ)
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
        private static float[]? TrackStops(Dictionary<string, string> entity,
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

        private static IEnumerable<Dictionary<string, string>> Entities(string lump)
        {
            foreach (Match block in Regex.Matches(lump, @"\{(.*?)\}", RegexOptions.Singleline))
            {
                var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match pair in Regex.Matches(block.Groups[1].Value, "\"([^\"]*)\"\\s*\"([^\"]*)\""))
                    kv[pair.Groups[1].Value] = pair.Groups[2].Value;

                yield return kv;
            }
        }

        private static string Get(Dictionary<string, string> entity, string key)
            => entity.TryGetValue(key, out string? value) ? value : string.Empty;

        private static float ParseFloat(string value)
            => float.TryParse(value, CultureInfo.InvariantCulture, out float f) ? f : 0f;

        private static BspFile.Vector3 ParseVector(string value)
        {
            var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return default;

            return new BspFile.Vector3(ParseFloat(parts[0]), ParseFloat(parts[1]), ParseFloat(parts[2]));
        }
    }
}
