namespace Lustral.Core.GameData;

/// <summary>Random access to a file's bytes, reading only the ranges requested: globalgamemanagers on disk or inside a bundle.</summary>
internal abstract class ByteSource : IDisposable
{
    public abstract long Length { get; }

    /// <exception cref="InvalidDataException">The range extends past the end of the data.</exception>
    public abstract byte[] Read(long offset, int count);

    public virtual void Dispose()
    {
    }

    public sealed class FromFile(string path) : ByteSource
    {
        private readonly FileStream _stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);

        public override long Length => _stream.Length;

        public override byte[] Read(long offset, int count)
        {
            if (offset < 0 || count < 0 || offset > _stream.Length - count)
                throw new InvalidDataException($"read of {count} bytes at {offset} extends past the end of {Path.GetFileName(path)}");
            var buffer = new byte[count];
            _stream.Position = offset;
            var read = 0;
            while (read < count)
            {
                var n = _stream.Read(buffer, read, count - read);
                if (n == 0)
                    throw new InvalidDataException($"unexpected end of {Path.GetFileName(path)}");
                read += n;
            }
            return buffer;
        }

        public override void Dispose() => _stream.Dispose();
    }

    public sealed class FromBytes(byte[] bytes) : ByteSource
    {
        public override long Length => bytes.Length;

        public override byte[] Read(long offset, int count)
        {
            if (offset < 0 || count < 0 || offset > bytes.Length - count)
                throw new InvalidDataException($"read of {count} bytes at {offset} extends past the end");
            var buffer = new byte[count];
            Buffer.BlockCopy(bytes, (int)offset, buffer, 0, count);
            return buffer;
        }
    }
}

/// <summary>Reads numbers and strings from a byte array, in either byte order.</summary>
internal sealed class ByteReader(byte[] bytes, bool bigEndian = false)
{
    public int Position { get; set; }

    public bool BigEndian { get; set; } = bigEndian;

    public int Remaining => bytes.Length - Position;

    public byte ReadByte()
    {
        Need(1);
        return bytes[Position++];
    }

    public short ReadInt16() => (short)ReadUInt16();

    public ushort ReadUInt16()
    {
        Need(2);
        var value = BigEndian ? (bytes[Position] << 8) | bytes[Position + 1] : bytes[Position] | (bytes[Position + 1] << 8);
        Position += 2;
        return (ushort)value;
    }

    public int ReadInt32() => (int)ReadUInt32();

    public uint ReadUInt32()
    {
        Need(4);
        uint value = 0;
        for (var i = 0; i < 4; i++)
            value |= (uint)bytes[Position + i] << (BigEndian ? (3 - i) * 8 : i * 8);
        Position += 4;
        return value;
    }

    public long ReadInt64()
    {
        Need(8);
        ulong value = 0;
        for (var i = 0; i < 8; i++)
            value |= (ulong)bytes[Position + i] << (BigEndian ? (7 - i) * 8 : i * 8);
        Position += 8;
        return (long)value;
    }

    public byte[] ReadBytes(int count)
    {
        Need(count);
        var result = new byte[count];
        Buffer.BlockCopy(bytes, Position, result, 0, count);
        Position += count;
        return result;
    }

    public void Skip(int count)
    {
        Need(count);
        Position += count;
    }

    /// <summary>Moves to the next multiple of <paramref name="alignment"/>, counted from the start of the bytes.</summary>
    public void Align(int alignment)
    {
        var padding = (alignment - Position % alignment) % alignment;
        Need(padding);
        Position += padding;
    }

    /// <summary>Reads a NUL-terminated string (ASCII in practice; decoded as UTF-8).</summary>
    public string ReadNullTerminated(int maxLength = 1024)
    {
        var end = Array.IndexOf(bytes, (byte)0, Position, Math.Min(maxLength, Remaining));
        if (end < 0)
            throw new InvalidDataException("unterminated string");
        var text = System.Text.Encoding.UTF8.GetString(bytes, Position, end - Position);
        Position = end + 1;
        return text;
    }

    private void Need(int count)
    {
        if (count < 0 || count > Remaining)
            throw new InvalidDataException($"{count} bytes required at {Position}, but only {Remaining} remain");
    }
}
