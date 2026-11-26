using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CDSE.Core.Saves;

/// <summary>One item of a dye file: its key, its name when the file was written, and its dyed parts; none for an item in its own colors.</summary>
public sealed record DyeEntry(uint Item, string Name, DyePart[] Parts);

/// <summary>
/// Dye files (.sudye, later .cdsedye): UTF-8 JSON, one entry per item. A file is data from someone else, so it is only parsed, every value must
/// fit its field, and what an item can take is checked again against the game's tables before it reaches the save
/// (<see cref="SaveDocument.PlanDyes"/>). Any damage reads as InvalidDataException.
/// </summary>
public static class DyeFiles
{
    // Written under the SaveUltimate name, which 1.9.0 reads. The CDSE name is read from 1.10.0 on, so a later version can
    // write it once few users are left on 1.9.0; the SaveUltimate name stays readable for the files already shared.
    public const string Extension = ".sudye", NextExtension = ".cdsedye";
    private const string Format = "SaveUltimate.Dye", NextFormat = "CDSE.Dye";
    private const int Version = 1, MaxBytes = 4 << 20, MaxItems = 1000, MaxName = 200;

    public static void Write(string path, IEnumerable<DyeEntry> entries)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, Json(entries, Format, indented: true), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// A dye code to paste in a message: the file's JSON without item names, deflated, in Base64 after <see cref="CodePrefix"/>.
    /// Only 1.10.0 and later read codes, so a code already carries the CDSE format name.
    /// </summary>
    public static string ToCode(IEnumerable<DyeEntry> entries)
    {
        using var packed = new MemoryStream();
        using (var deflate = new DeflateStream(packed, CompressionLevel.SmallestSize))
            deflate.Write(Encoding.UTF8.GetBytes(Json(entries.Select(e => e with { Name = "" }), NextFormat, indented: false)));
        return CodePrefix + Convert.ToBase64String(packed.ToArray());
    }
    public const string CodePrefix = "CDSE-DYE:";

    /// <summary>Reads a code from <see cref="ToCode"/>; the prefix may be missing and whitespace or line breaks may split it.</summary>
    public static DyeEntry[] FromCode(string text)
    {
        string code = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        if (code.StartsWith(CodePrefix, StringComparison.OrdinalIgnoreCase)) code = code[CodePrefix.Length..];
        if (code.Length is 0 or > MaxBytes) throw new InvalidDataException("Not a dye code.");
        byte[] packed;
        try { packed = Convert.FromBase64String(code); }
        catch (FormatException) { throw new InvalidDataException("Not a dye code."); }
        using var json = new MemoryStream();
        try
        {
            using var deflate = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress);
            byte[] buffer = new byte[81920]; int read;
            while ((read = deflate.Read(buffer)) > 0)
            {
                // Like a file, at most MaxBytes once unpacked.
                if (json.Length + read > MaxBytes) throw new InvalidDataException("The dye file is too large.");
                json.Write(buffer, 0, read);
            }
        }
        catch (InvalidDataException error) when (error.Message != "The dye file is too large.") { throw new InvalidDataException("Not a dye code."); }
        return Read(json.ToArray());
    }

    private static string Json(IEnumerable<DyeEntry> entries, string format, bool indented)
    {
        var items = entries.Select(e => (JsonNode)new JsonObject
        {
            ["item"] = e.Item, ["name"] = e.Name.Length > 0 ? e.Name : null,
            ["parts"] = new JsonArray(e.Parts.Select(p =>
            {
                var part = new JsonObject { ["part"] = p.Part };
                if (p.Colored) part["color"] = $"#{p.R:X2}{p.G:X2}{p.B:X2}";
                if (p.Group != 0) part["group"] = p.Group;
                if (p.Texture != 0) part["material"] = p.Texture;
                if (p.Grime is byte grime) part["grime"] = grime;
                if (p.HideSymbol) part["hideEmblem"] = true;
                return (JsonNode)part;
            }).ToArray())
        }).ToArray();
        var root = new JsonObject { ["format"] = format, ["version"] = Version, ["items"] = new JsonArray(items) };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = indented, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    public static DyeEntry[] Read(string path)
    {
        if (new FileInfo(path).Length > MaxBytes) throw new InvalidDataException("The dye file is too large.");
        return Read(File.ReadAllBytes(path));
    }

    public static DyeEntry[] Read(byte[] bytes)
    {
        try
        {
            var root = JsonNode.Parse(bytes) as JsonObject;
            if (root?["format"]?.GetValue<string>() is not (Format or NextFormat)) throw new InvalidDataException("Not a dye file.");
            if (root["version"]?.GetValue<int>() is not { } version || version > Version) throw new InvalidDataException("This dye file needs a newer CDSE.");
            var items = root["items"] as JsonArray ?? throw new InvalidDataException("The dye file is damaged.");
            if (items.Count is 0 or > MaxItems) throw new InvalidDataException("The dye file is damaged.");
            return items.Select(node =>
            {
                var item = node!.AsObject();
                string name = item["name"]?.GetValue<string>() ?? "";
                var parts = item["parts"]!.AsArray();
                if (parts.Count > Catalogs.GearCatalog.DyeParts) throw new InvalidDataException("The dye file is damaged.");
                return new DyeEntry(item["item"]!.GetValue<uint>(), name.Length > MaxName ? name[..MaxName] : name, parts.Select(p => Part(p!.AsObject())).ToArray());
            }).ToArray();
        }
        catch (Exception e) when (e is not InvalidDataException and not OutOfMemoryException) { throw new InvalidDataException("The dye file is damaged.", e); }
    }

    private static DyePart Part(JsonObject part)
    {
        byte[] rgb = [];
        if (part["color"]?.GetValue<string>() is { } color)
        {
            if (color.Length != 7 || color[0] != '#') throw new FormatException();
            rgb = Enumerable.Range(0, 3).Select(i => byte.Parse(color.AsSpan(1 + 2 * i, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)).ToArray();
        }
        int index = part["part"]!.GetValue<int>();
        if (index is < 0 or >= Catalogs.GearCatalog.DyeParts) throw new FormatException();
        return new(index, rgb.Length > 0, rgb.ElementAtOrDefault(0), rgb.ElementAtOrDefault(1), rgb.ElementAtOrDefault(2), part["group"]?.GetValue<uint>() ?? 0,
            part["material"]?.GetValue<ushort>() ?? 0, part["grime"]?.GetValue<byte>(), part["hideEmblem"]?.GetValue<bool>() ?? false);
    }
}
