using System.Text.Json;

namespace SaveUltimate.Core.Saves;

public sealed record KnowledgeGroup(uint Key, string Name, string NameKo, uint[] Children, uint[] Members);
public sealed record KnowledgeCatalog(uint[] Roots, KnowledgeGroup[] Groups, Dictionary<uint, uint[]> ItemPaths, Dictionary<uint, uint> LevelCounts)
{
    public static KnowledgeCatalog Shared { get; } = Load();
    private static KnowledgeCatalog Load()
    {
        using var stream = typeof(KnowledgeCatalog).Assembly.GetManifestResourceStream("Knowledge.json")!;
        var catalog = JsonSerializer.Deserialize<KnowledgeCatalog>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var groups = catalog.Groups.Select(g => g.Key).ToHashSet();
        if (catalog.ItemPaths.Any(p => p.Value.Length == 0 || p.Value.Any(g => !groups.Contains(g)) ||
            catalog.LevelCounts.GetValueOrDefault(p.Key) is < 1 or > 255))
            throw new InvalidDataException("Incomplete knowledge catalog.");
        return catalog;
    }
}

public enum KnowledgeAction { Learn, Unlearn, Level, Maximum, AllMaximum }

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
            if (key == 0 || !result.TryAdd(key, level)) throw new InvalidDataException("중복되거나 잘못된 지식 기록입니다.");
        }
        return result;
    }

    public (IReadOnlyDictionary<uint, uint> Levels, SkillState Skills) EditKnowledge(IEnumerable<uint> selection, KnowledgeAction action, uint level = 1)
    {
        var catalog = KnowledgeCatalog.Shared;
        uint[] keys = action == KnowledgeAction.AllMaximum ? catalog.ItemPaths.Keys.ToArray() : selection.Distinct().ToArray();
        if (!Enum.IsDefined(action) || keys.Any(k => !catalog.ItemPaths.ContainsKey(k)))
            throw new InvalidOperationException("지원되지 않는 지식입니다.");
        return EditKnowledgeKeys(keys, action, level);
    }

    private (IReadOnlyDictionary<uint, uint> Levels, SkillState Skills) EditKnowledgeKeys(uint[] keys, KnowledgeAction action, uint level)
    {
        var catalog = KnowledgeCatalog.Shared;
        var block = SaveRecords.Read(blob, Layout, "KnowledgeSaveData");
        var before = KnowledgeLevels(blob, block);
        if (action == KnowledgeAction.Level && (keys.Length != 1 || level < 1 ||
            level > catalog.LevelCounts[keys[0]] || before.GetValueOrDefault(keys[0]) == 0 || before.GetValueOrDefault(keys[0]) > catalog.LevelCounts[keys[0]]))
            throw new InvalidOperationException("습득한 지식 하나의 지원되는 단계를 선택하세요.");
        var changes = keys.Where(k => action switch
        {
            KnowledgeAction.Learn => before.GetValueOrDefault(k) == 0,
            KnowledgeAction.Unlearn or KnowledgeAction.Maximum => before.GetValueOrDefault(k) > 0,
            _ => true
        }).ToDictionary(k => k, k => action switch
        {
            KnowledgeAction.Learn => 1u, KnowledgeAction.Unlearn => 0u,
            KnowledgeAction.Level => level, _ => catalog.LevelCounts[k]
        });
        foreach (uint key in changes.Keys.Where(k => before.TryGetValue(k, out uint old) && old == changes[k]).ToArray())
            changes.Remove(key);
        if (changes.Count == 0) return (before, SkillState.Read(blob, block, Items));
        var list = block.Fields.Named("_list");
        if (changes.Keys.All(before.ContainsKey))
        {
            var patches = list.Elements("KnowledgeElementSaveData").Where(r => changes.ContainsKey((uint)r.ChildFields.Named("_key").Scalar(blob, "KnowledgeKey", 4)))
                .Select(r => CreatePatch(r.ChildFields.Named("_level").StartOffset,
                    BitConverter.GetBytes(changes[(uint)r.ChildFields.Named("_key").Scalar(blob, "KnowledgeKey", 4)])) with { CheckedScalar = true }).ToArray();
            byte[] fixedCandidate = CopyForEdit();
            foreach (var patch in patches) patch.After.CopyTo(fixedCandidate, patch.Offset);
            // Typed four-byte edits preserve all addresses; validate dependent skills before committing.
            var levels = KnowledgeLevels(fixedCandidate, block);
            var skills = SkillState.Read(fixedCandidate, block, Items);
            Edit(patches);
            return (levels, skills);
        }
        byte[] candidate = RecordLists.Rewrite(blob, Layout, EditBuffer, KnowledgeEdit(Layout, list, changes));
        var snapshots = PrepareSnapshots(blob, candidate);
        (IReadOnlyDictionary<uint, uint> Levels, SkillState Skills) result = default;
        Commit(candidate, items =>
        {
            var current = SaveRecords.Read(candidate, Layout.Reparse(candidate, blob), "KnowledgeSaveData");
            var after = KnowledgeLevels(candidate, current);
            var expected = new Dictionary<uint, uint>(before);
            foreach (var p in changes) expected[p.Key] = p.Value;
            result = (after, SkillState.Read(candidate, current, items));
            if (after.Count != expected.Count || expected.Any(p => !after.TryGetValue(p.Key, out uint value) || value != p.Value))
                throw new InvalidDataException("지식 변경 검증에 실패했습니다.");
        }, changed: ["KnowledgeSaveData"]);
        undo.Push((null, snapshots.Before, snapshots.After, ["KnowledgeSaveData"]));
        return result;
    }
    private static ListEdit KnowledgeEdit(ParcLayout layout, SaveField list, Dictionary<uint, uint> changes)
    {
        var type = layout.Types.Single(t => t.Name == "KnowledgeElementSaveData");
        if (!type.Fields.Select(f => (f.Name, f.TypeName, (int)f.Kind, (int)f.Size)).SequenceEqual(new[]
            { ("_key", "KnowledgeKey", 0, 4), ("_level", "TLevel", 0, 4), ("_learnedFieldTime", "uint64", 0, 8), ("_isNewMark", "bool", 0, 1) }))
            throw new InvalidDataException("Unsupported knowledge insertion layout.");
        return new ListEdit(list, "KnowledgeElementSaveData", "_key", changes.ToDictionary(p => p.Key, p => new Dictionary<string, ScalarValue>
        {
            ["_key"] = new("KnowledgeKey", 4, p.Key), ["_level"] = new("TLevel", 4, p.Value)
        }), CloneLast: true);
    }
}


