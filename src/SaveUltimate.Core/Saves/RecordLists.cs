using System.Runtime.InteropServices;
using System.Buffers.Binary;

namespace SaveUltimate.Core.Saves;

internal sealed record ScalarValue(string Type, int Size, ulong Value)
{
    internal static ScalarValue Remove { get; } = new("", 0, 0);
}
internal sealed record ListEdit(SaveField Field, string Type, string Key, Dictionary<uint, Dictionary<string, ScalarValue>> Changes, bool CloneLast = false);

internal static class RecordLists
{
    internal static IEnumerable<(int Start, int End, Func<int, byte[]> Encode)> Regions(byte[] raw, ParcLayout layout, IEnumerable<ListEdit> edits)
    {
        foreach (var edit in edits)
        {
            int index = layout.Types.Select((t, i) => (t, i)).Single(p => p.t.Name == edit.Type).i;
            var type = layout.Types[index];
            if (!Encode(raw, edit with { Changes = [] }, type, index, edit.Field.StartOffset)
                .AsSpan().SequenceEqual(raw.AsSpan(edit.Field.StartOffset, edit.Field.EndOffset - edit.Field.StartOffset)))
                throw new InvalidDataException("Record list does not round-trip.");
            yield return (edit.Field.StartOffset, edit.Field.EndOffset, start => Encode(raw, edit, type, index, start));
        }
    }

    internal static byte[] RewriteRegions(byte[] raw, Func<int, byte[]> bufferForEdit, IEnumerable<(int Start, int End, Func<int, byte[]> Encode)> regions)
    {
        using var packet = new MemoryStream();
        using var writer = new BinaryWriter(packet);
        int delta = 0, previous = 0;
        foreach (var (start, end, encode) in regions.OrderBy(r => r.Start))
        {
            if (start < previous || end < start || end > raw.Length) throw new InvalidDataException("Invalid replacement boundaries.");
            byte[] value = encode(checked(start + delta));
            writer.Write((uint)start); writer.Write((uint)end); writer.Write((uint)value.Length); writer.Write(value);
            delta = checked(delta + value.Length - (end - start)); previous = end;
        }
        return packet.Length == 0 ? raw : Relocate(raw, packet.ToArray(), bufferForEdit);
    }
    internal static byte[] Rewrite(byte[] raw, ParcLayout layout, Func<int, byte[]> bufferForEdit, params ListEdit[] edits)
    {
        using var packet = new MemoryStream();
        using var writer = new BinaryWriter(packet);
        int delta = 0, end = 0;
        var patches = new List<(SaveField Field, ScalarValue Value)>();
        bool existing = true;
        foreach (var edit in edits.OrderBy(e => e.Field.StartOffset))
        {
            var field = edit.Field;
            if (!field.Present || field.StartOffset < end || field.EndOffset > raw.Length)
                throw new InvalidDataException("Unsupported overlapping record lists.");
            end = field.EndOffset;
            int typeIndex = layout.Types.Select((t, i) => (t, i)).Single(p => p.t.Name == edit.Type).i;
            byte[] original = Encode(raw, edit with { Changes = [] }, layout.Types[typeIndex], typeIndex, field.StartOffset);
            if (!original.AsSpan().SequenceEqual(raw.AsSpan(field.StartOffset, field.EndOffset - field.StartOffset)))
                throw new InvalidDataException("Record list does not round-trip.");
            byte[] encoded = Encode(raw, edit, layout.Types[typeIndex], typeIndex, field.StartOffset + delta);
            writer.Write((uint)field.StartOffset); writer.Write((uint)field.EndOffset); writer.Write((uint)encoded.Length); writer.Write(encoded);
            delta += encoded.Length - (field.EndOffset - field.StartOffset);
            var records = field.ListElements.ToDictionary(r => Identity(raw, r.ChildFields.Named(edit.Key)), r => r);
            foreach (var (key, values) in edit.Changes)
                foreach (var (name, value) in values)
                {
                    if (!records.TryGetValue(key, out var record)) { existing = false; continue; }
                    var target = record.ChildFields.Named(name);
                    if (value == ScalarValue.Remove) { if (target.Present) existing = false; continue; }
                    if (!target.Present) { existing = false; continue; }
                    target.Scalar(raw, value.Type, value.Size);
                    patches.Add((target, value));
                }
        }
        if (!existing) return Relocate(raw, packet.ToArray(), bufferForEdit);
        byte[] result = bufferForEdit(raw.Length);
        raw.CopyTo(result, 0);
        foreach (var (field, value) in patches) Integer(value).CopyTo(result, field.StartOffset);
        return result;
    }

    private static uint Identity(byte[] raw, SaveField field)
    {
        if (!field.Present && field.TypeName != "SkillPointOwnerType") throw new InvalidDataException("Missing record identity.");
        return field.Present ? checked((uint)field.Scalar(raw, field.TypeName, field.TypeName == "SkillPointOwnerType" ? 1 : 4)) : 0;
    }

    private static byte[] Integer(ScalarValue value)
    {
        byte[] bytes = new byte[value.Size is 1 or 2 or 4 or 8 ? value.Size : throw new InvalidDataException("Unsupported record scalar.")];
        switch (value.Size)
        {
            case 1: bytes[0] = checked((byte)value.Value); break;
            case 2: BinaryPrimitives.WriteUInt16LittleEndian(bytes, checked((ushort)value.Value)); break;
            case 4: BinaryPrimitives.WriteUInt32LittleEndian(bytes, checked((uint)value.Value)); break;
            case 8: BinaryPrimitives.WriteUInt64LittleEndian(bytes, value.Value); break;
        }
        return bytes;
    }

    private static byte[] Encode(byte[] raw, ListEdit edit, ParcType type, int typeIndex, int start)
    {
        var field = edit.Field;
        if (field.ListHeaderSize is not 18 and not 19 || !string.IsNullOrEmpty(field.Note))
            throw new InvalidDataException("Unsupported record list header.");
        byte[] header = raw.AsSpan(field.StartOffset, field.ListHeaderSize).ToArray();
        int countStart = header.Length == 18 ? 1 : 2, countSize = header.Length == 18 ? 3 : 4;
        uint count = 0;
        for (int i = 0; i < countSize; i++) count |= (uint)header[countStart + i] << (8 * i);
        if (header[0] != (header.Length == 18 ? 0 : 1) || header.Length == 19 && header[1] != 0 ||
            header.Skip(countStart + countSize).Any(b => b != 0) || count != field.ListCount)
            throw new InvalidDataException("Unsupported record list encoding.");
        var records = field.Elements(type.Name).ToDictionary(r => Identity(raw, r.ChildFields.Named(edit.Key)), r => (SaveField?)r);
        foreach (uint key in edit.Changes.Keys.Except(records.Keys).Order())
            records.Add(key, edit.CloneLast ? field.ListElements.LastOrDefault() ?? throw new InvalidDataException("Missing insertion template.") : null);
        if (records.Count >= 0xffffff) throw new InvalidDataException("Too many records.");
        for (int i = 0; i < countSize; i++) header[countStart + i] = (byte)(records.Count >> (8 * i));
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write(header);
        foreach (var (key, record) in records)
        {
            var values = new Dictionary<string, ReadOnlyMemory<byte>>();
            ReadOnlyMemory<byte> suffix = default;
            int maskSize = (type.Fields.Count + 7) / 8;
            if (record is not null)
            {
                int offset = record.StartOffset;
                if (record.ChildUndecodedRanges.Length != 0 || record.ChildMaskByteCount != maskSize ||
                    record.ChildPayloadOffset != offset + 17 + maskSize || record.EndOffset > raw.Length ||
                    BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(offset)) != maskSize ||
                    BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(offset + 2 + maskSize)) != typeIndex ||
                    raw[offset + 4 + maskSize] != 0 ||
                    raw.AsSpan(offset + 5 + maskSize, 8).ContainsAnyExcept((byte)255) ||
                    BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offset + 13 + maskSize)) != record.ChildPayloadOffset ||
                    BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(record.ChildPayloadOffset)) != 0 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(record.EndOffset - 4)) != record.EndOffset - 4 - record.ChildPayloadOffset)
                    throw new InvalidDataException("Unsupported inline record header.");
                int cursor = record.ChildPayloadOffset + 4;
                foreach (var f in record.ChildFields.Where(f => f.Present))
                {
                    if (f.StartOffset != cursor || f.EndOffset < cursor || f.EndOffset > record.EndOffset - 4 ||
                        f.ChildFields.Length != 0 || f.ListElements.Length != 0 || f.MetaKind is not 0 and not 2 and not 3)
                        throw new InvalidDataException("Unsupported inline record fields.");
                    values.Add(f.Name, raw.AsMemory(f.StartOffset, f.EndOffset - f.StartOffset));
                    cursor = f.EndOffset;
                }
                suffix = raw.AsMemory(cursor, record.EndOffset - 4 - cursor);
            }
            if (edit.Changes.TryGetValue(key, out var changes))
                foreach (var (name, value) in changes)
                {
                    var f = type.Fields.Single(f => f.Name == name);
                    if (value == ScalarValue.Remove) { values.Remove(name); continue; }
                    if (f.TypeName != value.Type || f.Kind is not 0 and not 2 || f.Size != value.Size)
                        throw new InvalidDataException("Record scalar schema changed.");
                    values[name] = Integer(value);
                }
            byte[] mask = new byte[maskSize];
            using var payload = new MemoryStream();
            payload.Write(new byte[4]);
            for (int i = 0; i < type.Fields.Count; i++)
                if (values.TryGetValue(type.Fields[i].Name, out var value))
                {
                    mask[i / 8] |= (byte)(1 << (i % 8));
                    payload.Write(value.Span);
                }
            payload.Write(suffix.Span);
            uint position = checked((uint)(start + output.Length + 17 + maskSize));
            writer.Write(checked((ushort)maskSize)); writer.Write(mask); writer.Write(checked((ushort)typeIndex));
            writer.Write((byte)0); writer.Write(ulong.MaxValue); writer.Write(position);
            payload.WriteTo(output); writer.Write(checked((uint)payload.Length));
        }
        return output.ToArray();
    }
    internal static byte[] Relocate(byte[] raw, byte[] input, Func<int, byte[]> bufferForEdit)
    {
        if (parc_index_version() != 4) throw new InvalidDataException("Unsupported relocation backend.");
        int code = parc_replace_regions(raw, checked((uint)raw.Length), input, checked((uint)input.Length), out var output, out uint length);
        try
        {
            if (output == IntPtr.Zero) throw new InvalidDataException($"Record relocation failed ({code}).");
            if (code != 0) throw new InvalidDataException(Marshal.PtrToStringUTF8(output, checked((int)length)));
            byte[] result = bufferForEdit(checked((int)length));
            Marshal.Copy(output, result, 0, result.Length);
            return result;
        }
        finally { if (output != IntPtr.Zero) parc_index_free(output); }
    }

    [DllImport("parc_index.dll", CallingConvention = CallingConvention.Cdecl)] private static extern uint parc_index_version();
    [DllImport("parc_index.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int parc_replace_regions(byte[] raw, uint size, byte[] packet, uint packetSize, out IntPtr output, out uint length);
    [DllImport("parc_index.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void parc_index_free(IntPtr output);
}
