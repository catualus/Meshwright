using System;
using System.Collections.Generic;
using Meshwright;
using Xunit;

namespace Meshwright.Tests
{
    /// <summary>
    /// That one floor sampled twice is one cell, wherever it sits against the eight-unit bands the flood
    /// files its cells under.
    ///
    /// The sewers of rp_downtown_tits_v25 and rp_downtown_meowy are terrain laid 1.3 units over the brush
    /// face it was built from, at z=-195.7 over z=-197: two floors of every column there, either side of
    /// the edge between two bands at z=-196. The flood claimed one cell per band, so it took both, and the
    /// generator laid an area on each, one over the other - and the copy nothing linked to was kept as a
    /// cut-off group a body fits in.
    /// </summary>
    public class FloorBandTests
    {
        private const int Solid = 0x1;

        private const float Brush = -197f;
        private const float Terrain = -195.70874f;

        /// <summary>Brush ground at <see cref="Brush"/>, and terrain over all of it at <paramref name="terrain"/>.</summary>
        private static BspVisibility World(float terrain)
        {
            var vis = TestWorlds.Ground(Brush);

            BspFile.Vector3 V(float x, float y) => new(x, y, terrain);
            vis.AttachDisplacements(BspDisplacements.FromTriangles(
                [V(-1000, -1000), V(1000, -1000), V(1000, 1000), V(-1000, -1000), V(1000, 1000), V(-1000, 1000)],
                [Solid, Solid]));

            return vis;
        }

        private static NavFile Generate(BspVisibility vis)
        {
            var bsp = TestWorlds.Map($$"""
                {
                "classname" "worldspawn"
                }
                {
                "classname" "info_player_start"
                "origin" "103 103 -194"
                }
                """,
                [(new BspFile.Vector3(-197, -197, -500), new BspFile.Vector3(300, 300, 500))]);

            var nav = new NavFile();
            AreaGenerator.Generate(nav, vis, bsp);
            return nav;
        }

        /// <summary>Pairs of areas overlapping by more than two units both ways, at heights within a step.</summary>
        private static List<(uint, uint)> Overlapping(NavFile nav)
        {
            var pairs = new List<(uint, uint)>();

            for (int i = 0; i < nav.Areas.Count; i++)
            {
                var a = NavGeometry.GetBounds(nav.Areas[i]);

                for (int j = i + 1; j < nav.Areas.Count; j++)
                {
                    var b = NavGeometry.GetBounds(nav.Areas[j]);
                    float x0 = MathF.Max(a.MinX, b.MinX), x1 = MathF.Min(a.MaxX, b.MaxX);
                    float y0 = MathF.Max(a.MinY, b.MinY), y1 = MathF.Min(a.MaxY, b.MaxY);

                    if (x1 - x0 <= 2f || y1 - y0 <= 2f)
                        continue;

                    float x = (x0 + x1) / 2f, y = (y0 + y1) / 2f;

                    if (MathF.Abs(NavGeometry.SurfaceZ(nav.Areas[i], x, y) -
                                  NavGeometry.SurfaceZ(nav.Areas[j], x, y)) <= NavConstants.StepHeight)
                    {
                        pairs.Add((nav.Areas[i].Id, nav.Areas[j].Id));
                    }
                }
            }

            return pairs;
        }

        /// <summary>
        /// The regression: the terrain and the brush face under it, a band edge between them, give one
        /// layer of areas, not two laid one over the other.
        /// </summary>
        [Fact]
        public void TwoFloorsAcrossABandEdgeGiveOneLayerOfAreas()
        {
            var nav = Generate(World(Terrain));

            Assert.NotEmpty(nav.Areas);
            Assert.Empty(Overlapping(nav));
        }

        /// <summary>
        /// The same two floors in one band, which were always one cell: the control, so the test above
        /// fails for the band edge and not for something else about the world.
        /// </summary>
        [Fact]
        public void TwoFloorsInOneBandGiveOneLayerOfAreas()
        {
            var nav = Generate(World(Brush + 0.5f));

            Assert.NotEmpty(nav.Areas);
            Assert.Empty(Overlapping(nav));
        }
    }
}
