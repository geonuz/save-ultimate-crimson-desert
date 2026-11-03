using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.Tests;

[TestClass]
[TestCategory("PrivateFixtures")]
public sealed class SaveIntegrationTests
{
    [TestMethod]
    public void SavedBaselineSurvivesRepeatedEditsAndSaveCopyUndo()
    {
        string? fixtures = FixtureSaves.Scenarios;
        if (fixtures is null) Assert.Inconclusive("Set SAVEULTIMATE_FIXTURES.");
        var document = SaveDocument.Open(Path.Combine(fixtures, "editing_baseline_2.03.02.save"));
        var target = document.Items.First(i => i.CanEditQuantity);
        InventoryItem Target() => document.Items.Single(i => i.Number == target.Number && i.Source == target.Source && i.KeyOffset == target.KeyOffset);
        string original = document.BlobSha256;
        for (int i = 0; i < 4; i++) document.SetQuantity(Target(), 100u + (uint)i);
        for (int i = 0; i < 4; i++) document.Undo();
        Assert.AreEqual(original, document.BlobSha256);
        Assert.IsFalse(document.IsDirty);
        document.SetQuantity(Target(), 321);
        string saved = document.BlobSha256;
        string output = Path.Combine(Path.GetTempPath(), $"SaveUltimate-baseline-{Guid.NewGuid():N}.save");
        try
        {
            document.SaveCopy(output);
            Assert.IsFalse(document.IsDirty);
            for (int i = 0; i < 4; i++) document.SetQuantity(Target(), 500u + (uint)i);
            Assert.IsTrue(document.IsDirty);
            for (int i = 0; i < 4; i++) document.Undo();
            Assert.AreEqual(saved, document.BlobSha256);
            Assert.IsFalse(document.IsDirty);
            document.Undo();
            Assert.AreEqual(original, document.BlobSha256);
            Assert.IsTrue(document.IsDirty);
        }
        finally { if (File.Exists(output)) File.Delete(output); }
    }

    [TestMethod]
    public void ReparseReusesOnlyUnchangedSchemaAndStillRejectsBrokenToc()
    {
        string? fixtures = FixtureSaves.Scenarios;
        if (fixtures is null) Assert.Inconclusive("Set SAVEULTIMATE_FIXTURES.");
        byte[] raw = SaveContainer.Decode(File.ReadAllBytes(Path.Combine(fixtures, "editing_baseline_2.03.02.save"))).Blob;
        var layout = ParcLayout.Parse(raw);
        var candidate = (byte[])raw.Clone();
        Assert.AreSame(layout.Types, layout.Reparse(candidate, raw).Types);
        candidate[20 + 4] ^= 1; // Change a schema name without changing its encoded length.
        Assert.AreNotSame(layout.Types, layout.Reparse(candidate, raw).Types);
        candidate = (byte[])raw.Clone();
        candidate[layout.SchemaEnd] = 1;
        Assert.Throws<InvalidDataException>(() => layout.Reparse(candidate, raw));
        Assert.Throws<InvalidDataException>(() => NativeSave.ValidateBlob(candidate));
        Assert.Throws<InvalidDataException>(() => NativeSave.ValidateBlob([]));
        Assert.Throws<InvalidDataException>(() => NativeSave.ValidateBlob(raw[..(layout.SchemaEnd - 1)]));
        Assert.Throws<InvalidDataException>(() => layout.Reparse(raw[..(layout.SchemaEnd - 1)], raw));
    }

    [TestMethod]
    public void BatchEditsAreAtomicAndDeleteUndoRestoresExactBytes()
    {
        string? fixtures = FixtureSaves.Scenarios;
        if (fixtures is null) Assert.Inconclusive("Set SAVEULTIMATE_FIXTURES for private batch verification.");
        var document = SaveDocument.Open(Path.Combine(fixtures, "editing_baseline_2.03.02.save"));
        string original = document.BlobSha256;
        int count = document.Items.Count;
        Assert.AreEqual(1495, count);
        Assert.AreEqual(34, document.Items.Count(i => i.Source == ItemSource.Vendor));
        Assert.AreEqual(272, document.Items.Count(i => i.Source == ItemSource.Mercenary));
        Assert.AreEqual(1, document.Items.Count(i => i.Key == 1001750), "Reserve-slot cache must not duplicate Sonic Resonator.");
        var mercenary = document.Items.First(i => i.Source == ItemSource.Mercenary);
        Assert.Throws<InvalidOperationException>(() => document.SetQuantity([document.Items[0], mercenary], 123));
        Assert.Throws<InvalidOperationException>(() => document.Delete([document.Items[0], mercenary]));
        Assert.AreEqual(original, document.BlobSha256);
        Assert.IsFalse(document.CanUndo);
        var selected = document.Items.Where(i => i.Bag == 2).Take(3).ToArray();
        Assert.AreEqual(3, selected.Length);
        document.SetQuantity(selected.Concat(selected), 123);
        Assert.AreEqual(3, document.Items.Count(i => selected.Any(s => s.Number == i.Number) && i.Quantity == 123));
        string changed = document.BlobSha256;
        Assert.Throws<InvalidOperationException>(() => document.Delete([document.Items[0], selected[0]]));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.SetQuantity(document.Items.Take(2), 0));
        Assert.AreEqual(changed, document.BlobSha256);
        document.SetQuantity(document.Items.Where(i => selected.Any(s => s.Number == i.Number)), 123);
        document.Undo();
        Assert.AreEqual(original, document.BlobSha256);
        Assert.IsFalse(document.CanUndo, "A no-op batch must not add an undo entry.");
        selected = document.Items.Where(i => i.Bag == 2).Take(3).ToArray();
        document.Delete(selected.Concat(selected));
        Assert.AreEqual(count - 3, document.Items.Count);
        Assert.IsFalse(document.Items.Any(i => selected.Any(s => s.Number == i.Number)));
        string output = Path.Combine(Path.GetTempPath(), $"SaveUltimate-delete-{Guid.NewGuid():N}.save");
        try
        {
            document.SaveCopy(output);
            Assert.AreEqual(document.BlobSha256, SaveDocument.Open(output).BlobSha256);
        }
        finally { if (File.Exists(output)) File.Delete(output); }
        document.Undo();
        Assert.AreEqual(original, document.BlobSha256);
        Assert.AreEqual(count, document.Items.Count);
        Assert.IsTrue(document.IsDirty, "Undo after saving must differ from the saved deletion.");
        Assert.IsFalse(document.CanUndo);
    }

    [TestMethod]
    public void ItemSwapChangesOnlyVendorKeysAndUndoesExactly()
    {
        string? fixtures = FixtureSaves.Scenarios;
        if (fixtures is null) Assert.Inconclusive("Set SAVEULTIMATE_FIXTURES.");
        const uint key = 1002785;
        foreach (var (file, source, number) in new[] { ("old_version_2.00.02.save", ItemSource.Inventory, 334UL), ("old_version_2.00.02.save", ItemSource.Equipment, 1000813UL),
            ("old_version_2.00.02.save", ItemSource.Vendor, 1001256UL), ("old_version_2.00.02.save", ItemSource.Mercenary, 3043UL), ("editing_baseline_2.03.02.save", ItemSource.Inventory, 33UL),
            ("editing_baseline_2.03.02.save", ItemSource.Equipment, 1883UL), ("editing_baseline_2.03.02.save", ItemSource.Vendor, 1002441UL), ("editing_baseline_2.03.02.save", ItemSource.Mercenary, 3043UL) })
        {
            var document = SaveDocument.Open(Path.Combine(fixtures, file));
            InventoryItem Target() => document.Items.Single(i => i.Source == source && i.Number == number);
            var stale = Target();
            string original = document.BlobSha256;
            Assert.Throws<ArgumentOutOfRangeException>(() => document.SwapVendorItem(stale, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => document.SwapVendorItem(stale, uint.MaxValue));
            if (source != ItemSource.Vendor)
            {
                Assert.Throws<InvalidOperationException>(() => document.SwapVendorItem(stale, key));
                Assert.AreEqual(original, document.BlobSha256);
                Assert.IsFalse(document.CanUndo);
                continue;
            }
            document.SwapVendorItem(stale, stale.Key);
            Assert.IsFalse(document.CanUndo);
            document.SwapVendorItem(stale, key);
            Assert.AreEqual(key, Target().Key);
            Assert.AreEqual(stale.Quantity, Target().Quantity);
            Assert.AreEqual(stale.EnchantLevel, Target().EnchantLevel);
            Assert.Throws<InvalidOperationException>(() => document.SwapVendorItem(stale, key));
            document.SwapVendorItem(Target(), key);
            string output = Path.Combine(Path.GetTempPath(), $"SaveUltimate-swap-{Guid.NewGuid():N}.save");
            try
            {
                document.SaveCopy(output);
                Assert.AreEqual(document.BlobSha256, SaveDocument.Open(output).BlobSha256);
                byte[] before = SaveContainer.Decode(File.ReadAllBytes(document.SourcePath)).Blob;
                byte[] after = SaveContainer.Decode(File.ReadAllBytes(output)).Blob;
                Assert.AreEqual(before.Length, after.Length);
                // Only item-key fields may change: every differing byte lies in a u32 that held the old key and now holds the new one.
                var changed = Enumerable.Range(0, before.Length).Where(i => before[i] != after[i]).ToArray();
                Assert.IsTrue(changed.Length > 0);
                foreach (int i in changed)
                    Assert.IsTrue(Enumerable.Range(i - 3, 4).Any(start => start >= 0 && start + 4 <= before.Length &&
                        BitConverter.ToUInt32(before, start) == stale.Key && BitConverter.ToUInt32(after, start) == key), $"{file} byte {i}");
            }
            finally { if (File.Exists(output)) File.Delete(output); }
            document.Undo();
            Assert.AreEqual(original, document.BlobSha256);
            Assert.IsFalse(document.CanUndo);
        }
    }

    [TestMethod]
    public void SocketUnlockHandlesPresentAndOmittedCounts()
    {
        string? fixtures = FixtureSaves.Scenarios;
        if (fixtures is null) Assert.Inconclusive("Set SAVEULTIMATE_FIXTURES.");
        // Item 2069 stores its unlocked count; 1002966 omits it, so unlocking inserts the 1-byte field.
        foreach (var (number, count, inserted) in new[] { (2069UL, 5, 0), (1002966UL, 1, 1) })
        {
            var document = SaveDocument.Open(Path.Combine(fixtures, "editing_baseline_2.03.02.save"));
            InventoryItem Target() => document.Items.Single(i => i.Source == ItemSource.Inventory && i.Number == number);
            string original = document.BlobSha256;
            var stale = Target();
            Assert.Throws<InvalidOperationException>(() => document.SetUnlockedSockets(stale, -1));
            Assert.Throws<InvalidOperationException>(() => document.SetUnlockedSockets(stale, SocketState.Limit(stale.Key) + 1));
            Assert.AreEqual(original, document.BlobSha256);
            Assert.IsFalse(document.CanUndo);
            document.SetUnlockedSockets(stale, count);
            string unlocked = document.BlobSha256;
            Assert.AreNotEqual(original, unlocked);
            Assert.AreEqual(count, Target().Sockets.Unlocked);
            document.SetUnlockedSockets(Target(), count);
            Assert.Throws<InvalidOperationException>(() => document.SetUnlockedSockets(stale, count));
            document.SetSockets(Target(), Enumerable.Repeat(1002785u, count));
            string installed = document.BlobSha256;
            Assert.Throws<InvalidOperationException>(() => document.SetUnlockedSockets(Target(), count - 1));
            Assert.AreEqual(installed, document.BlobSha256);
            string output = Path.Combine(Path.GetTempPath(), $"SaveUltimate-unlock-{Guid.NewGuid():N}.save");
            try
            {
                document.SaveCopy(output);
                Assert.AreEqual(installed, SaveDocument.Open(output).BlobSha256);
                int delta = SaveContainer.Decode(File.ReadAllBytes(output)).Blob.Length - SaveContainer.Decode(File.ReadAllBytes(document.SourcePath)).Blob.Length;
                Assert.AreEqual(inserted + stale.Sockets.Slots.Take(count).Count(s => !s.HasGem) * 6, delta);
            }
            finally { if (File.Exists(output)) File.Delete(output); }
            document.Undo();
            Assert.AreEqual(unlocked, document.BlobSha256);
            document.Undo();
            Assert.AreEqual(original, document.BlobSha256);
            Assert.IsFalse(document.CanUndo, "No-op unlock must not create an undo entry.");
        }
    }

    [TestMethod]
    public void EnchantUsesCapsAllowsMercenariesAndUndoesExactly()
    {
        string? fixtures = FixtureSaves.Scenarios;
        if (fixtures is null) Assert.Inconclusive("Set SAVEULTIMATE_FIXTURES for enchant verification.");
        var document = SaveDocument.Open(Path.Combine(fixtures, "editing_baseline_2.03.02.save"));
        string original = document.BlobSha256;
        var mercenary = document.Items.First(i => i.Source == ItemSource.Mercenary && i.EnchantLevel.HasValue && i.MaxEnchant >= 2);
        var nonEquipment = document.Items.First(i => !i.EnchantLevel.HasValue);
        // Real fixtures have +10 caps; substitute a catalog key in a temporary test save to exercise +4.
        string cappedPath = Path.Combine(Path.GetTempPath(), $"SaveUltimate-cap-fixture-{Guid.NewGuid():N}.save");
        try
        {
            var raw = SaveContainer.Decode(File.ReadAllBytes(document.SourcePath)).Blob;
            var seed = document.Items.First(i => i.Source == ItemSource.Inventory && i.EnchantLevel.HasValue);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(seed.KeyOffset, 4), 1001071);
            NativeSave.Write(document.SourcePath, raw, cappedPath);
            var cappedDocument = SaveDocument.Open(cappedPath);
            var capped = cappedDocument.Items.Single(i => i.Key == 1001071);
            var other = cappedDocument.Items.First(i => i.Source == ItemSource.Mercenary && i.EnchantLevel.HasValue);
            Assert.AreEqual(4, capped.MaxEnchant);
            string beforeCap = cappedDocument.BlobSha256;
            Assert.Throws<InvalidOperationException>(() => cappedDocument.SetEnchant([other, capped], 5));
            Assert.AreEqual(beforeCap, cappedDocument.BlobSha256);
            Assert.IsFalse(cappedDocument.CanUndo);
            cappedDocument.SetEnchant([capped], 4);
            Assert.AreEqual((ushort?)4, cappedDocument.Items.Single(i => i.Key == 1001071).EnchantLevel);
        }
        finally { if (File.Exists(cappedPath)) File.Delete(cappedPath); }
        Assert.Throws<ArgumentOutOfRangeException>(() => document.SetEnchant([mercenary], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.SetEnchant([mercenary], 11));
        Assert.AreEqual(original, document.BlobSha256);
        Assert.IsFalse(document.CanUndo);
        document.SetEnchant([nonEquipment], 1);
        Assert.IsFalse(document.CanUndo);
        ushort next = mercenary.EnchantLevel == 1 ? (ushort)2 : (ushort)1;
        document.SetEnchant([mercenary, mercenary, nonEquipment], next);
        var edited = document.Items.Single(i => i.Source == mercenary.Source && i.OwnerNumber == mercenary.OwnerNumber && i.Number == mercenary.Number);
        Assert.AreEqual((ushort?)next, edited.EnchantLevel);
        Assert.Throws<InvalidOperationException>(() => document.SetEnchant([mercenary], 1));
        document.SetEnchant([edited], next);
        string output = Path.Combine(Path.GetTempPath(), $"SaveUltimate-enchant-{Guid.NewGuid():N}.save");
        try
        {
            document.SaveCopy(output);
            Assert.AreEqual(document.BlobSha256, SaveDocument.Open(output).BlobSha256);
        }
        finally { if (File.Exists(output)) File.Delete(output); }
        document.Undo();
        Assert.AreEqual(original, document.BlobSha256);
        Assert.IsFalse(document.CanUndo, "Duplicate/no-op/non-enchantable items must not add undo entries.");
    }

    [TestMethod]
    public void SavedCopyReopensAndExistingDestinationsSurvive()
    {
        string? fixtures = FixtureSaves.Scenarios;
        if (fixtures is null) Assert.Inconclusive("Set SAVEULTIMATE_FIXTURES for private write verification.");
        string directory = Path.Combine(Path.GetTempPath(), "SaveUltimate-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(fixtures, "editing_baseline_2.03.02.save");
            var document = SaveDocument.Open(source);
            var item = document.Items.First(i => i.Source == ItemSource.Equipment && i.Quantity < 999_999_999);
            document.SetQuantity(item, item.Quantity + 1);
            var vendor = document.Items.First(i => i.Source == ItemSource.Vendor);
            string beforeVendor = document.BlobSha256;
            document.Delete([vendor]);
            document.Undo();
            Assert.AreEqual(beforeVendor, document.BlobSha256);
            vendor = document.Items.First(i => i.Source == ItemSource.Vendor);
            document.SetQuantity(vendor, vendor.Quantity + 1);
            string output = Path.Combine(directory, "수정본.save");
            document.SaveCopy(output);
            var reopened = SaveDocument.Open(output);
            Assert.AreEqual(document.BlobSha256, reopened.BlobSha256);
            byte[] saved = File.ReadAllBytes(output);
            Assert.Throws<IOException>(() => document.SaveCopy(output));
            CollectionAssert.AreEqual(saved, File.ReadAllBytes(output));
            Assert.Throws<IOException>(() => document.SaveCopy(source));
            byte[] broken = (byte[])saved.Clone(); broken[^1] ^= 1;
            Assert.Throws<InvalidDataException>(() => SaveContainer.Decode(broken));
            Assert.Throws<InvalidDataException>(() => NativeSave.Write(source, [0, 1, 2], output));
            CollectionAssert.AreEqual(saved, File.ReadAllBytes(output));
        }
        finally { Directory.Delete(directory, true); }
    }
}
