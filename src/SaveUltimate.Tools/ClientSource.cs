using System.Security.Cryptography;
using System.Text.Json.Nodes;
using SaveUltimate.Core.Archives;

using SaveUltimate.Core.Catalogs;

namespace SaveUltimate.Tools;

// Client tables and string files for one catalog build, layered base first and tagged "" or ".<dlc>" as in the file
// names. Every file read is recorded for the catalog's sourceSha256.
public sealed class ClientSource(Func<string, IEnumerable<(string Tag, byte[] Header, byte[] Body)>> tables,
    Func<string, string, IEnumerable<(string Tag, byte[] Data)>> strings, Func<string, IEnumerable<(string Tag, byte[] Data)>>? misc = null,
    Func<string, byte[]>? ui = null)
{
    private readonly SortedDictionary<string, byte[]> read = new(StringComparer.Ordinal);
    public ClientSource(GameFiles game) : this(game.Tables, (language, name) => game.ReadLayers(
        ClientMetadata.Languages.Values.Single(l => l.Name == language).Group, $"gamedata/stringtable/binary__/{language}/{name}.paloc"), game.Misc,
        path => game.Read("0012", path)) { }

    // UI files such as ui/inputmap.xml.
    internal byte[] Ui(string path)
    {
        byte[] data = (ui ?? throw new InvalidOperationException("No UI file source.")).Invoke(path);
        read[path] = data;
        return data;
    }

    internal List<(string Tag, byte[] Data)> Misc(string name)
    {
        var layers = (misc ?? throw new InvalidOperationException("No misc static-info source.")).Invoke(name).ToList();
        foreach (var (tag, data) in layers) read[$"{name}{tag}.staticinfomisc"] = data;
        return layers;
    }

    internal List<(string Tag, byte[] Header, byte[] Body)> Tables(string name)
    {
        var layers = tables(name).ToList();
        foreach (var (tag, header, body) in layers) { read[$"{name}{tag}.staticinfoheader"] = header; read[$"{name}{tag}.staticinfobody"] = body; }
        return layers;
    }
    // Language is the client's folder name (eng, kor, ...); later layers replace earlier strings.
    internal Dictionary<string, string> Strings(string language, string name)
    {
        var result = new Dictionary<string, string>();
        foreach (var (tag, data) in strings(language, name))
        {
            read[$"{language}_{name}{tag}.paloc"] = data;
            foreach (var (key, text) in ClientTables.Strings(data)) result[key] = text;
        }
        return result;
    }
    internal JsonObject Digests() => new(read.Select(p => KeyValuePair.Create<string, JsonNode?>(p.Key, Convert.ToHexStringLower(SHA256.HashData(p.Value)))));
}
