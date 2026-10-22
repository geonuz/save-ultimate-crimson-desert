using System.Text;
using SaveUltimate.Core.Binary;

namespace SaveUltimate.Core.Saves;

internal ref struct RecordReader(ReadOnlySpan<byte> bytes)
{
    private ByteReader reader = new(bytes);
    private readonly Dictionary<string, string> names = new(StringComparer.Ordinal);
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static SaveObject[] Read(ReadOnlySpan<byte> bytes)
    {
        var input = new RecordReader(bytes);
        if (!input.reader.Take(4).SequenceEqual("SVR1"u8))
            throw new InvalidDataException("Unsupported record reader format.");
        var objects = new SaveObject[input.Count(12)];
        for (int i = 0; i < objects.Length; i++)
            objects[i] = new() { ClassName = input.Text(), Fields = input.Fields(0), UndecodedRanges = input.Ranges() };
        if (input.reader.Remaining != 0) throw new InvalidDataException("Trailing record data.");
        return objects;
    }

    private int Count(int minimumSize)
    {
        int count = reader.Count();
        if (count > reader.Remaining / minimumSize) throw new InvalidDataException("Invalid record count.");
        return count;
    }

    private string Text()
    {
        var bytes = reader.Take(reader.Count());
        if (bytes.IsEmpty) return "";
        int length = Utf8.GetCharCount(bytes);
        Span<char> text = length <= 256 ? stackalloc char[length] : new char[length];
        Utf8.GetChars(bytes, text);
        // Repeated schema names share one string without allocating a temporary string.
        var lookup = names.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(text, out var found)) return found;
        string value = new(text);
        names.Add(value, value);
        return value;
    }

    private SaveField[] Fields(int depth)
    {
        if (depth > 64) throw new InvalidDataException("Record nesting is too deep.");
        int count = Count(69);
        if (count == 0) return [];
        var fields = new SaveField[count];
        for (int i = 0; i < count; i++)
        {
            var field = new SaveField
            {
                Name = Text(), TypeName = Text(), MetaKind = reader.Count(), MetaSize = reader.Count(),
                FieldIndex = reader.Count(), ChildMaskByteCount = reader.Count(),
                Present = reader.U8() switch { 0 => false, 1 => true, _ => throw new InvalidDataException("Invalid presence flag.") },
                StartOffset = reader.Count(), EndOffset = reader.Count(), ChildTypeName = Text(),
                ChildPayloadOffset = reader.Count(), ChildSizeU32 = reader.Count(),
                ListCount = reader.Count(), ListHeaderSize = reader.Count()
            };
            string note = Text();
            field.Note = note.Length == 0 ? null : note;
            field.ChildFields = Fields(depth + 1);
            field.ListElements = Fields(depth + 1);
            field.ChildUndecodedRanges = Ranges();
            fields[i] = field;
        }
        return fields;
    }

    private int[][] Ranges()
    {
        int count = Count(8);
        if (count == 0) return [];
        var ranges = new int[count][];
        for (int i = 0; i < count; i++) ranges[i] = [reader.Count(), reader.Count()];
        return ranges;
    }
}
