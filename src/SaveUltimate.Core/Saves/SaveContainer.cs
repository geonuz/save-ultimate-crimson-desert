// SPDX-License-Identifier: MPL-2.0
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using K4os.Compression.LZ4;
using SaveUltimate.Core.Binary;

namespace SaveUltimate.Core.Saves;

public sealed record SaveContainer(ushort Version, byte[] Header, byte[] Blob)
{
    // A file with its encryption removed, before anything vouches for its contents.
    internal sealed record Opened(ushort Version, byte[] Key, int Expanded, byte[] Packed)
    {
        public bool SignedBy(ReadOnlySpan<byte> file) => CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(Key, Packed), file.Slice(0x2a, 32));
        public byte[] Expand() => Compression.Decode(Packed, 2, Expanded);
    }

    internal static Opened Open(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 144 || !bytes[..4].SequenceEqual("SAVE"u8))
            throw new InvalidDataException("Not a complete SAVE container.");
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        var key = DeriveKey(version);
        int expanded = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x12..]));
        int packed = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x16..]));
        if (expanded == 0 || packed == 0 || packed > bytes.Length - 128)
            throw new InvalidDataException("Invalid SAVE payload length.");
        return new(version, key, expanded, GameChaCha.Transform(bytes.Slice(128, packed), key, bytes.Slice(0x1a, 16)));
    }

    public static SaveContainer Decode(ReadOnlySpan<byte> bytes)
    {
        var opened = Open(bytes);
        if (!opened.SignedBy(bytes)) throw new InvalidDataException(BadSignature);
        return new(opened.Version, bytes[..128].ToArray(), opened.Expand());
    }
    internal const string BadSignature = "SAVE HMAC verification failed.";

    // Decodes a file and checks every record in it.
    public static SaveContainer Check(string path)
    {
        var save = Decode(File.ReadAllBytes(path));
        ParcReader.Validate(save.Blob, ParcLayout.Parse(save.Blob));
        return save;
    }

    internal static byte[] Encode(ReadOnlySpan<byte> source, byte[] blob)
    {
        // Only version 2 was ever checked in game; the first bytes after the magic stay as the game wrote them.
        if (BinaryPrimitives.ReadUInt16LittleEndian(source[4..]) != 2) throw new InvalidDataException("Only SAVE version 2 can be written.");
        byte[] key = DeriveKey(2);
        byte[] packed = new byte[LZ4Codec.MaximumOutputSize(blob.Length)];
        int size = LZ4Codec.Encode(blob, packed, LZ4Level.L09_HC);
        if (size <= 0) throw new InvalidDataException("LZ4 compression failed.");
        byte[] file = new byte[128 + size];
        source[..0x12].CopyTo(file);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(6), 0x80);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(0x12), blob.Length);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(0x16), size);
        var nonce = file.AsSpan(0x1a, 16);
        RandomNumberGenerator.Fill(nonce);
        HMACSHA256.HashData(key, packed.AsSpan(0, size), file.AsSpan(0x2a, 32));
        GameChaCha.Transform(packed.AsSpan(0, size), key, nonce).CopyTo(file, 128);
        return file;
    }

    // Refuses to create the file unless what reached the disk reads back as exactly these records. Existing files are never replaced.
    internal static void Write(ReadOnlySpan<byte> source, byte[] blob, string destination)
    {
        var original = Decode(source);
        int schema = ParcLayout.Parse(original.Blob).SchemaEnd;
        var layout = ParcLayout.Parse(blob);
        ParcReader.Validate(blob, layout);
        if (!blob.AsSpan(0, layout.SchemaEnd).SequenceEqual(original.Blob.AsSpan(0, schema)))
            throw new InvalidDataException("Edited save schema differs from the loaded source schema.");
        Store(Encode(source, blob), blob, destination);
    }

    // Signs a file again as it is, once every record in it reads. Writes a new file, like Write.
    internal static void Reseal(ReadOnlySpan<byte> source, string destination)
    {
        byte[] blob = Open(source).Expand();
        ParcReader.Validate(blob, ParcLayout.Parse(blob));
        Store(Encode(source, blob), blob, destination);
    }

    private static void Store(byte[] file, byte[] blob, string destination)
    {
        destination = Path.GetFullPath(destination);
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".saveultimate-{Guid.NewGuid():N}.save");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(file);
                stream.Flush(true);
            }
            if (!Decode(File.ReadAllBytes(temporary)).Blob.AsSpan().SequenceEqual(blob))
                throw new InvalidDataException("Written save payload does not match the edited data.");
            File.Move(temporary, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static byte[] DeriveKey(ushort version)
    {
        var prefix = version switch
        {
            1 => "^Qgbrm/.#@`zsr]\\@rvfal#\"",
            2 => "^Pearl--#Abyss__@!!",
            _ => throw new InvalidDataException($"Unsupported SAVE version {version}.")
        };
        var baseKey = Convert.FromHexString("C41B8E730DF259A637CC04E9B12F9668DA107A853E61F9224DB80AD75C13EF90");
        var material = Encoding.ASCII.GetBytes(prefix + "PRIVATE_HMAC_SECRET_CHECK");
        byte[] key = new byte[32];
        for (int i = 0; i < 31; i++) key[i] = (byte)(baseKey[i] ^ material[i]);
        return key;
    }
}
