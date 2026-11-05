namespace SaveUltimate.Core.Saves;

// Content only the DLC adds (DlcContentBuilder). While Hidden, edits never add DLC items, knowledge, skills, challenges or
// map records to a save, since a game without the DLC was never seen holding them.
public sealed record DlcContent(HashSet<uint> Items, HashSet<uint> Knowledge, HashSet<uint> Quests, HashSet<uint> Missions, HashSet<string> Scenes)
{
    public static DlcContent Shared { get; } = Embedded.Load<DlcContent>("DlcContent.json");
    public static bool Hidden { get; set; }
    internal static void Refuse(bool dlc) { if (Hidden && dlc) throw new InvalidOperationException("DLC content cannot be added to a game without the DLC installed."); }
}
