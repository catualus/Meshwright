using System;
using System.Collections.Generic;
using System.IO;

namespace Meshwright
{
    /// <summary>
    /// Every static prop in the map, as world-space collision triangles.
    ///
    /// The three pieces this joins up are <see cref="StaticPropLump"/> for where the props are,
    /// <see cref="GameFiles"/> for finding the model each one names, and <see cref="PhyFile"/> for what
    /// that model collides as. What comes out is a triangle soup in the same form displacement terrain
    /// already takes, which is what lets both share a tracer.
    ///
    /// **Models are loaded once and instanced.** rp_downtown_meowy places 1,523 solid props from 194
    /// models, and two of those models account for 636 placements between them - so parsing per prop
    /// would do the same work six hundred times over. The hull is parsed once per model and transformed
    /// per placement.
    ///
    /// Transforming rather than instancing at trace time is a deliberate trade. Keeping one hull and a
    /// per-prop matrix would use less memory, but every ray would then have to transform itself into
    /// each prop's local space before testing, and the ray count here is in the hundreds of millions.
    /// Flattening to world space costs memory once and makes every later query a plain triangle test.
    /// </summary>
    public sealed class StaticProps
    {
        /// <summary>
        /// Contents a static prop's collision carries. Props are baked into world collision as
        /// CONTENTS_SOLID, which is what makes them stop the engine's ground traces despite
        /// MASK_NPCSOLID_BRUSHONLY sounding as though it would exclude them.
        /// </summary>
        public const int PropContents = 0x1;

        public BspFile.Vector3[] Triangles { get; private set; } = [];

        public int TriangleCount => Triangles.Length / 3;

        /// <summary>The same tracer displacement terrain uses. Props pose an identical problem.</summary>
        private readonly TriangleMesh mesh = new();

        /// <summary>Whether the segment crosses any prop matching <paramref name="mask"/>.</summary>
        public bool Blocks(BspFile.Vector3 a, BspFile.Vector3 b, int mask) => mesh.Blocks(a, b, mask);

        /// <summary>The nearest prop surface the segment crosses, with the triangle's normal.</summary>
        public bool TryTraceSurface(BspFile.Vector3 a, BspFile.Vector3 b, int mask,
            out float fraction, out BspFile.Vector3 normal)
            => mesh.TryTraceSurface(a, b, mask, out fraction, out normal);

        /// <summary>Sweeps a box against the props.</summary>
        public bool TryTraceHull(BspFile.Vector3 a, BspFile.Vector3 b,
            BspFile.Vector3 mins, BspFile.Vector3 maxs, int mask,
            out float fraction, out BspFile.Vector3 normal, out bool startSolid)
            => mesh.TryTraceHull(a, b, mins, maxs, mask, out fraction, out normal, out startSolid);

        /// <summary>How many placed props contributed geometry.</summary>
        public int PropsBuilt { get; private set; }

        /// <summary>Props whose model could not be found at all.</summary>
        public int PropsMissingModel { get; private set; }

        /// <summary>Models that claim physics collision but ship no hull, so contribute nothing.</summary>
        public IReadOnlyList<string> ModelsWithoutHull => hullless;

        private readonly List<string> hullless = [];

        /// <summary>Props that fell back to the model's bounding box because no hull was available.</summary>
        public int PropsFromBoundingBox { get; private set; }

        /// <summary>Models named by the map, and how many of them yielded a collision hull.</summary>
        public int ModelsNamed { get; private set; }
        public int ModelsWithHull { get; private set; }

        /// <summary>Model paths that could not be resolved, for reporting rather than silent loss.</summary>
        public IReadOnlyList<string> MissingModels => missingModels;

        private readonly List<string> missingModels = [];

        public static StaticProps Load(string bspPath, IEnumerable<string>? extraRoots = null)
        {
            var lump = StaticPropLump.Load(bspPath);
            using var files = GameFiles.Open(bspPath, extraRoots);

            return Build(lump, files);
        }

        public static StaticProps Build(StaticPropLump lump, GameFiles files)
        {
            var result = new StaticProps
            {
                ModelsNamed = lump.ModelNames.Length,
            };

            var hulls = new BspFile.Vector3[lump.ModelNames.Length][];
            var resolved = new bool[lump.ModelNames.Length];
            var fromBox = new bool[lump.ModelNames.Length];

            var tris = new List<BspFile.Vector3>();

            foreach (var prop in lump.Props)
            {
                int m = prop.ModelIndex;

                if (!resolved[m])
                {
                    resolved[m] = true;
                    hulls[m] = result.LoadHull(files, lump.ModelNames[m], prop.Solid, out bool box);
                    fromBox[m] = box;

                    if (hulls[m].Length > 0) result.ModelsWithHull++;
                }

                var hull = hulls[m];

                if (hull.Length == 0) { result.PropsMissingModel++; continue; }

                if (fromBox[m]) result.PropsFromBoundingBox++;

                result.PropsBuilt++;

                foreach (var v in hull)
                {
                    var scaled = new BspFile.Vector3(v.X * prop.Scale, v.Y * prop.Scale, v.Z * prop.Scale);
                    var turned = StaticPropLump.Rotate(scaled, prop.Pitch, prop.Yaw, prop.Roll);

                    tris.Add(new BspFile.Vector3(
                        turned.X + prop.Origin.X,
                        turned.Y + prop.Origin.Y,
                        turned.Z + prop.Origin.Z));
                }
            }

            result.Triangles = tris.ToArray();

            // Every prop triangle carries CONTENTS_SOLID, uniformly. There is no per-triangle material
            // distinction here as there is for brushes - a prop's collision is solid or it does not
            // exist - so the array is filled rather than derived.
            var perTriangle = new int[result.TriangleCount];
            Array.Fill(perTriangle, PropContents);

            result.mesh.Build(result.Triangles, perTriangle);
            return result;
        }

        /// <summary>
        /// The collision shape for one model: its .phy hull, or its bounding box only when the prop
        /// itself says it collides as a box.
        ///
        /// **A missing .phy yields no collision, not a box.** That was the other way round first, and
        /// measurably worse. Filling a bounding box with solid is wrong in the direction that costs
        /// mesh: a hedgerow, a tree or a fence has a box many times the volume of anything solid inside
        /// it, so the box deletes walkable ground all around the prop rather than adding any.
        ///
        /// On rp_downtown_meowy ten of the models a solid prop names lack a .phy, and the list reads as the
        /// argument against guessing: bushgreenbig, two hedgerows, mall_bush01, tree cards, skybox
        /// props, a dock pole, a step trim and a length of railroad track. Fifty-four props use them,
        /// and boxing those cost real mesh with nothing gained.
        ///
        /// The skybox entries make the point twice over. A prop in the 3D skybox is scaled-down scenery
        /// far outside the playable space, and the engine never collides against it at all; a box round
        /// one is not an approximation of anything.
        ///
        /// So a prop whose hull cannot be found is counted and skipped. That leaves a known gap rather
        /// than an invented surface, and <see cref="PropsWithoutHull"/> says how big it is.
        /// </summary>
        private BspFile.Vector3[] LoadHull(GameFiles files, string modelPath, byte solid, out bool box)
        {
            box = false;

            if (solid == StaticPropLump.SolidVPhysics)
            {
                string phyPath = Path.ChangeExtension(modelPath, ".phy");

                if (files.TryRead(phyPath, out var phyBytes))
                {
                    var phy = PhyFile.Parse(phyBytes);
                    if (phy.TriangleCount > 0) return phy.Triangles;
                }

                hullless.Add(modelPath);
                return [];
            }

            if (!files.TryRead(modelPath, out var mdlBytes))
            {
                missingModels.Add(modelPath);
                return [];
            }

            var model = StudioModel.Parse(mdlBytes);

            if (!model.Valid) return [];

            box = true;
            return model.AsTriangles();
        }
    }
}
