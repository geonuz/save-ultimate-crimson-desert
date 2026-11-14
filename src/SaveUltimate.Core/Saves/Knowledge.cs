
namespace SaveUltimate.Core.Saves;

public sealed record KnowledgeGroup(uint Key, string Name, string NameKo, uint[] Children, uint[] Members);
public sealed record KnowledgeCatalog(uint[] Roots, KnowledgeGroup[] Groups, Dictionary<uint, uint[]> ItemPaths, Dictionary<uint, uint> LevelCounts)
{
    /// <summary>Character key → the knowledge entry that describes it (description and journal picture).</summary>
    public Dictionary<uint, uint> Characters { get; init; } = [];
    public static KnowledgeCatalog Shared { get; } = Load();
    private static KnowledgeCatalog Load()
    {
        var catalog = Embedded.Load<KnowledgeCatalog>("Knowledge.json");
        var groups = catalog.Groups.Select(g => g.Key).ToHashSet();
        if (catalog.ItemPaths.Any(p => p.Value.Length == 0 || p.Value.Any(g => !groups.Contains(g)) ||
            catalog.LevelCounts.GetValueOrDefault(p.Key) is < 1 or > 255))
            throw new InvalidDataException("Incomplete knowledge catalog.");
        return catalog;
    }
}

// LearnMaximum raises learned and unlearned keys to their last level; Maximum raises only learned keys.
public enum KnowledgeAction { Learn, Unlearn, Level, Maximum, AllMaximum, LearnMaximum }

public sealed partial class SaveDocument
{
    public IReadOnlyDictionary<uint, uint> ReadKnowledge() => KnowledgeLevels(blob, SaveRecords.Read(blob, Layout, "KnowledgeSaveData"));

    internal static Dictionary<uint, uint> KnowledgeLevels(byte[] raw, SaveObject block)
    {
        var result = new Dictionary<uint, uint>();
        foreach (var row in block.Fields.Named("_list").Elements("KnowledgeElementSaveData"))
        {
            uint key = checked((uint)row.ChildFields.Named("_key").Scalar(raw, "KnowledgeKey", 4));
            uint level = checked((uint)row.ChildFields.Named("_level").Scalar(raw, "TLevel", 4));
            if (key == 0 || !result.TryAdd(key, level)) throw new InvalidDataException("Duplicate or invalid knowledge record.");
        }
        return result;
    }

    public (IReadOnlyDictionary<uint, uint> Levels, SkillState Skills) EditKnowledge(IEnumerable<uint> selection, KnowledgeAction action, uint level = 1)
    {
        var catalog = KnowledgeCatalog.Shared;
        var dlc = DlcContent.Shared.Knowledge;
        uint[] keys = action == KnowledgeAction.AllMaximum ? catalog.ItemPaths.Keys.Where(k => !DlcContent.Hidden || !dlc.Contains(k)).ToArray() : selection.Distinct().ToArray();
        if (!Enum.IsDefined(action) || keys.Any(k => !catalog.ItemPaths.ContainsKey(k)))
            throw new InvalidOperationException("Unsupported knowledge.");
        DlcContent.Refuse(action != KnowledgeAction.Unlearn && keys.Any(dlc.Contains));
        return EditKnowledgeKeys(keys, action, level);
    }

    private (IReadOnlyDictionary<uint, uint> Levels, SkillState Skills) EditKnowledgeKeys(uint[] keys, KnowledgeAction action, uint level)
    {
        var catalog = KnowledgeCatalog.Shared;
        var block = SaveRecords.Read(blob, Layout, "KnowledgeSaveData");
        var before = KnowledgeLevels(blob, block);
        if (action == KnowledgeAction.Level && (keys.Length != 1 || level < 1 ||
            level > catalog.LevelCounts[keys[0]] || before.GetValueOrDefault(keys[0]) == 0 || before.GetValueOrDefault(keys[0]) > catalog.LevelCounts[keys[0]]))
            throw new InvalidOperationException("Select a supported level of one learned knowledge entry.");
        var changes = keys.Where(k => action switch
        {
            KnowledgeAction.Learn => before.GetValueOrDefault(k) == 0,
            KnowledgeAction.Unlearn or KnowledgeAction.Maximum => before.GetValueOrDefault(k) > 0,
            KnowledgeAction.LearnMaximum => before.GetValueOrDefault(k) < catalog.LevelCounts[k],
            _ => true
        }).ToDictionary(k => k, k => action switch
        {
            KnowledgeAction.Learn => 1u, KnowledgeAction.Unlearn => 0u,
            KnowledgeAction.Level => level, _ => catalog.LevelCounts[k]
        });
        foreach (uint key in changes.Keys.Where(k => before.TryGetValue(k, out uint old) && old == changes[k]).ToArray())
            changes.Remove(key);
        if (changes.Count == 0) return (before, SkillState.Read(blob, block));
        var list = block.Fields.Named("_list");
        var recorded = SkillState.Read(blob, block).Follow;
        var paths = NewLearnPaths(block, before, changes, recorded);
        if (paths.Count == 0 && changes.Keys.All(before.ContainsKey))
        {
            var patches = list.Elements("KnowledgeElementSaveData").Where(r => changes.ContainsKey((uint)r.ChildFields.Named("_key").Scalar(blob, "KnowledgeKey", 4)))
                .Select(r => CreatePatch(r.ChildFields.Named("_level").StartOffset,
                    BitConverter.GetBytes(changes[(uint)r.ChildFields.Named("_key").Scalar(blob, "KnowledgeKey", 4)]))).ToArray();
            byte[] fixedCandidate = CopyForEdit();
            foreach (var patch in patches) patch.After.CopyTo(fixedCandidate, patch.Offset);
            // Validate dependent skills before committing.
            var levels = KnowledgeLevels(fixedCandidate, block);
            var skills = SkillState.Read(fixedCandidate, block);
            Edit(patches);
            return (levels, skills);
        }
        byte[] candidate = RecordLists.Rewrite(blob, Layout, EditBuffer, KnowledgeEdits(block, before, changes, paths).ToArray());
        (IReadOnlyDictionary<uint, uint> Levels, SkillState Skills) result = default;
        Transact(candidate, ["KnowledgeSaveData"], (layout, items) =>
        {
            var current = SaveRecords.Read(candidate, layout, "KnowledgeSaveData");
            var after = KnowledgeLevels(candidate, current);
            var expected = new Dictionary<uint, uint>(before);
            foreach (var p in changes) expected[p.Key] = p.Value;
            var skills = SkillState.Read(candidate, current);
            var expectedPaths = new Dictionary<uint, uint>(recorded);
            foreach (var p in paths) expectedPaths[p.Key] = p.Value;
            result = (after, skills);
            if (after.Count != expected.Count || expected.Any(p => !after.TryGetValue(p.Key, out uint value) || value != p.Value) ||
                skills.Follow.Count != expectedPaths.Count || expectedPaths.Any(p => skills.Follow.GetValueOrDefault(p.Key, uint.MaxValue) != p.Value))
                throw new InvalidDataException("Knowledge edit verification failed.");
        });
        return result;
    }
    // The game records how a knowledge was first learned; newly learned knowledge without a record takes the one most players
    // hold. A save with no record yet has no list to add to.
    private static Dictionary<uint, uint> NewLearnPaths(SaveObject block, IReadOnlyDictionary<uint, uint> before, Dictionary<uint, uint> changes, IReadOnlyDictionary<uint, uint> recorded)
        => !block.Fields.Named("_learnedFollowLearnKnowledge").Present ? [] : changes
            .Where(p => p.Value > 0 && before.GetValueOrDefault(p.Key) == 0 && !recorded.ContainsKey(p.Key))
            .Select(p => (p.Key, From: LearnPathCatalog.Shared.Choose(p.Key))).Where(p => p.From is not null).ToDictionary(p => p.Key, p => p.From!.Value);

    // Levels of existing rows, new rows as the game writes a knowledge it teaches (the level, the current field time and a new
    // mark), and the learn-from records.
    private List<ListEdit> KnowledgeEdits(SaveObject block, IReadOnlyDictionary<uint, uint> before, Dictionary<uint, uint> changes, Dictionary<uint, uint> paths)
    {
        ulong now = changes.Keys.Any(k => !before.ContainsKey(k)) ? FieldTime() : 0;
        var type = Layout.Types.Single(t => t.Name == "KnowledgeElementSaveData");
        if (!type.Is(("_key", "KnowledgeKey", 0, 4), ("_level", "TLevel", 0, 4), ("_learnedFieldTime", "uint64", 0, 8), ("_isNewMark", "bool", 0, 1)))
            throw new InvalidDataException("Unsupported knowledge insertion layout.");
        var edits = new List<ListEdit>();
        if (changes.Count > 0)
            edits.Add(new(block.Fields.Named("_list"), "KnowledgeElementSaveData", "_key", changes.ToDictionary(p => p.Key, p => before.ContainsKey(p.Key)
                ? new Dictionary<string, ScalarValue> { ["_key"] = new("KnowledgeKey", 4, p.Key), ["_level"] = new("TLevel", 4, p.Value) }
                : new Dictionary<string, ScalarValue>
                {
                    ["_key"] = new("KnowledgeKey", 4, p.Key), ["_level"] = new("TLevel", 4, p.Value),
                    ["_learnedFieldTime"] = new("uint64", 8, now), ["_isNewMark"] = new("bool", 1, 1)
                })));
        if (paths.Count > 0)
            edits.Add(new(block.Fields.Named("_learnedFollowLearnKnowledge"), "FollowLearnElementSaveData", "_knowledgeKey", paths.ToDictionary(p => p.Key, p => new Dictionary<string, ScalarValue>
                { ["_knowledgeKey"] = new("KnowledgeKey", 4, p.Key), ["_learnFromTypeRaw"] = new("uint8", 1, p.Value) })));
        return edits;
    }
}
