using System.Buffers.Binary;

namespace SaveUltimate.Core.Saves;

public sealed partial class SaveDocument
{
    public SkillState ReadSkills() => SkillState.Read(blob, Layout, Items);

    public SkillState LearnSkill(uint key, uint target, bool watch = false)
    {
        var block = SaveRecords.Read(blob, Layout, "KnowledgeSaveData");
        var before = SkillState.Read(blob, block, Items);
        var plan = SkillPlan.Create(before, key, target, watch);
        byte[] candidate = CopyForEdit();
        foreach (var (itemKey, amount) in plan.Costs)
        {
            ulong remaining = amount;
            foreach (var item in Items.Where(i => i.Source == ItemSource.Inventory && i.Bag == 2 && i.Key == itemKey).OrderBy(i => i.KeyOffset))
            {
                ulong take = Math.Min(remaining, item.Quantity);
                if (take == 0) continue;
                BinaryPrimitives.WriteUInt64LittleEndian(candidate.AsSpan(item.QuantityOffset), item.Quantity - take);
                if (take == item.Quantity) BinaryPrimitives.WriteUInt32LittleEndian(candidate.AsSpan(item.KeyOffset), 0);
                remaining -= take;
            }
            if (remaining != 0) throw new InvalidDataException("스킬 재료 수량이 변경되었습니다.");
        }
        static Dictionary<string, ScalarValue> Level(uint k, uint level) => new()
            { ["_key"] = new("KnowledgeKey", 4, k), ["_level"] = new("TLevel", 4, level) };
        var edits = new List<ListEdit>
        {
            new(block.Fields.Named("_list"), "KnowledgeElementSaveData", "_key", plan.Changes.ToDictionary(p => p.Key, p => Level(p.Key, p.Value)))
        };
        var paid = new Dictionary<uint, SkillPayment>(before.Paid);
        if (plan.Investment != 0)
        {
            var payment = new SkillPayment(checked((before.Paid.GetValueOrDefault(key)?.Spent ?? 0) + plan.Investment), plan.Owner);
            paid[key] = payment;
            edits.Add(new(block.Fields.Named("_skillLearnSaveDataList"), "SkillLearnElementSaveData", "_knowledgeKey", new()
            {
                [key] = new() { ["_knowledgeKey"] = new("KnowledgeKey", 4, key), ["_usedArtifactCount"] = new("TStackCount", 8, payment.Spent),
                    ["_skillPointOwnerType"] = new("SkillPointOwnerType", 1, (ulong)plan.Owner) }
            }));
            edits.Add(new(block.Fields.Named("_skillPointSaveDataList"), "SkillPointSaveData", "_skillPointOwnerType",
                plan.Points.ToDictionary(p => (uint)p.Key, p => new Dictionary<string, ScalarValue>
                {
                    ["_skillPointOwnerType"] = new("SkillPointOwnerType", 1, (ulong)p.Key),
                    ["_currentSkillPoint"] = new("TSkillPoint", 2, p.Value.Current), ["_elapsedSkillPoint"] = new("TSkillPoint", 2, p.Value.Total)
                })));
        }
        if (plan.Follow.Count != 0)
            edits.Add(new(block.Fields.Named("_learnedFollowLearnKnowledge"), "FollowLearnElementSaveData", "_knowledgeKey",
                plan.Follow.ToDictionary(p => p.Key, p => new Dictionary<string, ScalarValue>
                { ["_knowledgeKey"] = new("KnowledgeKey", 4, p.Key), ["_learnFromTypeRaw"] = new("uint8", 1, p.Value) })));
        candidate = RecordLists.Rewrite(candidate, Layout, EditBuffer, edits.ToArray());
        SkillState? after = null;
        byte[] previous = blob;
        var snapshots = PrepareSnapshots(previous, candidate);
        string[] changed = plan.Costs.Count == 0 ? ["KnowledgeSaveData"] : ["KnowledgeSaveData", "InventorySaveData"];
        Commit(candidate, items =>
        {
            after = SkillState.Read(candidate, Layout.Reparse(candidate, blob), items);
            var expectedLevels = new Dictionary<uint, uint>(before.Levels);
            foreach (var entry in plan.Changes) expectedLevels[entry.Key] = entry.Value;
            var expectedFollow = new Dictionary<uint, uint>(before.Follow);
            foreach (var entry in plan.Follow) expectedFollow[entry.Key] = entry.Value;
            static bool Equal<K, V>(IReadOnlyDictionary<K, V> a, IReadOnlyDictionary<K, V> b) where K : notnull
                => a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var value) && EqualityComparer<V>.Default.Equals(p.Value, value));
            if (!Equal(after.Levels, expectedLevels) || !Equal(after.Paid, paid) || !Equal(after.Points, plan.Points) || !Equal(after.Follow, expectedFollow) ||
                before.Inventory.Keys.Union(after.Inventory.Keys).Any(k => after.Inventory.GetValueOrDefault(k) != before.Inventory.GetValueOrDefault(k) - plan.Costs.GetValueOrDefault(k)))
                throw new InvalidDataException("스킬 변경 검증에 실패했습니다.");
        }, changed: changed);
        undo.Push((null, snapshots.Before, snapshots.After, changed));
        return after!;
    }
}
