using System;
using System.IO;
using SharpCompress.Compressors.LZMA;

namespace Meshwright
{
    /// <summary>
    /// Decompresses lumps compiled with per-lump LZMA compression.
    ///
    /// Every reader in this project - <see cref="BspFile"/>, <see cref="BspVisibility"/>,
    /// <see cref="BspModels"/>, <see cref="BspDisplacements"/> - was written against uncompressed lump
    /// bytes, because every BSP tested against during development happened to be uncompressed. A real
    /// map compiled with a tool that applies Valve's optional per-lump LZMA compression (the format
    /// vbsp itself supports, and one some community compile chains - ficool2's tools among them - turn
    /// on to shrink the shipped file) breaks that assumption completely: <b>every</b> lump comes back
    /// compressed, not just the large ones. A reader that does not know to decompress first does not
    /// fail gracefully - it reads the four-byte "LZMA" signature and the header fields that follow as
    /// if they were the first record of real data, and every count and offset derived from that is
    /// garbage. On the map that exposed this, the visibility lump's first four bytes were read as a
    /// cluster count of 1,095,588,428, which is what actually threw - not a corrupt file, a compressed
    /// one nothing here knew how to open.
    /// </summary>
    public static class LzmaLump
    {
        private static readonly byte[] Magic = "LZMA"u8.ToArray();

        /// <summary>
        /// Valve's lump header, present before the LZMA stream itself: a 4-byte "LZMA" tag, the
        /// decompressed size, the compressed size, then five LZMA1 property bytes (lc/lp/pb and the
        /// dictionary size) - the same layout the LZMA SDK's own reference decoder expects.
        /// </summary>
        private const int HeaderSize = 4 + 4 + 4 + 5;

        /// <summary>
        /// Largest decompressed lump this will size a buffer for, from a header it has not verified.
        ///
        /// 512MB is far above anything a Source map contains - the largest lump on the maps this has
        /// been run against is the visibility lump at a few tens of megabytes - and far below the point
        /// where trusting the number costs the machine.
        /// </summary>
        private const int MaxDecompressedSize = 512 * 1024 * 1024;

        /// <summary>
        /// Returns the lump's bytes ready to read as real data: decompressed if the lump carries the
        /// LZMA tag, returned unchanged otherwise. Every lump reader should route through this rather
        /// than reading directly from the file stream, since a compiled BSP compresses lumps
        /// independently - some may be compressed and others not within the same file.
        /// </summary>
        public static byte[] Read(BinaryReader r, int offset, int length)
        {
            if (length < HeaderSize)
                return ReadRaw(r, offset, length);

            r.BaseStream.Seek(offset, SeekOrigin.Begin);
            var header = r.ReadBytes(HeaderSize);

            if (header.Length < 4 || header[0] != Magic[0] || header[1] != Magic[1] ||
                header[2] != Magic[2] || header[3] != Magic[3])
            {
                return ReadRaw(r, offset, length);
            }

            int decompressedSize = BitConverter.ToInt32(header, 4);
            int compressedSize = BitConverter.ToInt32(header, 8);
            var properties = header[12..17];

            if (decompressedSize < 0 || compressedSize < 0 ||
                (long)offset + HeaderSize + compressedSize > r.BaseStream.Length ||
                decompressedSize > MaxDecompressedSize)
            {
                // Looks tagged but the sizes do not fit the file - safer to hand back the raw bytes and
                // let the caller's own bounds checks reject it than to trust a header that is already
                // suspect.
                //
                // The size ceiling is the same judgement applied to the other direction. The
                // decompressed size is four bytes read off a file that has already failed to make
                // sense, and it is used to size a buffer immediately: a value near int.MaxValue asks
                // for a two-gigabyte allocation before a single byte has been decoded. No real Source
                // lump comes close to the ceiling below.
                return ReadRaw(r, offset, length);
            }

            var compressed = r.ReadBytes(compressedSize);

            using var source = new MemoryStream(compressed);
            using var destination = new MemoryStream(decompressedSize);

            var decoder = new Decoder();
            decoder.SetDecoderProperties(properties);
            decoder.Code(source, destination, compressedSize, decompressedSize, null);

            var expanded = destination.ToArray();

            // A short expansion is silent corruption, and the worst kind: every lump reader here sizes
            // its own array off the byte count it is handed, so a truncated lump does not throw - it
            // yields fewer brushes, fewer faces, fewer planes, and a world with holes in it. What that
            // looks like downstream is a floor trace falling through geometry that should have stopped
            // it, which reads as a mesh problem rather than a read problem and sends you looking in
            // entirely the wrong place.
            if (expanded.Length != decompressedSize)
            {
                throw new InvalidDataException(
                    $"lump at offset {offset} decompressed to {expanded.Length:N0} bytes, " +
                    $"its header declares {decompressedSize:N0}");
            }

            return expanded;
        }

        /// <summary>
        /// The lump's bytes as they sit on disk, bounded by the file rather than by what the header
        /// claims.
        ///
        /// Every lump reader in the project funnels through here, which makes it the one place worth
        /// validating: the offset and length come from the BSP's own lump table, sixteen bytes per entry
        /// read straight off disk and never checked against anything. A truncated download, a file that
        /// is not a BSP, or a lump table that a compressed-lump reader misparsed all produce entries
        /// pointing outside the file - and <see cref="BinaryReader.ReadBytes"/> sizes its buffer from
        /// the count before it reads a byte, so a length near int.MaxValue is a two-gigabyte allocation
        /// request rather than a short read.
        ///
        /// Clamped rather than thrown on, deliberately. A lump that runs off the end of the file is
        /// usually a lump this tool does not need, and the readers above already treat a short lump as
        /// "fewer records"; refusing the whole map because one unused lump has a bad entry would be
        /// worse than the truncation itself. What must not happen is trusting the number.
        /// </summary>
        private static byte[] ReadRaw(BinaryReader r, int offset, int length)
        {
            long size = r.BaseStream.Length;

            if (offset < 0 || offset >= size || length <= 0)
                return [];

            long available = size - offset;
            int take = (int)Math.Min(length, available);

            r.BaseStream.Seek(offset, SeekOrigin.Begin);
            return r.ReadBytes(take);
        }
    }
}
