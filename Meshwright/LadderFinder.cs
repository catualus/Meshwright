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

            /// <summary>The two candidate directions along the climbing axis.</summary>
            public (uint A, uint B) CandidateDirections =>
                MathF.Abs(Normal.X) > MathF.Abs(Normal.Y) ? (1u, 3u) : (2u, 0u);

            public string DirectionName => Direction switch
            {
                0 => "north", 1 => "east", 2 => "south", 3 => "west", _ => "?",
            };

            public string AxisName => MathF.Abs(Normal.X) > MathF.Abs(Normal.Y) ? "E/W" : "N/S";
        }

        public static List<LadderBrush> Find(BspFile bsp)
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
                if (ladder.Height < MinimumLadderHeight)
                    continue;

                results.Add(ladder);
            }

            // tallest first, so the most significant ladders are easiest to eyeball in output
            return results.OrderByDescending(l => l.Height).ToList();
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
