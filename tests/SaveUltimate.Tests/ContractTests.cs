using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.Tests;

// Holds every feature to its contract on every save that has what the feature needs, and writes COVERAGE.md beside the saves.
[TestClass]
public sealed class ContractTests
{
    private const int MinimumOwners = 3;
    // Features with too few players today, and why. Remove an entry once its saves arrive.
    private static readonly Dictionary<string, string> knownGaps = new() { ["DLC bond"] = "needs another player's save with a DLC bond below 100" };
    private static readonly Regex ownerFolder = new(@"^u\d+$");
    private static readonly Regex playerName = new(@"^(?<version>\d+\.\d{2}\.\d{2})_(?<stage>start|mid|late)_(?<purpose>[a-z0-9_]+)\.save$");
    private static readonly Regex specialName = new(@"^(?<version>\d+\.\d{2}\.\d{2})_(?<purpose>[a-z0-9_]+)\.save$");
    private static readonly string[] specialFolders = ["damaged", "unsupported"];

    private sealed record Save(string Path, string Owner, string Version, string Stage, string Purpose);

    private static string Folder(string path) => Path.GetFileName(Path.GetDirectoryName(path))!;

    // Progress is what makes saves differ, so it is measured rather than judged by eye.
    private static string Stage(int completedMissions) => completedMissions < 100 ? "start" : completedMissions < 3000 ? "mid" : "late";
    private static int CompletedMissions(SaveDocument doc) => doc.ReadChallenges().Mission.Count(p => p.Value.State == 5);

    private static string GameVersion(string path)
    {
        var blob = SaveContainer.Decode(File.ReadAllBytes(path)).Blob;
        var field = SaveRecords.ReadObjects(blob, ParcLayout.Parse(blob), "InventoryItemContentsSaveData").SelectMany(o => o.Fields).Single(f => f.Name == "_savedGameVersion");
        uint version = BitConverter.ToUInt32(blob, field.StartOffset);
        return $"{version >> 16}.{(version >> 8) & 255:00}.{version & 255:00}";
    }

    private static string[] SaveFiles(string root) => Directory.GetFiles(root, "*.save", SearchOption.AllDirectories);

    [TestMethod]
    [TestCategory("PrivateFixtures")]
    [TestCategory("Contract")]
    public void EverySaveHasACorrectUniqueNameVersionAndStage()
    {
        string root = FixtureSaves.Root ?? throw new AssertInconclusiveException("Set SAVEULTIMATE_FIXTURES.");
        var problems = new List<string>();
        var hashes = new Dictionary<string, string>();
        var purposes = new HashSet<string>();
        foreach (string path in SaveFiles(root))
        {
            string name = Path.GetFileName(path), folder = Folder(path), label = $"{folder}/{name}";
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            if (!hashes.TryAdd(hash, label)) problems.Add($"{label} has the same bytes as {hashes[hash]}.");
            bool special = specialFolders.Contains(folder);
            if (!special && !ownerFolder.IsMatch(folder)) { problems.Add($"{label}: move it to a player folder such as u1, or to {string.Join(" or ", specialFolders)}."); continue; }
            string version = GameVersion(path);
            string stage = special ? "" : Stage(CompletedMissions(SaveDocument.Open(path)));
            var match = (special ? specialName : playerName).Match(name);
            if (!match.Success) { problems.Add($"{label}: rename to {version}_{(special ? "" : stage + "_")}<purpose>.save."); continue; }
            if (!purposes.Add(match.Groups["purpose"].Value)) problems.Add($"{label}: another save already has this purpose.");
            if (version != match.Groups["version"].Value) problems.Add($"{label}: the save holds game version {version}.");
            if (!special && stage != match.Groups["stage"].Value) problems.Add($"{label}: the save's progress is {stage}.");
        }
        Assert.AreEqual("", string.Join(Environment.NewLine, problems));
    }

    [TestMethod]
    [TestCategory("PrivateFixtures")]
    [TestCategory("Contract")]
    public void UnsupportedSavesAreRefused()
    {
        string root = FixtureSaves.Root ?? throw new AssertInconclusiveException("Set SAVEULTIMATE_FIXTURES.");
        foreach (string path in SaveFiles(Path.Combine(root, "unsupported")))
            Assert.ThrowsExactly<InvalidDataException>(() => SaveDocument.Open(path), path);
    }

    [TestMethod]
    [TestCategory("PrivateFixtures")]
    [TestCategory("Contract")]
    public void EverySaveKeepsEveryContractAndEachFeatureCoversThreePlayers()
    {
        string root = FixtureSaves.Root ?? throw new AssertInconclusiveException("Set SAVEULTIMATE_FIXTURES.");
        var saves = SaveFiles(root).Where(p => ownerFolder.IsMatch(Folder(p)))
            .Select(p => (Path: p, Match: playerName.Match(Path.GetFileName(p)))).Where(s => s.Match.Success)
            .Select(s => new Save(s.Path, Folder(s.Path), s.Match.Groups["version"].Value, s.Match.Groups["stage"].Value, s.Match.Groups["purpose"].Value))
            .OrderBy(s => s.Owner).ThenBy(s => s.Version).ThenBy(s => s.Purpose).ToArray();
        Assert.IsTrue(saves.Length > 1, "No fixture saves.");
        var failures = new ConcurrentBag<string>();
        // Slow checks run on one standard save per player and progress stage: the largest, so the richest in records.
        var standard = saves.GroupBy(s => (s.Owner, s.Stage)).Select(g => g.MaxBy(s => new FileInfo(s.Path).Length)!).ToHashSet();
        // One save at a time per worker: fixed ranges would leave the standard saves waiting on one thread while others sit idle.
        var results = new ConcurrentDictionary<Save, HashSet<string>>();
        Parallel.ForEach(Partitioner.Create(saves, EnumerablePartitionerOptions.NoBuffering), new ParallelOptions { MaxDegreeOfParallelism = 8 },
            save => results[save] = Run(save, standard.Contains(save), failures));
        var covered = saves.Select(save => (Save: save, Features: results[save])).ToArray();
        WriteCoverage(root, covered);
        foreach (var feature in FeatureContracts.All)
        {
            int owners = covered.Where(c => c.Features.Contains(feature.Name)).Select(c => c.Save.Owner).Distinct().Count();
            if (owners < MinimumOwners && !knownGaps.ContainsKey(feature.Name)) failures.Add($"{feature.Name}: only {owners} player(s), {MinimumOwners} needed.");
        }
        Assert.AreEqual("", string.Join(Environment.NewLine, failures.Order()));
    }

    private static HashSet<string> Run(Save save, bool isStandard, ConcurrentBag<string> failures)
    {
        string name = Path.GetFileName(save.Path);
        var covered = new HashSet<string>();
        void Check(string what, Action action)
        {
            try { action(); }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException or ArgumentException or KeyNotFoundException)
            {
                failures.Add($"{name} {what}: {e.GetType().Name}: {e.Message}");
            }
        }
        Check("open", () =>
        {
            if (SaveDocument.Diagnose(save.Path).DamagedRows != 0) failures.Add($"{name}: damaged mission rows belong in damaged/.");
            var doc = SaveDocument.Open(save.Path);
            string original = doc.BlobSha256;
            var sections = isStandard ? FeatureContracts.Sections.ToDictionary(s => s.Key, s => s.Value(doc)) : [];
            void Restore(string what)
            {
                while (doc.CanUndo) doc.Undo();
                if (doc.BlobSha256 != original) { failures.Add($"{name} {what}: undo did not restore the save."); doc = SaveDocument.Open(save.Path); }
            }
            // Each feature alone, undone exactly. An edit that changes nothing tested nothing, so it does not count.
            foreach (var feature in FeatureContracts.All.Where(f => isStandard || !f.Slow))
            {
                Check(feature.Name, () =>
                {
                    if (feature.Find(doc) is not { } edit) return;
                    edit.Apply();
                    if (feature.Edits && doc.BlobSha256 == original) return;
                    edit.Verify();
                    foreach (var (section, before) in sections.Where(s => !feature.Touches.Contains(s.Key)))
                        // Order is not compared: moving a pet's record moves the items it carries.
                        if (FeatureContracts.Sections[section](doc) is var after && !after.Order().SequenceEqual(before.Order()))
                            throw new InvalidDataException($"It changed {section}, such as {after.Except(before).Concat(before.Except(after)).FirstOrDefault()}.");
                    covered.Add(feature.Name);
                });
                Restore(feature.Name);
            }
            if (!isStandard) return;
            // All of them on one document, as a player would, saved and read back once.
            foreach (var feature in FeatureContracts.All.Where(f => f.Edits && !f.Slow && covered.Contains(f.Name)))
                Check(feature.Name + " after the other edits", () =>
                {
                    if (feature.Find(doc) is not { } edit) return;
                    edit.Apply();
                    edit.Verify();
                });
            Check("save and reopen", () => RoundTrip(doc));
            Restore("all features");
        });
        return covered;
    }

    private static void RoundTrip(SaveDocument doc)
    {
        string output = Path.Combine(Path.GetTempPath(), $"SaveUltimate-fixture-{Guid.NewGuid():N}.save");
        try
        {
            doc.SaveCopy(output);
            if (SaveDocument.Open(output).BlobSha256 != doc.BlobSha256) throw new InvalidDataException("The saved copy reopened with different data.");
        }
        finally { File.Delete(output); }
    }

    private static void WriteCoverage(string root, (Save Save, HashSet<string> Features)[] covered)
    {
        var text = new StringBuilder("# Fixture coverage\n\nGenerated by `ContractTests`; do not edit.\n\n## By feature\n\n| Feature | Owners | Stages | Saves | Game versions | Owner list |\n|---|---|---|---|---|---|\n");
        foreach (var feature in FeatureContracts.All)
        {
            var rows = covered.Where(c => c.Features.Contains(feature.Name)).Select(c => c.Save).ToArray();
            var owners = rows.Select(s => s.Owner).Distinct().Order().ToArray();
            string note = knownGaps.TryGetValue(feature.Name, out var why) ? $" ({why})" : "";
            text.Append($"| {feature.Name} | {owners.Length}{(owners.Length < MinimumOwners ? " !" : "")} | {string.Join(", ", rows.Select(s => s.Stage).Distinct().Order())} | {rows.Length} | {string.Join(", ", rows.Select(s => s.Version).Distinct().Order())} | {string.Join(", ", owners)}{note} |\n");
        }
        text.Append("\n## By save\n\n| Save | Game version | Stage | Missions done | Items | Knowledge | Features with nothing to test |\n|---|---|---|---|---|---|---|\n");
        foreach (var (save, names) in covered)
        {
            var doc = SaveDocument.Open(save.Path);
            text.Append($"| {save.Owner}/{save.Purpose} | {save.Version} | {save.Stage} | {CompletedMissions(doc)} | {doc.Items.Count} | {doc.ReadKnowledge().Count(p => p.Value > 0)} | {string.Join(", ", FeatureContracts.All.Select(f => f.Name).Where(n => !names.Contains(n)))} |\n");
        }
        File.WriteAllText(Path.Combine(root, "COVERAGE.md"), text.ToString());
    }
}
