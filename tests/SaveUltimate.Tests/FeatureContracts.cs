using System.IO;
using SaveUltimate.Core.Catalogs;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.Tests;

// An edit a save allows and what must be true after it, whatever the save holds.
internal sealed record ContractEdit(Action Apply, Action Verify);

// Find returns null when the save holds nothing for the feature. Touches names the sections the edit may change; every other
// section must read the same afterwards. Slow features run only on the standard saves ContractTests picks.
internal sealed record FeatureContract(string Name, string[] Touches, Func<SaveDocument, ContractEdit?> Find, bool Edits = true, bool Slow = false);

internal static class FeatureContracts
{
    // What each page reads, one line per record so a difference names the record. Map state is left out: reading it is slow
    // and its knowledge and progress are already sections here.
    internal static readonly Dictionary<string, Func<SaveDocument, string[]>> Sections = new()
    {
        ["Items"] = ItemLines,
        ["Knowledge"] = doc => doc.ReadKnowledge().Select(p => $"{p.Key}={p.Value}").ToArray(),
        ["Skills"] = doc =>
        {
            var state = doc.ReadSkills();
            return state.Levels.Select(p => $"level {p.Key}={p.Value}").Concat(state.Paid.Select(p => $"paid {p.Key}={p.Value}"))
                .Concat(state.Points.Select(p => $"points {p.Key}={p.Value}")).Concat(state.Follow.Select(p => $"follow {p.Key}={p.Value}")).ToArray();
        },
        ["Challenges"] = doc =>
        {
            var state = doc.ReadChallenges();
            return state.Quest.Select(p => $"quest {p.Key}={p.Value}").Concat(state.Mission.Select(p => $"mission {p.Key}={p.Value}")).ToArray();
        },
        ["Fishing"] = doc => doc.ReadFishing().Select(r => $"{r.Fish.Key} level={r.Level} size={r.Size} available={r.RecordTypeAvailable}").ToArray(),
        ["Companions"] = doc => doc.ReadCompanions().Select(c => $"{c.Number} {c.Key} {c.Name}").ToArray(),
        ["Friendship"] = doc => doc.ReadFriendship().Select(FriendshipLine).ToArray(),
    };

    private static readonly string[] KnowledgeSections = ["Knowledge", "Skills", "Fishing"];

    internal static string[] ItemLines(SaveDocument doc) => doc.Items.Select(i =>
        $"{i.Source} no={i.Number} key={i.Key} qty={i.Quantity} enchant={i.EnchantLevel} unlocked={i.Sockets.Unlocked} gems={string.Join(',', i.Sockets.Slots.Select(s => s.HasGem ? s.Key : 0))}").ToArray();

    private static string FriendshipLine(NpcFriendship r)
        => $"{r.Key} value={r.Value} {string.Join(' ', r.Affinity.Select(a => $"{a.Playable}={a.Value}"))}";

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }

    private static void OnlyLines(string[] before, string[] after, Func<string, bool> allowed, string what)
    {
        var changed = before.Except(after).Concat(after.Except(before)).Where(l => !allowed(l)).ToArray();
        Require(changed.Length == 0, $"{what}: {changed.Length} other record(s) changed, such as {changed.FirstOrDefault()}");
    }

    private static InventoryItem[] Records(SaveDocument doc, ulong number) => doc.Items.Where(i => i.Number == number).ToArray();

    // Picks the first matching numbered item; afterwards only that item's records may differ.
    private static ContractEdit? ItemEdit(SaveDocument doc, Func<InventoryItem, bool> match, Action<InventoryItem> apply, Action<InventoryItem, InventoryItem[]> check)
    {
        if (doc.Items.FirstOrDefault(i => i.Number > 0 && match(i)) is not { } item) return null;
        var before = ItemLines(doc);
        return new(() => apply(item), () =>
        {
            OnlyLines(before, ItemLines(doc), l => l.Contains($" no={item.Number} "), $"Item {item.Number}");
            check(item, Records(doc, item.Number));
        });
    }

    private static bool Worn(InventoryItem i) => i.Source is ItemSource.Inventory or ItemSource.Equipment;

    private static ContractEdit? Swap(SaveDocument doc)
    {
        const uint replacement = 200919;
        byte[] before = doc.blob;
        return ItemEdit(doc, i => i.Source == ItemSource.Vendor && i.Key != replacement && i.CanBecome(replacement), i => doc.SwapVendorItem(i, replacement), (item, records) =>
        {
            var sold = records.Single(r => r.Source == ItemSource.Vendor);
            Require(sold.Key == replacement && sold.Quantity == item.Quantity && sold.EnchantLevel == item.EnchantLevel, "The swap changed more than the sold item's key.");
            // Same size, and every differing byte lies in a u32 that held the old key and now holds the new one.
            byte[] after = doc.blob;
            Require(before.Length == after.Length, "The swap changed the save's size.");
            for (int i = 0; i < before.Length; i++)
                if (before[i] != after[i])
                    Require(Enumerable.Range(i - 3, 4).Any(start => start >= 0 && start + 4 <= before.Length &&
                        BitConverter.ToUInt32(before, start) == item.Key && BitConverter.ToUInt32(after, start) == replacement), $"The swap changed byte {i}, outside the item key.");
        });
    }

    private static ContractEdit? SocketGem(SaveDocument doc) => ItemEdit(doc,
        i => Worn(i) && i.Sockets.Error is null && i.Sockets.Unlocked <= ItemRules.Sockets(i.Key) && !i.Sockets.Slots.Skip(ItemRules.Sockets(i.Key)).Any(s => s.HasGem) && i.Sockets.Slots.Take(i.Sockets.Unlocked).Any(s => !s.HasGem),
        item =>
        {
            var target = doc.SocketTarget(item);
            var slots = target.Sockets.Slots.Take(target.Sockets.Unlocked).ToArray();
            int empty = Array.FindIndex(slots, s => !s.HasGem);
            doc.SetSockets(item, slots.Select((s, n) => n == empty ? 1002785u : s.HasGem ? s.Key : 0));
        },
        (item, records) => Require(records.Any(r => r.Sockets.Slots.Count(s => s.HasGem) == item.Sockets.Slots.Count(s => s.HasGem) + 1), "The gem was not inserted."));

    private static ContractEdit? Knowledge(SaveDocument doc, Func<uint, uint, bool> match, KnowledgeAction action, Func<uint, uint> expected)
    {
        var levels = doc.ReadKnowledge();
        uint key = KnowledgeCatalog.Shared.ItemPaths.Keys.Order().FirstOrDefault(k => match(k, levels.GetValueOrDefault(k)));
        if (key == 0) return null;
        uint target = expected(levels.GetValueOrDefault(key));
        return new(() => doc.EditKnowledge([key], action, target), () =>
        {
            var after = doc.ReadKnowledge();
            Require(after.GetValueOrDefault(key) == target, $"Knowledge {key} is {after.GetValueOrDefault(key)}, not {target}.");
            OnlyLines(levels.Select(p => $"{p.Key}={p.Value}").ToArray(), after.Select(p => $"{p.Key}={p.Value}").ToArray(), l => l.StartsWith($"{key}="), $"Knowledge {key}");
        });
    }

    private static ContractEdit? AllKnowledge(SaveDocument doc) => new(() => doc.EditKnowledge([], KnowledgeAction.AllMaximum), () =>
    {
        var after = doc.ReadKnowledge();
        var low = KnowledgeCatalog.Shared.LevelCounts.Where(p => KnowledgeCatalog.Shared.ItemPaths.ContainsKey(p.Key) && after.GetValueOrDefault(p.Key) < p.Value).ToArray();
        Require(low.Length == 0, $"{low.Length} entries are below their maximum, such as {low.FirstOrDefault().Key}.");
    });

    private static ContractEdit? Skill(SaveDocument doc, bool watch)
    {
        var state = doc.ReadSkills();
        foreach (var (key, definition) in SkillCatalog.Shared.Skills.OrderBy(p => p.Key))
        {
            uint target = watch ? 1 : state.Level(key) + 1;
            if (target > definition.Levels.Length) continue;
            try { SkillPlan.Create(state, key, target, watch); }
            catch (InvalidOperationException) { continue; }
            return new(() => doc.LearnSkill(key, target, watch), () =>
            {
                var after = doc.ReadSkills();
                Require(after.Level(key) >= target, $"Skill {key} is level {after.Level(key)}, not {target}.");
                if (watch) Require(after.Observed(key), $"Skill {key} is not recorded as observed.");
            });
        }
        return null;
    }

    private static ContractEdit? Fishing(SaveDocument doc, bool existing)
    {
        var before = doc.ReadFishing();
        if (before.FirstOrDefault(r => r.Fish.RecordSupported && r.Level > 0 && r.RecordTypeAvailable && r.Size.HasValue == existing) is not { } record) return null;
        const float size = 12.5f;
        return new(() => doc.SetFishingRecord(record.Fish.Key, size), () =>
        {
            var after = doc.ReadFishing();
            Require(after.Single(r => r.Fish.Key == record.Fish.Key).Size == size, "The fish record does not hold the new size.");
            Require(after.Where(r => r.Fish.Key != record.Fish.Key).SequenceEqual(before.Where(r => r.Fish.Key != record.Fish.Key)), "Another fish changed.");
        });
    }

    private static ContractEdit? Challenges(SaveDocument doc)
    {
        var state = doc.ReadChallenges();
        uint[] open = ChallengeCatalog.Completion.Roots.Where(r => state.Mission.GetValueOrDefault(r)?.State != 5).ToArray();
        if (open.Length == 0) return null;
        return new(() => doc.CompleteChallenges(open), () =>
        {
            var after = doc.ReadChallenges();
            var left = open.Where(r => after.Mission.GetValueOrDefault(r)?.State != 5).ToArray();
            Require(left.Length == 0, $"{left.Length} challenges stayed open, such as {left.FirstOrDefault()}.");
        });
    }

    private static ContractEdit? MapGroups(SaveDocument doc, IEnumerable<int> groups)
    {
        var before = doc.ReadKnowledge();
        int[] indices = groups.ToArray();
        return new(() => { foreach (int i in indices) doc.ActivateMap(i); }, () =>
        {
            var after = doc.ReadKnowledge();
            Require(before.All(p => after.GetValueOrDefault(p.Key) >= p.Value), "A map group lowered a knowledge level.");
            string activated = doc.BlobSha256;
            doc.ActivateMap(indices[0]);
            Require(doc.BlobSha256 == activated, "Activating a map group twice changed the save again.");
        });
    }

    // Groups of one mode write the same records, so one group per mode shows the shape on any save.
    private static IEnumerable<int> OneGroupPerMode() => Enumerable.Range(0, MapAction.All.Length).GroupBy(i => MapAction.All[i].Mode).Select(g => g.First());

    private static ContractEdit? MapLocations(SaveDocument doc)
    {
        int index = Array.FindIndex(MapAction.All, a => a.Mode != "icons");
        return index < 0 ? null : new(() => doc.LearnMapLocations(index), () =>
        {
            var after = doc.ReadKnowledge();
            Require(MapAction.All[index].Keys.All(k => after.GetValueOrDefault(k) > 0), "A map location stayed unlearned.");
        });
    }

    private static ContractEdit? Rename(SaveDocument doc)
    {
        var before = doc.ReadCompanions();
        if (before.FirstOrDefault(c => c.Editable) is not { } companion) return null;
        const string name = "Fixture";
        return new(() => doc.RenameCompanions([companion], name), () =>
        {
            var after = doc.ReadCompanions();
            Require(after.Select(c => c.Number == companion.Number ? c with { Name = companion.Name } : c).SequenceEqual(before) && after.Single(c => c.Number == companion.Number).Name == name, "The rename changed more than one name.");
        });
    }

    private static ContractEdit? PetOrder(SaveDocument doc)
    {
        var before = doc.ReadCompanions();
        if (before.Where(c => c.Group == "pet").Take(2).ToArray() is not [var first, var second]) return null;
        return new(() => doc.SwapCompanions(first, second), () =>
        {
            var after = doc.ReadCompanions();
            Require(after.Single(c => c.Number == first.Number).Key == second.Key && after.Single(c => c.Number == second.Number).Key == first.Key, "The pets did not trade places.");
            Require(after.Where(c => c.Number != first.Number && c.Number != second.Number).SequenceEqual(before.Where(c => c.Number != first.Number && c.Number != second.Number)), "The swap changed another companion.");
        });
    }

    private static ContractEdit? GreyZero(SaveDocument doc)
    {
        var before = doc.ReadCompanions();
        if (!before.Any(c => c.Key == SaveDocument.RobotWorker)) return null;
        return new(() =>
        {
            doc.RemoveRobotWorker(doc.AddRobotWorker().Last(c => c.Key == SaveDocument.RobotWorker));
            doc.AddRobotWorker();
        }, () =>
        {
            var after = doc.ReadCompanions();
            Require(after.Length == before.Length + 1 && after.Count(c => c.Key == SaveDocument.RobotWorker) == before.Count(c => c.Key == SaveDocument.RobotWorker) + 1, "One add and one remove left the wrong roster.");
            Require(before.All(after.Contains), "A companion other than the added Grey-0 changed.");
        });
    }

    private static ContractEdit? Trust(SaveDocument doc)
    {
        var before = doc.ReadFriendship();
        if (before.FirstOrDefault(r => r.Editable) is not { } row) return null;
        int value = row.Value == 100 ? 95 : (int)Math.Min(100, row.Value!.Value + 5);
        return new(() => doc.SetFriendship(row.Key, row.Value!.Value, value), () =>
        {
            var after = doc.ReadFriendship();
            Require(after.Single(r => r.Key == row.Key).Value == (ulong)value, "Trust does not hold the new value.");
            OnlyLines(before.Select(FriendshipLine).ToArray(), after.Select(FriendshipLine).ToArray(), l => l.StartsWith($"{row.Key} "), $"NPC {row.Key}");
        });
    }

    private static ContractEdit? Bond(SaveDocument doc)
    {
        var before = doc.ReadFriendship();
        foreach (var row in before)
            if (row.Affinity.FirstOrDefault(a => a.Editable && a.Value < 100) is { } bond)
            {
                int value = (int)Math.Min(100, bond.Value!.Value + 5);
                return new(() => doc.SetAffinity(row.Key, bond.Playable, bond.Value!.Value, value), () =>
                {
                    var after = doc.ReadFriendship();
                    Require(after.Single(r => r.Key == row.Key).Affinity.Single(a => a.Playable == bond.Playable).Value == (ulong)value, "The bond does not hold the new value.");
                    OnlyLines(before.Select(FriendshipLine).ToArray(), after.Select(FriendshipLine).ToArray(), l => l.StartsWith($"{row.Key} "), $"NPC {row.Key}");
                });
            }
        return null;
    }

    internal static readonly FeatureContract[] All =
    [
        new("Read every page", [], doc => new(() => { doc.ReadSkills(); doc.ReadKnowledge(); doc.ReadChallenges(); doc.ReadFishing(); doc.ReadCompanionDetails(); doc.ReadFriendship(); doc.ReadMap(); }, () => { }), Edits: false),
        new("Item quantity", ["Items"], doc => ItemEdit(doc, i => i.Source == ItemSource.Inventory && ItemRules.Stack(i.Key) > i.Quantity, i => doc.SetQuantity(i, i.Quantity + 1),
            (item, records) => Require(records.Single(r => r.Source == item.Source).Quantity == item.Quantity + 1, "The quantity did not rise by one."))),
        new("Item delete", ["Items"], doc => ItemEdit(doc, i => i.Source == ItemSource.Inventory, i => doc.Delete([i]),
            (item, records) => Require(!records.Any(r => r.Source == item.Source), "The deleted item is still listed."))),
        new("Enchant", ["Items"], doc => ItemEdit(doc, i => i.EnchantLevel < i.MaxEnchant, i => doc.SetEnchant([i], (ushort)(i.EnchantLevel!.Value + 1)),
            (item, records) => Require(records.Single(r => r.Source == item.Source).EnchantLevel == item.EnchantLevel + 1, "The enchant level did not rise by one."))),
        new("Socket unlock", ["Items"], doc => ItemEdit(doc, i => Worn(i) && i.Sockets.Error is null && i.Sockets.Unlocked < ItemRules.Sockets(i.Key), i => doc.SetUnlockedSockets(i, i.Sockets.Unlocked + 1),
            (item, records) => Require(records.Any(r => r.Sockets.Unlocked == item.Sockets.Unlocked + 1), "The socket count did not rise by one."))),
        new("Socket gem", ["Items"], SocketGem),
        new("Item swap", ["Items"], Swap),
        new("Knowledge learn", KnowledgeSections, doc => Knowledge(doc, (_, level) => level == 0, KnowledgeAction.Learn, _ => 1)),
        new("Knowledge unlearn", KnowledgeSections, doc => Knowledge(doc, (_, level) => level > 0, KnowledgeAction.Unlearn, _ => 0)),
        new("Knowledge level", KnowledgeSections, doc => Knowledge(doc, (key, level) => level > 0 && level < KnowledgeCatalog.Shared.LevelCounts[key], KnowledgeAction.Level, level => level + 1)),
        new("Knowledge all maximum", KnowledgeSections, AllKnowledge),
        new("Challenges complete", ["Challenges"], Challenges),
        new("Fishing existing record", ["Fishing"], doc => Fishing(doc, true)),
        new("Fishing new record", ["Fishing"], doc => Fishing(doc, false)),
        new("Skill raise", ["Items", .. KnowledgeSections], doc => Skill(doc, false)),
        new("Skill observe", ["Items", .. KnowledgeSections], doc => Skill(doc, true)),
        new("Map fog", [], doc => doc.ReadMap().Fog.Full ? null : new(() => doc.RevealMap(), () => Require(doc.ReadMap().Fog.Full, "The fog is not fully revealed."))),
        new("Map one group per mode", [.. KnowledgeSections, "Challenges"], doc => MapGroups(doc, OneGroupPerMode())),
        new("Map every group", [.. KnowledgeSections, "Challenges"], doc => MapGroups(doc, Enumerable.Range(0, MapAction.All.Length)), Slow: true),
        new("Map locations", KnowledgeSections, MapLocations),
        new("Companion rename", ["Companions"], Rename),
        new("Pet order", ["Companions"], PetOrder),
        new("Grey-0 add and remove", ["Companions"], GreyZero),
        new("NPC trust", ["Friendship"], Trust),
        new("DLC bond", ["Friendship", "Challenges"], Bond),
    ];
}
