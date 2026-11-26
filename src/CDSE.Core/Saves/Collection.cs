using System.Buffers.Binary;

namespace CDSE.Core.Saves;

/// <summary>A knowledge entry the game teaches with a collection item, and the learn path it records (Read, Zoom or None).</summary>
public sealed record CollectionKnowledge(uint Key, uint From);
/// <summary>An item the collection box can hold (CollectionCatalogBuilder). Base items exist without the DLC. Quest items go
/// to the quest bag when picked up.</summary>
public sealed record CollectionEntry(uint Key, bool Base, bool Quest, CollectionKnowledge[] Knowledge);
public sealed record CollectionCatalog(CollectionEntry[] Items)
{
    public static CollectionCatalog Shared { get; } = Embedded.Load<CollectionCatalog>("Collection.json");
}

/// <summary>Collected: in the box. Elsewhere: held in another bag (quest items, bought maps), so filling would duplicate it.</summary>
public enum CollectionStatus { Missing, Collected, Elsewhere }
/// <summary>Important: a quest item in the box that the save does not list as moved out of the quest bag, which the game shows
/// as an important item that cannot be stored again once taken out. Filling it lists it.</summary>
public sealed record CollectionState(CollectionEntry Entry, CollectionStatus Status, bool Learned, bool Important = false);

public sealed partial class SaveDocument
{
    private const ushort CollectionBag = 18;

    /// <summary>Every item the box can hold (base items only while the DLC is hidden), in the game's order.</summary>
    public CollectionState[] ReadCollection()
    {
        var levels = ReadKnowledge();
        var important = ImportantInBox([]).ToHashSet();
        var held = Items.GroupBy(i => i.Key).ToDictionary(g => g.Key, g => g.Any(i => i.Source == ItemSource.Inventory && i.Bag == CollectionBag));
        return CollectionCatalog.Shared.Items.Where(e => e.Base || !DlcContent.Hidden).Select(e => new CollectionState(e,
            !held.TryGetValue(e.Key, out bool inBox) ? CollectionStatus.Missing : inBox ? CollectionStatus.Collected : CollectionStatus.Elsewhere,
            e.Knowledge.All(k => levels.GetValueOrDefault(k.Key) > 0), important.Contains(e.Key))).ToArray();
    }


    /// <summary>Puts the missing ones of <paramref name="keys"/> in the collection box, and with <paramref name="learn"/> also
    /// teaches the knowledge of every chosen item not held elsewhere, recording the path reading it would.</summary>
    public CollectionState[] FillCollection(IEnumerable<uint> keys, bool learn)
    {
        var chosen = keys.ToHashSet();
        var before = ReadCollection();
        var states = before.Where(s => chosen.Contains(s.Entry.Key) && s.Status != CollectionStatus.Elsewhere).ToArray();
        var add = states.Where(s => s.Status == CollectionStatus.Missing).Select(s => s.Entry.Key).ToArray();
        DlcContent.Refuse(states.Any(s => !s.Entry.Base));
        var levels = ReadKnowledge();
        // Reading records Read or Zoom. The one players hold wins over the item's inspect action: paper notices and wanted
        // posters with the zoom action are Read in every save that has them.
        var teach = learn ? states.SelectMany(s => s.Entry.Knowledge).Where(k => levels.GetValueOrDefault(k.Key) == 0).DistinctBy(k => k.Key)
            .Select(k => k.From == (uint)LearnFrom.None ? k : k with { From = LearnPathCatalog.Shared.Among(k.Key, LearnFrom.Read, LearnFrom.Zoom) ?? k.From }).ToArray() : [];
        DlcContent.Refuse(teach.Any(k => DlcContent.Shared.Knowledge.Contains(k.Key)));
        uint[] release = ImportantInBox(add);
        if (add.Length == 0 && teach.Length == 0 && release.Length == 0) return before;

        var regions = new List<(int Start, int End, Func<int, byte[]> Encode)>();
        if (add.Length > 0)
        {
            if (Bag(CollectionBag) is null) throw new InvalidOperationException("This save has no collection box yet. Play until the collection box opens first.");
            ulong[] numbers = NewNumbers(add.Length);
            regions.AddRange(AppendRegions(CollectionBag, add.Select((key, i) => With(PlainRow(key, 1), ("_itemNo", Bytes(numbers[i], 8)))).ToArray()));
        }
        if (release.Length > 0) regions.AddRange(MovedRegions(release));
        if (teach.Length > 0)
        {
            var block = SaveRecords.Read(blob, Layout, "KnowledgeSaveData");
            var follow = SkillState.Read(blob, block).Follow;
            // A save with no learn path at all has no list to add to; the knowledge is still learned.
            var paths = !block.Fields.Named("_learnedFollowLearnKnowledge").Present ? []
                : teach.Where(k => !follow.ContainsKey(k.Key)).ToDictionary(k => k.Key, k => k.From);
            regions.AddRange(RecordLists.Regions(blob, Layout, KnowledgeEdits(block, levels, teach.ToDictionary(k => k.Key, _ => 1u), paths)));
        }
        byte[] candidate = RecordLists.RewriteRegions(blob, EditBuffer, regions);
        string[] changed = [.. add.Length + release.Length > 0 ? ["InventorySaveData"] : Array.Empty<string>(), .. teach.Length > 0 ? ["KnowledgeSaveData"] : Array.Empty<string>()];
        Transact(candidate, changed, (layout, items) =>
        {
            var box = items.Where(i => i.Source == ItemSource.Inventory && i.Bag == CollectionBag).Select(i => i.Key).ToList();
            var learned = KnowledgeLevels(candidate, SaveRecords.Read(candidate, layout, "KnowledgeSaveData"));
            var after = MovedKeys(candidate, layout);
            if (add.Any(k => box.Count(b => b == k) != 1) || teach.Any(k => learned.GetValueOrDefault(k.Key) != 1) || release.Any(k => !after.Contains(k)))
                throw new InvalidDataException("Collection verification failed.");
        });
        return ReadCollection();
    }

}
