using System;

namespace NavPal
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

            if (!vis.TryTraceHull(a, b, BspVisibility.NavTraceMins, BspVisibility.NavTraceMaxs,
                    BspVisibility.GenerationMask, out float fraction, out _, out bool startSolid))
            {
                return true;
            }

            return !startSolid && fraction >= 1f;
        }
    }
}
