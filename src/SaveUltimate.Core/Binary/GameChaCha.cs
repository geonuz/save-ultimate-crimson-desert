using System.Runtime.InteropServices;

namespace SaveUltimate.Core.Binary;

public static class GameChaCha
{
    public static byte[] Transform(ReadOnlySpan<byte> input, ReadOnlySpan<byte> key, ReadOnlySpan<byte> counterNonce)
    {
        if (key.Length != 32 || counterNonce.Length != 16)
            throw new ArgumentException("ChaCha20 requires a 32-byte key and a 16-byte counter/nonce.");
        byte[] output = new byte[input.Length];
        if (input.IsEmpty) return output;
        if (parc_chacha20(key.ToArray(), counterNonce.ToArray(), input.ToArray(), checked((uint)input.Length), output) != 0)
            throw new InvalidDataException("Game ChaCha20 transformation failed.");
        return output;
    }

    // Reuse the existing save writer: its game-specific initial counter is not exposed by Bouncy Castle's public API.
    [DllImport("parc_validation.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int parc_chacha20(byte[] key, byte[] nonce, byte[] input, uint size, [Out] byte[] output);
}
