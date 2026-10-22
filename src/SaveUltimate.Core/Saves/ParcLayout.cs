// SPDX-License-Identifier: MPL-2.0
using SaveUltimate.Core.Binary;

namespace SaveUltimate.Core.Saves;

public sealed record ParcField(string Name, string TypeName, ushort Kind, ushort Size, uint Auxiliary);
public sealed record ParcType(string Name, IReadOnlyList<ParcField> Fields);
public sealed record ParcBlock(int Index, int TypeIndex, string Name, int Offset, int Size);
public sealed record ParcLayout(IReadOnlyList<ParcType> Types, IReadOnlyList<ParcBlock> Blocks, int SchemaEnd)
{
    public static ParcLayout Parse(ReadOnlySpan<byte> blob)
    {
        var reader = new ByteReader(blob);
        reader.Take(14);
        reader.U16();
        reader.U16();
        int count = reader.U16();
        if (count == 0) throw new InvalidDataException("Empty PARC schema.");
        string name = reader.String();
        var types = new List<ParcType>();
        for (int i = 0; i < count; i++)
        {
            int fields = reader.U16();
            var definitions = new List<ParcField>();
            for (int j = 0; j < fields; j++)
                definitions.Add(new(reader.String(), reader.String(), reader.U16(), reader.U16(), reader.U32()));
            types.Add(new(name, definitions));
            if (i + 1 != count) name = reader.String();
        }
        int schemaEnd = reader.Position;
        return ReadBlocks(blob, types, schemaEnd);
    }

    internal ParcLayout Reparse(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> previous)
        => blob.Length >= SchemaEnd && previous[..SchemaEnd].SequenceEqual(blob[..SchemaEnd])
            ? ReadBlocks(blob, Types, SchemaEnd) : Parse(blob);

    private static ParcLayout ReadBlocks(ReadOnlySpan<byte> blob, IReadOnlyList<ParcType> types, int schemaEnd)
    {
        var reader = new ByteReader(blob);
        reader.Take(schemaEnd);
        if (reader.U32() != 0) throw new InvalidDataException("Invalid PARC TOC prefix.");
        int blockCount = reader.Count();
        if (reader.Count() != blob.Length || blockCount > reader.Remaining / 20)
            throw new InvalidDataException("Invalid PARC stream or table length.");
        int end = checked(reader.Position + blockCount * 20);
        var blocks = new List<ParcBlock>();
        for (int i = 0; i < blockCount; i++)
        {
            int type = reader.Count();
            reader.U32(); reader.U32();
            int offset = reader.Count(), size = reader.Count();
            if (type >= types.Count || offset != end || size <= 0 || size > blob.Length - offset)
                throw new InvalidDataException("Invalid PARC block bounds or type.");
            blocks.Add(new(i, type, types[type].Name, offset, size));
            end = checked(offset + size);
        }
        if (end != blob.Length) throw new InvalidDataException("PARC table does not cover the stream.");
        return new(types, blocks, schemaEnd);
    }
}
