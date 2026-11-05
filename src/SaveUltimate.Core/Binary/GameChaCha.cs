using System.Numerics;
using System.Runtime.InteropServices;

namespace SaveUltimate.Core.Binary;

public static class GameChaCha
{
    // ChaCha20 whose 16-byte counter/nonce starts where the game's does, and whose counter carries into the next word,
    // which is why System.Security.Cryptography's fixed layout does not fit.
    public static byte[] Transform(ReadOnlySpan<byte> input, ReadOnlySpan<byte> key, ReadOnlySpan<byte> counterNonce)
    {
        if (key.Length != 32 || counterNonce.Length != 16)
            throw new ArgumentException("ChaCha20 requires a 32-byte key and a 16-byte counter/nonce.");
        byte[] output = new byte[input.Length];
        Span<uint> state = stackalloc uint[16], block = stackalloc uint[16];
        state[0] = 0x61707865; state[1] = 0x3320646E; state[2] = 0x79622D32; state[3] = 0x6B206574;
        MemoryMarshal.Cast<byte, uint>(key).CopyTo(state[4..]);
        MemoryMarshal.Cast<byte, uint>(counterNonce).CopyTo(state[12..]);
        Span<byte> stream = MemoryMarshal.AsBytes(block);
        for (int position = 0; position < input.Length; position += 64)
        {
            state.CopyTo(block);
            for (int round = 0; round < 10; round++)
            {
                Quarter(block, 0, 4, 8, 12); Quarter(block, 1, 5, 9, 13); Quarter(block, 2, 6, 10, 14); Quarter(block, 3, 7, 11, 15);
                Quarter(block, 0, 5, 10, 15); Quarter(block, 1, 6, 11, 12); Quarter(block, 2, 7, 8, 13); Quarter(block, 3, 4, 9, 14);
            }
            for (int i = 0; i < 16; i++) block[i] += state[i];
            int count = Math.Min(64, input.Length - position);
            for (int i = 0; i < count; i++) output[position + i] = (byte)(input[position + i] ^ stream[i]);
            if (++state[12] == 0) state[13]++;
        }
        return output;
    }

    private static void Quarter(Span<uint> x, int a, int b, int c, int d)
    {
        x[a] += x[b]; x[d] = BitOperations.RotateLeft(x[d] ^ x[a], 16);
        x[c] += x[d]; x[b] = BitOperations.RotateLeft(x[b] ^ x[c], 12);
        x[a] += x[b]; x[d] = BitOperations.RotateLeft(x[d] ^ x[a], 8);
        x[c] += x[d]; x[b] = BitOperations.RotateLeft(x[b] ^ x[c], 7);
    }
}
