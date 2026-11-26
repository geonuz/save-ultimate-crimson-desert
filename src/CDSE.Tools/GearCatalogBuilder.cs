using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CDSE.Core.Archives;
using CDSE.Core.Catalogs;
using CDSE.Core.Saves;

namespace CDSE.Tools;

// Which parts of an item take dye, and which gear a companion can wear. An item names its models (prefabs) by symbol; a prefab
// file names its meshes, and PartPrefabDyeSlotInfo holds the dye parts of each mesh. The wearable rule is the game's own tables:
// a character's tribe and slot set, the types a slot takes, and the tribes an item has models for.
// Checked against every save (2026-11-25): 131 dyed items and 153 pairs of companion and worn item, no exception.
public static class GearCatalogBuilder
{
    private const int CharacterTribe = 142, CharacterPortrait = 192, CharacterSlots = 204;
    // Story and encounter animals, which the game gives itself.
    private static readonly Regex Story = new("MainVehicle|Boss|Quest|Unique|Marius|Summoner|Bandits");
    // The wagons and ships players own; the rest are traffic, pirates and story props.
    private static readonly Regex Owned = new(@"Wagon_TwoHorseFourWheel_Player_\d$|Player_(Small_|Big_)?ship_(Graymane|Demeniss|Delesyia|Hernand|TurtleShip|PanokSeon)$|ghost_Big_Ship_01$|Monster_ship_02$", RegexOptions.IgnoreCase);
    // Stages that run a stable, a wagon yard, a harbor or a livestock yard. They name whichever mount the player last brought there.
    private static readonly Regex Service = new(@"^(Shop_\w+_(Ship|Carriage|Stable)|\w+Store_(\w*Fence|Stable\w*)|BaseCamp_(Pailune_)?Func_\w+_(Carriage|Stable))$");
    // Slot sets of Blackstar, the ships and the A.T.A.G. Their parts are left out of the items an inventory can be given.
    private static bool PartSet(uint set) => set is 203 or 750 or >= 205 and <= 209;
    private static readonly Regex Tribe = new("^[a-z0-9_]+_(none|male|female)$"), Meshes = new(@"[A-Za-z0-9_/]+\.pac");

    public static JsonObject Build(GameFiles game)
    {
        // Pets, mounts, wagons and ships; people's slots are not edited.
        var companions = CompanionTypes.Shared.Characters.Keys.Where(k => CompanionTypes.Shared.Group(k) is "pet" or "horse" or "special" or "dedicated" or "wagon" or "ship").ToHashSet();
        var source = new ClientSource(game);
        var items = ClientTables.Records(source.Tables("iteminfo"));
        var characters = ClientTables.Records(source.Tables("characterinfo"));
        var types = ClientTables.Records(source.Tables("equiptypeinfo"));
        var symbols = new Dictionary<uint, string>();
        foreach (var (key, record) in ClientTables.Records(source.Tables("stringinfo"), emptyNames: true))
            symbols[key] = new ClientReader(record.Bytes, record.Prefix).Text().ToLowerInvariant();
        // shortcut: the item fields before the models are not decoded, so every 4 bytes are looked up; a symbol is a hash, so a
        // chance match is unlikely to name a model or tribe. Walk the fields once they are decoded.
        IEnumerable<(uint Key, string Name)> Named(ClientRecord r)
        {
            for (int i = r.Prefix; i + 4 <= r.Bytes.Length; i++)
                if (symbols.TryGetValue(BitConverter.ToUInt32(r.Bytes.Span[i..]), out var name)) yield return (BitConverter.ToUInt32(r.Bytes.Span[i..]), name);
        }
        var parts = DyeParts(source);
        var wanted = items.Values.SelectMany(Named).Select(s => s.Name).ToHashSet();
        var prefabs = new Dictionary<string, HashSet<string>>();
        foreach (var archive in game.Layers("0009"))
            foreach (string path in archive.Index.Entries.Keys.Where(k => k.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)))
            {
                string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                if (!wanted.Contains(name)) continue;
                if (!prefabs.TryGetValue(name, out var meshes)) prefabs[name] = meshes = [];
                foreach (Match m in Meshes.Matches(Encoding.Latin1.GetString(archive.Extract(path)))) meshes.Add(Path.GetFileNameWithoutExtension(m.Value).ToLowerInvariant());
            }
        var dye = new JsonObject();
        foreach (var (key, record) in items.OrderBy(p => p.Key))
        {
            var found = Enumerable.Range(0, GearCatalog.DyeParts).Select(_ => new HashSet<char>()).ToArray();
            foreach (string name in Named(record).Select(s => s.Name).Distinct())
                foreach (string mesh in prefabs.GetValueOrDefault(name, []).Append(name))
                    foreach (var (part, material) in parts.GetValueOrDefault(mesh, [])) found[part].Add(material);
            if (found.All(s => s.Count == 0)) continue;
            dye[key.ToString()] = string.Concat(found.Select(s => s.Count == 0 ? '-' : s.Count == 1 ? s.First() : s.Count == 2 && s.Remove('n') ? s.First() : 'x'));
        }

        var sets = ClientTables.Records(source.Tables("equipslotinfo")).ToDictionary(p => p.Key, p => Slots(p.Value, types));
        var mounts = new JsonObject();
        var usedSets = new SortedSet<uint>();
        foreach (uint key in companions.Order())
        {
            if (!characters.TryGetValue(key, out var record)) continue;
            uint set = ClientTables.ItemU32(record, CharacterSlots);
            if (!sets.ContainsKey(set)) continue;
            mounts[key.ToString()] = new JsonArray(ClientTables.ItemU32(record, CharacterTribe), set);
            usedSets.Add(set);
        }
        var usedTypes = usedSets.SelectMany(s => sets[s]).SelectMany(t => t).ToHashSet();
        var wearables = new JsonObject();
        foreach (var (key, record) in items.OrderBy(p => p.Key))
        {
            uint type = ClientTables.ItemU32(record, ClientTables.ItemEquipType);
            if (!usedTypes.Contains(type)) continue;
            var tribes = Named(record).Where(s => Tribe.IsMatch(s.Name)).Select(s => s.Key).Distinct().Order();
            wearables[key.ToString()] = new JsonArray(tribes.Prepend(type).Select(v => (JsonNode)v).ToArray());
        }

        // What can be made for those sets: what the game would put in the inventory, with the charges it holds.
        var tables = source.Tables("entitlementinfo");
        var partTypes = usedSets.Where(PartSet).SelectMany(s => sets[s]).SelectMany(t => t).ToHashSet();
        var parted = new JsonObject(items.Where(p => wearables.ContainsKey(p.Key.ToString()) && partTypes.Contains(ClientTables.ItemU32(p.Value, ClientTables.ItemEquipType)) && ClientTables.PickupBag(p.Value) == 2 &&
            tables.All(t => t.Body.AsSpan().IndexOf(BitConverter.GetBytes(p.Key)) < 0)).OrderBy(p => p.Key).Select(p => KeyValuePair.Create<string, JsonNode?>(p.Key.ToString(), ClientTables.ItemU32(p.Value, ClientTables.ItemCharges))));

        // What a ship or the A.T.A.G. comes with: [count] entries of 64 bytes, the item key first. The fields before are not decoded,
        // so it is the one run of items the mount can wear.
        var comes = new JsonObject();
        foreach (var (name, mount) in mounts.Where(m => PartSet(m.Value![1]!.GetValue<uint>())))
        {
            uint[] fits = wearables.Where(w => sets[mount![1]!.GetValue<uint>()].Any(t => t.Contains(w.Value![0]!.GetValue<uint>())) &&
                (w.Value!.AsArray().Count == 1 || w.Value.AsArray().Skip(1).Any(t => t!.GetValue<uint>() == mount[0]!.GetValue<uint>()))).Select(w => uint.Parse(w.Key)).ToArray();
            var record = characters[uint.Parse(name)];
            var runs = new List<uint[]>();
            for (int i = record.Prefix; i + 68 <= record.Bytes.Length; i++)
            {
                uint count = BitConverter.ToUInt32(record.Bytes.Span[i..]);
                if (count is 0 or > 12 || i + 4 + 64 * count > record.Bytes.Length) continue;
                int start = i + 4;
                uint[] run = Enumerable.Range(0, (int)count).Select(n => BitConverter.ToUInt32(record.Bytes.Span[(start + 64 * n)..])).ToArray();
                if (run.All(fits.Contains)) runs.Add(run);
            }
            if (runs.Count > 1) throw new InvalidDataException($"Character {name} lists its gear twice.");
            if (runs.Count == 1) comes[name] = new JsonArray(runs[0].Order().Select(k => (JsonNode)k).ToArray());
        }
        JsonArray Owns(string group) => new(companions.Where(k => CompanionTypes.Shared.Group(k) == group && characters.TryGetValue(k, out var r) && Owned.IsMatch(r.Name)).Order().Select(k => (JsonNode)k).ToArray());

        // One of each look: breeds repeat a list picture for every way they are dressed, and the wild animal is the plain one.
        JsonArray Addable(string group) => new(companions.Where(k => CompanionTypes.Shared.Group(k) == group && characters.TryGetValue(k, out var r) && !Story.IsMatch(r.Name))
            .GroupBy(k => ClientTables.ItemU32(characters[k], CharacterPortrait)).Where(g => symbols.TryGetValue(g.Key, out var picture) && picture.Length > 0)
            .Select(g => g.OrderByDescending(k => characters[k].Name.Contains("_Wild_")).ThenBy(k => k).First()).Order().Select(k => (JsonNode)k).ToArray());

        JsonObject Names(string file, uint member, IEnumerable<uint> keys)
        {
            var texts = ClientMetadata.Languages.ToDictionary(l => l.Key, l => source.Strings(l.Value.Name, file));
            return new(keys.Select(k => KeyValuePair.Create<string, JsonNode?>(k.ToString(), new JsonObject(texts.Select(t =>
                KeyValuePair.Create<string, JsonNode?>(t.Key, t.Value.GetValueOrDefault((((ulong)k << 32) | member).ToString(), "")))))));
        }
        return new JsonObject
        {
            ["dye"] = dye,
            ["colorGroups"] = Names("dyecolorgroup", 561, ClientTables.Records(source.Tables("dyecolorgroupinfo")).Keys),
            ["equipTypes"] = Names("equiptype", 0x2E0, usedTypes.Order()),
            ["addable"] = new JsonObject { ["horse"] = Addable("horse"), ["special"] = Addable("special"), ["wagon"] = Owns("wagon"), ["ship"] = Owns("ship") },
            ["mounts"] = mounts, ["wearables"] = wearables, ["parts"] = parted, ["comes"] = comes,
            ["services"] = new JsonArray(ClientTables.Records(source.Tables("stageinfo")).Where(p => Service.IsMatch(p.Value.Name)).Select(p => p.Key).Order().Select(k => (JsonNode)k).ToArray()),
            ["slots"] = new JsonObject(usedSets.Select(s => KeyValuePair.Create<string, JsonNode?>(s.ToString(),
                new JsonArray(sets[s].Select(t => (JsonNode)new JsonArray(t.Select(v => (JsonNode)v).ToArray())).ToArray())))),
            ["sourceSha256"] = source.Digests()
        };
    }

    // PartPrefabDyeSlotInfo by mesh and by record name: [count] parts of [name][dye part of channels R, G and B, 255 for none]
    // [material of each channel][12 flags][byte][count of variants: 3 materials, 12 flags, a byte], then the mesh path.
    private static Dictionary<string, HashSet<(int Part, char Material)>> DyeParts(ClientSource source)
    {
        var result = new Dictionary<string, HashSet<(int, char)>>();
        static char Material(string name) => name switch { "cloth" => 'c', "leather" => 'l', "metal" => 'm', "" => 'n', _ => throw new InvalidDataException("Unknown dye material.") };
        foreach (var (_, record) in ClientTables.Records(source.Tables("partprefabdyeslotinfo")))
        {
            var r = new ClientReader(record.Bytes, record.Prefix);
            var found = new HashSet<(int, char)>();
            for (int i = r.Count(256); i > 0; i--)
            {
                r.SkipText();
                byte[] channels = r.Take(3).ToArray();
                void Materials()
                {
                    foreach (byte part in channels)
                    {
                        char material = Material(r.Text());
                        if (part == 255) continue;
                        if (part >= GearCatalog.DyeParts) throw new InvalidDataException("Invalid dye part.");
                        found.Add((part, material));
                    }
                    r.Take(13);
                }
                Materials();
                for (int v = r.Count(16); v > 0; v--) Materials();
            }
            string mesh = Path.GetFileNameWithoutExtension(r.Text()).ToLowerInvariant();
            if (r.Remaining != 0) throw new InvalidDataException("Unsupported PartPrefabDyeSlotInfo layout.");
            foreach (string name in new[] { mesh, record.Name.ToLowerInvariant() }.Distinct())
                if (!result.TryAdd(name, found)) result[name] = [.. result[name], .. found];
        }
        return result;
    }

    // The equipment types each slot takes, by slot number: [count][types][hash][u16 slot] runs; the fields between are not decoded.
    private static uint[][] Slots(ClientRecord record, Dictionary<uint, ClientRecord> types)
    {
        var span = record.Bytes.Span;
        var slots = new SortedDictionary<int, uint[]>();
        for (int i = record.Prefix; i + 10 <= span.Length; i++)
        {
            uint count = BitConverter.ToUInt32(span[i..]);
            if (count is 0 or > 20 || i + 10 + 4 * (int)count > span.Length) continue;
            uint[] run = new uint[count];
            for (int j = 0; j < count; j++) run[j] = BitConverter.ToUInt32(span[(i + 4 + 4 * j)..]);
            if (!run.All(types.ContainsKey)) continue;
            slots.Add(BitConverter.ToUInt16(span[(i + 8 + 4 * (int)count)..]), run);
            i += 3 + 4 * (int)count;
        }
        return Enumerable.Range(0, slots.Count == 0 ? 0 : slots.Keys.Max() + 1).Select(i => slots.GetValueOrDefault(i, [])).ToArray();
    }
}
