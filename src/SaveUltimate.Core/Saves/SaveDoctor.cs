using System.Buffers.Binary;

namespace SaveUltimate.Core.Saves;

public sealed record SaveDiagnosis(int MissionRows, int DamagedRows);

// Repairs saves written wrongly by released versions of this editor: inserted mission rows without array markers
// make the game read past the row and crash at the title screen, even for a slot the player never selects.
public sealed partial class SaveDocument
{
    public static SaveDiagnosis Diagnose(string path)
    {
        byte[] raw = SaveContainer.Decode(File.ReadAllBytes(path)).Blob;
        var layout = ParcLayout.Parse(raw);
        var list = SaveRecords.ReadLists(raw, layout, "QuestSaveData", "_questStateList", "_missionStateList").Fields.Named("_missionStateList");
        var type = layout.Types.Single(t => t.Name == "MissionStateData");
        return new(list.ListElements.Length, list.ListElements.Count(r => RecordLists.Fields(raw, type, r) is null));
    }

    // Writes the repair over the file, keeping the usual backups of the damaged one.
    public static void Repair(string path, string mirrorRoot)
    {
        var document = Open(path);
        if (document.RepairMissionRows() == 0) return;
        if (document.SaveTo(path) is { } recovery) SaveBackups.Mirror(path, recovery, mirrorRoot);
    }

    public int RepairMissionRows()
    {
        var block = SaveRecords.ReadLists(blob, Layout, "QuestSaveData", "_questStateList", "_missionStateList");
        var list = block.Fields.Named("_missionStateList");
        var type = Layout.Types.Single(t => t.Name == "MissionStateData");
        int damaged = list.ListElements.Count(r => RecordLists.Fields(blob, type, r) is null);
        if (damaged == 0) return 0;
        var before = ChallengeState.Read(blob, block);
        var edit = new ListEdit(list, type.Name, "_key", [], Salvage: Salvage);
        byte[] raw = blob;
        var layout = Layout;
        byte[] candidate = RecordLists.RewriteRegions(raw, EditBuffer, [(list.StartOffset, list.EndOffset, start => RecordLists.Rewritten(raw, layout, edit, start))]);
        var snapshots = PrepareSnapshots(raw, candidate);
        Commit(candidate, _ =>
        {
            var reparsed = Layout.Reparse(candidate, raw);
            var after = SaveRecords.ReadLists(candidate, reparsed, "QuestSaveData", "_questStateList", "_missionStateList");
            var state = ChallengeState.Read(candidate, after);
            if (after.Fields.Named("_missionStateList").ListElements.Any(r => RecordLists.Fields(candidate, type, r) is null) ||
                !before.Quest.OrderBy(p => p.Key).SequenceEqual(state.Quest.OrderBy(p => p.Key)) ||
                !before.Mission.OrderBy(p => p.Key).SequenceEqual(state.Mission.OrderBy(p => p.Key)))
                throw new InvalidDataException("Mission row repair verification failed.");
        }, changed: ["QuestSaveData"]);
        undo.Push((null, snapshots.Before, snapshots.After, ["QuestSaveData"]));
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
