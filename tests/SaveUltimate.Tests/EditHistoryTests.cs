using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.Tests;

[TestClass]
public sealed class EditHistoryTests
{
    [TestMethod]
    [TestCategory("Scenario")]
    [TestCategory("PrivateFixtures")]
    public void EditsKeepOwnershipCurrentAndRefuseUnrelatedChanges()
    {
        var document = SaveDocument.Open(FixtureSaves.Named("editing_baseline"));
        void Check()
        {
            var raw = document.blob;
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
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("PrivateFixtures")]
    public void DamagedStructuralUndoLeavesDocumentAndHistoryIntact(bool after)
    {
        var document = SaveDocument.Open(FixtureSaves.Named("editing_baseline"));
        string original = document.BlobSha256;
        document.SetSockets(document.Items.Single(i => i.Source == ItemSource.Inventory && i.Number == 1218), [0, 0]);
        string edited = document.BlobSha256;
        // Corrupt a compressed history checksum without adding a production mutation API.
        var entry = document.undo.Peek();
        byte[] packed = (after ? entry.After : entry.Before)!.Packed;
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
    public void EqualSizeSocketRelocationAndScalarRefreshSurviveReopening()
    {
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
        using var temp = new TempFolder();
        string output = temp.Named("output.save");
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

    [TestMethod]
    [TestCategory("Scenario")]
    [TestCategory("PrivateFixtures")]
    public void ScalarRefreshMatchesFullReader()
    {
        foreach (string name in new[] { "early_progress", "fishing_no_first_catch", "editing_baseline" })
        {
            string path = FixtureSaves.Named(name);
            var document = SaveDocument.Open(path);
            byte[] raw = SaveContainer.Decode(File.ReadAllBytes(path)).Blob;
            string original = document.BlobSha256;
            int index = document.Items.ToList().FindIndex(i => i.CanEditQuantity && i.Quantity < 999_999_999);
            for (int run = 0; run < 2; run++)
            {
                var target = document.Items[index];
                document.SetQuantity(target, target.Quantity + 1);
                byte[] candidate = (byte[])raw.Clone();
                BinaryPrimitives.WriteUInt64LittleEndian(candidate.AsSpan(target.QuantityOffset), target.Quantity + 1);
                ParcReader.Validate(candidate, ParcLayout.Parse(candidate));
                var read = Inventory.Read(candidate, ParcLayout.Parse(candidate), Guid.NewGuid());
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
        }
    }
}
