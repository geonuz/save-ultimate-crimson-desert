using System.Text.Json.Nodes;

namespace SaveUltimate.Core.Catalogs;

internal static class ClientMetadata
{
    internal static readonly Dictionary<string, (string Group, string Name)> Languages = new()
    { ["en"] = ("0020", "eng"), ["ko"] = ("0019", "kor"), ["ja"] = ("0021", "jpn"), ["ru"] = ("0022", "rus") };
    private static readonly Dictionary<string, string> BroadNames = new()
    {
        ["ItemGroup_Equip_Weapon"] = "3090942479503982768", ["ItemGroup_Equip_Armor"] = "10295248913040408752",
        ["ItemGroup_Equip_Horse"] = "7737074371868491952"
    };
    private sealed record Group(string Internal, string Reference, uint[] Children, uint[] Items);
    public static Dictionary<string, JsonNode> Build(Func<string, string, byte[]> read, string resources, string destination,
        IReadOnlySet<string> valid, out int itemCount, out int added, out int changed)
    {
        (byte[], byte[]) Table(string name) => (read("0008", $"gamedata/binarystaticinfo__/bin/{name}.staticinfoheader"),
            read("0008", $"gamedata/binarystaticinfo__/bin/{name}.staticinfobody"));
        Dictionary<uint, ClientRecord> Records(string name) { var (h, b) = Table(name); return ClientTables.Records(h, b); }
        Dictionary<uint, (string Name, ClientReader Reader)> Small(string name) { var (h, b) = Table(name); return ClientTables.Records16(h, b); }
        Dictionary<string, string> Strings(string code, string name)
        {
            var (group, language) = Languages[code]; return ClientTables.Strings(read(group, $"gamedata/stringtable/binary__/{language}/{name}.paloc"),
                name == "localstringinfo" ? BroadNames.Values.ToHashSet() : null);
        }
        var items = Records("iteminfo"); var statuses = Records("statusinfo");
        var texts = Languages.Keys.ToDictionary(c => c, c => Strings(c, "item"));
        var groupTexts = Languages.Keys.ToDictionary(c => c, c => Strings(c, "itemgroup"));
        var uiTexts = Languages.Keys.ToDictionary(c => c, c => Strings(c, "localstringinfo"));
        var categories = Small("categoryinfo").ToDictionary(p => p.Key, p =>
        {
            var r = p.Value.Reader; var indices = new[] { r.U16(), r.U16(), r.U16() };
            if (r.Remaining != 0) throw new InvalidDataException("Unsupported CategoryInfo layout.");
            return indices;
        });
        var groups = Small("itemgroupinfo").ToDictionary(p => p.Key, p =>
        {
            var r = p.Value.Reader; string reference = r.Reference(8, 0x80, p.Key);
            var children = Enumerable.Range(0, r.Count()).Select(_ => (uint)r.U16()).ToArray();
            var members = Enumerable.Range(0, r.Count()).Select(_ => r.U32()).ToArray();
            r.Array(1); r.Take(7);
            foreach (byte flag in r.Take(4)) if (flag > 1) throw new InvalidDataException("Invalid item group flag.");
            if (r.Remaining != 0) throw new InvalidDataException("Unsupported ItemGroupInfo layout.");
            return new Group(p.Value.Name, reference, children, members);
        });
        if (groups.Values.Any(g => g.Children.Any(k => !groups.ContainsKey(k)))) throw new InvalidDataException("Unresolved item subgroup.");
        var broad = new Dictionary<uint, uint>(); var detail = new Dictionary<uint, uint>();
        foreach (var (key, group) in groups)
        {
            var target = BroadNames.ContainsKey(group.Internal) ? broad :
                group.Internal.StartsWith("ItemGroup_SubCategory_") || group.Internal == "ItemGroup_Collection" ? detail : null;
            if (target is null) continue;
            foreach (string code in new[] { "en", "ko" })
                if (Label(key, code).Length == 0) throw new InvalidDataException("Missing game classification translation.");
            foreach (uint member in group.Items) if (!target.TryAdd(member, key)) throw new InvalidDataException("Ambiguous item classification.");
        }
        string Label(uint key, string code)
        {
            if (!groups.TryGetValue(key, out var group)) return "";
            return BroadNames.TryGetValue(group.Internal, out var name) ? uiTexts[code].GetValueOrDefault(name, "") : groupTexts[code].GetValueOrDefault(group.Reference, "");
        }
        var packs = new Dictionary<string, JsonObject>();
        foreach (string code in Languages.Keys.Where(c => c != "en"))
        {
            string name = $"locale/names_{code}.json";
            var pack = JsonNode.Parse(File.ReadAllBytes(Path.Combine(resources, name)))!.AsObject();
            if (valid.Contains(name))
                foreach (var p in JsonNode.Parse(File.ReadAllBytes(Path.Combine(destination, name)))!.AsObject()) pack[p.Key] = p.Value?.DeepClone();
            pack["item_groups"] = new JsonObject(groups.Where(p => Label(p.Key, code).Length > 0).Select(p => KeyValuePair.Create<string, JsonNode?>(p.Key.ToString(), JsonValue.Create(Label(p.Key, code)))));
            pack["items"] ??= new JsonObject(); pack["item_descriptions"] ??= new JsonObject(); packs[code] = pack;
        }
        var baseItems = JsonNode.Parse(File.ReadAllBytes(Path.Combine(resources, "data/item_names.json")))!.AsObject();
        var previous = baseItems["items"]!.AsArray().ToDictionary(n => n!["itemKey"]!.GetValue<uint>(), n => n!);
        var updated = new Dictionary<uint, JsonNode>(previous);
        var details = new JsonObject(); var caps = new JsonObject(); changed = 0;
        foreach (var (key, item) in items)
        {
            var r = new ClientReader(item.Bytes, item.Prefix); ulong maxStack = r.U64();
            if (maxStack == 0) throw new InvalidDataException("Zero item stack limit.");
            string nameRef = r.Reference(7, 0x70, key);
            var header = ClientTables.ItemHeader(key, item);
            var categoryReader = new ClientReader(item.Bytes, header.End); categoryReader.Take(4); uint category = categoryReader.U16();
            if (!categories.TryGetValue(category, out var indices)) throw new InvalidDataException("Unknown item category.");
            uint d = detail.GetValueOrDefault(key), g = broad.GetValueOrDefault(key, d);
            var classification = JsonValue.Create(new
            {
                categoryKey = category, indices, groupKey = g, name = Label(g, "en"), nameKo = Label(g, "ko"),
                detailKey = d, detail = Label(d, "en"), detailKo = Label(d, "ko")
            });
            string Description(string code) => string.Join("\n\n", new[] { texts[code].GetValueOrDefault(header.Description, ""), texts[code].GetValueOrDefault(header.Extra, "") }.Where(s => s.Length > 0));
            var row = new JsonObject { ["description"] = Description("en"), ["descriptionKo"] = Description("ko"),
                ["icons"] = new JsonArray(header.Icons.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()) };
            if (ClientTables.EnchantLevels(item, header.End, statuses, items) is { } levels)
            { row["levels"] = levels; row["maxEnchant"] = levels.Count - 1; caps[key.ToString()] = levels.Count - 1; }
            details[key.ToString()] = row;
            foreach (var (code, pack) in packs)
            {
                if (texts[code].TryGetValue(nameRef, out string? translated) && translated.Length > 0) pack["items"]![key.ToString()] = translated;
                else pack["items"]!.AsObject().Remove(key.ToString());
                pack["item_descriptions"]![key.ToString()] = Description(code);
            }
            string name = texts["en"].GetValueOrDefault(nameRef, "");
            var old = previous.GetValueOrDefault(key);
            var next = new JsonObject { ["itemKey"] = key, ["name"] = name.Length > 0 ? name : old?["name"]?.GetValue<string>() ?? item.Name,
                ["internalName"] = item.Name, ["category"] = old?["category"]?.GetValue<string>() ?? GuessCategory(item.Name),
                ["maxStack"] = maxStack, ["socketLimit"] = ClientTables.SocketLimit(item, header.End, items), ["gameClassification"] = classification };
            if (!JsonNode.DeepEquals(old, next)) changed++;
            updated[key] = next;
        }
        var rows = new JsonArray(updated.OrderBy(p => p.Key).Select(p => p.Value.Parent is null ? p.Value : p.Value.DeepClone()).ToArray());
        var outputs = new Dictionary<string, JsonNode>
        {
            ["data/item_names.json"] = new JsonObject { ["version"] = (baseItems["version"]?.GetValue<int>() ?? 0) + (JsonNode.DeepEquals(rows, baseItems["items"]) ? 0 : 1), ["items"] = rows },
            ["data/item_details.json"] = new JsonObject { ["items"] = details, ["stats"] = new JsonObject(statuses.Select(p => KeyValuePair.Create<string, JsonNode?>(p.Key.ToString(), JsonValue.Create(p.Value.Name)))) },
            ["data/max_enchant_map.json"] = caps
        };
        foreach (var (code, pack) in packs) outputs[$"locale/names_{code}.json"] = pack;
        itemCount = items.Count; added = items.Keys.Except(previous.Keys).Count(); return outputs;
    }
    private static string GuessCategory(string name)
    {
        name = name.ToLowerInvariant();
        if (new[] { "weapon", "armor", "shield", "helmet", "glove", "boot", "ring", "earring", "necklace" }.Any(name.Contains)) return "Equipment";
        if (new[] { "potion", "food", "elixir", "meal", "drink" }.Any(name.Contains)) return "Consumable";
        if (new[] { "ore", "ingot", "leather", "timber", "material" }.Any(name.Contains)) return "Material";
        return name.Contains("quest") ? "Quest" : "Misc";
    }
}
