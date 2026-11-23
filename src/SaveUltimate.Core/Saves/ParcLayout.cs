// SPDX-License-Identifier: MPL-2.0
using SaveUltimate.Core.Binary;

namespace SaveUltimate.Core.Saves;

public sealed record ParcField(string Name, string TypeName, ushort Kind, ushort Size, uint Auxiliary, int Index);
public sealed record ParcType(string Name, IReadOnlyList<ParcField> Fields)
{
    public int MaskSize => (Fields.Count + 7) / 8;
    // Edits that insert rows write every field themselves, so they first check the type is exactly the one they know.
    public bool Is(params (string Name, string Type, int Kind, int Size)[] fields) =>
        Fields.Select(f => (f.Name, f.TypeName, (int)f.Kind, (int)f.Size)).SequenceEqual(fields);
}
public sealed record ParcBlock(int Index, int TypeIndex, string Name, int Offset, int Size);
public sealed record ParcLayout(IReadOnlyList<ParcType> Types, IReadOnlyList<ParcBlock> Blocks, int SchemaEnd)
{
    internal int TypeIndex(string name)
    {
        for (int i = 0; i < Types.Count; i++)
            if (Types[i].Name == name) return i;
        throw new InvalidDataException($"The save has no {name} type.");
    }

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
                definitions.Add(new(reader.String(), reader.String(), reader.U16(), reader.U16(), reader.U32(), j));
            types.Add(new(name, definitions));
            if (i + 1 != count) name = reader.String();
        }
        int schemaEnd = reader.Position;
        return ReadBlocks(blob, types, schemaEnd);
    }

    // The schema header: 14 bytes, two u16, then the u16 type count; the type definitions follow it.
    internal const int TypeCountOffset = 18;

    // A grown schema is the original with types appended: the same bytes but the type count, then new names only.
    internal static bool Extends(ReadOnlySpan<byte> grown, ParcLayout grownLayout, ReadOnlySpan<byte> original, ParcLayout originalLayout)
    {
        int end = originalLayout.SchemaEnd, start = TypeCountOffset + 2;
        if (grownLayout.SchemaEnd < end || !grown[..TypeCountOffset].SequenceEqual(original[..TypeCountOffset]) || !grown[start..end].SequenceEqual(original[start..end]))
            return false;
        var names = originalLayout.Types.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        return grownLayout.Types.Skip(originalLayout.Types.Count).All(t => names.Add(t.Name));
    }

    // One type definition as the schema stores it: name, u16 field count, then each field's name, type name, kind, size and auxiliary.
    internal static byte[] TypeBytes(ParcType type)
    {
        using var s = new MemoryStream();
        void Text(string value) { byte[] b = System.Text.Encoding.UTF8.GetBytes(value); s.Write(BitConverter.GetBytes(b.Length)); s.Write(b); }
        Text(type.Name);
        s.Write(BitConverter.GetBytes(checked((ushort)type.Fields.Count)));
        foreach (var f in type.Fields)
        {
            Text(f.Name); Text(f.TypeName);
            s.Write(BitConverter.GetBytes(f.Kind)); s.Write(BitConverter.GetBytes(f.Size)); s.Write(BitConverter.GetBytes(f.Auxiliary));
        }
        return s.ToArray();
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
