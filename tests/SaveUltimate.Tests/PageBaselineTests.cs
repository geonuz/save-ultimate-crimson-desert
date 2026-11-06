using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.Tests;

// What each save's pages read, recorded beside the saves, so a change to how saves are read shows every record it changes.
// A missing file is written. After an intended change, set SAVEULTIMATE_UPDATE_BASELINE=1 once and review the difference.
[TestClass]
public sealed class PageBaselineTests
{
    private static string ItemLine(InventoryItem i) =>
        $"{i.Source} no={i.Number} key={i.Key} qty={i.Quantity} bag={i.Bag} store={i.StoreKey} owner={i.OwnerNumber}/{i.OwnerCharacter}/{i.OwnerName}/{i.OwnerSlot} " +
        $"slot={i.Slot} endurance={i.Endurance} sharpness={i.Sharpness} enchant={i.EnchantLevel} state={i.HoldsGearState}/{i.HoldsStackState}/{i.HoldsContents} " +
        $"at={i.RecordOffset} key@{i.KeyOffset} qty@{i.QuantityOffset} enchant@{(i.EnchantLevel.HasValue ? i.EnchantOffset : 0)} " +
        $"sockets={i.Sockets.Capacity}/{i.Sockets.Unlocked}/{string.Join(',', i.Sockets.Slots.Select(s => $"{s.Key}:{s.Endurance}:{s.HasGem}"))}/{i.Sockets.Error}";

    private static string Friendship(NpcFriendship row)
    {
        static string Value(FriendshipValue v) => $"{v.Level}:{v.Value}@{(v.Value.HasValue ? v.Offset : 0)}";
        static string Gauge(NpcGauge g) => $"{Value(g.Global)} [{string.Join(' ', g.Fields.Select(Value))}]";
        return $"{row.Key} {Gauge(row)} {string.Join(' ', row.Affinity.Select(a => $"{a.Playable}/{a.MaritalState}={Gauge(a)}"))}";
    }

    private static string[] Lines(string path)
    {
        SaveDocument document;
        try { document = SaveDocument.Open(path); }
        catch (InvalidDataException) { return ["refused"]; }
        var lines = new List<string>();
        void Section(string name, Func<IEnumerable<string>> read)
        {
            lines.Add("## " + name);
            // A page that cannot read this save must keep failing the same way, and must not hide the other pages.
            try { lines.AddRange(read()); }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException) { lines.Add("failed: " + e.GetType().Name); }
        }
        Section("Items", () => document.Items.Select(ItemLine));
        foreach (var (name, read) in FeatureContracts.Sections.Where(s => s.Key is not ("Items" or "Friendship")))
            Section(name, () => read(document));
        Section("Friendship", () => document.ReadFriendship().Select(Friendship));
        Section("UsedTags", () => document.ReadUsedTags().Select(p => $"{p.Key}={string.Join(',', p.Value)}"));
        Section("CompanionDetails", () => document.ReadCompanionDetails().Select(p => $"{p.Key}={p.Value}"));
        Section("Map", () =>
        {
            var map = document.ReadMap();
            return map.Discoveries.Select(p => $"{p.Key}={p.Value}").Prepend($"fog@{map.Fog.Offset} {Convert.ToHexString(SHA256.HashData(map.Fog.Codes))}");
        });
        Section("Diagnosis", () => [$"SaveDiagnosis {{ MissionRows = {SaveRecords.Read(document.blob, document.Layout, "QuestSaveData").Fields.Named("_missionStateList").ListElements.Length}, DamagedRows = {document.DamagedRows} }}"]);
        return lines.ToArray();
    }

    [TestMethod]
    [TestCategory("PrivateFixtures")]
    [TestCategory("Contract")]
    public void EveryPageReadsWhatTheBaselineRecorded()
    {
        string root = FixtureSaves.Root ?? throw new AssertInconclusiveException("Set SAVEULTIMATE_FIXTURES.");
        string folder = Path.Combine(root, "baseline");
        Directory.CreateDirectory(folder);
        bool update = Environment.GetEnvironmentVariable("SAVEULTIMATE_UPDATE_BASELINE") == "1";
        var differences = new ConcurrentBag<string>();
        Parallel.ForEach(Directory.GetFiles(root, "*.save", SearchOption.AllDirectories), new ParallelOptions { MaxDegreeOfParallelism = 8 }, path =>
        {
            string file = Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + ".txt");
            string[] now = Lines(path);
            if (update || !File.Exists(file)) { File.WriteAllLines(file, now); return; }
            string[] recorded = File.ReadAllLines(file);
            if (recorded.SequenceEqual(now)) return;
            string[] gone = recorded.Except(now).ToArray(), added = now.Except(recorded).ToArray();
            differences.Add($"{Path.GetFileName(path)}: {gone.Length} lines gone, {added.Length} added; was {gone.FirstOrDefault()}; now {added.FirstOrDefault()}");
        });
        Assert.AreEqual("", string.Join(Environment.NewLine, differences.Order()));
    }
}
