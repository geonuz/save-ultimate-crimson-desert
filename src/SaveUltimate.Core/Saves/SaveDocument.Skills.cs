namespace SaveUltimate.Core.Saves;

public sealed partial class SaveDocument
{
    public SkillState ReadSkills() => SkillState.Read(blob, Layout);

    /// <summary>Learns <paramref name="keys"/> by watching, in one edit: the rows and records the game writes when the
    /// player watches each skill (<see cref="SkillState.Observe"/>). Skills already learned by watching are left alone.</summary>
    public SkillState ObserveSkills(IEnumerable<uint> keys)
    {
        uint[] chosen = keys.Distinct().ToArray();
        DlcContent.Refuse(chosen.Any(DlcContent.Shared.Knowledge.Contains));
        var block = SaveRecords.Read(blob, Layout, "KnowledgeSaveData");
        var before = SkillState.Read(blob, block);
        var (changes, follow) = before.Observe(chosen);
        var stored = KnowledgeLevels(blob, block);
        byte[] candidate = RecordLists.Rewrite(blob, Layout, EditBuffer, KnowledgeEdits(block, stored, changes, follow).ToArray());
        SkillState? after = null;
        Transact(candidate, ["KnowledgeSaveData"], (layout, _) =>
        {
            var current = SaveRecords.Read(candidate, layout, "KnowledgeSaveData");
            after = SkillState.Read(candidate, current);
            var expectedLevels = new Dictionary<uint, uint>(stored);
            foreach (var entry in changes) expectedLevels[entry.Key] = entry.Value;
            var expectedFollow = new Dictionary<uint, uint>(before.Follow);
            foreach (var entry in follow) expectedFollow[entry.Key] = entry.Value;
            if (!expectedLevels.OrderBy(p => p.Key).SequenceEqual(KnowledgeLevels(candidate, current).OrderBy(p => p.Key)) ||
                !expectedFollow.OrderBy(p => p.Key).SequenceEqual(after.Follow.OrderBy(p => p.Key)) || !chosen.All(after.Observed))
                throw new InvalidDataException("Skill edit verification failed.");
        });
        return after!;
    }
}
