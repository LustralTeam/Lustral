namespace Lustral.Core.GameData;

/// <summary>LZ4 block decompression (no frame), for the LZ4 and LZ4HC blocks in Unity's bundles.</summary>
internal static class Lz4
{
    /// <exception cref="InvalidDataException">The data is not a valid LZ4 block of the specified size.</exception>
    public static byte[] Decompress(byte[] source, int sourceOffset, int sourceLength, int decompressedLength)
    {
        var output = new byte[decompressedLength];
        var input = sourceOffset;
        var end = sourceOffset + sourceLength;
        var written = 0;
        while (input < end)
        {
            var token = source[input++];

            var literals = ReadLength(token >> 4, source, ref input, end);
            if (literals > end - input || literals > decompressedLength - written)
                throw new InvalidDataException("LZ4 literals extend past the end of the block");
            Buffer.BlockCopy(source, input, output, written, literals);
            input += literals;
            written += literals;
            if (input == end)
                break; // The last sequence contains only literals.

            if (end - input < 2)
                throw new InvalidDataException("LZ4 block ends inside a match offset");
            var offset = source[input] | (source[input + 1] << 8);
            input += 2;
            var match = ReadLength(token & 0xF, source, ref input, end) + 4;
            if (offset == 0 || offset > written || match > decompressedLength - written)
                throw new InvalidDataException("LZ4 match points outside the output");
            // Copied byte by byte, because a match may overlap the bytes being copied.
            for (var from = written - offset; match > 0; match--)
                output[written++] = output[from++];
        }

        if (written != decompressedLength)
            throw new InvalidDataException($"LZ4 block decompressed to {written} bytes instead of {decompressedLength}");
        return output;
    }

    private static int ReadLength(int length, byte[] source, ref int input, int end)
    {
        if (length != 15)
            return length;
        byte next;
        do
        {
            if (input >= end)
                throw new InvalidDataException("LZ4 block ends inside a length");
            next = source[input++];
            length += next;
        }
        while (next == 255);
        return length;
    }
}
