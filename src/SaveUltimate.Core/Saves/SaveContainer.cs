// SPDX-License-Identifier: MPL-2.0
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SaveUltimate.Core.Binary;

namespace SaveUltimate.Core.Saves;

public sealed record SaveContainer(ushort Version, byte[] Header, byte[] Blob)
{
    public static SaveContainer Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 144 || !bytes[..4].SequenceEqual("SAVE"u8))
            throw new InvalidDataException("Not a complete SAVE container.");
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        var key = DeriveKey(version);
        int expanded = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x12..]));
        int packed = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x16..]));
        if (expanded == 0 || packed == 0 || packed > bytes.Length - 128)
            throw new InvalidDataException("Invalid SAVE payload length.");
        var compressed = GameChaCha.Transform(bytes.Slice(128, packed), key, bytes.Slice(0x1a, 16));
        var digest = HMACSHA256.HashData(key, compressed);
        if (!CryptographicOperations.FixedTimeEquals(digest, bytes.Slice(0x2a, 32)))
            throw new InvalidDataException("SAVE HMAC verification failed.");
        return new(version, bytes[..128].ToArray(), Compression.Decode(compressed, 2, expanded));
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
