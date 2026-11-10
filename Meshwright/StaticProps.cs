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

        /// <summary>
        /// The same hulls kept apart, one convex piece each, for <see cref="Contains"/>: the triangle
        /// soup above can say what a line crosses but not whether a point is inside.
        /// </summary>
        private ConvexSolids solids = new();

        /// <summary>How many closed convex pieces <see cref="Contains"/> tests against.</summary>
        public int SolidCount => solids.Count;

        /// <summary>
        /// Whether a point is inside a prop's collision. Every prop is solid, so the mask only decides
        /// whether props are being asked about at all.
        /// </summary>
        public bool Contains(BspFile.Vector3 point, int mask)
            => (mask & PropContents) != 0 && solids.Contains(point);

        /// <summary>
        /// Sweeps a box against the props, reporting <paramref name="startSolid"/> when the box starts
        /// anywhere in a prop's collision: across its surface or wholly inside it.
        ///
        /// The triangle sweep sees only the first. It counts a box overlapping a triangle at the start
        /// as starting solid, so a box lying across a prop's surface, or holding a whole small prop, is
        /// caught - but a box wholly inside a prop's hull touches none of its faces and swept clear. A
        /// crate, a vehicle or a building-sized prop then had room inside it for every body test built
        /// on the sweep: the flood's step, the crouching body, and the bodies the connection pass and
        /// the pocket pass stand at the ends of a crossing.
        ///
        /// So the start box's centre is also tested against the props' convex hulls. A box overlaps a
        /// convex hull exactly when it crosses the hull's surface, holds the whole hull, or lies wholly
        /// inside it, and the centre is inside in the last case; when it is inside in any other, the
        /// box crosses the surface and the triangles have already said so. A centre on a face, within
        /// <see cref="ConvexSolids.Margin"/>, is not inside, so a box resting against a prop is not
        /// made start-solid by this - only, as before, by overlapping the face. Only the start needs
        /// it: a box that ends inside a prop crossed one of its faces to get there, and stops on it.
        /// </summary>
        public bool TryTraceHull(BspFile.Vector3 a, BspFile.Vector3 b,
            BspFile.Vector3 mins, BspFile.Vector3 maxs, int mask,
            out float fraction, out BspFile.Vector3 normal, out bool startSolid)
        {
            bool hit = mesh.TryTraceHull(a, b, mins, maxs, mask, out fraction, out normal, out startSolid);

            if (!startSolid && (mask & PropContents) != 0)
            {
                startSolid = solids.Contains(new BspFile.Vector3(
                    a.X + (mins.X + maxs.X) / 2f, a.Y + (mins.Y + maxs.Y) / 2f, a.Z + (mins.Z + maxs.Z) / 2f));
            }

            return hit;
        }

        /// <summary>How much content had to be searched, which is most of what loading props costs.</summary>
        public int SearchRoots { get; private set; }
        public int SearchVpks { get; private set; }
        public int SearchGmas { get; private set; }
        public int GmasOpened { get; private set; }

        /// <summary>How many VPK directories actually had to be parsed - they are opened only on a miss.</summary>
        public int VpksOpened { get; private set; }
        public int PakfileEntries { get; private set; }

        /// <summary>Milliseconds spent loading hulls, placing them, and indexing the result, plus the
        /// content-opening costs carried up from <see cref="GameFiles"/>.</summary>
        public long LoadMs, PlaceMs, IndexMs, VpkMs, PakMs, GmaMs;

        /// <summary>Where the model files came from - see <see cref="GameFiles"/>.</summary>
        public int ReadsFromPakfile, ReadsFromDisk, ReadsFromVpk, ReadsFromGma;

        /// <summary>How many placed props contributed geometry.</summary>
        public int PropsBuilt { get; private set; }

        /// <summary>
        /// Props that ended up with no collision, whether the model was missing entirely or was found
        /// but shipped no hull. Deliberately one number: from the mesh's point of view the two are the
        /// same hole, and which it was is answered by <see cref="MissingModels"/> against
        /// <see cref="ModelsWithoutHull"/>.
        /// </summary>
        public int PropsWithoutCollision { get; private set; }

        /// <summary>Models that claim physics collision but ship no hull, so contribute nothing.</summary>
        public IReadOnlyList<string> ModelsWithoutHull => hullless;

        private readonly List<string> hullless = [];

        /// <summary>Props that fell back to the model's bounding box because no hull was available.</summary>
        public int PropsFromBoundingBox { get; private set; }

        /// <summary>
        /// Models the map's dictionary names, how many of those any solid prop actually uses, and how
        /// many of those yielded a hull.
        ///
        /// The middle number is the one to judge success against. A map's dictionary includes models
        /// only non-solid props reference - gm_construct names seventeen and needs three - so scoring
        /// against the total reports a failure that never happened.
        /// </summary>
        public int ModelsNamed { get; private set; }
        public int ModelsUsed { get; private set; }
        public int ModelsWithHull { get; private set; }

        /// <summary>Model paths that could not be resolved, for reporting rather than silent loss.</summary>
        public IReadOnlyList<string> MissingModels => missingModels;

        private readonly List<string> missingModels = [];

        /// <summary>
        /// Props made of the given hulls, each one convex piece of whole triangles (three vertices
        /// each), with no map or models behind them - for tests, which have neither.
        /// </summary>
        internal static StaticProps FromHulls(params BspFile.Vector3[][] hulls)
        {
            var result = new StaticProps();
            var pieces = new List<(int First, int Count)>();
            var tris = new List<BspFile.Vector3>();

            foreach (var hull in hulls)
            {
                pieces.Add((tris.Count / 3, hull.Length / 3));
                tris.AddRange(hull);
            }

            result.Triangles = [.. tris];

            var perTriangle = new int[result.TriangleCount];
            Array.Fill(perTriangle, PropContents);

            // The hulls first: they orient each triangle outwards, which the sweep needs to tell a box
            // leaving a prop from one entering it.
            var outward = new BspFile.Vector3[result.TriangleCount];
            result.solids = ConvexSolids.Build(result.Triangles, pieces, outward);
            result.mesh.Build(result.Triangles, perTriangle, outward);
            return result;
        }

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
                SearchRoots = files.RootCount,
                SearchVpks = files.VpkCount,
                PakfileEntries = files.PakfileEntries,
            };

            int models = lump.ModelNames.Length;

            var hulls = new BspFile.Vector3[models][];
            var ledges = new int[models][];
            var fromBox = new bool[models];

            // Which models are actually used, and with what solidity. A model's shape depends on the
            // solidity of the prop asking for it, so the value is taken from the first prop in lump
            // order that names it - the same one the sequential version happened to use, which keeps
            // the result identical rather than merely equivalent.
            var solidFor = new byte[models];
            var used = new bool[models];
            var wanted = new List<int>();

            foreach (var prop in lump.Props)
            {
                if (used[prop.ModelIndex]) continue;

                used[prop.ModelIndex] = true;
                solidFor[prop.ModelIndex] = prop.Solid;
                wanted.Add(prop.ModelIndex);
            }

            // Loading is per model and independent, so it goes wide.
            //
            // Worth knowing how little this turned out to be worth on its own, because the obvious guess
            // was wrong twice. Static props looked like they cost 710ms to load, and both the file
            // reading and the hull parsing are here, so this is where the work went first. It bought
            // 22%. The actual breakdown - which <see cref="LoadMs"/> and its siblings exist to report,
            // having been added to settle it - is 51ms here, 11ms placing the hulls, and 218ms building
            // the BVH over the result. Parallelising a twentieth of the runtime is not a speed-up.
            //
            // It is kept because it is free and correct, and because a map whose models live in VPKs
            // rather than loose on disk pays a lot more here than this one does. But the cost of static
            // props was never the props; see the note on TriangleMesh's build scratch.
            //
            // Diagnostics are collected into per-model slots rather than appended to shared lists, and
            // flattened afterwards in model order. A concurrent collection would have been less code and
            // would have made `props` report its missing models in a different order every run, which is
            // a miserable thing to diff against.
            var clock = System.Diagnostics.Stopwatch.StartNew();

            result.ModelsUsed = wanted.Count;


            var missingAt = new string?[models];
            var hulllessAt = new string?[models];

            System.Threading.Tasks.Parallel.ForEach(wanted, NavConcurrency.Options, m =>
            {
                hulls[m] = LoadHull(files, lump.ModelNames[m], solidFor[m],
                    out ledges[m], out fromBox[m], out missingAt[m], out hulllessAt[m]);
            });

            result.LoadMs = clock.ElapsedMilliseconds; clock.Restart();



            for (int m = 0; m < models; m++)
            {
                if (hulls[m] is { Length: > 0 }) result.ModelsWithHull++;
                if (missingAt[m] is { } gone) result.missingModels.Add(gone);
                if (hulllessAt[m] is { } bare) result.hullless.Add(bare);
            }

            // Sized up front. The total is known from the hulls and the placements, and letting a list
            // grow to a hundred and fifty thousand vertices means copying the whole thing seventeen
            // times on the way there.
            int total = 0;
            foreach (var prop in lump.Props) total += hulls[prop.ModelIndex]?.Length ?? 0;

            var tris = new BspFile.Vector3[total];
            var pieces = new List<(int First, int Count)>();
            int at = 0;

            foreach (var prop in lump.Props)
            {
                int m = prop.ModelIndex;
                var hull = hulls[m] ?? [];

                if (hull.Length == 0) { result.PropsWithoutCollision++; continue; }

                if (fromBox[m]) result.PropsFromBoundingBox++;

                result.PropsBuilt++;

                // Once per prop, not once per vertex - see StaticPropLump.Basis.
                var basis = StaticPropLump.Basis.For(prop.Pitch, prop.Yaw, prop.Roll);
                float scale = prop.Scale;

                int piece = at / 3;
                foreach (int size in ledges[m] ?? [])
                {
                    pieces.Add((piece, size));
                    piece += size;
                }

                foreach (var v in hull)
                {
                    var turned = basis.Apply(new BspFile.Vector3(v.X * scale, v.Y * scale, v.Z * scale));

                    tris[at++] = new BspFile.Vector3(
                        turned.X + prop.Origin.X,
                        turned.Y + prop.Origin.Y,
                        turned.Z + prop.Origin.Z);
                }
            }

            result.PlaceMs = clock.ElapsedMilliseconds; clock.Restart();



            result.VpksOpened = files.VpkOpened;



            result.VpkMs = files.VpkMs;



            result.PakMs = files.PakMs;



            result.ReadsFromPakfile = files.ReadsFromPakfile;

            result.ReadsFromDisk = files.ReadsFromDisk;

            result.ReadsFromVpk = files.ReadsFromVpk;

            result.ReadsFromGma = files.ReadsFromGma;

            result.SearchGmas = files.GmaCount;

            result.GmasOpened = files.GmasOpened;

            result.GmaMs = files.GmaMs;


            result.Triangles = tris;

            // Every prop triangle carries CONTENTS_SOLID, uniformly. There is no per-triangle material
            // distinction here as there is for brushes - a prop's collision is solid or it does not
            // exist - so the array is filled rather than derived.
            var perTriangle = new int[result.TriangleCount];
            Array.Fill(perTriangle, PropContents);

            // The hulls first: they orient each triangle outwards, which the sweep needs to tell a box
            // leaving a prop from one entering it.
            var outward = new BspFile.Vector3[result.TriangleCount];
            result.solids = ConvexSolids.Build(result.Triangles, pieces, outward);
            result.mesh.Build(result.Triangles, perTriangle, outward);

            result.IndexMs = clock.ElapsedMilliseconds;
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
        /// <remarks>
        /// Static, and reports its two failure kinds through out parameters rather than appending to the
        /// instance's lists, because it runs on many threads at once. The caller flattens them in model
        /// order afterwards.
        /// </remarks>
        private static BspFile.Vector3[] LoadHull(GameFiles files, string modelPath, byte solid,
            out int[] ledges, out bool box, out string? missing, out string? noHull)
        {
            ledges = [];
            box = false;
            missing = null;
            noHull = null;

            if (solid == StaticPropLump.SolidVPhysics)
            {
                string phyPath = Path.ChangeExtension(modelPath, ".phy");

                if (files.TryRead(phyPath, out var phyBytes))
                {
                    var phy = PhyFile.Parse(phyBytes);
                    if (phy.TriangleCount > 0)
                    {
                        ledges = phy.LedgeSizes;
                        return phy.Triangles;
                    }

                    noHull = modelPath;
                    return [];
                }

                // No .phy, and which kind of absence it is decides what to tell the user. A model that
                // is there without one has no hull to find, and nothing they do changes that. A model
                // that is not there at all is content this run could not see - a mod outside the
                // install inferred from the map's path - and -content fixes it. Both used to be
                // reported as the first, which hid every prop of an unmounted mod from the warning
                // that exists for exactly that case. The .mdl is read only on this path, so a map
                // whose hulls all resolve pays nothing for the distinction.
                if (files.TryRead(modelPath, out _))
                    noHull = modelPath;
                else
                    missing = modelPath;

                return [];
            }

            if (!files.TryRead(modelPath, out var mdlBytes))
            {
                missing = modelPath;
                return [];
            }

            var model = StudioModel.Parse(mdlBytes);

            if (!model.Valid) return [];

            box = true;
            var boxTriangles = model.AsTriangles();
            ledges = [boxTriangles.Length / 3];
            return boxTriangles;
        }
    }
}
