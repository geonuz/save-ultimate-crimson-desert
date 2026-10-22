using System.IO.Compression;
using K4os.Compression.LZ4;

namespace SaveUltimate.Core.Binary;

public static class Compression
{
    public static byte[] Decode(ReadOnlySpan<byte> input, int format, int outputLength)
    {
        if (outputLength < 0) throw new InvalidDataException("Invalid uncompressed length.");
        if (format == 0)
        {
            if (input.Length != outputLength) throw new InvalidDataException("Uncompressed length mismatch.");
            return input.ToArray();
        }
        if (format is not (2 or 3)) throw new NotSupportedException($"Compression {format} is unsupported.");
        byte[] output = new byte[outputLength];
        Decode(input, format, output);
        return output;
    }

    public static void Decode(ReadOnlySpan<byte> input, int format, Span<byte> output)
    {
        if (format == 0)
        {
            if (input.Length != output.Length) throw new InvalidDataException("Uncompressed length mismatch.");
            input.CopyTo(output);
            return;
        }
        if (format is not (2 or 3)) throw new NotSupportedException($"Compression {format} is unsupported.");
        if (format == 2)
        {
            if (LZ4Codec.Decode(input, output) != output.Length)
                throw new InvalidDataException("LZ4 output length mismatch.");
        }
        else
        {
            using var source = new MemoryStream(input.ToArray(), false);
            using var stream = new ZLibStream(source, CompressionMode.Decompress);
            stream.ReadExactly(output);
            if (stream.ReadByte() != -1) throw new InvalidDataException("Zlib output exceeds declared length.");
        }
    }
}
