using System;
using System.Collections.Generic;
using System.IO;

namespace Meshwright
{
    /// <summary>
    /// A model's collision mesh, read from its .phy file.
    ///
    /// This is the geometry the engine actually collides against for a static prop - not the .mdl, which
    /// is what gets drawn. A prop's visible mesh and its collision mesh are different objects with
    /// different triangle counts, and using the visible one would be both slower and wrong.
    ///
    /// **The format is Havok's, not Valve's, and it shows.** Everything inside a .phy after the first
    /// sixteen bytes is IVP/Havok's "compact surface": a bounding-volume tree of convex hulls, with
    /// bitfielded headers, offsets relative to four different bases depending on which structure you are
    /// standing in, and vertices in metres on a left-handed axis system that has to be converted back to
    /// Hammer units. None of it resembles the rest of the BSP.
    ///
    /// Two things make it tractable. The first is that only the leaves matter: the tree exists to
    /// accelerate the engine's own queries, and Meshwright has its own BVH downstream, so the tree is
    /// walked purely to enumerate leaf ledges and then discarded. The second is that a convex hull's
    /// triangles are already triangles - once the points are converted there is no reconstruction to do,
    /// unlike displacements.
    ///
    /// Everything here is bounds-checked and returns what it managed to read rather than throwing. A
    /// .phy that cannot be parsed should cost one prop's collision, not the whole map's - and third
    /// party models are the common case in Garry's Mod, where a mapper's content comes from a hundred
    /// sources and some of it is malformed.
    /// </summary>
    public sealed class PhyFile
    {
        /// <summary>'VPHY', identifying a compact surface.</summary>
        private const int VphysicsId = 0x59485056;

        /// <summary>
        /// Metres to Hammer units. Havok works in metres; Source stores an inch as 0.0254 of one, so
        /// every coordinate coming out of a .phy is scaled by the reciprocal.
        /// </summary>
        private const float IvpToHammer = 1f / 0.0254f;

        private const int CompactSurfaceHeaderSize = 28;
        private const int LegacySurfaceHeaderSize = 48;
        private const int LedgeNodeSize = 28;
        private const int LedgeHeaderSize = 16;
        private const int TriangleSize = 16;
        private const int PointSize = 16;

        /// <summary>Collision triangles in model space, three vertices per triangle.</summary>
        public BspFile.Vector3[] Triangles { get; private set; } = [];

        public int TriangleCount => Triangles.Length / 3;

        /// <summary>How many convex hulls the tree walk reached.</summary>
        public int LedgeCount { get; private set; }

        /// <summary>How many solids the file declared, against how many parsed.</summary>
        public int SolidsDeclared { get; private set; }
        public int SolidsParsed { get; private set; }

        public static PhyFile Parse(byte[] bytes)
        {
            var result = new PhyFile();
            var tris = new List<BspFile.Vector3>();

            if (bytes.Length < 16) return result;

            using var ms = new MemoryStream(bytes);
            using var r = new BinaryReader(ms);

            int headerSize = r.ReadInt32();
            r.ReadInt32();                       // id, unused
            int solidCount = r.ReadInt32();
            r.ReadInt32();                       // checksum of the .mdl this belongs to

            result.SolidsDeclared = solidCount;

            if (headerSize < 16 || headerSize > bytes.Length || solidCount <= 0 || solidCount > 4096)
                return result;

            long at = headerSize;

            for (int solid = 0; solid < solidCount; solid++)
            {
                if (at + 4 > bytes.Length) break;

                ms.Seek(at, SeekOrigin.Begin);
                int solidSize = r.ReadInt32();

                if (solidSize <= 0 || at + 4 + solidSize > bytes.Length) break;

                if (result.ReadSolid(r, ms, at + 4, bytes.Length, tris))
                    result.SolidsParsed++;

                // Solids are laid end to end, each preceded by its own length. Advancing by the declared
                // size rather than by however far the reader happened to get is what keeps a malformed
                // solid from derailing the ones after it.
                at += 4 + solidSize;
            }

            result.Triangles = tris.ToArray();
            return result;
        }

        private bool ReadSolid(BinaryReader r, MemoryStream ms, long start, long length, List<BspFile.Vector3> tris)
        {
            if (start + CompactSurfaceHeaderSize + LegacySurfaceHeaderSize > length) return false;

            ms.Seek(start, SeekOrigin.Begin);

            int id = r.ReadInt32();
            r.ReadInt16();                       // version
            short modelType = r.ReadInt16();

            // Only the compact-surface form carries convex hulls. The alternative Havok shapes - mopp
            // codes above all - encode collision as a bytecode this does not implement, and guessing at
            // one produces geometry in the wrong place rather than no geometry.
            if (id != VphysicsId || modelType != 0) return false;

            // The ledge tree's root offset is measured from the legacy header, not from the file or from
            // the compact surface header. Three different bases are in play inside one solid and mixing
            // them up lands in the middle of a vertex array, which parses without complaint.
            long surfaceStart = start + CompactSurfaceHeaderSize;

            ms.Seek(surfaceStart + 28, SeekOrigin.Begin);   // past mass centre, inertia, radius
            r.ReadInt32();                                  // max deviation and byte size, bitfielded
            int rootOffset = r.ReadInt32();

            long root = surfaceStart + rootOffset;

            if (rootOffset <= 0 || root + LedgeNodeSize > length)
            {
                // No usable tree. The surface may still be a single ledge sitting where the tree would
                // have been, which is how the simplest models are written.
                return ReadLedge(r, ms, surfaceStart, length, tris);
            }

            return WalkTree(r, ms, root, length, tris, 0);
        }

        /// <summary>
        /// Descends the ledge tree collecting leaves.
        ///
        /// Recursion is bounded rather than trusted: the offsets are read from a file that may be
        /// corrupt or hostile, and a right-child offset of zero pointing back at its own parent is an
        /// infinite loop that would otherwise hang the whole build on one bad model.
        /// </summary>
        private bool WalkTree(BinaryReader r, MemoryStream ms, long node, long length,
            List<BspFile.Vector3> tris, int depth)
        {
            if (depth > 64 || node < 0 || node + LedgeNodeSize > length) return false;

            ms.Seek(node, SeekOrigin.Begin);
            int rightOffset = r.ReadInt32();
            int ledgeOffset = r.ReadInt32();

            if (rightOffset == 0)
                return ReadLedge(r, ms, node + ledgeOffset, length, tris);

            // The left child sits immediately after this node; the right one is where the offset says.
            bool left = WalkTree(r, ms, node + LedgeNodeSize, length, tris, depth + 1);
            bool right = WalkTree(r, ms, node + rightOffset, length, tris, depth + 1);

            return left || right;
        }

        private bool ReadLedge(BinaryReader r, MemoryStream ms, long ledge, long length,
            List<BspFile.Vector3> tris)
        {
            if (ledge < 0 || ledge + LedgeHeaderSize > length) return false;

            ms.Seek(ledge, SeekOrigin.Begin);

            int pointOffset = r.ReadInt32();
            r.ReadInt32();                       // node offset back up the tree, or client data
            r.ReadUInt32();                      // flags and size, bitfielded; unused here
            short triangleCount = r.ReadInt16();
            r.ReadInt16();

            if (triangleCount <= 0 || triangleCount > 8192) return false;

            long points = ledge + pointOffset;
            long triangles = ledge + LedgeHeaderSize;

            if (points < 0 || points > length || triangles + (long)triangleCount * TriangleSize > length)
                return false;

            LedgeCount++;

            for (int t = 0; t < triangleCount; t++)
            {
                ms.Seek(triangles + (long)t * TriangleSize + 4, SeekOrigin.Begin);

                // Three edges, each of which names the point its own corner starts at. The rest of the
                // edge record is adjacency the engine uses for its own traversal and this does not.
                int a = (int)(r.ReadUInt32() & 0xFFFF);
                int b = (int)(r.ReadUInt32() & 0xFFFF);
                int c = (int)(r.ReadUInt32() & 0xFFFF);

                if (!TryPoint(r, ms, points, a, length, out var pa) ||
                    !TryPoint(r, ms, points, b, length, out var pb) ||
                    !TryPoint(r, ms, points, c, length, out var pc))
                    continue;

                tris.Add(pa);
                tris.Add(pb);
                tris.Add(pc);
            }

            return true;
        }

        private static bool TryPoint(BinaryReader r, MemoryStream ms, long points, int index, long length,
            out BspFile.Vector3 point)
        {
            point = default;

            long at = points + (long)index * PointSize;
            if (index < 0 || at + 12 > length) return false;

            ms.Seek(at, SeekOrigin.Begin);

            float x = r.ReadSingle(), y = r.ReadSingle(), z = r.ReadSingle();

            // Havok's axes are not Hammer's: its Y is up where Source's Z is, and its Z is Source's Y.
            // Scale and swap in one step.
            //
            // The direction of that quarter turn about X is worth stating, because the obvious guess is
            // wrong. Valve's own ConvertPositionToHL reads (x, -z, y), and applying it here puts every
            // hull upside down and back to front - checked against the same models' declared bounds,
            // which came out with Y and Z both negated on all of them. That function converts a live
            // IVP world position, and the points baked into a .phy have already been through the
            // opposite turn on the way in. Anything claiming otherwise should be tested by comparing
            // `props -model` output with the .mdl bounds before being believed.
            point = new BspFile.Vector3(x * IvpToHammer, z * IvpToHammer, -y * IvpToHammer);
            return true;
        }
    }
}
