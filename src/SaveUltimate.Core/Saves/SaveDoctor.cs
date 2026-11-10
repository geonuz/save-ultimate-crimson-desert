using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SaveUltimate.Core.Saves;

// Repairs saves written wrongly by released versions of this editor: inserted mission rows without array markers
// make the game read past the row and crash at the title screen, even for a slot the player never selects.
public sealed partial class SaveDocument
{
    // Writes the chosen repairs over the file, keeping the usual backups of the damaged one.
    public static void Repair(string path, string mirrorRoot, IReadOnlySet<SaveFix> fixes)
    {
        if (fixes.Contains(SaveFix.Integrity)) Reseal(path, mirrorRoot);
        var document = Open(path);
        if (fixes.Contains(SaveFix.MissionRows)) document.RepairMissionRows();
        if (fixes.Contains(SaveFix.Enchants))
            // Each edit replaces the items, so the selection is taken again for every limit.
            foreach (int limit in document.Items.Where(CanLowerEnchant).Select(i => i.MaxEnchant).Distinct().ToArray())
                document.SetEnchant(document.Items.Where(i => CanLowerEnchant(i) && i.MaxEnchant == limit), (ushort)limit);
        if (document.IsDirty && document.SaveTo(path) is { } recovery) SaveBackups.Mirror(path, recovery, mirrorRoot);
    }
    private static bool CanLowerEnchant(InventoryItem item) => SaveCheckup.OverEnchant(item) && item.MaxEnchant > 0;

    private static void Reseal(string path, string mirrorRoot)
    {
        byte[] file = File.ReadAllBytes(path);
        if (SaveContainer.Open(file).SignedBy(file)) return;
        string prepared = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $".prepared-{Guid.NewGuid():N}.save");
        try
        {
            SaveContainer.Reseal(file, prepared);
            if (SaveBackups.ReplacePrepared(prepared, path, SHA256.HashData(file), true) is { } recovery) SaveBackups.Mirror(path, recovery, mirrorRoot);
        }
        finally { if (File.Exists(prepared)) File.Delete(prepared); }
    }

    // The newest backup that opens and has no damaged rows, for a save nothing here can repair.
    public static string? LatestGoodBackup(string path) => SaveBackups.List(path).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault(backup =>
    {
        try { return Open(backup).DamagedRows == 0; }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    });

    public static void RestoreBackup(string path, string backup, string mirrorRoot)
    {
        if (SaveBackups.Restore(path, backup, SaveBackups.Hash(path)) is { } recovery) SaveBackups.Mirror(path, recovery, mirrorRoot);
    }

    public int RepairMissionRows()
    {
        var block = SaveRecords.Read(blob, Layout, "QuestSaveData");
        var list = block.Fields.Named("_missionStateList");
        var type = Layout.Types.Single(t => t.Name == "MissionStateData");
        int damaged = list.ListElements.Count(r => RecordLists.Fields(blob, type, r) is null);
        if (damaged == 0) return 0;
        var before = ChallengeState.Read(blob, block);
        var edit = new ListEdit(list, type.Name, "_key", [], Salvage: Salvage);
        byte[] candidate = RecordLists.Rewrite(blob, Layout, EditBuffer, edit);
        Transact(candidate, ["QuestSaveData"], (layout, _) =>
        {
            var after = SaveRecords.Read(candidate, layout, "QuestSaveData");
            var state = ChallengeState.Read(candidate, after);
            if (after.Fields.Named("_missionStateList").ListElements.Any(r => RecordLists.Fields(candidate, type, r) is null) ||
                !before.Matches(state))
                throw new InvalidDataException("Mission row repair verification failed.");
        });
        return damaged;
    }

    // Damaged rows hold their scalars in schema order; whatever follows them was written without regard to the layout.
    private static Dictionary<string, ReadOnlyMemory<byte>> Salvage(byte[] raw, ParcType type, SaveField record)
    {
        var values = new Dictionary<string, ReadOnlyMemory<byte>>();
        int cursor = record.ChildPayloadOffset + 4, end = record.EndOffset - 4;
        for (int i = 0; i < type.Fields.Count; i++)
        {
            var f = type.Fields[i];
            if ((raw[record.StartOffset + 2 + i / 8] & (1 << (i % 8))) == 0 || f.Name == "_newAlarm") continue;
            if (f.Kind is not 0 and not 2 || cursor + f.Size > end) throw new InvalidDataException("Unsupported damaged mission row.");
            values.Add(f.Name, raw.AsMemory(cursor, f.Size));
            cursor += f.Size;
        }
        if (!values.TryGetValue("_key", out var key)) throw new InvalidDataException("Unsupported damaged mission row.");
        values["_newAlarm"] = new byte[1];
        if (values.ContainsKey("_completedTime"))
            values["_usedTagList"] = RecordLists.ArrayBytes(ChallengeCatalog.CompletedTags(BinaryPrimitives.ReadUInt32LittleEndian(key.Span)));
        return values;
    }
}
