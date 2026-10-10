namespace Lustral.Core.GameData;

/// <summary>
/// A minimal reader for Unity's serialized file format (globalgamemanagers, format version 9 and later, from Unity 3.5),
/// sufficient to locate an object: the file's Unity version, each type's hash, and the location of each object's data.
/// </summary>
internal sealed class SerializedFile
{
    private const int MonoBehaviourClassId = 114;

    private readonly ByteSource _source;
    private readonly List<(int ClassId, byte[]? Hash)> _types = [];
    private readonly List<(long Offset, int Size, int TypeIndex, int ClassId)> _objects = [];
    private readonly long _dataOffset;

    private SerializedFile(ByteSource source)
    {
        _source = source;
        var header = new ByteReader(source.Read(0, (int)Math.Min(64, source.Length)), bigEndian: true);
        var metadataSize = (long)header.ReadUInt32();
        header.ReadUInt32(); // file size
        Version = header.ReadInt32();
        _dataOffset = header.ReadUInt32();
        if (Version < 9)
            throw new InvalidDataException($"serialized file version {Version} is too old (metadata at the end)");
        var bigEndian = header.ReadByte() != 0;
        header.Skip(3);
        if (Version >= 22)
        {
            metadataSize = header.ReadUInt32();
            header.ReadInt64(); // file size
            _dataOffset = header.ReadInt64();
            header.ReadInt64();
        }
        if (metadataSize <= 0 || metadataSize > 64 * 1024 * 1024)
            throw new InvalidDataException($"implausible metadata size {metadataSize}");

        // The metadata follows the header. Offsets within it are aligned relative to the start of the file, so reading
        // starts at 0.
        var reader = new ByteReader(source.Read(0, (int)(header.Position + metadataSize)), bigEndian) { Position = header.Position };
        BigEndian = bigEndian;
        UnityVersion = Version >= 7 ? reader.ReadNullTerminated() : "";
        if (Version >= 8)
            reader.ReadInt32(); // target platform
        var typeTrees = Version < 13 || reader.ReadByte() != 0;

        var typeCount = reader.ReadInt32();
        for (var i = 0; i < typeCount; i++)
        {
            var classId = reader.ReadInt32();
            if (Version >= 16)
                reader.ReadByte(); // stripped
            if (Version >= 17)
                reader.ReadInt16(); // script type index
            byte[]? hash = null;
            if (Version >= 13)
            {
                if ((Version < 16 && classId < 0) || (Version >= 16 && classId == MonoBehaviourClassId))
                    reader.Skip(16); // script id
                hash = reader.ReadBytes(16);
            }
            if (typeTrees)
                SkipTypeTree(reader);
            _types.Add((classId, hash));
        }

        var bigIds = Version is >= 7 and < 14 && reader.ReadInt32() != 0;
        var objectCount = reader.ReadInt32();
        for (var i = 0; i < objectCount; i++)
        {
            if (bigIds)
                reader.ReadInt64();
            else if (Version < 14)
                reader.ReadInt32();
            else
            {
                reader.Align(4);
                reader.ReadInt64(); // path id
            }
            var start = Version >= 22 ? reader.ReadInt64() : reader.ReadUInt32();
            var size = reader.ReadInt32();
            var typeIndex = reader.ReadInt32();
            var classId = Version >= 16 ? (typeIndex >= 0 && typeIndex < _types.Count ? _types[typeIndex].ClassId : -1) : reader.ReadUInt16();
            if (Version < 11)
                reader.ReadUInt16(); // destroyed
            if (Version is >= 11 and < 17)
                reader.ReadInt16(); // script type index
            if (Version is 15 or 16)
                reader.ReadByte(); // stripped
            _objects.Add((start, size, typeIndex, classId));
        }
    }

    /// <summary>The serialized file format version: 22 from Unity 2020.1 on.</summary>
    public int Version { get; }

    /// <summary>The Unity version that wrote the file, for example <c>2022.3.62f2</c>.</summary>
    public string UnityVersion { get; }

    /// <summary>Whether object data is big-endian (older console builds); PC builds are little-endian.</summary>
    public bool BigEndian { get; }

    /// <exception cref="InvalidDataException">The file is not a serialized file supported by this reader.</exception>
    public static SerializedFile Read(ByteSource source) => new(source);

    /// <summary>
    /// Returns the first object of a class, with its type's hash (Unity's MD4 hash of the type tree; null before format 13).
    /// </summary>
    public (byte[] Data, byte[]? TypeHash)? FindObject(int classId)
    {
        foreach (var (offset, size, typeIndex, objectClassId) in _objects)
        {
            if (objectClassId != classId)
                continue;
            if (size < 0)
                throw new InvalidDataException($"object of class {classId} has size {size}");
            var hash = Version >= 16 && typeIndex >= 0 && typeIndex < _types.Count ? _types[typeIndex].Hash
                : _types.FirstOrDefault(t => t.ClassId == classId).Hash;
            return (_source.Read(_dataOffset + offset, size), hash);
        }
        return null;
    }

    // A type tree embedded in the file (editor builds and some players): node count, string buffer size, nodes and strings.
    private void SkipTypeTree(ByteReader reader)
    {
        if (Version is >= 12 or 10)
        {
            var nodes = reader.ReadInt32();
            var strings = reader.ReadInt32();
            if (nodes < 0 || strings < 0)
                throw new InvalidDataException("invalid type tree");
            reader.Skip(checked(nodes * (Version >= 19 ? 32 : 24) + strings));
            if (Version >= 21)
            {
                var dependencies = reader.ReadInt32();
                reader.Skip(checked(dependencies * 4));
            }
        }
        else
            throw new InvalidDataException($"type trees of serialized file version {Version} are not supported");
    }
}
