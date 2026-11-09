using System;

namespace Meshwright
{
    /// <summary>
    /// Whether a walker could actually get from one sampled point to the one beside it.
    ///
    /// Grid adjacency is not reachability. Two sample points a step apart can sit on opposite sides of a
    /// wall, on the two floors of a doorway's frame, or either side of a railing, and every one of those
    /// reads as "adjacent, similar height, both standable" to a test that only looks at the two ends.
    /// Deciding on the ends alone is what let areas grow straight through vertical geometry: the mesh
    /// followed the floor into the wall because the wall was never consulted.
    /// </summary>
    public static class Traversability
    {
        /// <summary>
        /// Whether a body could actually be dragged from one sample to the next.
        ///
        /// Swept flat at the height of the *higher* surface rather than following the ground between
        /// them. Sloping the sweep with the terrain looks more faithful and is wrong: on a step up, a
        /// sweep starting a step-height above the low side runs directly into the face of the step,
        /// which is the one obstruction a walker is guaranteed to be able to cross.
        /// </summary>
        public static bool CanStep(BspVisibility vis, BspFile.Vector3 from, BspFile.Vector3 to)
        {
            float top = MathF.Max(from.Z, to.Z);

            // Valve's own generation sweep: the NavTraceMins/Maxs box, dragged from here to there
            // against GetGenerationTraceMask. This replaced two separate lines at knee and chest
            // height, which between them proved only that those two heights were clear - a railing,
            // a pipe or a sill sitting between them read as open floor, and anything above chest
            // height but below standing height was never consulted at all. Sweeping the box tests the
            // whole 0..55 span continuously, which is the question "can a body get across" actually
            // asks.
            //
            // Lifted by a full step, not by a whisker.
            //
            // A whisker (0.5) was harmless while the sweep could not see displacements, because on
            // terrain it saw nothing at all and the height it ran at made no difference to the answer.
            // Now that it does see terrain, running it just above the surface means any undulation
            // between two samples taller than half a unit reads as an obstruction - and ground that
            // rolls by a few units between samples 25 apart is what terrain *is*. It refused links
            // across open hillside, taking rp_downtown_meowy from 20,921 areas to 28,523 and its
            // isolated areas from 967 to 3,784.
            //
            // Half a step, which is where the answer stops changing rather than a number picked to look
            // principled. Swept across both test maps the trade is a shallow bowl: too low and the box
            // catches ground undulation, too high and it starts catching *ceilings* indoors instead, and
            // both ends fragment the mesh. Anything from about 6 to 12 sits at the bottom - gm_construct
            // gives 2,339 areas across that whole span - while 0.5 gives 2,382 and 28,523 on downtown,
            // and 18 gives 2,518 and 22,002. Coverage does not move at all across the range (269
            // uncovered reference areas at every value tested), so this is purely about how finely the
            // same ground gets cut up.
            //
            // Anything below a step is by definition something a walker steps over rather than something
            // that stops them, so testing from half of one is still asking the right question. The body
            // is swept from there up through its full height.
            const float Lift = NavConstants.StepHeight / 2f;

            var a = new BspFile.Vector3(from.X, from.Y, top + Lift);
            var b = new BspFile.Vector3(to.X, to.Y, top + Lift);

            bool hit = vis.TryTraceHull(a, b, BspVisibility.NavTraceMins, BspVisibility.NavTraceMaxs,
                BspVisibility.GenerationMask, out float fraction, out _, out bool startSolid);

            if (!startSolid)
                return !hit || fraction >= 1f;

            // The box began inside something. When it is dragged out of that and meets nothing else,
            // the sweep enters nothing and reports no hit, and that used to be read as clear - so a
            // step was allowed out from under a ceiling the body could not have stood under, or
            // through a wall the start point sat a fraction of a unit inside.
            //
            // Refusing every start-solid step is wrong the other way, though. The box is 0.9 wide and
            // the start point is a grid sample, which lands exactly on a wall face whenever the grid
            // line and the face coincide - and a face counts as inside the brush. In the flood on the
            // twelve benchmark maps 235 of the 287 start-solid steps were that: a sample on the foot of
            // a wall, stepping straight away from it into open floor (cp_dustbowl's yard wall, a run of
            // samples on y = -1456 with the wall face at -1456). Refusing those made the foot of the
            // wall a dead end the flood could drop into and never walk out of. The rest, and 202 of
            // CornerPatcher's 211, ran on through whatever they started in: a patch area built inside a
            // post on rp_downtown, and links through pillars on cp_granary and cp_badlands.
            //
            // So a start-solid step is let through only when the box's overlap at the start is a
            // graze: swept back the other way, from the far end, the first face the box meets lies at
            // or behind the start point, so the box only touched it because the box is wider than the
            // point. A box buried in a ceiling, or starting inside a thin wall the step would pass
            // through, meets a face that lies out ahead of the start and is refused, as a step that
            // enters something on the way already is.
            if (hit)
                return false;

            return GrazesOnlyAtStart(vis, a, b);
        }

        /// <summary>
        /// Whether a box swept from <paramref name="b"/> back to <paramref name="a"/> starts clear and
        /// meets nothing, or meets first a face that only its width would overlap at
        /// <paramref name="a"/> - a vertical line through the middle of the box there would be clear.
        ///
        /// Judged from the face the reverse sweep stops on rather than from how far short of
        /// <paramref name="a"/> it stops. A box reaches an axis-aligned face half its width before its
        /// centre does, but reaches a slanted face, or a brush corner it is clipped against through a
        /// bevel plane, further out than that - on rp_downtown a sample beside a block's corner met its
        /// bevel 0.69 units out, while sitting 0.2 units clear of it. How far the box would sink into
        /// the face follows from the stopping point and the face normal, whatever its angle.
        /// </summary>
        private static bool GrazesOnlyAtStart(BspVisibility vis, BspFile.Vector3 a, BspFile.Vector3 b)
        {
            float dx = b.X - a.X, dy = b.Y - a.Y;
            float length = MathF.Sqrt(dx * dx + dy * dy);
            if (length < 1e-3f)
                return false;

            var mins = BspVisibility.NavTraceMins;
            var maxs = BspVisibility.NavTraceMaxs;

            bool hit = vis.TryTraceHull(b, a, mins, maxs, BspVisibility.GenerationMask,
                out float fraction, out var normal, out bool startSolid);

            if (startSolid)
                return false;

            if (!hit)
                return true;

            // How far the box's width reaches out along the normal - the part of the box a vertical
            // line through its middle does not have. Its height is left out on purpose: that is the
            // body the step is testing, and a ceiling the full height runs into is no graze.
            float reach = Reach(normal.X, mins.X, maxs.X) + Reach(normal.Y, mins.Y, maxs.Y);

            // How much further into the face the box would have gone, carried on from where it
            // stopped back to a, measured along the normal.
            float approach = (1f - fraction) * MathF.Abs(normal.X * dx + normal.Y * dy);

            // Within the width's reach, a line through the middle of the box at a would still be
            // clear of the face: the overlap is the box's width and nothing else.
            return approach <= reach + GrazeTolerance;
        }

        /// <summary>The box's reach along one component of a face normal, towards the face.</summary>
        private static float Reach(float normal, float min, float max)
            => normal < 0 ? -max * normal : -min * normal;

        /// <summary>
        /// Slack on the graze test: covers Source's DIST_EPSILON, the 1/32 unit a sweep stops short of
        /// what it hits, with room to spare, and is still far too little to step through any wall.
        /// </summary>
        private const float GrazeTolerance = 0.1f;
    }
}
