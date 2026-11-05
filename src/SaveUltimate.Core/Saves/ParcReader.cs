using System.Buffers.Binary;

namespace SaveUltimate.Core.Saves;

// Reads records by the one layout the game writes, driven only by the save's own schema, so no type needs a rule of its own:
//
//   kind                absent   present
//   0, 2  fixed         -        value
//   1     inline        -        u32 count, elements
//   3     array         01       00, u32 count, elements
//   4     object        -        object
//   5     object        -        01, object
//   6     list          01       00, u32 count, 13 zero bytes, objects
//   7     indexed list  01       00, u32 count, 00, u32 next id, 8 zero bytes, objects
//
// A block is u16 mask size, mask, u32 0, its fields. An object is u16 mask size, mask, u16 type, 00, 8-byte id (all FF outside
// indexed lists), u32 address of its payload, then the payload: u32 0, its fields, u32 payload size.
// Every byte is accounted for, so a save that passes has no bytes this reader did not understand.
internal sealed class ParcReader
{
    private const int MaximumDepth = 64;
    private const int ListHeaderSize = 18;
    private static readonly byte[] EmptyId = [255, 255, 255, 255, 255, 255, 255, 255];
    private readonly byte[] raw;
    private readonly ParcLayout layout;
    private readonly bool build;
    private readonly List<int>? pointers;

    private ParcReader(byte[] raw, ParcLayout layout, bool build, List<int>? pointers)
        => (this.raw, this.layout, this.build, this.pointers) = (raw, layout, build, pointers);

    internal static SaveObject Read(byte[] raw, ParcLayout layout, ParcBlock block)
    {
        var reader = new ParcReader(raw, layout, build: true, pointers: null);
        return new() { ClassName = block.Name, Fields = reader.Block(block) };
    }

    // Checks every block without keeping the records. A pointer no record accounts for means this reader missed something.
    internal static void Validate(byte[] raw, ParcLayout layout)
    {
        var pointers = new List<int>();
        var reader = new ParcReader(raw, layout, build: false, pointers);
        foreach (var block in layout.Blocks) reader.Block(block);
        // The walk runs in file order, so the list is sorted.
        var span = raw.AsSpan();
        for (int at = layout.SchemaEnd; ; at++)
        {
            int found = span[at..].IndexOf(EmptyId);
            if (found < 0) return;
            at += found;
            int address = at + EmptyId.Length;
            if (address <= raw.Length - 4 && reader.U32(address) == (uint)address + 4 && pointers.BinarySearch(address) < 0)
                throw new InvalidDataException($"Record pointer at {address} belongs to no record.");
        }
    }

    private static int MaskSize(ParcType type) => (type.Fields.Count + 7) / 8;
    private ushort U16(int position) => BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(position));
    private uint U32(int position) => BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(position));
    private static void Need(int position, long size, int limit, string what)
    {
        if (size > limit - position) throw new InvalidDataException($"{what} at {position} overruns its record.");
    }

    private SaveField[] Block(ParcBlock block)
    {
        var type = layout.Types[block.TypeIndex];
        int end = block.Offset + block.Size, mask = block.Offset + 2, maskSize = MaskSize(type);
        Need(block.Offset, 2L + maskSize + 4, end, type.Name);
        if (U16(block.Offset) != maskSize || U32(mask + maskSize) != 0) throw new InvalidDataException($"Invalid {type.Name} block header at {block.Offset}.");
        int stop = Fields(type, mask, mask + maskSize + 4, end, 0, out var fields);
        if (stop != end) throw new InvalidDataException($"{type.Name} block ends {end - stop} bytes after its last field.");
        return fields;
    }

    private ParcType Header(int start, int limit, bool indexed, out int mask, out int payload)
    {
        Need(start, 2, limit, "Object");
        int maskSize = U16(start), header = start + 2 + maskSize;
        mask = start + 2;
        payload = header + 15;
        Need(mask, maskSize + 19L, limit, "Object");
        int typeIndex = U16(header);
        if (typeIndex >= layout.Types.Count) throw new InvalidDataException($"Object at {start} has an unknown type.");
        var type = layout.Types[typeIndex];
        if (maskSize != MaskSize(type) || raw[header + 2] != 0 || (!indexed && !raw.AsSpan(header + 3, 8).SequenceEqual(EmptyId)) ||
            U32(payload - 4) != (uint)payload || U32(payload) != 0)
            throw new InvalidDataException($"Invalid {type.Name} header at {start}.");
        return type;
    }

    private static void Fill(SaveField holder, ParcType type, int payload, uint size, SaveField[] fields)
    {
        holder.ChildTypeName = type.Name;
        holder.ChildMaskByteCount = MaskSize(type);
        holder.ChildPayloadOffset = payload;
        holder.ChildSizeU32 = (int)size;
        holder.ChildFields = fields;
    }

    private static SaveField New(ParcField definition, int index, bool present, int cursor) => new()
    {
        Name = definition.Name, TypeName = definition.TypeName, MetaKind = definition.Kind, MetaSize = definition.Size,
        FieldIndex = index, Present = present, StartOffset = cursor, EndOffset = cursor
    };

    private int Object(int start, int limit, bool indexed, int depth, SaveField? holder)
    {
        if (depth >= MaximumDepth) throw new InvalidDataException("Record nesting is too deep.");
        var type = Header(start, limit, indexed, out int mask, out int payload);
        pointers?.Add(payload - 4);
        int end = Fields(type, mask, payload + 4, limit, depth, out var fields);
        Need(end, 4, limit, type.Name);
        uint size = U32(end);
        if (size != (uint)(end - payload)) throw new InvalidDataException($"{type.Name} at {start} does not end after its last field.");
        if (holder is not null) Fill(holder, type, payload, size, fields);
        return end + 4;
    }

    // Released versions inserted mission rows that hold only their present scalars, without the markers of absent arrays, and only
    // the payload size still ends them. Read keeps what they hold so Save Doctor can repair them; Validate refuses them.
    private int MarkerlessRow(int start, int limit, SaveField row)
    {
        var type = Header(start, limit, false, out int mask, out int payload);
        int end = payload + 4;
        for (Need(end, 4, limit, type.Name); U32(end) != (uint)(end - payload); Need(++end, 4, limit, type.Name)) { }
        var fields = new SaveField[type.Fields.Count];
        int cursor = payload + 4;
        bool lost = false;
        for (int i = 0; i < fields.Length; i++)
        {
            var definition = type.Fields[i];
            bool present = (raw[mask + i / 8] & (1 << (i % 8))) != 0;
            fields[i] = New(definition, i, present, cursor);
            if (!present) continue;
            // A field with no room or no fixed size leaves the position of everything after it unknown.
            lost |= definition.Kind is not 0 and not 2 || definition.Size == 0 || definition.Size > end - cursor;
            if (!lost) fields[i].EndOffset = cursor += definition.Size;
        }
        Fill(row, type, payload, (uint)(end - payload), fields);
        return end + 4;
    }

    private int Fields(ParcType type, int mask, int cursor, int limit, int depth, out SaveField[] built)
    {
        var definitions = type.Fields;
        built = build ? new SaveField[definitions.Count] : [];
        for (int i = 0; i < definitions.Count; i++)
        {
            var definition = definitions[i];
            bool present = (raw[mask + i / 8] & (1 << (i % 8))) != 0;
            var field = build ? New(definition, i, present, cursor) : null;
            string What() => $"{type.Name}.{definition.Name}";
            int Marker(byte expected)
            {
                Need(cursor, 1, limit, What());
                if (raw[cursor] != expected) throw new InvalidDataException($"{What()} at {cursor} lacks its {expected:X2} marker.");
                return cursor + 1;
            }
            if (!present)
            {
                if (definition.Kind is 3 or 6 or 7) cursor = Marker(1);
            }
            else switch (definition.Kind)
            {
                case 0 or 2:
                    if (definition.Size == 0) throw new InvalidDataException($"{What()} has no size.");
                    Need(cursor, definition.Size, limit, What());
                    cursor += definition.Size;
                    break;
                case 1:
                    Need(cursor, 4, limit, What());
                    long inline = 4L + (long)U32(cursor) * definition.Size;
                    Need(cursor, inline, limit, What());
                    cursor += (int)inline;
                    break;
                case 3:
                    Need(cursor, 5, limit, What());
                    Marker(0);
                    long array = 5L + (long)U32(cursor + 1) * definition.Size;
                    Need(cursor, array, limit, What());
                    cursor += (int)array;
                    break;
                case 4:
                    cursor = Object(cursor, limit, false, depth + 1, field);
                    break;
                case 5:
                    cursor = Object(Marker(1), limit, false, depth + 1, field);
                    break;
                case 6 or 7:
                    cursor = List(cursor, limit, definition.Kind == 7, depth, field, What());
                    break;
                default:
                    throw new InvalidDataException($"{What()} has unknown kind {definition.Kind}.");
            }
            if (field is not null)
            {
                field.EndOffset = cursor;
                built[i] = field;
            }
        }
        return cursor;
    }

    private int List(int start, int limit, bool indexed, int depth, SaveField? field, string what)
    {
        Need(start, ListHeaderSize, limit, what);
        uint count = U32(start + 1);
        // The next id of an indexed list is the only value after the count.
        bool blank = indexed
            ? raw[start + 5] == 0 && !raw.AsSpan(start + 10, 8).ContainsAnyExcept((byte)0)
            : !raw.AsSpan(start + 5, ListHeaderSize - 5).ContainsAnyExcept((byte)0);
        if (raw[start] != 0 || !blank) throw new InvalidDataException($"{what} at {start} has an invalid list header.");
        int cursor = start + ListHeaderSize;
        // Bound the allocation before trusting the count; an object takes at least 18 bytes.
        Need(cursor, count * 18L, limit, what);
        var elements = field is null ? [] : new SaveField[count];
        for (int i = 0; i < count; i++)
        {
            var element = field is null ? null : new SaveField { Present = true, FieldIndex = i, StartOffset = cursor };
            try { cursor = Object(cursor, limit, indexed, depth + 1, element); }
            catch (InvalidDataException) when (element is not null && Header(cursor, limit, indexed, out _, out _).Name == "MissionStateData") { cursor = MarkerlessRow(cursor, limit, element); }
            if (element is not null)
            {
                element.TypeName = element.ChildTypeName;
                element.EndOffset = cursor;
                elements[i] = element;
            }
        }
        if (field is not null)
        {
            field.ListElements = elements;
            field.ListCount = (int)count;
            field.ListHeaderSize = ListHeaderSize;
        }
        return cursor;
    }
}
