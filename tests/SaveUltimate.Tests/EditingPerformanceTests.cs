using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.Tests;

[TestClass]
public sealed class EditingPerformanceTests
{
    [TestMethod]
    [TestCategory("Scenario")]
    [TestCategory("PrivateFixtures")]
    public void EditsKeepOwnershipCurrentAndRefuseUnrelatedChanges()
    {
        string? fixtures = FixtureSaves.Root;
        if (fixtures is null) Assert.Inconclusive("Set private fixtures.");
        var document = SaveDocument.Open(FixtureSaves.Named("editing_baseline"));
        void Check()
        {
            var raw = (byte[])typeof(SaveDocument).GetField("blob",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(document)!;
            var full = Inventory.Read(raw, document.Layout, Guid.NewGuid());
            Assert.AreEqual(JsonSerializer.Serialize(full), JsonSerializer.Serialize(document.Items));
            for (int i = 0; i < full.Length; i++)
            {
                var a = full[i]; var b = document.Items[i];
                Assert.AreEqual((a.KeyOffset, a.QuantityOffset, a.EnchantOffset, a.RecordOffset), (b.KeyOffset, b.QuantityOffset, b.EnchantOffset, b.RecordOffset));
                Assert.AreEqual(JsonSerializer.Serialize(a.Sockets.Slots.Select(s => s.Record)),
                    JsonSerializer.Serialize(b.Sockets.Slots.Select(s => s.Record)));
            }
            var corrupt = (byte[])raw.Clone();
            var item = document.Items.First();
            corrupt[item.KeyOffset] ^= 1;
            Assert.Throws<InvalidDataException>(() => document.Transact(corrupt, ["KnowledgeSaveData"]));
            byte[] schema = (byte[])raw.Clone();
            schema[20 + 4] ^= 1;
            Assert.Throws<InvalidDataException>(() => document.Transact(schema, ["KnowledgeSaveData"]));
        }
        var stale = document.Items.Single(i => i.Source == ItemSource.Inventory && i.Number == 1218);
        document.SetSockets(stale, [0, 0]);
        Check();
        Assert.Throws<InvalidOperationException>(() => document.SetSockets(stale, [0, 0]));
        document.SetSockets(document.Items.Single(i => i.Source == ItemSource.Inventory && i.Number == 1218), [1002785, 0]);
        Check();
        document.Undo(); Check();
        document.Undo(); Check();
    }

    [TestMethod]
    [TestCategory("Scenario")]
    [DataRow("Item2")]
    [DataRow("Item3")]
    [TestCategory("PrivateFixtures")]
    public void DamagedStructuralUndoLeavesDocumentAndHistoryIntact(string snapshotField)
    {
        string? fixtures = FixtureSaves.Root;
        if (fixtures is null) Assert.Inconclusive("Set private fixtures.");
        var document = SaveDocument.Open(FixtureSaves.Named("editing_baseline"));
        string original = document.BlobSha256;
        document.SetSockets(document.Items.Single(i => i.Source == ItemSource.Inventory && i.Number == 1218), [0, 0]);
        string edited = document.BlobSha256;
        // Corrupt a compressed history checksum without adding a production mutation API.
        var history = (System.Collections.IEnumerable)typeof(SaveDocument).GetField("undo",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(document)!;
        var entry = history.Cast<object>().First();
        var snapshot = entry.GetType().GetField(snapshotField)!.GetValue(entry)!;
        var packed = (byte[])snapshot.GetType().GetProperty("Packed")!.GetValue(snapshot)!;
        packed[^1] ^= 1;
        Assert.Throws<InvalidDataException>(document.Undo);
        Assert.AreEqual(edited, document.BlobSha256);
        Assert.IsTrue(document.CanUndo);
        packed[^1] ^= 1;
        document.Undo();
        Assert.AreEqual(original, document.BlobSha256);
        Assert.IsFalse(document.CanUndo);
        Assert.IsFalse(document.IsDirty);
    }

    [TestMethod]
    [TestCategory("Scenario")]
    [TestCategory("PrivateFixtures")]
    public void StructuralHistoryRestoresAcrossScalarEditsAndBranching()
    {
        string? fixtures = FixtureSaves.Root;
        if (fixtures is null) Assert.Inconclusive("Set private fixtures.");
        var document = SaveDocument.Open(FixtureSaves.Named("editing_baseline"));
        InventoryItem Target() => document.Items.Single(i => i.Source == ItemSource.Inventory && i.Number == 1218);
        string original = document.BlobSha256;
        document.SetSockets(Target(), [0, 0]);
        string first = document.BlobSha256;
        document.SetQuantity(Target(), Target().Quantity + 1);
        string scalar = document.BlobSha256;
        document.SetSockets(Target(), [1002785, 1002785]);
        document.Undo();
        Assert.AreEqual(scalar, document.BlobSha256);
        document.Undo();
        Assert.AreEqual(first, document.BlobSha256);
        document.SetSockets(Target(), [1002786, 0]);
        document.SetSockets(Target(), [1002785, 0]);
        document.Undo();
        document.Undo();
        Assert.AreEqual(first, document.BlobSha256);
        document.Undo();
        Assert.AreEqual(original, document.BlobSha256);
        Assert.IsFalse(document.CanUndo);
    }

    [TestMethod]
    [TestCategory("Scenario")]
    [TestCategory("PrivateFixtures")]
    public void RecordReaderStageTimings()
    {
        string? fixtures = FixtureSaves.Root;
        if (fixtures is null) Assert.Inconclusive("Set private fixtures.");
        var rows = new List<object>();
        foreach (string name in new[] { "fishing_no_first_catch", "editing_baseline" })
        {
            byte[] raw = SaveContainer.Decode(File.ReadAllBytes(FixtureSaves.Named(name))).Blob;
            var layout = ParcLayout.Parse(raw);
            foreach (string stage in new[] { "validate", "InventorySaveData", "EquipmentSaveData", "StoreSaveData", "MercenaryClanSaveData", "KnowledgeSaveData" })
            {
                var samples = new List<double>();
                for (int run = 0; run < 4; run++)
                {
                    var timer = Stopwatch.StartNew();
                    if (stage == "validate") ParcReader.Validate(raw, layout);
                    else SaveRecords.Read(raw, layout, stage);
                    if (run > 0) samples.Add(timer.Elapsed.TotalMilliseconds);
                }
                rows.Add(new { file = name, stage, ms = samples.Order().ElementAt(1) });
            }
        }
        string json = JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        string? output = Environment.GetEnvironmentVariable("SAVEULTIMATE_STAGE_PERFORMANCE");
        if (output is not null) File.WriteAllText(output, json);
    }

    [TestMethod]
    [TestCategory("Scenario")]
    [TestCategory("PrivateFixtures")]
    public void EqualSizeSocketRelocationAndScalarRefreshSurviveReopening()
    {
        string? fixtures = FixtureSaves.Root;
        if (fixtures is null) Assert.Inconclusive("Set private fixtures.");
        var document = SaveDocument.Open(FixtureSaves.Named("editing_baseline"));
        InventoryItem Target() => document.Items.Single(i => i.Source == ItemSource.Inventory && i.Number == 1218);
        document.SetSockets(Target(), [1002785, 0]);
        string before = document.BlobSha256;
        // Removing one record payload while inserting another preserves total size, but moves fields.
        document.SetSockets(Target(), [0, 1002785]);
        var stale = Target();
        document.SetSockets(stale, [0, 1002786]);
        Assert.Throws<InvalidOperationException>(() => document.SetSockets(stale, [0, 1002785]));
        Assert.AreEqual(1002786u, Target().Sockets.Slots[1].Key);
        string output = Path.Combine(Path.GetTempPath(), $"SaveUltimate-socket-refresh-{Guid.NewGuid():N}.save");
        try
        {
            document.SaveCopy(output);
            var reopened = SaveDocument.Open(output);
            var actual = reopened.Items.Single(i => i.Source == ItemSource.Inventory && i.Number == 1218).Sockets;
            Assert.AreEqual(document.BlobSha256, reopened.BlobSha256);
            CollectionAssert.AreEqual(Target().Sockets.Slots.Select(s => (s.Key, s.HasGem, s.Endurance)).ToArray(),
                actual.Slots.Select(s => (s.Key, s.HasGem, s.Endurance)).ToArray());
            Assert.AreEqual(Target().Sockets.Unlocked, actual.Unlocked);
            document.Undo();
            document.Undo();
            Assert.AreEqual(before, document.BlobSha256);
        }
        finally { if (File.Exists(output)) File.Delete(output); }
    }

    [TestMethod]
    [TestCategory("Scenario")]
    [TestCategory("PrivateFixtures")]
    public void ScalarRefreshMatchesFullReaderAndRecordsBackendTimings()
    {
        string? fixtures = FixtureSaves.Root;
        if (fixtures is null) Assert.Inconclusive("Set private fixtures.");
        var results = new List<object>();
        foreach (string name in new[] { "early_progress", "fishing_no_first_catch", "editing_baseline" })
        {
            string path = FixtureSaves.Named(name);
            var document = SaveDocument.Open(path);
            byte[] raw = SaveContainer.Decode(File.ReadAllBytes(path)).Blob;
            string original = document.BlobSha256;
            int index = document.Items.ToList().FindIndex(i => i.CanEditQuantity && i.Quantity < 999_999_999);
            var fast = new List<double>(); var full = new List<double>();
            for (int run = 0; run < 6; run++)
            {
                var target = document.Items[index];
                var timer = Stopwatch.StartNew();
                document.SetQuantity(target, target.Quantity + 1);
                double elapsed = timer.Elapsed.TotalMilliseconds;
                if (run > 0) fast.Add(elapsed);
                timer.Restart();
                byte[] candidate = (byte[])raw.Clone();
                BinaryPrimitives.WriteUInt64LittleEndian(candidate.AsSpan(target.QuantityOffset), target.Quantity + 1);
                ParcReader.Validate(candidate, ParcLayout.Parse(candidate));
                var read = Inventory.Read(candidate, ParcLayout.Parse(candidate), Guid.NewGuid());
                elapsed = timer.Elapsed.TotalMilliseconds;
                if (run > 0) full.Add(elapsed);
                Assert.AreEqual(read.Length, document.Items.Count);
                for (int i = 0; i < read.Length; i++)
                {
                    Assert.AreEqual(read[i].Quantity, document.Items[i].Quantity);
                    Assert.AreEqual(read[i].EnchantLevel, document.Items[i].EnchantLevel);
                    Assert.AreEqual(read[i].Number, document.Items[i].Number);
                    Assert.AreEqual(read[i].Key, document.Items[i].Key);
                }
                Assert.Throws<InvalidOperationException>(() => document.SetQuantity(target, target.Quantity + 2));
                document.Undo();
                Assert.AreEqual(original, document.BlobSha256);
            }
            double Median(List<double> samples) => samples.Order().ElementAt(samples.Count / 2);
            results.Add(new { file = name, quantityEditMs = Median(fast), previousFullReadMs = Median(full), samples = 5 });
        }
        string json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        string? output = Environment.GetEnvironmentVariable("SAVEULTIMATE_PERFORMANCE");
        if (output is not null) File.WriteAllText(output, json);
    }
}
