using System.Buffers.Binary;
using System.Text;
using Lustral.Core.GameData;

namespace Lustral.Core.Tests;

/// <summary>
/// Reads the game's name, developer and version from synthetic files laid out as Unity writes them. PlayerSettings is
/// built from Lustral's layout table, so the reader is verified against the layouts it ships.
/// </summary>
public sealed class GameMetadataTests : IDisposable
{
    // PlayerSettings' type hash in Unity 2022.3.61f1 to 2022.3.67f1 builds (Lethal Company's and Bendy's 2022.3.62f2).
    private const string Unity2022Hash = "d00e6b7846b7c72167b16846108ca624";

    private readonly string _game = Path.Combine(Path.GetTempPath(), "lustral-tests", "game-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(_game, "Game_Data");
    private string LastRun => Path.Combine(_game, "Lustral", "cache", "game.txt");

    public GameMetadataTests()
    {
        Directory.CreateDirectory(Data);
        File.WriteAllText(Path.Combine(Data, "app.info"), "Zeekerss\nLethal Company");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_game, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Reads_the_version_with_the_layout_its_type_hash_names()
    {
        File.WriteAllBytes(Path.Combine(Data, "globalgamemanagers"), SerializedFile22("2022.3.62f2", Unity2022Hash, PlayerSettings(Unity2022Hash, "Zeekerss", "Lethal Company", "v72")));

        var metadata = GameMetadata.Read(Data, _game, LastRun);

        Assert.Equal(("Zeekerss", "Lethal Company", "v72", "2022.3.62f2"), (metadata.Developer, metadata.Name, metadata.Version, metadata.UnityVersion));
        Assert.Equal(GameVersionSource.Files, metadata.VersionSource);
    }

    [Fact]
    public void Reads_older_serialized_files()
    {
        var (version, hash) = TableEntry("2019.4");
        File.WriteAllBytes(Path.Combine(Data, "globalgamemanagers"), SerializedFile17(version, hash, PlayerSettings(hash, "Studio", "Old Game", "1.0.3")));

        var metadata = GameMetadata.Read(Data, _game, LastRun);

        Assert.Equal(("Studio", "Old Game", "1.0.3", version), (metadata.Developer, metadata.Name, metadata.Version, metadata.UnityVersion));
    }

    [Fact]
    public void Reads_globalgamemanagers_from_an_LZ4_bundle()
    {
        var file = SerializedFile22("2022.3.62f2", Unity2022Hash, PlayerSettings(Unity2022Hash, "LandCrab", "PEAK", "2.6.b"));
        File.WriteAllBytes(Path.Combine(Data, "data.unity3d"), Bundle(("sharedassets0.assets", new byte[300]), ("globalgamemanagers", file)));

        var metadata = GameMetadata.Read(Data, _game, LastRun);

        Assert.Equal(("LandCrab", "PEAK", "2.6.b"), (metadata.Developer, metadata.Name, metadata.Version));
    }

    [Fact]
    public void A_newer_Unity_leaves_the_version_unknown_until_the_running_game_reports_it()
    {
        var path = Path.Combine(Data, "globalgamemanagers");
        File.WriteAllBytes(path, SerializedFile22("6000.9.1f1", "00112233445566778899aabbccddeeff", new byte[64]));

        var first = GameMetadata.Read(Data, _game, LastRun);
        Assert.Equal(("Zeekerss", "Lethal Company", null, "6000.9.1f1", GameVersionSource.Unknown),
            (first.Developer, first.Name, first.Version, first.UnityVersion, first.VersionSource));

        GameMetadata.RecordVersion(LastRun, first.DataIdentity!, "v80");
        var second = GameMetadata.Read(Data, _game, LastRun);
        Assert.Equal(("v80", GameVersionSource.LastRun), (second.Version, second.VersionSource));

        // An update changes the data file: the recorded version no longer applies.
        File.AppendAllText(path, "update");
        var third = GameMetadata.Read(Data, _game, LastRun);
        Assert.Equal((null, GameVersionSource.Unknown), (third.Version, third.VersionSource));
    }

    [Fact]
    public void A_broken_data_file_falls_back_to_app_info()
    {
        File.WriteAllBytes(Path.Combine(Data, "globalgamemanagers"), [1, 2, 3]);
        var messages = new List<string>();

        var metadata = GameMetadata.Read(Data, _game, LastRun, messages.Add);

        Assert.Equal(("Zeekerss", "Lethal Company", null), (metadata.Developer, metadata.Name, metadata.Version));
        Assert.NotEmpty(messages);
    }

    [Fact]
    public void Lz4_decompresses_literals_and_overlapping_matches()
    {
        // "abc", then a 9-byte match 3 back (overlapping itself), then a last literal: "abcabcabcabcX".
        byte[] block = [0x35, (byte)'a', (byte)'b', (byte)'c', 0x03, 0x00, 0x10, (byte)'X'];

        Assert.Equal("abcabcabcabcX", Encoding.ASCII.GetString(Lz4.Decompress(block, 0, block.Length, 13)));
        Assert.Throws<InvalidDataException>(() => Lz4.Decompress(block, 0, block.Length, 12));
        Assert.Throws<InvalidDataException>(() => Lz4.Decompress([0x35, (byte)'a', (byte)'b', (byte)'c', 0x09, 0x00], 0, 6, 13));
    }

    [Theory]
    [InlineData("2022.3.62f2", "2022.3.62f1", 1)]
    [InlineData("2022.3.62f1", "2022.3.62b5", 1)]
    [InlineData("6000.0.67f1-DWR", "6000.0.67f1", 0)]
    [InlineData("2022.3.9f1", "2022.3.10f1", -1)]
    [InlineData("2023.1.0a1", "2022.3.70f1", 1)]
    public void Unity_versions_compare_by_number_then_release_type(string a, string b, int expected)
    {
        Assert.True(UnityVersionNumber.TryParse(a, out var left));
        Assert.True(UnityVersionNumber.TryParse(b, out var right));
        Assert.Equal(expected, Math.Sign(left.CompareTo(right)));
    }

    [Fact]
    public void The_embedded_table_knows_recent_Unity_versions()
    {
        var table = PlayerSettingsLayouts.Embedded;
        Assert.NotNull(table.ForTypeHash(Convert.FromHexString(Unity2022Hash)));
        Assert.Contains(Lines(), line => line.StartsWith("V 6000.", StringComparison.Ordinal));
    }

    // PlayerSettings data for the layout that the table assigns to a hash: zeros for skipped bytes, empty arrays and the
    // strings.
    private static byte[] PlayerSettings(string hash, string company, string product, string version)
    {
        // A hash may be listed for several Unity versions, always with the same layout.
        var id = Lines().First(l => l.StartsWith("V ", StringComparison.Ordinal) && l.Split(' ')[2] == hash).Split(' ')[3];
        var tokens = Lines().Single(l => l.StartsWith($"L {id} ", StringComparison.Ordinal)).Split(' ').Skip(2).ToArray();
        var data = new List<byte>();
        for (var i = 0; i < tokens.Length; i++)
        {
            switch (tokens[i])
            {
                case "!":
                    while (data.Count % 4 != 0)
                        data.Add(0);
                    break;
                case "[":
                    data.AddRange(new byte[4]); // no elements
                    for (var depth = 1; depth > 0;)
                        depth += tokens[++i] switch { "[" => 1, "]" => -1, _ => 0 };
                    break;
                case "s:company" or "s:product" or "s:version":
                    var text = Encoding.UTF8.GetBytes(tokens[i] switch { "s:company" => company, "s:product" => product, _ => version });
                    data.AddRange(BitConverter.GetBytes(text.Length));
                    data.AddRange(text);
                    break;
                default:
                    data.AddRange(new byte[int.Parse(tokens[i])]);
                    break;
            }
        }
        data.AddRange(new byte[32]); // the fields after bundleVersion
        return data.ToArray();
    }

    private static (string Version, string Hash) TableEntry(string unityPrefix)
    {
        var parts = Lines().First(l => l.StartsWith("V " + unityPrefix, StringComparison.Ordinal)).Split(' ');
        return (parts[1], parts[2]);
    }

    private static string[] Lines()
    {
        using var stream = typeof(GameMetadata).Assembly.GetManifestResourceStream("Lustral.PlayerSettingsLayouts.txt")!;
        return new StreamReader(stream).ReadToEnd().Split('\n');
    }

    // Serialized file format 22 (Unity 2020.1 and later): a 48-byte header, followed by the metadata and the objects.
    private static byte[] SerializedFile22(string unityVersion, string hash, byte[] playerSettings)
    {
        var metadata = new Writer();
        metadata.CString(unityVersion).Int32(19).Byte(0);
        metadata.Int32(1).Int32(129).Byte(0).Int16(-1).Bytes(Convert.FromHexString(hash));
        metadata.Int32(1).Align(4, 48).Int64(1).Int64(0).Int32(playerSettings.Length).Int32(0);
        var dataOffset = (48 + metadata.Length + 15) / 16 * 16;

        var file = new Writer();
        file.Int32BE(0).Int32BE(0).Int32BE(22).Int32BE(0).Byte(0).Bytes(new byte[3]);
        file.Int32BE(metadata.Length).Int64BE(dataOffset + playerSettings.Length).Int64BE(dataOffset).Int64BE(0);
        file.Bytes(metadata.ToArray()).Pad(dataOffset).Bytes(playerSettings);
        return file.ToArray();
    }

    // Format 17 (Unity 2017.3 to 2019.4): a 20-byte header and 32-bit object offsets.
    private static byte[] SerializedFile17(string unityVersion, string hash, byte[] playerSettings)
    {
        var metadata = new Writer();
        metadata.CString(unityVersion).Int32(19).Byte(0);
        metadata.Int32(1).Int32(129).Byte(0).Int16(-1).Bytes(Convert.FromHexString(hash));
        metadata.Int32(1).Align(4, 20).Int64(1).Int32(0).Int32(playerSettings.Length).Int32(0);
        var dataOffset = (20 + metadata.Length + 15) / 16 * 16;

        var file = new Writer();
        file.Int32BE(metadata.Length).Int32BE(dataOffset + playerSettings.Length).Int32BE(17).Int32BE(dataOffset).Byte(0).Bytes(new byte[3]);
        file.Bytes(metadata.ToArray()).Pad(dataOffset).Bytes(playerSettings);
        return file.ToArray();
    }

    // A UnityFS bundle as written by LZ4-compressed players to data.unity3d (format 8, LZ4 block info, padded), with each
    // file split into 100-byte LZ4 blocks so that reads cross block boundaries.
    private static byte[] Bundle(params (string Name, byte[] Bytes)[] files)
    {
        var all = files.SelectMany(f => f.Bytes).ToArray();
        var blocks = all.Chunk(100).Select(chunk => (Size: chunk.Length, Compressed: Lz4Literals(chunk))).ToList();

        var info = new Writer();
        info.Bytes(new byte[16]).Int32BE(blocks.Count);
        foreach (var block in blocks)
            info.Int32BE(block.Size).Int32BE(block.Compressed.Length).Int16BE(2);
        info.Int32BE(files.Length);
        long offset = 0;
        foreach (var (name, bytes) in files)
        {
            info.Int64BE(offset).Int64BE(bytes.Length).Int32BE(4).CString(name);
            offset += bytes.Length;
        }
        var compressedInfo = Lz4Literals(info.ToArray());

        var bundle = new Writer();
        bundle.CString("UnityFS").Int32BE(8).CString("5.x.x").CString("2022.3.62f2").Int64BE(0)
            .Int32BE(compressedInfo.Length).Int32BE(info.Length).Int32BE(0x200 | 0x40 | 2);
        bundle.Align(16).Bytes(compressedInfo).Align(16);
        foreach (var block in blocks)
            bundle.Bytes(block.Compressed);
        return bundle.ToArray();
    }

    // An LZ4 block containing a single literal run: valid LZ4 that any decoder reads back as the input.
    private static byte[] Lz4Literals(byte[] bytes)
    {
        var block = new List<byte>();
        if (bytes.Length < 15)
            block.Add((byte)(bytes.Length << 4));
        else
        {
            block.Add(0xF0);
            var rest = bytes.Length - 15;
            for (; rest >= 255; rest -= 255)
                block.Add(255);
            block.Add((byte)rest);
        }
        block.AddRange(bytes);
        return block.ToArray();
    }

    private sealed class Writer
    {
        private readonly List<byte> _bytes = [];

        public int Length => _bytes.Count;

        public byte[] ToArray() => _bytes.ToArray();

        public Writer Byte(byte value) => Bytes([value]);

        public Writer Bytes(byte[] bytes)
        {
            _bytes.AddRange(bytes);
            return this;
        }

        public Writer Int16(short value) => Bytes(BitConverter.GetBytes(value));
        public Writer Int32(int value) => Bytes(BitConverter.GetBytes(value));
        public Writer Int64(long value) => Bytes(BitConverter.GetBytes(value));

        public Writer Int16BE(short value)
        {
            var bytes = new byte[2];
            BinaryPrimitives.WriteInt16BigEndian(bytes, value);
            return Bytes(bytes);
        }

        public Writer Int32BE(int value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            return Bytes(bytes);
        }

        public Writer Int64BE(long value)
        {
            var bytes = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(bytes, value);
            return Bytes(bytes);
        }

        public Writer CString(string text) => Bytes([.. Encoding.UTF8.GetBytes(text), 0]);

        // Pads to a multiple of the alignment, relative to the position of this writer's bytes in the file.
        public Writer Align(int alignment, int startsAt = 0)
        {
            while ((startsAt + _bytes.Count) % alignment != 0)
                _bytes.Add(0);
            return this;
        }

        public Writer Pad(int length)
        {
            while (_bytes.Count < length)
                _bytes.Add(0);
            return this;
        }
    }
}
