using System;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// Tests for how areas are grown out of a sampled node grid.
    ///
    /// These exist because the rule they cover was wrong for a long time in a way no aggregate number
    /// showed. Areas over staircases came out one step deep, which is not a crash, not a gap in
    /// coverage, and not an isolated area - the mesh had the right shape everywhere else and the right
    /// area count, so every metric the generator reported looked healthy. It only surfaced as "stairs
    /// are never marked", two passes downstream, because an area spanning one riser has a total rise
    /// below step height and the stair test correctly declines to classify it.
    ///
    /// A node grid needs no BSP, so the growth rule is testable in isolation even though almost nothing
    /// else in the generator is.
    /// </summary>
    public class NodeAreaBuilderTests
    {
        private const float Step = NavConstants.GenerationStepSize;

        /// <summary>
        /// Builds a grid running south, one node per grid step, climbing <paramref name="risePerStep"/>
        /// each step. Links are one-way south and east, as the real sampler makes them.
        ///
        /// <paramref name="flatTreads"/> is the whole distinction under test, so it decides the surface
        /// normal rather than the caller supplying one directly. A staircase has flat treads and normals
        /// pointing straight up whatever its overall pitch; a ramp's normal tilts to stay perpendicular
        /// to the slope. Handing in a normal that does not match the geometry describes a surface that
        /// cannot exist, and the growth rule reasons about exactly that relationship - an earlier version
        /// of this helper took a bare normal.z, which made the ramp case a physically impossible surface
        /// and failed for that reason rather than for anything wrong in the code.
        /// </summary>
        private static NavNodeGrid Run(int length, float risePerStep, bool flatTreads, int width = 3)
        {
            var grid = new NavNodeGrid();
            var nodes = new NavNode[width, length];

            // Perpendicular to a slope running (0, Step, risePerStep) in the YZ plane.
            float length2d = System.MathF.Sqrt(Step * Step + risePerStep * risePerStep);
            var sloped = new BspFile.Vector3(0, -risePerStep / length2d, Step / length2d);
            var upright = new BspFile.Vector3(0, 0, 1f);

            for (int gx = 0; gx < width; gx++)
            {
                for (int gy = 0; gy < length; gy++)
                {
                    var position = new BspFile.Vector3(gx * Step, gy * Step, gy * risePerStep);
                    nodes[gx, gy] = grid.Add(gx, gy, position, flatTreads ? upright : sloped);
                }
            }

            for (int gx = 0; gx < width; gx++)
            {
                for (int gy = 0; gy < length; gy++)
                {
                    if (gy + 1 < length) nodes[gx, gy].ConnectTo(NavGeometry.South, nodes[gx, gy + 1]);
                    if (gx + 1 < width) nodes[gx, gy].ConnectTo(NavGeometry.East, nodes[gx + 1, gy]);
                }
            }

            return grid;
        }

        private static float DeepestArea(NavFile nav)
        {
            float deepest = 0;
            foreach (var area in nav.Areas)
                deepest = System.MathF.Max(deepest, NavGeometry.GetBounds(area).Depth);

            return deepest;
        }

        [Fact]
        public void FlatGroundGrowsIntoOneArea()
        {
            var nav = new NavFile();
            NodeAreaBuilder.Build(nav, Run(length: 6, risePerStep: 0f, flatTreads: true), Step);

            Assert.Single(nav.Areas);
            Assert.Equal(6 * Step, DeepestArea(nav));
        }

        /// <summary>
        /// A staircase: flat treads (normal straight up) with a real riser at every sampling step.
        ///
        /// This is the case that regressed. Coplanarity against the seed's own surface normal makes the
        /// reference plane horizontal here, so every riser reads as off-plane and growth stops after one
        /// or two steps.
        /// </summary>
        [Fact]
        public void StaircaseGrowsPastASingleRiser()
        {
            var nav = new NavFile();
            NodeAreaBuilder.Build(nav, Run(length: 6, risePerStep: 8f, flatTreads: true), Step);

            // The point is that it spans the flight rather than one tread, which is what makes the total
            // rise exceed step height and so lets the stair test engage at all.
            Assert.True(DeepestArea(nav) > 2 * Step,
                $"expected an area spanning several treads, deepest was {DeepestArea(nav)}");
        }

        /// <summary>
        /// A smooth ramp, where the true surface normal tilts with the slope. The seed's plane is exact
        /// here and must keep being used - fitting a straight-line gradient instead was measurably worse
        /// on real terrain, because a fitted line cannot follow curvature that the normal handles for
        /// free.
        /// </summary>
        [Fact]
        public void SmoothRampGrowsIntoOneArea()
        {
            // A ramp climbing 8 units per 25 of run: normal.z works out at about 0.952, below MinStairNormal,
            // so this must take the surface-plane path rather than the stepped-gradient one.
            var nav = new NavFile();
            NodeAreaBuilder.Build(nav, Run(length: 6, risePerStep: 8f, flatTreads: false), Step);

            Assert.Single(nav.Areas);
        }

        /// <summary>
        /// A single threshold beside the seed - a kerb, a doorsill - followed by flat ground.
        ///
        /// The gradient must not be adopted from it. Fitting off one neighbour made every such threshold
        /// the climb the whole area then expected, so the flat ground beyond stopped matching at once;
        /// on a detailed map that cost thousands of extra fragmented areas.
        /// </summary>
        [Fact]
        public void SingleThresholdDoesNotBecomeAGradient()
        {
            var grid = new NavNodeGrid();
            var nodes = new NavNode[1, 6];

            for (int gy = 0; gy < 6; gy++)
            {
                // one 8-unit step up between the first and second node, dead flat after that
                float z = gy == 0 ? 0f : 8f;
                nodes[0, gy] = grid.Add(0, gy, new BspFile.Vector3(0, gy * Step, z),
                    new BspFile.Vector3(0, 0, 1f));
            }

            for (int gy = 0; gy + 1 < 6; gy++)
                nodes[0, gy].ConnectTo(NavGeometry.South, nodes[0, gy + 1]);

            var nav = new NavFile();
            NodeAreaBuilder.Build(nav, grid, Step);

            // The five flat nodes belong together; adopting 8-units-per-step would have split them all.
            Assert.True(DeepestArea(nav) >= 4 * Step,
                $"threshold was treated as a gradient; deepest area was {DeepestArea(nav)}");
        }

        /// <summary>
        /// A staircase whose treads do not line up with the sampling grid.
        ///
        /// This is the ordinary case rather than an exotic one: a tread is around twelve units deep and
        /// samples are twenty-five apart, so consecutive samples straddle two treads and the rise
        /// alternates purely from where the grid falls. An area still has to follow it. Requiring
        /// consecutive risers to be *equal* - which reads as the obvious way to confirm a staircase -
        /// rejects exactly this, and leaves real flights one step deep.
        /// </summary>
        [Fact]
        public void StaircaseWithAlternatingRisersStillGrows()
        {
            var grid = new NavNodeGrid();
            var nodes = new NavNode[8];

            float z = 0;
            for (int gy = 0; gy < 8; gy++)
            {
                nodes[gy] = grid.Add(0, gy, new BspFile.Vector3(0, gy * Step, z),
                    new BspFile.Vector3(0, 0, 1f));

                // 8, 16, 8, 16 ... one flight, sampled out of phase with its own treads.
                z += gy % 2 == 0 ? 8f : 16f;
            }

            for (int gy = 0; gy + 1 < 8; gy++)
                nodes[gy].ConnectTo(NavGeometry.South, nodes[gy + 1]);

            var nav = new NavFile();
            NodeAreaBuilder.Build(nav, grid, Step);

            Assert.True(DeepestArea(nav) > 2 * Step,
                $"alternating risers stopped the area growing; deepest was {DeepestArea(nav)}");
        }

        /// <summary>
        /// Ground that goes up, then back down, is not a flight of stairs and must not be read as one.
        /// The corroborating step has to agree about the direction of travel, not merely be a step.
        /// </summary>
        [Fact]
        public void AStepUpThenDownIsNotAGradient()
        {
            var grid = new NavNodeGrid();
            var nodes = new NavNode[6];
            ReadOnlySpan<float> heights = [0f, 10f, 0f, 10f, 0f, 10f];

            for (int gy = 0; gy < 6; gy++)
            {
                nodes[gy] = grid.Add(0, gy, new BspFile.Vector3(0, gy * Step, heights[gy]),
                    new BspFile.Vector3(0, 0, 1f));
            }

            for (int gy = 0; gy + 1 < 6; gy++)
                nodes[gy].ConnectTo(NavGeometry.South, nodes[gy + 1]);

            var nav = new NavFile();
            NodeAreaBuilder.Build(nav, grid, Step);

            Assert.True(nav.Areas.Count > 1,
                "ground alternating up and down was treated as a consistent climb");
        }

        /// <summary>
        /// Nodes carrying different attributes must not share an area - Valve's rule, and what keeps a
        /// crouch region from being swallowed by the standing floor around it.
        /// </summary>
        [Fact]
        public void DifferingAttributesSplitAreas()
        {
            var grid = Run(length: 4, risePerStep: 0f, flatTreads: true, width: 1);
            grid.Nodes[2].Attributes = NavAttributes.Crouch;

            var nav = new NavFile();
            NodeAreaBuilder.Build(nav, grid, Step);

            Assert.True(nav.Areas.Count >= 2,
                "a crouch node was absorbed into the standing area around it");
        }
    }
}
