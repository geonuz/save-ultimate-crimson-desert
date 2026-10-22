using System.Buffers.Binary;
using System.Text;
using SaveUltimate.Core.Binary;

namespace SaveUltimate.Core.Archives;

public sealed class GameArchive
{
    private readonly string directory;
    public PamtIndex Index { get; }
    public GameArchive(string groupDirectory)
    {
        directory = Path.GetFullPath(groupDirectory);
        Index = PamtIndex.Parse(File.ReadAllBytes(Path.Combine(directory, "0.pamt")));
    }

    public byte[] Extract(string virtualPath)
    {
        if (!Index.Entries.TryGetValue(virtualPath, out var entry))
            throw new FileNotFoundException("Archive entry not found.", virtualPath);
        using var stream = File.OpenRead(Path.Combine(directory, $"{entry.ChunkId}.paz"));
        if ((ulong)entry.Offset + entry.CompressedSize > (ulong)stream.Length)
            throw new InvalidDataException("Archive entry exceeds physical chunk size.");
        stream.Position = entry.Offset;
        byte[] data = new byte[checked((int)entry.CompressedSize)];
        stream.ReadExactly(data);
        if (entry.Crypto == 3) data = Decrypt(data, Index.EncryptionInfo, virtualPath);
        else if (entry.Crypto != 0) throw new NotSupportedException($"Archive crypto {entry.Crypto} is unsupported.");
        if (entry.Compression == 0)
        {
            if (data.Length != entry.UncompressedSize) throw new InvalidDataException("Uncompressed length mismatch.");
            return data;
        }
        if (entry.Compression == 1)
        {
            if (entry.Crypto != 0) throw new NotSupportedException("Encrypted partial textures are unsupported.");
            return RestorePartialDds(data, checked((int)entry.UncompressedSize));
        }
        return Compression.Decode(data, entry.Compression, checked((int)entry.UncompressedSize));
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> data, ReadOnlySpan<byte> encryptionInfo, string path)
    {
        if (encryptionInfo.Length != 3) throw new ArgumentException("Archive encryption info must have three bytes.");
        string name = path[(path.LastIndexOf('/') + 1)..];
        uint checksum = GameChecksum.Calculate(Encoding.UTF8.GetBytes(name));
        Span<byte> nonce = stackalloc byte[16];
        for (int i = 0; i < 4; i++) BinaryPrimitives.WriteUInt32LittleEndian(nonce[(i * 4)..], checksum);
        byte[] key = Convert.FromHexString("0E0F0C0D040506070203000108090A0B000102030405060708090A0B0C0D0E0F");
        byte salt = (byte)(encryptionInfo[0] ^ encryptionInfo[1] ^ encryptionInfo[2]);
        for (int i = 0; i < key.Length; i++) key[i] ^= (byte)(nonce[i % 16] ^ salt);
        return GameChaCha.Transform(data, key, nonce);
    }

    public static byte[] RestorePartialDds(ReadOnlySpan<byte> data, int expanded)
    {
        if (data.Length < 128 || !data[..4].SequenceEqual("DDS "u8))
            throw new InvalidDataException("Partial texture is not a complete DDS header.");
        int headerSize = data.Slice(84, 4).SequenceEqual("DX10"u8) ? 148 : 128;
        if (data.Length < headerSize) throw new InvalidDataException("Truncated DX10 header.");
        if (data.Length == expanded) return data.ToArray();
        int packed = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[32..]));
        int unpacked = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[36..]));
        if (packed != data.Length - headerSize || unpacked != expanded - headerSize)
            throw new InvalidDataException("Partial DDS length mismatch.");
        byte[] output = new byte[expanded];
        data[..headerSize].CopyTo(output);
        Compression.Decode(data[headerSize..], packed == unpacked ? 0 : 2, output.AsSpan(headerSize));
        return output;
    }
}
