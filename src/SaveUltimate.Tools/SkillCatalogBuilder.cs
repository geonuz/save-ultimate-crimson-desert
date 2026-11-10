using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using SaveUltimate.Core.Catalogs;

namespace SaveUltimate.Tools;

// Port of the Python skills catalog builder: the three skill trees and the learning routes of their knowledge,
// including DLC tables.
public static partial class SkillCatalogBuilder
{
    private static readonly uint[] Conditions = [1008767, 1008768, 1008769, 1008770];
    private static readonly (int Owner, uint Key)[] Trees = [(0, 50), (1, 51), (2, 52)];

    [GeneratedRegex(@"\{Staticinfo:Knowledge:([^}]+)\}", RegexOptions.IgnoreCase)]
    private static partial Regex KnowledgeLink();
    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex Break();
    [GeneratedRegex(@"<[^>]+>|\{[^}]+\}")]
    private static partial Regex Markup();
    [GeneratedRegex(@"\{Key:Key_([^}]+)\}")]
    private static partial Regex KeyPrompt();
    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])")]
    private static partial Regex WordBreak();

    [GeneratedRegex(@"<Input Name=""Key_(\w+)""(?:[^>/]|/(?!>))*>(.*?)</>", RegexOptions.Singleline)]
    private static partial Regex InputBlock();
    [GeneratedRegex(@"<KeyboardMouse\b[^>]*\bKey=""([^""]+)""")]
    private static partial Regex KeyboardKey();
    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XmlComment();

    // Default PC keys of ui/inputmap.xml by action (Skill_17 -> "f mouseR"); the first keyboard binding is the primary one.
    // Names are matched without underscores: descriptions write Key_FocusMabongpa for the map's Key_Focus_Mabongpa.
    internal static Dictionary<string, string> KeyBindings(string inputMap)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match input in InputBlock().Matches(XmlComment().Replace(inputMap, "")))
            if (KeyboardKey().Match(input.Groups[2].Value) is { Success: true } key) result.TryAdd(input.Groups[1].Value.Replace("_", ""), key.Groups[1].Value);
        return result;
    }
    // By language: en, ko, ja, ru.
    private static readonly Dictionary<string, string[]> KeyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mouseL"] = ["Left Click", "좌클릭", "左クリック", "ЛКМ"], ["mouseR"] = ["Right Click", "우클릭", "右クリック", "ПКМ"],
        ["mouseM"] = ["Middle Click", "휠 클릭", "ホイールクリック", "СКМ"], ["mouseX1"] = ["Mouse 4", "마우스 4", "マウス4", "Мышь 4"],
        ["mouseX2"] = ["Mouse 5", "마우스 5", "マウス5", "Мышь 5"], ["mouseWheel"] = ["Mouse Wheel", "마우스 휠", "マウスホイール", "Колесо мыши"],
        ["lshift"] = ["Shift", "Shift", "Shift", "Shift"], ["caps"] = ["Caps Lock", "Caps Lock", "Caps Lock", "Caps Lock"], ["escape"] = ["Esc", "Esc", "Esc", "Esc"]
    };
    private static string KeyName(string key, int language) => KeyNames.TryGetValue(key, out var names) ? names[language]
        : key.Length == 1 || key[0] == 'f' && key[1..].All(char.IsAsciiDigit) ? key.ToUpperInvariant() : char.ToUpperInvariant(key[0]) + key[1..];

    // The game draws the bound key here; show the default PC keys ({Key:Key_Skill_17} -> [F + Right Click]),
    // or name the action when the map has no keyboard binding (Key_BlackWolfSeal -> [Black Wolf Seal]).
    internal static string KeyLabels(string text, IReadOnlyDictionary<string, string> bindings, int language) => KeyPrompt().Replace(text,
        m => "[" + (bindings.TryGetValue(m.Groups[1].Value.Replace("_", ""), out string? keys)
            ? string.Join(" + ", keys.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(k => KeyName(k, language)))
            : WordBreak().Replace(m.Groups[1].Value.Replace("NorAttack", "NormalAttack"), " ").Replace('_', ' ')) + "]");

    private static string Reference(ClientReader r)
    {
        byte category = r.U8(); uint member = r.U32(), owner = r.U32(); string value = r.Text();
        if (!(category == 9 && value == (((ulong)owner << 32) | member).ToString()) && (category, member, owner, value) != (0, 0u, 0u, ""))
            throw new InvalidDataException("Unsupported skill string reference.");
        return value;
    }
    private static JsonArray Pairs(ClientReader r, bool wide = false)
        => new(Enumerable.Range(0, r.Count(256)).Select(_ => (JsonNode)new JsonArray(r.U32(), wide ? r.U64() : r.U32())).ToArray());
    private static double Float(ClientReader r) => BitConverter.UInt32BitsToSingle(r.U32());

    private static JsonObject Definition(KnowledgeCatalogBuilder.Knowledge k)
    {
        var r = new ClientReader(k.Record.Bytes, k.LevelStart); var levels = new JsonArray();
        for (int level = 1; level <= k.Levels; level++)
        {
            if (r.U32() != level || r.Take(34).ContainsAnyExcept((byte)0)) throw new InvalidDataException("Unsupported skill level header.");
            uint icon = r.U32();
            if (r.Count() != 0) throw new InvalidDataException("Unsupported skill level flags.");
            int count = r.Count(2);
            if (count == 0) throw new InvalidDataException("Unsupported skill learning routes.");
            var routes = new JsonArray();
            for (int i = 0; i < count; i++)
            {
                var conditions = new JsonArray();
                for (int c = r.Count(); c > 0; c--)
                {
                    uint key = r.U32();
                    if (!Conditions.Contains(key)) throw new InvalidDataException("Unreviewed skill condition.");
                    conditions.Add(new JsonArray(key, Reference(r)));
                }
                JsonArray items = Pairs(r, true), requires = Pairs(r), unlocks = Pairs(r);
                uint sequencer = r.U32(); byte mode = r.U8(), flag = r.U8();
                if (r.U32() != 1 || r.Count() != 0 || mode is not (0 or 1 or 2 or 3 or 5) || flag > 1) throw new InvalidDataException("Unsupported skill learning rule.");
                r.Take(4);  // UI hint hash; not a save key.
                routes.Add(new JsonObject { ["mode"] = mode, ["items"] = items, ["requires"] = requires, ["unlocks"] = unlocks, ["conditions"] = conditions, ["sequencer"] = sequencer, ["flag"] = flag });
            }
            var grants = Pairs(r);
            int variants = r.Count(8);
            if (variants == 0) throw new InvalidDataException("Unsupported skill description variants.");
            for (int i = 0; i < variants; i++) { Reference(r); Reference(r); Reference(r); r.Take(12); }
            string title = Reference(r), description = Reference(r);
            levels.Add(new JsonObject
            {
                // Localized after every definition is read, like the Python builder.
                ["routes"] = routes, ["grants"] = grants, ["icon"] = icon, ["name"] = title, ["description"] = description
            });
        }
        // Enhanced skills add dependencies (Enlightenment) to every route in a [0][pairs] tail; fixed fields follow.
        if (r.U32() != 0) throw new InvalidDataException("Unsupported skill definition tail.");
        var extra = Pairs(r);
        if (r.Remaining != 12) throw new InvalidDataException("Unsupported skill definition tail.");
        foreach (var route in levels.SelectMany(l => l!["routes"]!.AsArray()))
            foreach (var pair in extra) route!["requires"]!.AsArray().Add(pair!.DeepClone());
        return new JsonObject { ["key"] = k.Key, ["skill"] = k.Skill, ["default"] = k.Default, ["levels"] = levels };
    }

    private static JsonArray Tree(uint key, ClientRecord record)
    {
        var r = new ClientReader(record.Bytes, record.Prefix);
        r.Take(32); r.Reference(25, 402, key);
        for (int i = r.Count(); i > 0; i--) { r.Take(12); r.SkipText(); }
        var nodes = new JsonArray(); var ids = new HashSet<uint>(); var guides = new List<(uint Node, uint Parent)>();
        for (int i = r.Count(); i > 0; i--)
        {
            uint id = r.U32(), skill = r.U32(); r.Take(24);
            var xy = new JsonArray(Float(r), Float(r)); r.Take(4); r.Array(4);
            var lines = new JsonArray();
            for (int g = r.Count(); g > 0; g--)
            {
                uint parent = r.U32(); var points = new JsonArray();
                for (int p = r.Count(); p > 0; p--) points.Add(new JsonArray(Float(r), Float(r)));
                lines.Add(new JsonObject { ["parent"] = parent, ["points"] = points }); guides.Add((id, parent));
            }
            uint[] children = r.Array32(); r.Take(1); r.Array(4);
            var color = new JsonArray(Float(r), Float(r), Float(r), Float(r)); r.Take(4);
            if (!ids.Add(id)) throw new InvalidDataException("Duplicate skill tree node.");
            nodes.Add(new JsonObject { ["id"] = id, ["skill"] = skill, ["xy"] = xy, ["guides"] = lines, ["children"] = new JsonArray(children.Select(c => (JsonNode)c).ToArray()), ["color"] = color });
        }
        r.Take(4); r.Array(4); r.Take(16);
        // The Kliff layout keeps one guide to a removed visual node.
        (uint, uint)[] dangling = guides.Where(g => !ids.Contains(g.Parent)).ToArray(), expected = key == 50 ? [(1650, 850)] : [];
        if (r.Remaining != 0 || !dangling.SequenceEqual(expected)) throw new InvalidDataException("Invalid skill tree layout.");
        return nodes;
    }

    // data/skills.json (en/ko names and descriptions) and the ja/ru packs' skill_descriptions, keyed "<knowledge>:<level>".
    public static Dictionary<string, JsonNode> Build(ClientSource source, Func<string, JsonNode> load)
    {
        var records = KnowledgeCatalogBuilder.Records(source, KnowledgeCatalogBuilder.Groups(source).Keys.ToHashSet());
        var tables = ClientTables.Records(source.Tables("skilltreeinfo"));
        var en = source.Strings("eng", "knowledge"); var ko = source.Strings("kor", "knowledge");
        var ja = source.Strings("jpn", "knowledge"); var ru = source.Strings("rus", "knowledge");
        var packs = new[] { "ja", "ru" }.Select(c => load($"locale/names_{c}.json").AsObject()).ToArray();
        var bindings = KeyBindings(System.Text.Encoding.UTF8.GetString(source.Ui("ui/inputmap.xml")));
        var trees = Trees.ToDictionary(t => t.Owner, t => Tree(t.Key, tables[t.Key]));
        var skillIds = trees.Values.SelectMany(n => n).Select(n => n!["skill"]!.GetValue<uint>()).ToHashSet();

        var names = new Dictionary<uint, string[]>(); var internalNames = new Dictionary<string, uint>();
        foreach (var k in records.Values)
        {
            // Names of linked knowledge are display-only; editable fields come from the level lists.
            string label = KnowledgeCatalogBuilder.References(k.Record).FirstOrDefault(r => r.Member is 0x490 or 0x49D).Key ?? "";
            string english = en.GetValueOrDefault(label, k.Internal);
            names[k.Key] = [english, ko.GetValueOrDefault(label, english)];
            internalNames[k.Internal] = k.Key;
        }
        string Clean(string text, int language)
        {
            // Links may carry their own label after '#' (Knowledge_Evade_Shot#Evasive Shot).
            text = KnowledgeLink().Replace(text, m =>
            {
                string[] link = m.Groups[1].Value.Split('#', 2);
                if (!internalNames.TryGetValue(link[0], out uint key)) return link[^1];
                return language < 2 ? names[key][language] : packs[language - 2]["knowledge"]?[key.ToString()]?.GetValue<string>() ?? names[key][0];
            });
            return Markup().Replace(Break().Replace(KeyLabels(text, bindings, language), "\n"), "").Trim();
        }
        var definitions = records.Values.Where(k => skillIds.Contains(k.Skill)).ToDictionary(k => k.Key, k => Definition(k));
        var skillKeys = definitions.Values.ToDictionary(d => d["skill"]!.GetValue<uint>(), d => d["key"]!.GetValue<uint>());
        if (!skillIds.Except(skillKeys.Keys).Order().SequenceEqual([1026u])) throw new InvalidDataException("Unresolved or ambiguous skill knowledge.");
        static bool Paid(JsonObject d) => d["levels"]!.AsArray().Any(l => l!["routes"]!.AsArray().Any(r => r!["items"]!.AsArray().Count > 0));
        foreach (var (owner, nodes) in trees)
            foreach (var node in nodes)
            {
                uint? key = skillKeys.TryGetValue(node!["skill"]!.GetValue<uint>(), out uint k) ? k : null;
                node["key"] = key;
                if (key is not uint found) continue;
                var definition = definitions[found];
                if (definition["owner"] is { } previous && previous.GetValue<int>() != owner && Paid(definition)) throw new InvalidDataException("Shared skill ownership is unsupported.");
                definition["owner"] ??= owner;
            }
        var pending = definitions.Values.ToList();
        for (int i = 0; i < pending.Count; i++)
        {
            var definition = pending[i]; var related = new JsonArray();
            foreach (uint key in definition["levels"]!.AsArray().SelectMany(l => l!["grants"]!.AsArray()).Select(g => g![0]!.GetValue<uint>()).Distinct().Order())
            {
                var k = records[key];
                if (k.Skill == 0) continue;
                related.Add(key);
                if (definitions.ContainsKey(key)) continue;
                var child = Definition(k); child["owner"] = definition["owner"]!.DeepClone();
                definitions[key] = child; pending.Add(child);
            }
            definition["related"] = related;
        }
        var described = packs.Select(_ => new JsonObject()).ToArray();
        foreach (var (key, definition) in definitions)
            foreach (var (level, number) in definition["levels"]!.AsArray().Select((l, i) => (l!, i + 1)))
            {
                string title = level["name"]!.GetValue<string>(), description = level["description"]!.GetValue<string>();
                level["name"] = new JsonArray(en.GetValueOrDefault(title, ""), ko.GetValueOrDefault(title, ""));
                level["description"] = new JsonArray(Clean(en.GetValueOrDefault(description, ""), 0), Clean(ko.GetValueOrDefault(description, ""), 1));
                foreach (var (texts, index) in new[] { ja, ru }.Select((t, i) => (t, i)))
                    if (Clean(texts.GetValueOrDefault(description, ""), index + 2) is { Length: > 0 } text) described[index][$"{key}:{number}"] = text;
            }
        for (int i = 0; i < packs.Length; i++) packs[i]["skill_descriptions"] = described[i];
        // Kliff's Double Jump is learned by watching it in Pororin (saves record FollowManualLockOn), but its data has only
        // a default route; add the region observation route the game evidently uses.
        var doubleJump = definitions[1001116]["levels"]![0]!["routes"]!.AsArray();
        if (!doubleJump.Any(r => r!["mode"]!.GetValue<byte>() is 1 or 2))
            doubleJump.Add(new JsonObject { ["mode"] = (byte)2, ["items"] = new JsonArray(), ["requires"] = new JsonArray(), ["unlocks"] = new JsonArray(),
                ["conditions"] = new JsonArray(new JsonArray(1008768u, "")), ["sequencer"] = 0u, ["flag"] = (byte)0 });
        foreach (var definition in definitions.Values)
            definition["watch"] = definition["levels"]!.AsArray().Any(l => l!["routes"]!.AsArray().Any(r => r!["mode"]!.GetValue<byte>() is 1 or 2));
        var skills = new JsonObject
        {
            ["version"] = 1,
            ["trees"] = new JsonObject(trees.Select(t => KeyValuePair.Create<string, JsonNode?>(t.Key.ToString(), t.Value))),
            ["skills"] = new JsonObject(definitions.Select(d => KeyValuePair.Create<string, JsonNode?>(d.Key.ToString(), d.Value))),
            ["names"] = new JsonObject(names.Select(n => KeyValuePair.Create<string, JsonNode?>(n.Key.ToString(), new JsonArray(n.Value[0], n.Value[1])))),
            ["sourceSha256"] = source.Digests()
        };
        return new() { ["data/skills.json"] = skills, ["locale/names_ja.json"] = packs[0], ["locale/names_ru.json"] = packs[1] };
    }
}
