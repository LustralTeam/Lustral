namespace Lustral.Core.GameData;

/// <summary>
/// A file inside a UnityFS bundle (<c>data.unity3d</c>, in which LZ4-compressed player builds store their data). Only
/// the blocks covered by a read are decompressed. Supports uncompressed, LZ4 and LZ4HC blocks; LZMA (used by older asset
/// bundles) is not supported.
/// </summary>
internal sealed class UnityBundle : ByteSource
{
    private const uint CompressionMask = 0x3F;
    private const uint BlocksInfoAtTheEnd = 0x80;
    private const uint BlockInfoNeedPaddingAtStart = 0x200;

    private readonly FileStream _stream;
    private readonly List<Block> _blocks = [];
    private readonly Dictionary<int, byte[]> _decompressed = new();
    private readonly long _fileOffset;
    private readonly long _fileSize;

    private UnityBundle(string path, string fileName)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
        try
        {
            var header = new ByteReader(ReadAt(0, (int)Math.Min(256, _stream.Length)), bigEndian: true);
            if (header.ReadNullTerminated() != "UnityFS")
                throw new InvalidDataException("not a UnityFS bundle");
            var format = header.ReadUInt32();
            header.ReadNullTerminated(); // "5.x.x"
            header.ReadNullTerminated(); // Unity version
            header.ReadInt64(); // bundle size
            var compressedInfoSize = header.ReadInt32();
            var infoSize = header.ReadInt32();
            var flags = header.ReadUInt32();
            if (compressedInfoSize < 0 || infoSize < 0 || infoSize > 64 * 1024 * 1024)
                throw new InvalidDataException("implausible block info size");

            long position = header.Position;
            if (format >= 7)
                position = Align16(position);
            byte[] compressedInfo;
            if ((flags & BlocksInfoAtTheEnd) != 0)
                compressedInfo = ReadAt(_stream.Length - compressedInfoSize, compressedInfoSize);
            else
            {
                compressedInfo = ReadAt(position, compressedInfoSize);
                position += compressedInfoSize;
            }
            if ((flags & BlockInfoNeedPaddingAtStart) != 0)
                position = Align16(position);

            var info = new ByteReader(Decompress(compressedInfo, infoSize, flags & CompressionMask), bigEndian: true);
            info.Skip(16); // Hash of the uncompressed data
            var blockCount = info.ReadInt32();
            long uncompressedOffset = 0;
            for (var i = 0; i < blockCount; i++)
            {
                var size = info.ReadInt32();
                var compressedSize = info.ReadInt32();
                var blockFlags = info.ReadUInt16();
                if (size < 0 || compressedSize < 0)
                    throw new InvalidDataException("invalid block size");
                _blocks.Add(new Block(position, compressedSize, uncompressedOffset, size, blockFlags & CompressionMask));
                position += compressedSize;
                uncompressedOffset += size;
            }

            var nodeCount = info.ReadInt32();
            for (var i = 0; i < nodeCount; i++)
            {
                var offset = info.ReadInt64();
                var size = info.ReadInt64();
                info.ReadUInt32(); // flags
                if (info.ReadNullTerminated() == fileName)
                {
                    _fileOffset = offset;
                    _fileSize = size;
                    return;
                }
            }
            throw new FileNotFoundException($"the bundle does not contain {fileName}");
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
    }

    public override long Length => _fileSize;

    /// <summary>Opens a file inside the bundle.</summary>
    /// <exception cref="InvalidDataException">The bundle is not supported by this reader.</exception>
    /// <exception cref="FileNotFoundException">The bundle does not contain the file.</exception>
    public static UnityBundle Open(string bundlePath, string fileName) => new(bundlePath, fileName);

    public override byte[] Read(long offset, int count)
    {
        if (offset < 0 || count < 0 || offset > _fileSize - count)
            throw new InvalidDataException($"read of {count} bytes at {offset} extends past the end of the bundled file");
        var result = new byte[count];
        var written = 0;
        var at = _fileOffset + offset;
        while (written < count)
        {
            var index = _blocks.FindIndex(b => at >= b.UncompressedOffset && at < b.UncompressedOffset + b.Size);
            if (index < 0)
                throw new InvalidDataException("the bundled file extends past the bundle's blocks");
            var block = _blocks[index];
            if (!_decompressed.TryGetValue(index, out var data))
                _decompressed[index] = data = Decompress(ReadAt(block.FileOffset, block.CompressedSize), block.Size, block.Compression);
            var from = (int)(at - block.UncompressedOffset);
            var take = Math.Min(count - written, block.Size - from);
            Buffer.BlockCopy(data, from, result, written, take);
            written += take;
            at += take;
        }
        return result;
    }

    public override void Dispose() => _stream.Dispose();

    private static byte[] Decompress(byte[] data, int size, uint compression) => compression switch
    {
        0 when data.Length == size => data,
        2 or 3 => Lz4.Decompress(data, 0, data.Length, size),
        1 => throw new InvalidDataException("LZMA-compressed bundles are not supported"),
        _ => throw new InvalidDataException($"unknown bundle compression {compression}"),
    };

    private byte[] ReadAt(long offset, int count)
    {
        if (offset < 0 || offset > _stream.Length - count)
            throw new InvalidDataException("the bundle is shorter than its header indicates");
        var buffer = new byte[count];
        _stream.Position = offset;
        var read = 0;
        while (read < count)
        {
            var n = _stream.Read(buffer, read, count - read);
            if (n == 0)
                throw new InvalidDataException("unexpected end of the bundle");
            read += n;
        }
        return buffer;
    }

    private static long Align16(long position) => (position + 15) & ~15L;

    private readonly record struct Block(long FileOffset, int CompressedSize, long UncompressedOffset, int Size, uint Compression);
}
