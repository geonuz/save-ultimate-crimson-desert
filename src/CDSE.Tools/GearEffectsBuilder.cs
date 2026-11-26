using System.Buffers.Binary;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CDSE.Core.Archives;
using CDSE.Core.Catalogs;

namespace CDSE.Tools;

// What each Abyss Gear does, for socket_gems.json. A gear's ItemInfo record holds one of three things:
//  - its own stat list (the enchant layout with one level): Attack, Defense, and stat levels such as Crit Rate,
//  - a passive buff and the buff level it applies (u32 1, buff key, level): regeneration, damage reduction, guard, climb, swim,
//    stamina, and the skill gears' special effects,
//  - an elemental resistance level (u8 1, u32 1, element 8037 to 8039, level).
// A buff's levels each hold the status the level changes and its value. StatusInfo marks rate statuses (flag at +9), which
// count 10,000,000 per percent; other statuses count 1,000 per point, as the equipment stats do. A stat level reads its value
// from the status's own level table (entry 0 is level 0).
// The knowledge entry of a gear family names the family's first gear (MultiChange:<family>_LV1) and describes the effect. Boss
// gears, which are not crafted, have an entry of their own name.
// Each buff level also names a line of the game's PatternDescription strings, the effect line of the item tooltip ("반려동물
// 친밀도 획득 시 {Param1} 추가"). For a skill gear whose line states an effect, rather than naming its skill and key, socket_gems.json
// keeps that line in every language with the level's value filled in.
// Checked 2026-11-27 against 2.05.01 and 2.06.00 (the same catalog): every gear of socket_gems.json resolves; values are not checked in game.
public static class GearEffectsBuilder
{
    private static readonly Regex Gear = new(@"^Item_(Stat|Skill)_AbyssGear_(\w+?)(_LV\d+|_Special)?$");

    public static JsonArray Build(GameFiles game, string resources)
    {
        var items = ClientTables.Records(game.Tables("iteminfo"));
        var statuses = ClientTables.Records(game.Tables("statusinfo"));
        var buffs = ClientTables.Records(game.Tables("buffinfo"));
        var previous = JsonNode.Parse(File.ReadAllBytes(Path.Combine(resources, "data", "socket_gems.json")))!.AsArray()
            .ToDictionary(g => g!["key"]!.GetValue<uint>(), g => g!["category"]!.GetValue<string>());
        var names = JsonNode.Parse(File.ReadAllBytes(Path.Combine(resources, "data", "item_names.json")))!["items"]!.AsArray()
            .ToDictionary(i => i!["itemKey"]!.GetValue<uint>(), i => i!["name"]!.GetValue<string>());
        var knowledge = new Dictionary<string, uint>();
        foreach (var (key, entry) in JsonNode.Parse(File.ReadAllBytes(Path.Combine(resources, "data", "knowledge_details.json")))!["items"]!.AsObject())
            foreach (Match family in Regex.Matches(entry!["description"]?.GetValue<string>() ?? "", @"MultiChange:(Item_(?:Stat|Skill)_AbyssGear_\w+?)_LV1:"))
                if (!knowledge.TryAdd(family.Groups[1].Value, uint.Parse(key))) throw new InvalidDataException($"Two knowledge entries describe {family.Groups[1].Value}.");
        var named = JsonNode.Parse(File.ReadAllBytes(Path.Combine(resources, "data", "knowledge_keys_all.json")))!.AsArray()
            .GroupBy(k => k!["display_name"]!.GetValue<string>()).ToDictionary(g => g.Key, g => g.Select(k => k!["key"]!.GetValue<uint>()).ToArray());

        var patterns = ClientMetadata.Languages.ToDictionary(l => l.Key, l => Patterns(game, l.Value.Group, l.Value.Name));

        var result = new JsonArray();
        foreach (var (key, record) in items.Where(p => Gear.IsMatch(p.Value.Name)).OrderBy(p => previous.ContainsKey(p.Key) ? 0 : 1).ThenBy(p => p.Key))
        {
            var name = Gear.Match(record.Name);
            string category = previous.GetValueOrDefault(key) ?? (name.Groups[1].Value == "Skill" ? "Special Effects"
                : throw new InvalidDataException($"New stat gear {key} {record.Name} needs a category in socket_gems.json."));
            string family = name.Groups[3].Success ? record.Name[..^name.Groups[3].Length] : record.Name;
            var gem = new JsonObject { ["key"] = key, ["name"] = names.GetValueOrDefault(key, record.Name), ["category"] = category };
            if (knowledge.TryGetValue(family, out uint described) || named.TryGetValue(gem["name"]!.GetValue<string>(), out var same) && same.Length == 1 && (described = same[0]) > 0)
                gem["knowledge"] = described;
            else throw new InvalidDataException($"No knowledge entry describes {record.Name}.");
            if (name.Groups[1].Value == "Stat")
                foreach (var (field, value) in Effect(record, statuses, buffs)) gem[field] = value;
            else if (Buff(record, buffs) is var (buff, level) && Line(buffs[buff], level, statuses, patterns) is { } line) gem["effect"] = line;
            result.Add(gem);
        }
        return result;
    }

    private static IEnumerable<(string, JsonNode)> Effect(ClientRecord record, Dictionary<uint, ClientRecord> statuses, Dictionary<uint, ClientRecord> buffs)
    {
        if (OwnStat(record, statuses) is var (status, value, level))
            return Amount(statuses[status], level ? LevelValue(statuses[status], (int)value) : value);
        if (Buff(record, buffs) is var (buff, buffLevel) && BuffValue(buffs[buff], buffLevel, statuses) is var (buffStatus, buffValue))
            return Amount(statuses[buffStatus], buffValue);
        if (Element(record) is int element) return [("level", element)];
        throw new InvalidDataException($"No effect found in {record.Name}.");
    }

    private static (string, JsonNode)[] Amount(ClientRecord status, long value)
    {
        bool rate = status.Bytes.Span[status.Prefix + 9] == 1;
        return [("status", status.Name), ("amount", Math.Round(value / (rate ? 10_000_000.0 : 1000.0), 3)), ("percent", rate)];
    }

    // The one-level enchant layout: u32 1, u16 0, then the maximum, regeneration, stat and stat level lists.
    private static (uint Status, long Value, bool Level)? OwnStat(ClientRecord record, Dictionary<uint, ClientRecord> statuses)
    {
        var span = record.Bytes.Span; var found = new List<(uint, long, bool)>();
        for (int p = record.Prefix; p + 6 < span.Length; p++)
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(span[p..]) != 1 || BinaryPrimitives.ReadUInt16LittleEndian(span[(p + 4)..]) != 0) continue;
            try
            {
                var r = new ClientReader(record.Bytes, p + 6); var stats = new List<(uint, long, bool)>();
                foreach (string kind in new[] { "maximum", "regen", "stat", "level" })
                    for (int i = r.Count(32); i > 0; i--)
                    {
                        uint status = r.U32(); long value = kind == "level" ? (sbyte)r.U8() : BinaryPrimitives.ReadInt64LittleEndian(r.Take(8));
                        if (!statuses.ContainsKey(status)) throw new InvalidDataException("Unresolved stat.");
                        if (value != 0) stats.Add((status, value, kind == "level"));
                    }
                if (r.Count(8) == 0) throw new InvalidDataException("Missing price.");
                found.AddRange(stats);
            }
            catch (InvalidDataException) { }
        }
        return found.Count switch { 0 => null, 1 => found[0], _ => throw new InvalidDataException($"{record.Name} changes several stats.") };
    }

    private static long LevelValue(ClientRecord status, int level)
    {
        // u32 count, then count increasing i64 values from level 0 (which is 0).
        var span = status.Bytes.Span;
        for (int p = status.Prefix; p + 4 <= span.Length; p++)
        {
            int count = BinaryPrimitives.ReadInt32LittleEndian(span[p..]);
            if (count is < 2 or > 64 || p + 4 + count * 8 > span.Length) continue;
            var values = new long[count];
            for (int i = 0; i < count; i++) values[i] = BinaryPrimitives.ReadInt64LittleEndian(span[(p + 4 + i * 8)..]);
            if (values[0] == 0 && values.Zip(values.Skip(1)).All(v => v.Second > v.First) && level < count) return values[level];
        }
        throw new InvalidDataException($"No level table in {status.Name}.");
    }

    private static (uint Buff, int Level)? Buff(ClientRecord record, Dictionary<uint, ClientRecord> buffs)
    {
        var span = record.Bytes.Span; (uint, int)? found = null;
        for (int p = record.Prefix; p + 12 <= span.Length; p++)
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(span[p..]) != 1) continue;
            uint buff = BinaryPrimitives.ReadUInt32LittleEndian(span[(p + 4)..]); int level = BinaryPrimitives.ReadInt32LittleEndian(span[(p + 8)..]);
            if (!buffs.ContainsKey(buff) || level is < 1 or > 100) continue;
            if (found is not null && found != (buff, level)) throw new InvalidDataException($"{record.Name} names several buffs.");
            found = (buff, level);
        }
        return found;
    }

    // A buff's levels: before the first level's label (u32 length, UTF-8) stand the level count (-48), the entry count (-44) and the first level (-40),
    // which is below 1 for buffs that can also lower a stat. Each level then changes its status as "03000000 00 status i64".
    private static (uint Status, long Value)? BuffValue(ClientRecord buff, int level, Dictionary<uint, ClientRecord> statuses)
    {
        var span = buff.Bytes.Span; var values = new List<(uint, long)>();
        for (int p = buff.Prefix + 5; p + 12 <= span.Length; p++)
        {
            uint status = BinaryPrimitives.ReadUInt32LittleEndian(span[p..]);
            if (status >= 1000000 && statuses.ContainsKey(status) && span[p - 1] == 0 && BinaryPrimitives.ReadInt32LittleEndian(span[(p - 5)..]) == 3)
                values.Add((status, BinaryPrimitives.ReadInt64LittleEndian(span[(p + 4)..])));
        }
        int label = FirstLabel(buff);
        if (label < 0) return null;
        int count = BinaryPrimitives.ReadInt32LittleEndian(span[(label - 44)..]), first = BinaryPrimitives.ReadInt32LittleEndian(span[(label - 40)..]);
        if (count != values.Count || level - first is < 0 || level - first >= count) throw new InvalidDataException($"Unexpected levels in {buff.Name}.");
        return values[level - first];
    }

    // Every line of a language's PatternDescription table, by the id the buff levels name: a key's upper 32 bits.
    private static Dictionary<uint, string> Patterns(GameFiles game, string group, string folder)
    {
        var lines = new Dictionary<uint, string>();
        foreach (var (_, layer) in game.ReadLayers(group, $"gamedata/stringtable/binary__/{folder}/patterndescription.paloc"))
            foreach (var (key, text) in ClientTables.Strings(layer)) lines[(uint)(ulong.Parse(key) >> 32)] = text;
        return lines;
    }

    private static readonly Regex Param = new(@"\{\|?Param\d\|?\}");

    // The effect line of a buff level in each language, or null where the line names a skill and its key ("까마귀 추격 ({Key:...})",
    // "천둥의 송곳니 (벼락 속성을 부여한 공격)"): the knowledge text describes those better. Each level's label is followed by its
    // line's id, and 56 bytes on (59 before 2.06) by "03000000 00" and the value, which comes in one of three layouts:
    //  - a status and its value (i64, as BuffValue reads it), or a status or other key and a level (u8, for "Lv{Param1}"),
    //  - a one-byte kind and the value (i64 for a percentage, u32 for a count),
    //  - a target (a hash: creature type, armor material, gathering kind) and the value (i64).
    // Percentages that are not statuses count 10,000 per percent: arrows 1,000,000 at the top level is 100%.
    private static JsonObject? Line(ClientRecord buff, int level, Dictionary<uint, ClientRecord> statuses, Dictionary<string, Dictionary<uint, string>> patterns)
    {
        var span = buff.Bytes.Span; var korean = patterns["ko"]; var ids = new List<int>(); var lines = new List<uint>();
        for (int p = buff.Prefix; p + 4 <= span.Length; p++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(span[p..]) is > 1_000_000 and var id && korean.ContainsKey(id)) { ids.Add(p); lines.Add(id); }
        if (ids.Count == 0 || Named(korean[lines[0]])) return null;
        uint[] named = lines.Distinct().ToArray();
        // A line without a value needs no level: some single-level buffs (the immunities) lay out their label differently.
        if (named.Length == 1 && !Param.IsMatch(korean[named[0]])) return Localized(named[0], null, patterns);
        int label = FirstLabel(buff);
        if (label < 0) return null;
        // -48 counts the levels and -44 the entries (BuffValue's count): some buffs give each level a second entry, and the line
        // sits on one of the two.
        int count = BinaryPrimitives.ReadInt32LittleEndian(span[(label - 48)..]), first = BinaryPrimitives.ReadInt32LittleEndian(span[(label - 40)..]);
        if (count != ids.Count || level - first is < 0 || level - first >= count) throw new InvalidDataException($"Unexpected lines in {buff.Name}: {count} levels from {first}, {ids.Count} lines, level {level}, label at {label - buff.Prefix}.");
        int at = ids[level - first]; uint line = lines[level - first];
        string template = korean[line];
        if (Named(template)) return null;
        string? value = null;
        if (Param.IsMatch(template))
        {
            if (at + 66 > span.Length || BinaryPrimitives.ReadInt32LittleEndian(span[(at + 56)..]) != 3 || span[at + 60] != 0)
                throw new InvalidDataException($"No value after the line of {buff.Name}.");
            int m = at + 61; bool percent = template.Contains('%'); double amount;
            uint head = BinaryPrimitives.ReadUInt32LittleEndian(span[m..]);
            // The last level can end the record right after a count.
            long tagged = m + 9 <= span.Length ? BinaryPrimitives.ReadInt64LittleEndian(span[(m + 1)..]) : -1;
            long targeted = m + 12 <= span.Length ? BinaryPrimitives.ReadInt64LittleEndian(span[(m + 4)..]) : -1;
            bool leveled = template.Contains("Lv{");
            if (statuses.TryGetValue(head, out var status))
                amount = leveled ? span[m + 4] : Amount(status, targeted)[1].Item2.GetValue<double>();
            else if (span[m] < 0x20 && (percent ? tagged is > 0 and < uint.MaxValue : BinaryPrimitives.ReadUInt32LittleEndian(span[(m + 1)..]) is > 0 and < 1000))
                amount = percent ? tagged / 10_000.0 : BinaryPrimitives.ReadUInt32LittleEndian(span[(m + 1)..]);
            else if (leveled && span[m + 4] > 0) amount = span[m + 4];
            else if (targeted is > 0 and < uint.MaxValue) amount = percent ? targeted / 10_000.0 : targeted;
            else throw new InvalidDataException($"No value of {buff.Name} level {level}.");
            value = Math.Round(amount, 3).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }
        return Localized(line, value, patterns);
    }

    private static JsonObject? Localized(uint line, string? value, Dictionary<string, Dictionary<uint, string>> patterns)
    {
        var result = new JsonObject();
        foreach (var (language, lines) in patterns)
        {
            if (!lines.TryGetValue(line, out var text)) continue;
            if (value is not null)
            {
                string local = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture).ToString("0.###", System.Globalization.CultureInfo.GetCultureInfo(language));
                text = Param.Replace(text, local);
            }
            if (!text.Contains('{')) result[language] = text.Trim();
        }
        return result.ContainsKey("en") && result.ContainsKey("ko") ? result : null;
    }

    private static bool Named(string line) => line.Contains("{Key:") || line.TrimEnd().EndsWith(')');

    // The first level's Korean label (u32 length, UTF-8), which the level and entry counts precede.
    private static int FirstLabel(ClientRecord buff)
    {
        var span = buff.Bytes.Span;
        for (int p = buff.Prefix + 48; p + 4 <= span.Length; p++)
            if (BinaryPrimitives.ReadInt32LittleEndian(span[p..]) is > 3 and < 80 and var length && p + 4 + length <= span.Length &&
                System.Text.Encoding.UTF8.GetString(span.Slice(p + 4, length)).Any(c => c is >= '가' and <= '힣'))
                return p;
        return -1;
    }

    private static int? Element(ClientRecord record)
    {
        var span = record.Bytes.Span; int? found = null;
        for (int p = record.Prefix; p + 13 <= span.Length; p++)
            if (span[p] == 1 && BinaryPrimitives.ReadInt32LittleEndian(span[(p + 1)..]) == 1 && BinaryPrimitives.ReadUInt32LittleEndian(span[(p + 5)..]) is >= 8037 and <= 8039)
                found = found is null ? BinaryPrimitives.ReadInt32LittleEndian(span[(p + 9)..]) : throw new InvalidDataException($"{record.Name} names several elements.");
        return found;
    }
}
