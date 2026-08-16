using System.Collections.Generic;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    public class NavGeometryTests
    {
        private static NavArea Quad(float x0, float y0, float x1, float y1,
            float nw, float ne, float se, float sw)
        {
            var area = new NavArea { Id = 1 };
            area.NwCorner[0] = x0; area.NwCorner[1] = y0; area.NwCorner[2] = nw;
            area.SeCorner[0] = x1; area.SeCorner[1] = y1; area.SeCorner[2] = se;
            area.NeZ = ne;
            area.SwZ = sw;
            return area;
        }

        [Fact]
        public void SurfaceZInterpolatesAcrossAllFourCorners()
        {
            // A quad tilted along both axes, so a bug that ignores one of them cannot pass.
            var area = Quad(0, 0, 100, 100, nw: 0, ne: 10, se: 30, sw: 20);

            Assert.Equal(0f, NavGeometry.SurfaceZ(area, 0, 0), 3);
            Assert.Equal(10f, NavGeometry.SurfaceZ(area, 100, 0), 3);
            Assert.Equal(20f, NavGeometry.SurfaceZ(area, 0, 100), 3);
            Assert.Equal(30f, NavGeometry.SurfaceZ(area, 100, 100), 3);
            Assert.Equal(15f, NavGeometry.SurfaceZ(area, 50, 50), 3);
        }

        [Fact]
        public void SurfaceZClampsOutsideTheFootprint()
        {
            var area = Quad(0, 0, 100, 100, nw: 0, ne: 0, se: 40, sw: 40);

            // Off the south edge: clamped, not extrapolated. Extrapolating would let a caller probing
            // just past an edge get a height the area never claimed.
            Assert.Equal(40f, NavGeometry.SurfaceZ(area, 50, 500), 3);
            Assert.Equal(0f, NavGeometry.SurfaceZ(area, 50, -500), 3);
        }

        [Fact]
        public void SurfaceZSurvivesADegenerateQuad()
        {
            // Zero width. The interpolation divides by the span, so this is the shape that would throw
            // or return NaN if the guard were missing, and clipping can produce very thin areas.
            var area = Quad(50, 0, 50, 100, nw: 0, ne: 0, se: 10, sw: 10);

            float z = NavGeometry.SurfaceZ(area, 50, 50);
            Assert.False(float.IsNaN(z));
            Assert.Equal(5f, z, 3);
        }

        [Fact]
        public void GetBoundsNormalisesReversedCorners()
        {
            // A hand-edited mesh can carry the corners either way round.
            var area = Quad(100, 100, 0, 0, nw: 0, ne: 0, se: 0, sw: 0);
            var b = NavGeometry.GetBounds(area);

            Assert.Equal(0f, b.MinX, 3);
            Assert.Equal(0f, b.MinY, 3);
            Assert.Equal(100f, b.MaxX, 3);
            Assert.Equal(100f, b.MaxY, 3);
        }
    }

    /// <summary>
    /// Movement constants, pinned because getting them wrong is silent.
    ///
    /// nav.h defines several of these twice, once under <c>#if defined(CSTRIKE_DLL)</c> and once for
    /// every other game, and this codebase had picked the Counter-Strike branch for a tool aimed at
    /// Garry's Mod and TF2. Nothing fails when that is wrong; the mesh simply refuses climbs a player
    /// can make and drops a player can survive, and the areas beyond them go unreachable.
    /// </summary>
    public class NavConstantsTests
    {
        [Fact]
        public void UseTheNonCounterStrikeBranchOfNavH()
        {
            Assert.Equal(64f, NavConstants.JumpCrouchHeight);   // 58 under CSTRIKE_DLL
            Assert.Equal(400f, NavConstants.DeathDrop);          // 200 under CSTRIKE_DLL
            Assert.Equal(55f, NavConstants.HumanCrouchHeight);   // not HumanCrouchEyeHeight, which is 37
        }

        [Fact]
        public void MovementLimitsAreOrdered()
        {
            Assert.True(NavConstants.StepHeight < NavConstants.JumpHeight);
            Assert.True(NavConstants.JumpHeight < NavConstants.JumpCrouchHeight);
            Assert.True(NavConstants.HumanCrouchHeight < NavConstants.HumanHeight);
            Assert.True(NavConstants.SlopeLimit < NavConstants.StairNormal);
        }
    }

    public class AreaMergerTests
    {
        private static NavArea Quad(uint id, float x0, float y0, float x1, float y1, float z)
        {
            var area = new NavArea { Id = id };
            area.NwCorner[0] = x0; area.NwCorner[1] = y0; area.NwCorner[2] = z;
            area.SeCorner[0] = x1; area.SeCorner[1] = y1; area.SeCorner[2] = z;
            area.NeZ = z;
            area.SwZ = z;
            return area;
        }

        [Fact]
        public void AdjacentCoplanarAreasMerge()
        {
            var nav = new NavFile();
            nav.Areas.Add(Quad(1, 0, 0, 100, 100, 0));
            nav.Areas.Add(Quad(2, 100, 0, 200, 100, 0));

            var result = AreaMerger.Merge(nav);

            Assert.Equal(1, result.Merges);
            Assert.Single(nav.Areas);
            Assert.Equal(200f, NavGeometry.GetBounds(nav.Areas[0]).MaxX, 3);
        }

        [Fact]
        public void AStepAtTheSeamPreventsAMerge()
        {
            var nav = new NavFile();
            nav.Areas.Add(Quad(1, 0, 0, 100, 100, 0));
            nav.Areas.Add(Quad(2, 100, 0, 200, 100, 40));   // 40 units higher: a step, not one surface

            var result = AreaMerger.Merge(nav);

            Assert.Equal(0, result.Merges);
            Assert.Equal(2, nav.Areas.Count);
            Assert.True(result.HeightMismatch > 0);
        }

        [Fact]
        public void NoMergeAreasAreLeftAlone()
        {
            var nav = new NavFile();
            nav.Areas.Add(Quad(1, 0, 0, 100, 100, 0));

            var second = Quad(2, 100, 0, 200, 100, 0);
            second.AttributeFlags = (int)NavAttributes.NoMerge;
            nav.Areas.Add(second);

            Assert.Equal(0, AreaMerger.Merge(nav).Merges);
            Assert.Equal(2, nav.Areas.Count);
        }

        [Fact]
        public void MismatchedSpansDoNotMerge()
        {
            // Documents a real limitation rather than an intention: merging keys on an exact shared
            // edge, so a partner covering only part of it is not found. On gm_construct this leaves
            // roughly ten times as many areas unmerged as a genuine step at the seam does.
            var nav = new NavFile();
            nav.Areas.Add(Quad(1, 0, 0, 100, 100, 0));
            nav.Areas.Add(Quad(2, 100, 0, 200, 50, 0));

            Assert.Equal(0, AreaMerger.Merge(nav).Merges);
        }
    }

    public class JumpAreaStitcherTests
    {
        private static NavArea Quad(uint id, float y0, float y1, NavAttributes attributes = NavAttributes.None)
        {
            var area = new NavArea { Id = id, AttributeFlags = (int)attributes };
            area.NwCorner[0] = 0; area.NwCorner[1] = y0; area.NwCorner[2] = 0;
            area.SeCorner[0] = 100; area.SeCorner[1] = y1; area.SeCorner[2] = 0;
            return area;
        }

        /// <summary>
        /// Ground too steep to stand on becomes a jump area, and this pass turns it into a direct
        /// connection between what it joined before deleting it. Dropping it without bridging loses the
        /// route as well as the area, which leaves the two walkable pieces with nothing between them.
        /// </summary>
        [Fact]
        public void AJumpAreaIsReplacedByADirectConnection()
        {
            var nav = new NavFile();
            var north = Quad(1, 0, 100);
            var jump = Quad(2, 100, 125, NavAttributes.Jump);
            var south = Quad(3, 125, 225);

            north.Connections[NavGeometry.South].Add(jump.Id);
            jump.Connections[NavGeometry.South].Add(south.Id);

            nav.Areas.Add(north);
            nav.Areas.Add(jump);
            nav.Areas.Add(south);

            var result = JumpAreaStitcher.Stitch(nav);

            Assert.Equal(1, result.JumpAreas);
            Assert.Equal(2, nav.Areas.Count);
            Assert.DoesNotContain(nav.Areas, a => a.Id == 2);
            Assert.Contains(3u, north.Connections[NavGeometry.South]);
        }

        /// <summary>
        /// Steep ground arrives in runs, so a chain of jump areas has to be followed all the way through.
        /// Stopping at the first neighbour bridges one jump area to the next and then deletes both,
        /// leaving exactly the gap the pass exists to span.
        /// </summary>
        [Fact]
        public void ChainsOfJumpAreasAreFollowedThrough()
        {
            var nav = new NavFile();
            var north = Quad(1, 0, 100);
            var first = Quad(2, 100, 125, NavAttributes.Jump);
            var second = Quad(3, 125, 150, NavAttributes.Jump);
            var south = Quad(4, 150, 250);

            north.Connections[NavGeometry.South].Add(first.Id);
            first.Connections[NavGeometry.South].Add(second.Id);
            second.Connections[NavGeometry.South].Add(south.Id);

            nav.Areas.Add(north);
            nav.Areas.Add(first);
            nav.Areas.Add(second);
            nav.Areas.Add(south);

            JumpAreaStitcher.Stitch(nav);

            Assert.Equal(2, nav.Areas.Count);
            Assert.Contains(4u, north.Connections[NavGeometry.South]);
        }

        [Fact]
        public void ALoopOfJumpAreasTerminates()
        {
            var nav = new NavFile();
            var a = Quad(1, 0, 25, NavAttributes.Jump);
            var b = Quad(2, 25, 50, NavAttributes.Jump);

            a.Connections[NavGeometry.South].Add(b.Id);
            b.Connections[NavGeometry.South].Add(a.Id);

            nav.Areas.Add(a);
            nav.Areas.Add(b);

            JumpAreaStitcher.Stitch(nav);   // must not recurse forever
            Assert.Empty(nav.Areas);
        }
    }
}
