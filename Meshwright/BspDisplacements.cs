using System;
using System.Collections.Generic;
using System.IO;

namespace Meshwright
{
    /// <summary>
    /// Displacement surfaces, triangulated and indexed for ray tests.
    ///
    /// Displacements are the last geometry class a BSP tracer has to handle separately. The brush face a
    /// displacement was built from stays in the tree, but the surface itself is displaced off that face
    /// by a per-vertex offset, so terrain that rises above its base brush is invisible to a trace that
    /// only knows about brushes. Measured against the engine on gm_construct, once world brushes, detail
    /// brushes and brush entities were all handled, displacements were the *only* remaining cause of
    /// disagreement - 25 of 250 sampled rays.
    ///
    /// **Base quads come from the original faces where the map still has them.** A displacement is
    /// drawn on one quad and laid out across it, and LUMP_ORIGINALFACES is where that quad lives -
    /// the ordinary face lump holds what vbsp left after cutting it up for the tree, and a piece of a
    /// quad is the wrong footprint to stretch a grid over.
    ///
    /// Worth knowing that this is correctness rather than a measured win. On both maps there is an
    /// engine mesh to check against it changes nothing: gm_construct's displacement faces were never
    /// split, so the two lumps describe the same quads, and rp_downtown_meowy has no original faces at
    /// all. It is right for the case neither of them happens to be.
    ///
    /// **Checked against the running engine, vertex by vertex, and it agrees.** This was suspected of
    /// being wrong for a long time on the strength of one offline measure - <see cref="Surface.SpillsBase"/>
    /// flags 45 of rp_downtown_meowy's 183 displacements as carrying offsets that lie *in* the plane of
    /// their base quad, by up to two thousand units, against none of gm_construct's 110. That looked
    /// like the offsets and the quad failing to describe each other.
    ///
    /// It is not. Reconstructing the grid for the three worst of them and tracing to the engine's own
    /// terrain at each of the 81 vertices puts the median gap at **0.0 units**, with 73% to 86% of
    /// vertices inside 8 units and the rest explained by neighbouring geometry sitting over the probe.
    /// Large in-plane offsets are ordinary mapping: the outer ring of a big terrain displacement gets
    /// flared outwards and dropped to the world floor so the terrain seals against it, which moves
    /// border vertices a long way sideways while the interior stays on the quad. <c>disp -verts</c>
    /// shows the shape plainly - a constant ~855-unit drop right around the border of #32, and interior
    /// offsets of about +90 that match the engine to a tenth of a unit.
    ///
    /// So the measure is real but it is not a fault, and nothing here should be changed to make it
    /// smaller. Also verified along the way, and worth not re-deriving: the vertex runs tile the lump
    /// exactly and in order, the powers sum to the vertex count, <c>startPosition</c> identifies a base
    /// corner to within a unit on every displacement in both maps, and the face back-links are a
    /// bijection - 183 faces naming 183 distinct displacements, no fallback to <c>m_iMapFace</c> used.
    ///
    /// rp_downtown_meowy's mesh does fit its ground worse than gm_construct's, but terrain is not why.
    /// Static props are: they carry CONTENTS_SOLID, the engine builds areas on top of them, and nothing
    /// here reads them.
    /// </summary>
    public sealed class BspDisplacements
    {
        private const int LumpVertexes = 3;
        private const int LumpFaces = 7;
        private const int LumpEdges = 12;
        private const int LumpSurfEdges = 13;
        private const int LumpDispInfo = 26;
        private const int LumpDispVerts = 33;

        /// <summary>
        /// Faces as they stood before vbsp cut them up for the tree - where a displacement's base quad
        /// lives. Absent on maps that have been through a repacker, which discards it.
        /// </summary>
        private const int LumpOriginalFaces = 27;

        private const int DispInfoSize = 176;
        private const int DispVertSize = 20;
        private const int FaceSize = 56;

        /// <summary>The triangles and their index. Shared with static props, which pose the same problem.</summary>
        private readonly TriangleMesh mesh = new();

        /// <summary>
        /// Contents worth triangulating at all: the union of every mask a caller might later trace
        /// against, exactly as <c>BspVisibility.ReadLeafBrushes</c> keeps every brush either trace
        /// purpose could care about and filters at the point of use.
        ///
        /// Storing the union and filtering per query is the whole point. This used to filter by
        /// <see cref="BspVisibility.MaskBlockLos"/> once, here, and then answer every later query
        /// regardless of the mask it was asked about - so a displacement was either solid to everything
        /// or invisible to everything. The brush path has always re-checked the caller's mask at trace
        /// time; terrain silently did not, which meant the two geometry classes disagreed about what a
        /// mask means. In practice displacements carry CONTENTS_SOLID and every mask here includes it,
        /// so this corrects a real inconsistency rather than a symptom anyone has reported.
        /// </summary>
        private const int TracedContents = BspVisibility.MaskBlockLos | BspVisibility.GenerationMask;

        public int TriangleCount => mesh.TriangleCount;
        public int DisplacementCount { get; private set; }

        /// <summary>How many records the vertex lump actually holds, against how many the grids consume.</summary>
        public int DispVertRecords { get; private set; }

        /// <summary>The map's BSP version, since lump layouts are version-specific.</summary>
        public int BspVersion { get; private set; }

        /// <summary>Whether the map still carries the unsplit faces displacements are drawn on.</summary>
        public bool HasOriginalFaces { get; private set; }

        /// <summary>How many faces carry a back-link naming a displacement.</summary>
        public int FacesClaimingDisplacement { get; private set; }

        /// <summary>How many displacements are named by at least one face - the number that matters.</summary>
        public int DisplacementsWithBackLink { get; private set; }

        /// <summary>
        /// How many displacements have their two links naming different faces. Zero on a map whose
        /// original faces are in use, because there the two index different lumps and cannot be compared.
        /// </summary>
        public int BackLinkDisagreesWithMapFace { get; private set; }

        /// <summary>
        /// Range of the direction field's length across every displacement vertex. Nominally one, but
        /// zero where a vertex has no offset, and gm_construct stores lengths up to 7.75 while
        /// reconstructing correctly - so this locates a misread stride, not a fault on its own.
        /// </summary>
        public float ShortestDirection { get; private set; } = float.MaxValue;
        public float LongestDirection { get; private set; }

        /// <summary>
        /// What one displacement reconstructed to, kept so the result can be inspected rather than
        /// inferred.
        ///
        /// A wrong displacement does not fail - it produces a surface, in about the right place, with
        /// the wrong shape. Nothing downstream can tell that from correct terrain, so the only way to
        /// find one is to look at the individual surfaces and check them against what the base quad and
        /// the vertex offsets say they should be.
        ///
        /// <see cref="StartGap"/> is the most diagnostic field. The grid's orientation is recovered by
        /// matching <c>startPosition</c> to whichever base corner it is nearest, so that distance ought
        /// to be about zero. A large gap means the match was a guess, and a guess that lands on the
        /// wrong corner rotates the whole surface.
        /// </summary>
        public readonly record struct Surface(
            int Index, int Power, int VertStart, int Contents, int Triangles,
            BspFile.Vector3 Start,
            float StartGap, float StartGapRunnerUp,
            float MinX, float MaxX, float MinY, float MaxY, float MinZ, float MaxZ,
            float BaseMinZ, float BaseMaxZ,
            float MaxLateralOffset, float MaxVerticalOffset,
            BspFile.Vector3 C0, BspFile.Vector3 C1, BspFile.Vector3 C2, BspFile.Vector3 C3)
        {
            /// <summary>Whether the point lies inside this displacement's footprint in plan view.</summary>
            public bool Covers(float x, float y) => x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;

            /// <summary>How far the displaced surface sits from the quad it was built on.</summary>
            public float Lift => MaxZ - BaseMaxZ;

            /// <summary>
            /// Whether the offsets carry vertices a long way across the base quad rather than only in
            /// and out of it. Judged in the quad's own frame, so a wall sculpted horizontally does not
            /// count - comparing world-space XY looks equivalent and condemns every cliff on the map.
            ///
            /// **This is not a fault indicator, and it reads like one.** It was treated as one here for
            /// a long time. Every displacement it flags on rp_downtown_meowy that has since been checked
            /// against the running engine reconstructs correctly, to a median of zero units; see the
            /// note on the class. What it actually detects is a mapper flaring a big terrain
            /// displacement's outer ring outwards and dropping it to the world floor to seal the
            /// terrain, which is ordinary and correct.
            ///
            /// It is kept because it says something true about the shape - a surface that spills is one
            /// whose footprint is much larger than its quad, which matters to anything reasoning about
            /// coverage from the quad alone. It is not evidence that the reconstruction is wrong.
            /// </summary>
            public bool SpillsBase => MaxLateralOffset > 64f;
        }

        private readonly List<Surface> surfaces = [];

        /// <summary>The reconstructed vertex grid of each built displacement, for point-by-point comparison.</summary>
        private readonly List<BspFile.Vector3[]> grids = [];

        /// <summary>The raw lump records behind each built grid, so a misread field can be seen rather than inferred.</summary>
        private readonly List<DispVert[]> rawGrids = [];

        public IReadOnlyList<BspFile.Vector3[]> Grids => grids;


        

        public IReadOnlyList<DispVert[]> RawGrids => rawGrids;

        /// <summary>Every displacement that produced geometry, in the order they were built.</summary>
        public IReadOnlyList<Surface> Surfaces => surfaces;


        public static BspDisplacements Load(string path)
        {
            var result = new BspDisplacements();

            using var stream = File.OpenRead(path);
            using var r = new BinaryReader(stream);

            r.BaseStream.Seek(8, SeekOrigin.Begin);
            var lumps = new (int Offset, int Length)[BspFile.HeaderLumps];
            for (int i = 0; i < BspFile.HeaderLumps; i++)
            {
                lumps[i] = (r.ReadInt32(), r.ReadInt32());
                r.ReadInt32();
                r.ReadInt32();
            }

            // A presence check only, not a size validation - lumps[].Length is the on-disk (possibly
            // LZMA-compressed) size, which for a compressed lump has no fixed relationship to how many
            // full DispInfoSize records the decompressed content holds. ReadDispInfos below decompresses
            // first and sizes its own array off the real byte count.
            if (lumps[LumpDispInfo].Length <= 0)
                return result;

            var worldVerts = ReadVectors(r, lumps[LumpVertexes]);
            var edges = ReadEdges(r, lumps[LumpEdges]);
            var surfEdges = ReadInts(r, lumps[LumpSurfEdges]);
            var dispVerts = ReadDispVerts(r, lumps[LumpDispVerts]);
            var infos = ReadDispInfos(r, lumps[LumpDispInfo]);

            // Original faces in preference to split ones. A displacement is drawn on a single quad and
            // laid out across it; the ordinary face lump holds what vbsp left after cutting that quad
            // up for the tree, and a piece of a quad is the wrong footprint to stretch a grid over.
            //
            // Not every map still has them. Repackers discard the lump, and a map that has been through
            // one leaves nothing to fall back on but the split faces - see the note on the class.
            var original = ReadFaces(r, lumps[LumpOriginalFaces]);
            var faces = ReadFaces(r, lumps[LumpFaces]);

            result.DisplacementCount = infos.Length;
            result.DispVertRecords = dispVerts.Length;
            result.HasOriginalFaces = original.Length > 0;

            r.BaseStream.Seek(4, SeekOrigin.Begin);
            result.BspVersion = r.ReadInt32();

            result.Build(infos, result.HasOriginalFaces ? original : faces, edges, surfEdges,
                worldVerts, dispVerts, !result.HasOriginalFaces);

            return result;
        }

        private readonly record struct DispInfo(BspFile.Vector3 StartPosition, int VertStart, int Power, int Contents, int MapFace);
        public readonly record struct DispVert(BspFile.Vector3 Vector, float Distance, float Alpha);
        private readonly record struct Face(int FirstEdge, int NumEdges, int DispInfo);

        private static BspFile.Vector3[] ReadVectors(BinaryReader r, (int Offset, int Length) lump)
        {
            var bytes = LzmaLump.Read(r, lump.Offset, lump.Length);
            var result = new BspFile.Vector3[bytes.Length / 12];

            using var ms = new MemoryStream(bytes);
            using var lr = new BinaryReader(ms);
            for (int i = 0; i < result.Length; i++)
                result[i] = new BspFile.Vector3(lr.ReadSingle(), lr.ReadSingle(), lr.ReadSingle());
            return result;
        }

        private static (ushort A, ushort B)[] ReadEdges(BinaryReader r, (int Offset, int Length) lump)
        {
            var bytes = LzmaLump.Read(r, lump.Offset, lump.Length);
            var result = new (ushort, ushort)[bytes.Length / 4];

            using var ms = new MemoryStream(bytes);
            using var lr = new BinaryReader(ms);
            for (int i = 0; i < result.Length; i++)
                result[i] = (lr.ReadUInt16(), lr.ReadUInt16());
            return result;
        }

        private static int[] ReadInts(BinaryReader r, (int Offset, int Length) lump)
        {
            var bytes = LzmaLump.Read(r, lump.Offset, lump.Length);
            var result = new int[bytes.Length / 4];

            using var ms = new MemoryStream(bytes);
            using var lr = new BinaryReader(ms);
            for (int i = 0; i < result.Length; i++)
                result[i] = lr.ReadInt32();
            return result;
        }

        private static Face[] ReadFaces(BinaryReader r, (int Offset, int Length) lump)
        {
            var bytes = LzmaLump.Read(r, lump.Offset, lump.Length);
            var result = new Face[bytes.Length / FaceSize];

            using var ms = new MemoryStream(bytes);
            using var lr = new BinaryReader(ms);
            for (int i = 0; i < result.Length; i++)
            {
                lr.BaseStream.Seek(i * FaceSize + 4, SeekOrigin.Begin);
                int firstEdge = lr.ReadInt32();
                short numEdges = lr.ReadInt16();
                lr.ReadInt16(); // texinfo
                short dispInfo = lr.ReadInt16();
                result[i] = new Face(firstEdge, numEdges, dispInfo);
            }
            return result;
        }

        private static DispVert[] ReadDispVerts(BinaryReader r, (int Offset, int Length) lump)
        {
            var bytes = LzmaLump.Read(r, lump.Offset, lump.Length);
            var result = new DispVert[bytes.Length / DispVertSize];

            using var ms = new MemoryStream(bytes);
            using var lr = new BinaryReader(ms);
            for (int i = 0; i < result.Length; i++)
            {
                var v = new BspFile.Vector3(lr.ReadSingle(), lr.ReadSingle(), lr.ReadSingle());
                float dist = lr.ReadSingle();
                float alpha = lr.ReadSingle();
                result[i] = new DispVert(v, dist, alpha);
            }
            return result;
        }

        private static DispInfo[] ReadDispInfos(BinaryReader r, (int Offset, int Length) lump)
        {
            var bytes = LzmaLump.Read(r, lump.Offset, lump.Length);
            var result = new DispInfo[bytes.Length / DispInfoSize];

            using var ms = new MemoryStream(bytes);
            using var lr = new BinaryReader(ms);
            for (int i = 0; i < result.Length; i++)
            {
                lr.BaseStream.Seek(i * DispInfoSize, SeekOrigin.Begin);

                var start = new BspFile.Vector3(lr.ReadSingle(), lr.ReadSingle(), lr.ReadSingle());
                int vertStart = lr.ReadInt32();
                lr.ReadInt32();              // triangle tag start
                int power = lr.ReadInt32();
                lr.ReadInt32();              // minimum tesselation
                lr.ReadSingle();             // smoothing angle
                int contents = lr.ReadInt32();
                int mapFace = lr.ReadUInt16();

                result[i] = new DispInfo(start, vertStart, power, contents, mapFace);
            }
            return result;
        }

        private void Build(DispInfo[] infos, Face[] faces, (ushort A, ushort B)[] edges,
            int[] surfEdges, BspFile.Vector3[] worldVerts, DispVert[] dispVerts, bool comparableLinks)
        {
            float shortestDirection = float.MaxValue, longestDirection = 0f;
            var tris = new List<BspFile.Vector3>();
            var triContents = new List<int>();

            // A displacement is paired with its face by whichever of the two links resolves inside the
            // set of faces being used. They name each other - the displacement records `m_iMapFace`,
            // the face records `dispinfo` - and following it from the face is the sturdier direction,
            // because then the quad and the claim to it come from the same record and cannot disagree.
            // The displacement's own index is the fallback for maps where the faces carry no back-link.
            int claimCount = 0;
            var faceFor = new int[infos.Length];
            Array.Fill(faceFor, -1);

            for (int f = 0; f < faces.Length; f++)
            {
                int claimed = faces[f].DispInfo;
                if ((uint)claimed < (uint)infos.Length)
                {
                    claimCount++;
                    if (faceFor[claimed] < 0) faceFor[claimed] = f;
                }
            }

            FacesClaimingDisplacement = claimCount;

            // Whether those claims are a bijection, which is the part that actually matters and which
            // the count alone does not show. 183 claims over 183 displacements looks conclusive and is
            // not: two faces claiming one displacement and none claiming another produces the same
            // total, and the unclaimed one then falls back to `m_iMapFace` without saying so.
            for (int d = 0; d < faceFor.Length; d++)
            {
                if (faceFor[d] >= 0) DisplacementsWithBackLink++;

                // The two links naming different faces is worth knowing when both index the same lump:
                // it means one of them is stale, and which one is followed decides the base quad.
                //
                // Only then, though. `m_iMapFace` always indexes the ordinary face lump, so on a map
                // where the original faces are being used the two are not comparable and every
                // displacement "disagrees" - which is how this read on gm_construct before the guard,
                // 110 of 110, and meant nothing at all.
                if (!comparableLinks) continue;

                if (faceFor[d] >= 0 && faceFor[d] != infos[d].MapFace) BackLinkDisagreesWithMapFace++;
            }



            for (int dispIndex = 0; dispIndex < infos.Length; dispIndex++)
            {
                var info = infos[dispIndex];

                if ((info.Contents & TracedContents) == 0)
                    continue;

                int faceIndex = faceFor[dispIndex] >= 0 ? faceFor[dispIndex] : info.MapFace;

                if ((uint)faceIndex >= (uint)faces.Length)
                    continue;

                var corners = FaceCorners(faces[faceIndex], edges, surfEdges, worldVerts);
                if (corners is null)
                    continue;

                // Measured before the rotation consumes it: how convincingly startPosition identifies
                // one corner, and by how much the next-nearest was beaten. A small gap and a clear
                // runner-up means the orientation is known; anything else means it was picked.
                float nearest = float.MaxValue, runnerUp = float.MaxValue;
                float baseMinZ = float.MaxValue, baseMaxZ = float.MinValue;

                foreach (var corner in corners)
                {
                    float dx = corner.X - info.StartPosition.X;
                    float dy = corner.Y - info.StartPosition.Y;
                    float dz = corner.Z - info.StartPosition.Z;
                    float gap = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

                    if (gap < nearest) { runnerUp = nearest; nearest = gap; }
                    else if (gap < runnerUp) { runnerUp = gap; }

                    baseMinZ = MathF.Min(baseMinZ, corner.Z);
                    baseMaxZ = MathF.Max(baseMaxZ, corner.Z);
                }

                int firstTriangle = tris.Count / 3;

                float maxLateral = 0f, maxVertical = 0f;

                Rotate(corners, info.StartPosition);

                // The quad's own normal, so the offsets below can be judged against the face they
                // belong to rather than against the world.
                float e1x = corners[1].X - corners[0].X;
                float e1y = corners[1].Y - corners[0].Y;
                float e1z = corners[1].Z - corners[0].Z;
                float e2x = corners[3].X - corners[0].X;
                float e2y = corners[3].Y - corners[0].Y;
                float e2z = corners[3].Z - corners[0].Z;

                float planeX = e1y * e2z - e1z * e2y;
                float planeY = e1z * e2x - e1x * e2z;
                float planeZ = e1x * e2y - e1y * e2x;

                float planeLength = MathF.Sqrt(planeX * planeX + planeY * planeY + planeZ * planeZ);

                if (planeLength > 1e-6f)
                {
                    planeX /= planeLength; planeY /= planeLength; planeZ /= planeLength;
                }
                else
                {
                    planeX = 0f; planeY = 0f; planeZ = 1f;
                }

                int size = 1 << info.Power;      // cells per side
                int stride = size + 1;           // vertices per side
                var grid = new BspFile.Vector3[stride * stride];
                var raw = new DispVert[stride * stride];

                for (int i = 0; i < stride; i++)
                {
                    float tx = i / (float)size;
                    var left = Lerp(corners[0], corners[1], tx);
                    var right = Lerp(corners[3], corners[2], tx);

                    for (int j = 0; j < stride; j++)
                    {
                        float ty = j / (float)size;
                        var p = Lerp(left, right, ty);

                        int index = info.VertStart + i * stride + j;
                        if ((uint)index < (uint)dispVerts.Length)
                        {
                            var dv = dispVerts[index];
                            raw[i * stride + j] = dv;

                            float ox = dv.Vector.X * dv.Distance;
                            float oy = dv.Vector.Y * dv.Distance;
                            float oz = dv.Vector.Z * dv.Distance;

                            // How far the offsets push across the base quad versus through it.
                            //
                            // Measured in the quad's own frame, not the world's. A displacement is
                            // sculpted along its face's normal - out of the surface - so the component
                            // lying *in* the plane should stay small whatever way the face happens to
                            // point. Judging this by world Z instead reads every wall displacement as
                            // broken, because a wall is sculpted horizontally by definition; that
                            // mistake was made here first and flagged 55 of this map's 183 as
                            // suspect when most were ordinary cliffs.
                            float along = ox * planeX + oy * planeY + oz * planeZ;
                            float inPlaneX = ox - along * planeX;
                            float inPlaneY = oy - along * planeY;
                            float inPlaneZ = oz - along * planeZ;

                            maxLateral = MathF.Max(maxLateral, MathF.Sqrt(
                                inPlaneX * inPlaneX + inPlaneY * inPlaneY + inPlaneZ * inPlaneZ));
                            maxVertical = MathF.Max(maxVertical, MathF.Abs(along));

                            // Unit length is the contract on the direction field. Anything else means
                            // the record is being read at the wrong stride or offset.
                            float unit = MathF.Sqrt(dv.Vector.X * dv.Vector.X +
                                                    dv.Vector.Y * dv.Vector.Y +
                                                    dv.Vector.Z * dv.Vector.Z);
                            shortestDirection = MathF.Min(shortestDirection, unit);
                            longestDirection = MathF.Max(longestDirection, unit);

                            p = new BspFile.Vector3(p.X + ox, p.Y + oy, p.Z + oz);
                        }

                        grid[i * stride + j] = p;
                    }
                }

                for (int i = 0; i < size; i++)
                {
                    for (int j = 0; j < size; j++)
                    {
                        var a = grid[i * stride + j];
                        var b = grid[i * stride + j + 1];
                        var c = grid[(i + 1) * stride + j + 1];
                        var d = grid[(i + 1) * stride + j];

                        // The diagonal alternates in a checkerboard, which is how Source splits a
                        // displacement cell - not a fixed corner-to-corner cut.
                        //
                        // A quad's four corners rarely lie in a plane, so the two ways of splitting it
                        // describe two different surfaces, and they part company most in the middle of
                        // the cell. Cutting always the same way is invisible on fine terrain: cells a
                        // few units across with gentle relief differ by a fraction of a unit, which is
                        // why gm_construct agreed with the engine to a median of half a unit. It is not
                        // invisible on coarse terrain. This map has displacements whose cells run
                        // hundreds of units across a thousand units of relief, and there the wrong
                        // diagonal moves the surface by tens of units - enough to lift ground above a
                        // nav area, and enough to tilt it past the walkable slope limit so the floor
                        // finder rejects it and falls through to whatever lies beneath.
                        if (((i + j) & 1) == 0)
                        {
                            tris.Add(a); tris.Add(b); tris.Add(c);
                            tris.Add(a); tris.Add(c); tris.Add(d);
                        }
                        else
                        {
                            tris.Add(a); tris.Add(b); tris.Add(d);
                            tris.Add(b); tris.Add(c); tris.Add(d);
                        }

                        // One entry per triangle, not per vertex - the traces index by triangle.
                        triContents.Add(info.Contents);
                        triContents.Add(info.Contents);
                    }
                }

                float loX = float.MaxValue, loY = float.MaxValue, loZ = float.MaxValue;
                float hiX = float.MinValue, hiY = float.MinValue, hiZ = float.MinValue;

                foreach (var p in grid)
                {
                    loX = MathF.Min(loX, p.X); hiX = MathF.Max(hiX, p.X);
                    loY = MathF.Min(loY, p.Y); hiY = MathF.Max(hiY, p.Y);
                    loZ = MathF.Min(loZ, p.Z); hiZ = MathF.Max(hiZ, p.Z);
                }

                grids.Add(grid);

                rawGrids.Add(raw);

                surfaces.Add(new Surface(
                    dispIndex, info.Power, info.VertStart, info.Contents, tris.Count / 3 - firstTriangle,
                    info.StartPosition, nearest, runnerUp,
                    loX, hiX, loY, hiY, loZ, hiZ, baseMinZ, baseMaxZ,
                    maxLateral, maxVertical,
                    corners[0], corners[1], corners[2], corners[3]));
            }

            ShortestDirection = shortestDirection;

            LongestDirection = longestDirection;

            mesh.Build(tris.ToArray(), triContents.ToArray());
        }

        /// <summary>
        /// The four corners of a displacement's base face, in winding order. Displacements are always
        /// built on quads, so anything else is not one and is skipped.
        /// </summary>
        private static BspFile.Vector3[]? FaceCorners(Face face, (ushort A, ushort B)[] edges,
            int[] surfEdges, BspFile.Vector3[] worldVerts)
        {
            if (face.NumEdges != 4)
                return null;

            var corners = new BspFile.Vector3[4];

            for (int i = 0; i < 4; i++)
            {
                int se = face.FirstEdge + i;
                if ((uint)se >= (uint)surfEdges.Length)
                    return null;

                int value = surfEdges[se];
                int edge = Math.Abs(value);
                if ((uint)edge >= (uint)edges.Length)
                    return null;

                // a negative surfedge means the edge is walked backwards
                int vertex = value >= 0 ? edges[edge].A : edges[edge].B;
                if ((uint)vertex >= (uint)worldVerts.Length)
                    return null;

                corners[i] = worldVerts[vertex];
            }

            return corners;
        }

        /// <summary>
        /// Rotates the corners so the one nearest the displacement's recorded start position comes
        /// first. vbsp stores the grid relative to that corner, and getting it wrong mirrors or rotates
        /// the whole surface rather than failing outright.
        /// </summary>
        private static void Rotate(BspFile.Vector3[] corners, BspFile.Vector3 start)
        {
            int nearest = 0;
            float best = float.MaxValue;

            for (int i = 0; i < 4; i++)
            {
                float dx = corners[i].X - start.X;
                float dy = corners[i].Y - start.Y;
                float dz = corners[i].Z - start.Z;
                float d = dx * dx + dy * dy + dz * dz;

                if (d >= best) continue;
                best = d;
                nearest = i;
            }

            if (nearest == 0) return;

            var copy = (BspFile.Vector3[])corners.Clone();
            for (int i = 0; i < 4; i++)
                corners[i] = copy[(i + nearest) % 4];
        }

        private static BspFile.Vector3 Lerp(BspFile.Vector3 a, BspFile.Vector3 b, float t) =>
            new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);

        /// <summary>Whether the segment crosses any displacement surface matching <paramref name="mask"/>.</summary>
        public bool Blocks(BspFile.Vector3 a, BspFile.Vector3 b, int mask) => mesh.Blocks(a, b, mask);

        /// <summary>
        /// The nearest displacement surface the segment crosses, with the triangle's normal.
        ///
        /// A displacement has no single plane to read a normal off - it is a triangulated grid - so the
        /// normal is the face normal of whichever triangle was hit first. That is the true surface
        /// orientation, which is what the walkability and stair tests need.
        /// </summary>
        public bool TryTraceSurface(BspFile.Vector3 a, BspFile.Vector3 b, int mask,
            out float fraction, out BspFile.Vector3 normal)
            => mesh.TryTraceSurface(a, b, mask, out fraction, out normal);

        /// <summary>Sweeps a box against the terrain. See <see cref="TriangleMesh.TryTraceHull"/>.</summary>
        public bool TryTraceHull(BspFile.Vector3 a, BspFile.Vector3 b,
            BspFile.Vector3 mins, BspFile.Vector3 maxs, int mask,
            out float fraction, out BspFile.Vector3 normal, out bool startSolid)
            => mesh.TryTraceHull(a, b, mins, maxs, mask, out fraction, out normal, out startSolid);
    }
}