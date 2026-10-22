using System.Buffers.Binary;
using System.Numerics;

namespace SaveUltimate.Core.Archives;

// Port of crimson-rs crypto/checksum.rs (MIT, Tommy Tran); wrapping arithmetic is part of the format.
public static class GameChecksum
{
    public static uint Calculate(ReadOnlySpan<byte> data)
    {
        unchecked
        {
            uint a = (uint)data.Length + 0xDEBA1DCD, b = a, c = a;
            while (data.Length > 12)
            {
                a += BinaryPrimitives.ReadUInt32LittleEndian(data);
                b += BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
                c += BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
                a = (a - c) ^ BitOperations.RotateLeft(c, 4); c += b;
                b = (b - a) ^ BitOperations.RotateLeft(a, 6); a += c;
                c = (c - b) ^ BitOperations.RotateLeft(b, 8); b += a;
                a = (a - c) ^ BitOperations.RotateLeft(c, 16); c += b;
                b = (b - a) ^ BitOperations.RotateLeft(a, 19); a += c;
                c = (c - b) ^ BitOperations.RotateLeft(b, 4); b += a;
                data = data[12..];
            }
            if (data.IsEmpty) return c;
            Span<byte> tail = stackalloc byte[12];
            tail.Clear();
            data.CopyTo(tail);
            a += BinaryPrimitives.ReadUInt32LittleEndian(tail);
            b += BinaryPrimitives.ReadUInt32LittleEndian(tail[4..]);
            c += BinaryPrimitives.ReadUInt32LittleEndian(tail[8..]);
            c = (c ^ b) - BitOperations.RotateLeft(b, 14);
            a = (a ^ c) - BitOperations.RotateLeft(c, 11);
            b = (b ^ a) - BitOperations.RotateLeft(a, 25);
            c = (c ^ b) - BitOperations.RotateLeft(b, 16);
            a = (a ^ c) - BitOperations.RotateLeft(c, 4);
            b = (b ^ a) - BitOperations.RotateLeft(a, 14);
            return (c ^ b) - BitOperations.RotateLeft(b, 24);
        }
    }
}
