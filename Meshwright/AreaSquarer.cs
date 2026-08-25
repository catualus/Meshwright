using System;
using System.Collections.Generic;

namespace Meshwright
{
    /// <summary>
    /// Splits long thin areas into roughly square ones - Valve's <c>SquareUpAreas</c>, with its
    /// <c>splitX</c> and <c>splitY</c>.
    ///
    /// Growing a rectangle greedily across nodes produces slivers: a corridor becomes one area hundreds
    /// of units long and one node wide, and a large open floor becomes a few enormous plates. Measured
    /// against Valve's own gm_construct mesh, whose areas are the same algorithm's output after this
    /// pass has run, the difference was stark - aspect ratio at the 90th percentile 13 against their 3,
    /// and a largest area of 4,625 units against their 1,275.
    ///
    /// That matters beyond tidiness. Pathing quality aside, an area that spans a whole staircase and the
    /// floor either side of it averages into one gentle plane, which is why the stair pass found almost
    /// nothing on a generated mesh: there was no area whose extent was *just* the stairs.
    /// </summary>
    public static class AreaSquarer
    {
        /// <summary>
        /// How square is square enough.
        ///
        /// Taken from what Valve's finished mesh actually looks like rather than from the stopping rule
        /// their code appears to describe. gm_construct's own areas have a median aspect ratio of 2.0
        /// and 3.0 at the 90th percentile, so they are plainly not split until nearly square - a 1.2
        /// tolerance turned 2,544 areas into 53,961, more than twenty times Valve's count.
        /// </summary>
        private const float AspectTolerance = 3f;

        /// <summary>
        /// Areas shorter than this are left alone whatever their shape. A small area being twice as
        /// long as it is wide is not a sliver worth cutting, and Valve's own median longest side is
        /// 125 units.
        /// </summary>
        private const float SplitThreshold = 200f;

        /// <summary>
        /// Never split below one sampling step. Areas are built from nodes a step apart, so halves
        /// thinner than that describe ground no sample ever looked at.
        /// </summary>
        private const float MinimumExtent = NavConstants.GenerationStepSize;

        /// <summary>Guard against a pathological area splitting without end.</summary>
        private const int MaxDepth = 12;

        public sealed class Result
        {
            public int Split;
            public int Created;
        }

        /// <param name="firstGeneratedId">
        /// The lowest id this run created; areas below it are passed through untouched. Splitting one
        /// is not a tidy-up but a rewrite - the original is discarded and replaced by two areas with
        /// fresh ids, and because there is no single successor to repoint to, every reference to it is
        /// lost. On a mesh generated here that costs nothing, because the split happens before any
        /// connection exists. On a mesh someone edited in game it costs the connections, hiding spots,
        /// encounters and ladder links that were already on the area, and cuts a hand-drawn shape in
        /// half for a reason that only applies to greedily grown rectangles. Zero - the default - means
        /// every area is treated as generated, which is what a mesh built from scratch is.
        /// </param>
        public static Result SquareUp(NavFile nav, uint firstGeneratedId = 0)
        {
            var result = new Result();

            uint nextId = 1;
            foreach (var area in nav.Areas)
                nextId = Math.Max(nextId, area.Id + 1);

            var output = new List<NavArea>(nav.Areas.Count);

            foreach (var area in nav.Areas)
            {
                if (area.Id < firstGeneratedId)
                {
                    output.Add(area);
                    continue;
                }

                int before = output.Count;
                Split(area, output, ref nextId, 0);

                if (output.Count - before > 1)
                {
                    result.Split++;
                    result.Created += output.Count - before - 1;
                }
            }

            nav.Areas.Clear();
            nav.Areas.AddRange(output);

            return result;
        }

        private static void Split(NavArea area, List<NavArea> output, ref uint nextId, int depth)
        {
            var b = NavGeometry.GetBounds(area);
            float width = b.Width;
            float depthY = b.Depth;

            float longest = MathF.Max(width, depthY);
            bool squareEnough = longest <= MathF.Min(width, depthY) * AspectTolerance;

            // Splitting purely on size was tried here and removed again. The idea was that the huge
            // square plates the merge can produce were the reason whole rooms came out unconnected -
            // one 1,190x1,063 quad covering a sewer had no connections at all. Cutting everything back
            // to Valve's 90th-percentile longest side did shrink the plates (max side 1,300 -> 375) and
            // did not move isolation at all (2,208 areas -> 2,240 on rp_downtown_meowy), so the size
            // was not the cause and the rule was buying nothing but an 8% larger mesh. Valve splits on
            // aspect alone; so does this.
            if (depth >= MaxDepth || squareEnough ||
                longest < SplitThreshold ||
                longest < MinimumExtent * 2f)
            {
                output.Add(area);
                return;
            }

            var (first, second) = width > depthY
                ? SplitAlongX(area, ref nextId)
                : SplitAlongY(area, ref nextId);

            Split(first, output, ref nextId, depth + 1);
            Split(second, output, ref nextId, depth + 1);
        }

        /// <summary>
        /// Cuts at the midpoint in X. Corner heights on the new edge are interpolated along the two
        /// edges being cut, so both halves keep sitting on the same surface the original described.
        /// </summary>
        private static (NavArea, NavArea) SplitAlongX(NavArea area, ref uint nextId)
        {
            float x0 = area.NwCorner[0], y0 = area.NwCorner[1];
            float x1 = area.SeCorner[0], y1 = area.SeCorner[1];
            float mid = (x0 + x1) / 2f;

            float nw = area.NwCorner[2], ne = area.NeZ, se = area.SeCorner[2], sw = area.SwZ;

            float midNorth = (nw + ne) / 2f;
            float midSouth = (sw + se) / 2f;

            var left = Make(area, ref nextId, x0, y0, mid, y1, nw, midNorth, midSouth, sw);
            var right = Make(area, ref nextId, mid, y0, x1, y1, midNorth, ne, se, midSouth);

            return (left, right);
        }

        private static (NavArea, NavArea) SplitAlongY(NavArea area, ref uint nextId)
        {
            float x0 = area.NwCorner[0], y0 = area.NwCorner[1];
            float x1 = area.SeCorner[0], y1 = area.SeCorner[1];
            float mid = (y0 + y1) / 2f;

            float nw = area.NwCorner[2], ne = area.NeZ, se = area.SeCorner[2], sw = area.SwZ;

            float midWest = (nw + sw) / 2f;
            float midEast = (ne + se) / 2f;

            var north = Make(area, ref nextId, x0, y0, x1, mid, nw, ne, midEast, midWest);
            var south = Make(area, ref nextId, x0, mid, x1, y1, midWest, midEast, se, sw);

            return (north, south);
        }

        /// <summary>
        /// Builds one half. Both halves take fresh ids: this runs before any connections exist, so no
        /// reference to the original can be left dangling, and handing out new ids throughout avoids
        /// having to track which originals have already been reused.
        /// </summary>
        private static NavArea Make(NavArea source, ref uint nextId,
            float x0, float y0, float x1, float y1, float nw, float ne, float se, float sw)
        {
            var area = new NavArea
            {
                Id = nextId++,
                AttributeFlags = source.AttributeFlags,
                PlaceIndex = source.PlaceIndex,
            };

            area.NwCorner[0] = x0; area.NwCorner[1] = y0; area.NwCorner[2] = nw;
            area.SeCorner[0] = x1; area.SeCorner[1] = y1; area.SeCorner[2] = se;
            area.NeZ = ne;
            area.SwZ = sw;

            return area;
        }
    }
}
