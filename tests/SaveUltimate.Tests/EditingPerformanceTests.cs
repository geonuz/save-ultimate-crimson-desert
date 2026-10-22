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
    [TestCategory("PrivateFixtures")]
    public void IncrementalOwnershipMatchesFullReaderAcrossStructuralEditsAndUndo()
    {
        string? fixtures = Environment.GetEnvironmentVariable("SAVEULTIMATE_FIXTURES");
        if (fixtures is null) Assert.Inconclusive("Set private fixtures.");
        var document = SaveDocument.Open(Path.Combine(fixtures, "save_v2.03.02.save"));
        void Check()
        {
            var raw = (byte[])typeof(SaveDocument).GetField("blob",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(document)!;
            var full = Inventory.Read(raw, document.Layout, Guid.NewGuid());
            Assert.AreEqual(JsonSerializer.Serialize(full), JsonSerializer.Serialize(document.Items));
            for (int i = 0; i < full.Length; i++)
            {
                var a = full[i]; var b = document.Items[i];
                Assert.AreEqual((a.KeyOffset, a.QuantityOffset, a.EnchantOffset, a.NumberOffset, a.RecordStart, a.RecordEnd),
                    (b.KeyOffset, b.QuantityOffset, b.EnchantOffset, b.NumberOffset, b.RecordStart, b.RecordEnd));
                CollectionAssert.AreEqual(a.Sockets.ScalarFields.ToArray(), b.Sockets.ScalarFields.ToArray());
                Assert.AreEqual(JsonSerializer.Serialize(a.Sockets.Slots.Select(s => s.Record)),
                    JsonSerializer.Serialize(b.Sockets.Slots.Select(s => s.Record)));
            }
            var corrupt = (byte[])raw.Clone();
            var item = document.Items.First();
            corrupt[item.KeyOffset] ^= 1;
            Assert.Throws<InvalidDataException>(() => Inventory.Refresh(raw, corrupt, document.Layout, document.Layout,
                document.Items, Guid.NewGuid(), ["KnowledgeSaveData"]));
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
    [DataRow("Item2")]
    [DataRow("Item3")]
    [TestCategory("PrivateFixtures")]
    public void DamagedStructuralUndoLeavesDocumentAndHistoryIntact(string snapshotField)
    {
        string? fixtures = Environment.GetEnvironmentVariable("SAVEULTIMATE_FIXTURES");
        if (fixtures is null) Assert.Inconclusive("Set private fixtures.");
        var document = SaveDocument.Open(Path.Combine(fixtures, "save_v2.03.02.save"));
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
    [TestCategory("PrivateFixtures")]
    public void OwnershipReaderKeepsStockCountsWithoutRetainingStockRecords()
    {
        string? fixtures = Environment.GetEnvironmentVariable("SAVEULTIMATE_FIXTURES");
        if (fixtures is null) Assert.Inconclusive("Set private fixtures.");
        byte[] raw = SaveContainer.Decode(File.ReadAllBytes(Path.Combine(fixtures, "save_v2.03.02.save"))).Blob;
        var store = SaveRecords.Read(raw, ParcLayout.Parse(raw), "StoreSaveData");
        static IEnumerable<SaveField> Walk(IEnumerable<SaveField> fields)
            => fields.SelectMany(f => new[] { f }.Concat(Walk(f.ChildFields)).Concat(Walk(f.ListElements)));
        var stock = Walk(store.Fields).Where(f => f.Name == "_storeItemList" && f.Present && f.ListCount > 0).ToArray();
        Assert.IsTrue(stock.Length > 0);
        foreach (var field in stock)
        {
            Assert.AreEqual(0, field.ListElements.Length);
            Assert.IsTrue(field.EndOffset > field.StartOffset && field.EndOffset <= raw.Length);
            Assert.Throws<InvalidDataException>(() => field.Elements("ItemSaveData"));
        }
    }

    [TestMethod]
    [TestCategory("PrivateFixtures")]
    public void StructuralHistoryRestoresAcrossScalarEditsAndBranching()
    {
        string? fixtures = Environment.GetEnvironmentVariable("SAVEULTIMATE_FIXTURES");
        if (fixtures is null) Assert.Inconclusive("Set private fixtures.");
        var document = SaveDocument.Open(Path.Combine(fixtures, "save_v2.03.02.save"));
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
    [TestCategory("PrivateFixtures")]
    public void RecordReaderStageTimings()
    {
        string? fixtures = Environment.GetEnvironmentVariable("SAVEULTIMATE_FIXTURES");
        if (fixtures is null) Assert.Inconclusive("Set private fixtures.");
        var rows = new List<object>();
        foreach (string name in new[] { "save_fishnot.save", "save_v2.03.02.save" })
        {
            byte[] raw = SaveContainer.Decode(File.ReadAllBytes(Path.Combine(fixtures, name))).Blob;
            var layout = ParcLayout.Parse(raw);
            foreach (string stage in new[] { "validate", "InventorySaveData", "EquipmentSaveData", "StoreSaveData", "MercenaryClanSaveData", "KnowledgeSaveData" })
            {
                var samples = new List<double>();
                for (int run = 0; run < 4; run++)
                {
                    var timer = Stopwatch.StartNew();
                    if (stage == "validate") NativeSave.ValidateBlob(raw);
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
    [TestCategory("PrivateFixtures")]
    public void EqualSizeSocketRelocationAndScalarRefreshSurviveReopening()
    {
        string? fixtures = Environment.GetEnvironmentVariable("SAVEULTIMATE_FIXTURES");
        if (fixtures is null) Assert.Inconclusive("Set private fixtures.");
        var document = SaveDocument.Open(Path.Combine(fixtures, "save_v2.03.02.save"));
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
    [TestCategory("PrivateFixtures")]
    public void DomainEditsMatchPythonAndRecordBackendTimings()
    {
        string? reference = Environment.GetEnvironmentVariable("SAVEULTIMATE_DOMAIN_REFERENCE");
        if (reference is null) Assert.Inconclusive("Set the private Python domain benchmark reference.");
        using var json = JsonDocument.Parse(File.ReadAllText(reference));
        var results = new List<object>();
        foreach (var row in json.RootElement.EnumerateArray())
        {
            string operation = row.GetProperty("operation").GetString()!;
            var samples = new List<double>();
            for (int run = 0; run < 6; run++)
            {
                var document = SaveDocument.Open(row.GetProperty("path").GetString()!);
                string original = document.BlobSha256;
                uint key = row.GetProperty("key").GetUInt32();
                uint value = row.GetProperty("value").GetUInt32();
                var item = document.Items.SingleOrDefault(i => i.Number == row.GetProperty("number").GetUInt64()
                    && i.Source == (operation == "repurchase" ? ItemSource.Vendor : ItemSource.Inventory));
                uint[] keys = row.GetProperty("keys").EnumerateArray().Select(k => k.GetUInt32()).ToArray();
                var timer = Stopwatch.StartNew();
                switch (operation)
                {
                    case "repurchase": document.SwapVendorItem(item!, key); break;
                    case "unlock-present":
                    case "unlock-insert": document.SetUnlockedSockets(item!, (int)value); break;
                    case "gear-remove":
                    case "gear-install":
                    case "gear-replace": document.SetSockets(item!, keys); break;
                    case "skill-invest":
                    case "skill-watch": document.LearnSkill(key, value, operation == "skill-watch"); break;
                    case "enchant": document.SetEnchant([item!], (ushort)value); break;
                    default: Assert.Fail($"Unknown operation: {operation}"); break;
                }
                double elapsed = timer.Elapsed.TotalMilliseconds;
                if (run > 0) samples.Add(elapsed);
                Assert.AreEqual(row.GetProperty("sha256").GetString(), document.BlobSha256, operation);
                document.Undo();
                Assert.AreEqual(original, document.BlobSha256, operation + " undo");
            }
            var result = new { operation, csharpMs = samples.Order().ElementAt(samples.Count / 2),
                pythonMs = row.GetProperty("pythonMs").GetDouble(), samplesMs = samples };
            results.Add(result);
            Console.WriteLine(JsonSerializer.Serialize(result));
        }
        string? output = Environment.GetEnvironmentVariable("SAVEULTIMATE_DOMAIN_PERFORMANCE");
        if (output is not null) File.WriteAllText(output, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }

    [TestMethod]
    [TestCategory("PrivateFixtures")]
    public void ScalarRefreshMatchesFullReaderAndRecordsBackendTimings()
    {
        string? fixtures = Environment.GetEnvironmentVariable("SAVEULTIMATE_FIXTURES");
        if (fixtures is null) Assert.Inconclusive("Set private fixtures.");
        var results = new List<object>();
        foreach (string name in new[] { "new.save", "save_fishnot.save", "save_v2.03.02.save" })
        {
            string path = Path.Combine(fixtures, name);
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
                NativeSave.ValidateBlob(candidate);
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
