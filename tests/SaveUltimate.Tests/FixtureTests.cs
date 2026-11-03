using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.Tests;

[TestClass]
public sealed class FixtureTests
{
    // 1.14.00 item records end in 0x00 bytes where 2.x saves keep 0x01 markers; the editor refuses that layout.
    private static readonly string[] unsupported = ["1.14.00.save"];

    [TestMethod]
    [TestCategory("PrivateFixtures")]
    public void EveryFixtureReadsAndActivatesEveryMapGroupThenUndoesExactly()
    {
        string root = FixtureSaves.Root ?? throw new AssertInconclusiveException("Set SAVEULTIMATE_FIXTURES.");
        // Collected saves and the scenario saves promoted from them, whatever their folder.
        var files = new[] { "collected", "scenarios" }.SelectMany(d => Directory.GetFiles(Path.Combine(root, d), "*.save", SearchOption.AllDirectories)).ToArray();
        Assert.IsTrue(files.Length > 1, "No fixture saves.");
        var failures = new List<string>();
        foreach (string path in files)
        {
            string name = Path.GetFileName(path);
            if (unsupported.Contains(name))
            {
                Assert.ThrowsExactly<InvalidDataException>(() => SaveDocument.Open(path), name);
                continue;
            }
            var doc = SaveDocument.Open(path); string original = doc.BlobSha256;
            void Check(string what, Action action)
            {
                try { action(); }
                catch (Exception e) when (e is InvalidDataException or InvalidOperationException) { failures.Add($"{name} {what}: {e.Message}"); }
            }
            Check("skills", () => doc.ReadSkills()); Check("knowledge", () => doc.ReadKnowledge()); Check("challenges", () => doc.ReadChallenges());
            Check("fishing", () => doc.ReadFishing()); Check("companions", () => doc.ReadCompanionDetails()); Check("friendship", () => doc.ReadFriendship());
            Check("map", () => doc.ReadMap());
            for (int i = 0; i < MapAction.All.Length; i++) Check("map " + MapAction.All[i].Names["en"], () => doc.ActivateMap(i));
            while (doc.CanUndo) doc.Undo();
            Assert.AreEqual(original, doc.BlobSha256, name);
        }
        Assert.AreEqual("", string.Join(Environment.NewLine, failures));
    }
}
