using System.Buffers.Binary;
using System.Text;
using SaveUltimate.Core.Binary;

namespace SaveUltimate.Core.Archives;

public sealed record ArchiveEntry(string Path, ushort ChunkId, uint Offset, uint CompressedSize, uint UncompressedSize, byte Flags)
{
    public int Compression => Flags & 15;
    public int Crypto => Flags >> 4;
}

public sealed class PamtIndex
{
    public byte[] EncryptionInfo { get; }
    public IReadOnlyDictionary<string, ArchiveEntry> Entries { get; }
    private PamtIndex(byte[] encryptionInfo, Dictionary<string, ArchiveEntry> entries)
        => (EncryptionInfo, Entries) = (encryptionInfo, entries);

    // Format derived from pinned crimson-rs binary/pamt.rs and binary/trie.rs (MIT).
    public static PamtIndex Parse(ReadOnlySpan<byte> data)
    {
        var reader = new ByteReader(data);
        uint checksum = reader.U32();
        int chunkCount = reader.U16();
        if (reader.U16() != 0) throw new InvalidDataException("Invalid PAMT reserved field.");
        reader.U8();
        byte[] encryptionInfo = reader.Take(3).ToArray();
        if (GameChecksum.Calculate(data[reader.Position..]) != checksum)
            throw new InvalidDataException("PAMT checksum mismatch.");
        var chunks = new Dictionary<uint, uint>();
        for (int i = 0; i < chunkCount; i++)
        {
            uint id = reader.U32(); reader.U32();
            if (!chunks.TryAdd(id, reader.U32())) throw new InvalidDataException("Duplicate PAMT chunk.");
        }
        var directories = new Trie(reader.Take(reader.Count()).ToArray());
        var filenames = new Trie(reader.Take(reader.Count()).ToArray());
        int directoryCount = reader.Count();
        if (directoryCount > reader.Remaining / 16) throw new InvalidDataException("Truncated PAMT directory table.");
        var directoryRows = new List<(uint Hash, int Name, uint Start, uint Count)>();
        for (int i = 0; i < directoryCount; i++)
            directoryRows.Add((reader.U32(), unchecked((int)reader.U32()), reader.U32(), reader.U32()));
        int fileCount = reader.Count();
        if (fileCount > reader.Remaining / 20) throw new InvalidDataException("Truncated PAMT file table.");
        var files = new List<(uint Name, uint Offset, uint Packed, uint Expanded, ushort Chunk, byte Flags)>();
        for (int i = 0; i < fileCount; i++)
        {
            var row = (reader.U32(), reader.U32(), reader.U32(), reader.U32(), reader.U16(), reader.U8());
            if (reader.U8() != 0) throw new InvalidDataException("Invalid PAMT file reserved field.");
            files.Add(row);
        }
        if (reader.Remaining != 0) throw new InvalidDataException("Trailing PAMT bytes.");
        var entries = new Dictionary<string, ArchiveEntry>(StringComparer.Ordinal);
        foreach (var dir in directoryRows)
        {
            string path = directories.Resolve(dir.Name);
            if (GameChecksum.Calculate(Encoding.UTF8.GetBytes(path)) != dir.Hash || (ulong)dir.Start + dir.Count > (ulong)files.Count)
                throw new InvalidDataException("Invalid PAMT directory hash or file range.");
            for (uint i = 0; i < dir.Count; i++)
            {
                var file = files[checked((int)(dir.Start + i))];
                if (!chunks.TryGetValue(file.Chunk, out uint size) || (ulong)file.Offset + file.Packed > size)
                    throw new InvalidDataException("PAMT entry lies outside its chunk.");
                string name = filenames.Resolve(checked((int)file.Name));
                string fullPath = path.Length == 0 ? name : path + "/" + name;
                if (!entries.TryAdd(fullPath, new(fullPath, file.Chunk, file.Offset, file.Packed, file.Expanded, file.Flags)))
                    throw new InvalidDataException("Duplicate archive path.");
            }
        }
        return new(encryptionInfo, entries);
    }

    private sealed class Trie(byte[] data)
    {
        private readonly Dictionary<int, string> cache = new() { [-1] = "" };
        public string Resolve(int offset)
        {
            var seen = new HashSet<int>();
            var pending = new Stack<(int Offset, string Text)>();
            while (!cache.ContainsKey(offset))
            {
                if (offset < 0 || offset > data.Length - 5 || !seen.Add(offset))
                    throw new InvalidDataException("Invalid or cyclic PAMT string reference.");
                int parent = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
                int size = data[offset + 4];
                if (size > data.Length - offset - 5) throw new InvalidDataException("Truncated PAMT string.");
                pending.Push((offset, new UTF8Encoding(false, true).GetString(data, offset + 5, size)));
                offset = parent;
            }
            string value = cache[offset];
            while (pending.TryPop(out var part))
            {
                value += part.Text;
                cache.Add(part.Offset, value);
            }
            return value;
        }
    }
}
