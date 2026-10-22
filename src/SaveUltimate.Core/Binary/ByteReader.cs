using System.Buffers.Binary;
using System.Text;

namespace SaveUltimate.Core.Binary;

internal ref struct ByteReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> data = data;
    public int Position { get; private set; }
    public int Remaining => data.Length - Position;
    public ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining) throw new InvalidDataException("Truncated binary record.");
        var value = data.Slice(Position, count);
        Position += count;
        return value;
    }
    public byte U8() => Take(1)[0];
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public int Count() => checked((int)U32());
    public string String() => new UTF8Encoding(false, true).GetString(Take(Count())).TrimEnd('\0');
}
