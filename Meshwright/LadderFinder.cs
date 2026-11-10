using System;
using System.Collections.Generic;
using System.Linq;

namespace Meshwright
{
    /// <summary>
    /// Finds climbable brushes in a BSP so nav ladders can be built for them.
    ///
    /// Why this exists: CNavMesh::BuildLadders() has its entire body wrapped in #ifdef TERROR, so
    /// outside Left 4 Dead it destroys existing ladders and does nothing else. Even where enabled it
    /// only searches for func_simpleladder entities. Maps that build ladders as brushes textured with
    /// tools/toolsinvisibleladder - the standard HL2/GMod approach - therefore never get nav ladders
    /// from any Source game.
    /// </summary>
    public static class LadderFinder
    {
        /// <summary>
        /// Materials that mark a climbable surface. Matched case-insensitively as a substring so both
        /// "TOOLS/TOOLSINVISIBLELADDER" and "tools/toolsinvisibleladder" hit.
        /// </summary>
        private static readonly string[] LadderMaterials =
        [
            "toolsinvisibleladder",
            "toolsladder",
        ];

        /// <summary>
        /// CONTENTS_LADDER from bspflags.h. This is the authoritative marker: vbsp resolves the
        /// toolsinvisibleladder material into a contents flag on the brush and the tool material itself
        /// does not necessarily survive into the compiled texdata, so matching on material name alone
        /// finds nothing on a real map.
        /// </summary>
        private const int ContentsLadder = 0x20000000;

        /// <summary>Below this height a climbable brush is more likely a decal or trim than a ladder.</summary>
        private const float MinimumLadderHeight = 32f;

        public sealed class LadderBrush
        {
            public required string Material { get; init; }
            public required BspFile.Vector3 Mins { get; init; }
            public required BspFile.Vector3 Maxs { get; init; }

            public float Height => Maxs.Z - Mins.Z;

            /// <summary>Horizontal extent across the ladder face.</summary>
            public float Width => Math.Max(Maxs.X - Mins.X, Maxs.Y - Mins.Y);

            public BspFile.Vector3 Bottom => new((Mins.X + Maxs.X) / 2f, (Mins.Y + Maxs.Y) / 2f, Mins.Z);
            public BspFile.Vector3 Top => new((Mins.X + Maxs.X) / 2f, (Mins.Y + Maxs.Y) / 2f, Maxs.Z);

            /// <summary>
            /// CNavLadder NavDirType: 0 north (-Y), 1 east (+X), 2 south (+Y), 3 west (-X). Taken from
            /// the outward normal of the brush's largest vertical face, so it is correct for angled
            /// ladders rather than inferred from which axis happens to be thinner.
            /// </summary>
            public uint Direction { get; init; }

            /// <summary>Outward normal of the climbing face.</summary>
            public BspFile.Vector3 Normal { get; init; }

            /// <summary>True if the brush is not axis aligned, so bounds came from plane intersection.</summary>
            public bool IsAngled { get; init; }

            /// <summary>
            /// True while only the axis is known and the sign is a guess. Resolved once the ladder is
            /// matched against nav areas, since the open side is whichever one has floor at the bottom.
            /// </summary>
            public bool DirectionIsProvisional { get; init; }

            /// <summary>
            /// False when not even the axis is known: a func_useableladder is two points a climber's
            /// origin travels between, with nothing to say which way the ladder faces. LadderBuilder then
            /// tries all four directions instead of the two along <see cref="Normal"/>.
            /// </summary>
            public bool AxisKnown { get; init; } = true;

            /// <summary>
            /// How many brushes this ladder was built from: more than one when a ladder was made of
            /// pieces stacked one above another (see <see cref="MergeStacked"/>).
            /// </summary>
            public int Pieces { get; init; } = 1;

            /// <summary>The two candidate directions along the climbing axis.</summary>
            public (uint A, uint B) CandidateDirections =>
                MathF.Abs(Normal.X) > MathF.Abs(Normal.Y) ? (1u, 3u) : (2u, 0u);

            public string DirectionName => Direction switch
            {
                0 => "north", 1 => "east", 2 => "south", 3 => "west", _ => "?",
            };

            public string AxisName => MathF.Abs(Normal.X) > MathF.Abs(Normal.Y) ? "E/W" : "N/S";
        }

        /// <summary>
        /// Every ladder in the map: climbable brushes, stacked pieces merged into one, and the
        /// func_useableladder entities no brush already covers.
        ///
        /// <paramref name="vis"/> is what tells a split in mid-air from a split at a floor (see
        /// <see cref="MergeStacked"/>); without it, stacked pieces are always joined.
        /// </summary>
        public static List<LadderBrush> Find(BspFile bsp, BspVisibility? vis = null)
        {
            // The height filter waits until the pieces are merged: a short piece is not a ladder on its
            // own, but it is part of one when it sits on top of another.
            var ladders = MergeStacked(FindBrushes(bsp), vis)
                .Where(l => l.Height >= MinimumLadderHeight)
                .ToList();

            ladders.AddRange(FindUseable(bsp.EntityLump, ladders));

            // tallest first, so the most significant ladders are easiest to eyeball in output
            return ladders.OrderByDescending(l => l.Height).ToList();
        }

        /// <summary>How far apart two stacked pieces may be vertically and still be one ladder.</summary>
        private const float StackGap = 8f;

        /// <summary>
        /// Joins ladder brushes that are pieces of one ladder: the same thin axis, overlapping in plan,
        /// and meeting vertically - touching, overlapping, or within <see cref="StackGap"/>.
        ///
        /// Each CONTENTS_LADDER brush used to become a ladder of its own, and a ladder stacked from two
        /// brushes then became two half ladders that LadderBuilder could not finish: the lower one has
        /// no floor at its top, which is mid-air, and the upper one no floor at its base, so it was
        /// skipped outright and the climb could not be made at all. Long climbs are often built in
        /// pieces, and a ladder is often split where it passes a floor.
        ///
        /// A split at a floor is left alone. Each half has ground at both ends and already works, and a
        /// nav ladder has only a bottom and a top: joined, the climb would pass the floor in the middle
        /// with no way off onto it. So pieces are only joined when nothing walkable lies beside the
        /// ladder where they meet, which needs <paramref name="vis"/> to see.
        /// </summary>
        internal static List<LadderBrush> MergeStacked(List<LadderBrush> pieces, BspVisibility? vis = null)
        {
            var ladders = new List<LadderBrush>(pieces);

            for (bool merged = true; merged;)
            {
                merged = false;

                for (int i = 0; i < ladders.Count && !merged; i++)
                {
                    for (int j = i + 1; j < ladders.Count; j++)
                    {
                        if (!AreStacked(ladders[i], ladders[j]) || FloorWhereTheyMeet(vis, ladders[i], ladders[j]))
                            continue;

                        ladders[i] = Join(ladders[i], ladders[j]);
                        ladders.RemoveAt(j);
                        merged = true;
                        break;
                    }
                }
            }

            return ladders;
        }

        private static bool AreStacked(LadderBrush a, LadderBrush b)
        {
            if (a.AxisName != b.AxisName)
                return false;

            // Overlapping in plan, by more than a sliver: two ladders that merely touch at a corner
            // are two ladders.
            const float MinimumOverlap = 1f;

            float overlapX = MathF.Min(a.Maxs.X, b.Maxs.X) - MathF.Max(a.Mins.X, b.Mins.X);
            float overlapY = MathF.Min(a.Maxs.Y, b.Maxs.Y) - MathF.Max(a.Mins.Y, b.Mins.Y);

            if (overlapX < MinimumOverlap || overlapY < MinimumOverlap)
                return false;

            float gap = MathF.Max(a.Mins.Z, b.Mins.Z) - MathF.Min(a.Maxs.Z, b.Maxs.Z);
            return gap <= StackGap;
        }

        /// <summary>
        /// Whether there is a floor beside the ladder, on either side of its thin axis, within a step
        /// of where two pieces meet. A side that is inside solid there - the wall the ladder is fixed
        /// to - is not looked at, since a floor search starting in solid finds the solid.
        /// </summary>
        private static bool FloorWhereTheyMeet(BspVisibility? vis, LadderBrush a, LadderBrush b)
        {
            if (vis is null)
                return false;

            var (lower, upper) = a.Mins.Z <= b.Mins.Z ? (a, b) : (b, a);

            float top = MathF.Max(lower.Maxs.Z, upper.Mins.Z) + NavConstants.StepHeight;
            float bottom = MathF.Min(lower.Maxs.Z, upper.Mins.Z) - NavConstants.StepHeight;

            float minX = MathF.Min(a.Mins.X, b.Mins.X), maxX = MathF.Max(a.Maxs.X, b.Maxs.X);
            float minY = MathF.Min(a.Mins.Y, b.Mins.Y), maxY = MathF.Max(a.Maxs.Y, b.Maxs.Y);
            float cx = (minX + maxX) / 2f, cy = (minY + maxY) / 2f;

            // A climber's width out from the face on each side.
            const float Out = NavConstants.HumanWidth;

            ReadOnlySpan<(float X, float Y)> sides = a.AxisName == "E/W"
                ? [(minX - Out, cy), (maxX + Out, cy)]
                : [(cx, minY - Out), (cx, maxY + Out)];

            foreach (var (x, y) in sides)
            {
                if (vis.IsPointSolid(x, y, top, BspVisibility.GroundMask))
                    continue;

                if (StairMarker.TryFindFloor(vis, x, y, top, top - bottom, out _))
                    return true;
            }

            return false;
        }

        private static LadderBrush Join(LadderBrush a, LadderBrush b)
        {
            var mins = new BspFile.Vector3(MathF.Min(a.Mins.X, b.Mins.X), MathF.Min(a.Mins.Y, b.Mins.Y),
                MathF.Min(a.Mins.Z, b.Mins.Z));
            var maxs = new BspFile.Vector3(MathF.Max(a.Maxs.X, b.Maxs.X), MathF.Max(a.Maxs.Y, b.Maxs.Y),
                MathF.Max(a.Maxs.Z, b.Maxs.Z));

            return new LadderBrush
            {
                Material = a.Material,
                Mins = mins,
                Maxs = maxs,
                Normal = a.Normal,
                Direction = a.Direction,
                DirectionIsProvisional = a.DirectionIsProvisional,
                IsAngled = a.IsAngled || b.IsAngled,
                Pieces = a.Pieces + b.Pieces,
            };
        }

        /// <summary>
        /// HL2-style ladders: func_useableladder, a point entity whose <c>point0</c> and
        /// <c>point1</c> are where a climber's origin is at the top and the bottom. Nothing read these,
        /// so a map using them - common in content built for Half-Life 2 - had no nav ladder there, and
        /// whatever the ladder led to was unconnected.
        ///
        /// Emitted as a box a player wide around the line between the two points, with the axis left
        /// open: the climber stands in front of the ladder, and which side of them it is cannot be read
        /// off the entity. LadderBuilder settles it from where the floor is, and moves the ladder onto
        /// the face once it knows.
        ///
        /// The points are taken as world positions when they sit near the entity, which is what Hammer's
        /// sweptplayerhull helper writes - gm_construct's are 24 units from its origin in plan, and read
        /// as offsets they would put the ladder 3,000 units away - and as offsets from the origin
        /// otherwise, which is what CFuncLadder makes of them once spawned.
        ///
        /// Skipped when a ladder brush already covers it. gm_construct's ladder is both, and two nav
        /// ladders on one climb is one too many.
        /// </summary>
        internal static IEnumerable<LadderBrush> FindUseable(string entityLump, IReadOnlyList<LadderBrush> brushes)
        {
            foreach (var entity in BspEntities.Parse(entityLump))
            {
                if (!entity.TryGetValue("classname", out string? classname) ||
                    !classname.Equals("func_useableladder", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!TryVector(entity, "point0", out var a) || !TryVector(entity, "point1", out var b))
                    continue;

                var origin = TryVector(entity, "origin", out var o) ? o : default;

                var mid = new BspFile.Vector3((a.X + b.X) / 2f, (a.Y + b.Y) / 2f, (a.Z + b.Z) / 2f);

                if (Distance(mid, origin) > Distance(mid, default))
                {
                    a = Add(a, origin);
                    b = Add(b, origin);
                }

                var low = a.Z <= b.Z ? a : b;
                var high = a.Z <= b.Z ? b : a;

                if (high.Z - low.Z < MinimumLadderHeight)
                    continue;

                float cx = (low.X + high.X) / 2f, cy = (low.Y + high.Y) / 2f;
                const float Half = NavConstants.HalfHumanWidth;

                var ladder = new LadderBrush
                {
                    Material = "func_useableladder",
                    Mins = new BspFile.Vector3(cx - Half, cy - Half, low.Z),
                    Maxs = new BspFile.Vector3(cx + Half, cy + Half, high.Z),
                    Normal = new BspFile.Vector3(1, 0, 0),
                    Direction = 1,
                    DirectionIsProvisional = true,
                    AxisKnown = false,
                };

                if (brushes.Any(brush => Covers(brush, ladder)))
                    continue;

                yield return ladder;
            }
        }

        /// <summary>
        /// Whether a ladder brush is already the ladder a useable one describes: within a player's width
        /// of it in plan, and overlapping it in height.
        /// </summary>
        private static bool Covers(LadderBrush brush, LadderBrush useable)
        {
            const float Reach = NavConstants.HumanWidth;

            float cx = (useable.Mins.X + useable.Maxs.X) / 2f, cy = (useable.Mins.Y + useable.Maxs.Y) / 2f;

            float dx = MathF.Max(0, MathF.Max(brush.Mins.X - cx, cx - brush.Maxs.X));
            float dy = MathF.Max(0, MathF.Max(brush.Mins.Y - cy, cy - brush.Maxs.Y));

            return dx <= Reach && dy <= Reach &&
                   MathF.Min(brush.Maxs.Z, useable.Maxs.Z) > MathF.Max(brush.Mins.Z, useable.Mins.Z);
        }

        private static bool TryVector(Dictionary<string, string> entity, string key, out BspFile.Vector3 value)
        {
            value = default;

            if (!entity.TryGetValue(key, out string? text))
                return false;

            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var c = System.Globalization.CultureInfo.InvariantCulture;

            if (parts.Length < 3 ||
                !float.TryParse(parts[0], c, out float x) ||
                !float.TryParse(parts[1], c, out float y) ||
                !float.TryParse(parts[2], c, out float z))
            {
                return false;
            }

            value = new BspFile.Vector3(x, y, z);
            return true;
        }

        private static float Distance(BspFile.Vector3 a, BspFile.Vector3 b)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static BspFile.Vector3 Add(BspFile.Vector3 a, BspFile.Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        /// <summary>Every climbable brush, one ladder per brush and of any height.</summary>
        private static List<LadderBrush> FindBrushes(BspFile bsp)
        {
            var results = new List<LadderBrush>();

            foreach (var brush in bsp.Brushes)
            {
                bool isLadder = (brush.Contents & ContentsLadder) != 0;

                // fall back to the material name for maps where the tool texture did survive
                string? material = GetLadderMaterial(bsp, brush);
                if (!isLadder && material is null)
                    continue;

                // Plane intersection first: it is correct for angled brushes, where reading bounds off
                // the axis planes would report the whole wall the ladder spans. Fall back to the axis
                // method only if the brush is degenerate or unbounded.
                bool angled = false;
                if (BrushGeometry.TryGetBounds(bsp, brush, out var mins, out var maxs))
                {
                    if (bsp.TryGetBrushBounds(brush, out var axisMins, out var axisMaxs))
                    {
                        angled = MathF.Abs(axisMins.X - mins.X) > 0.5f || MathF.Abs(axisMaxs.X - maxs.X) > 0.5f
                              || MathF.Abs(axisMins.Y - mins.Y) > 0.5f || MathF.Abs(axisMaxs.Y - maxs.Y) > 0.5f
                              || MathF.Abs(axisMins.Z - mins.Z) > 0.5f || MathF.Abs(axisMaxs.Z - maxs.Z) > 0.5f;
                    }
                }
                else if (!bsp.TryGetBrushBounds(brush, out mins, out maxs))
                {
                    continue;
                }

                // Axis of the climbing face = the thin horizontal axis. A ladder volume is wide across
                // the face and shallow into the wall.
                //
                // Picking the "most horizontal" face normal does NOT work: a box has four vertical faces
                // all with a horizontal component of 1.0, so it just returns whichever came first in
                // side order - which is why every ladder initially reported the same direction.
                //
                // The sign (which of the two opposing directions faces open space) cannot be determined
                // from the brush alone; it is resolved in the area-connection step by seeing which side
                // has a nav area at the bottom.
                bool thinOnX = (maxs.X - mins.X) <= (maxs.Y - mins.Y);
                var normal = thinOnX
                    ? new BspFile.Vector3(1, 0, 0)
                    : new BspFile.Vector3(0, 1, 0);

                var ladder = new LadderBrush
                {
                    Material = material ?? "(CONTENTS_LADDER)",
                    Mins = mins,
                    Maxs = maxs,
                    Normal = normal,
                    Direction = BrushGeometry.ToNavDirection(normal),
                    DirectionIsProvisional = true,
                    IsAngled = angled,
                };

                results.Add(ladder);
            }

            return results;
        }

        private static string? GetLadderMaterial(BspFile bsp, BspFile.Brush brush)
        {
            for (int i = 0; i < brush.NumSides; i++)
            {
                int index = brush.FirstSide + i;
                if (index < 0 || index >= bsp.BrushSides.Length)
                    continue;

                string name = bsp.GetMaterialName(bsp.BrushSides[index]);
                if (name.Length == 0)
                    continue;

                foreach (var marker in LadderMaterials)
                {
                    if (name.Contains(marker, StringComparison.OrdinalIgnoreCase))
                        return name;
                }
            }

            return null;
        }
    }
}
