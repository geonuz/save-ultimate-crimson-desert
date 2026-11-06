using System.Buffers.Binary;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.App;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.Tests;

[TestClass]
public sealed class DoctorTests
{
    private const string Tested = "2.04.08";

    private static SaveCheck[] Run(string path) => SaveCheckup.Run(path, Tested);
    private static SaveCheck Check(SaveCheck[] checks, string id) => checks.Single(c => c.Id == id);

    private static string Copy(string directory, string purpose)
    {
        string path = Path.Combine(directory, purpose + ".save");
        File.Copy(FixtureSaves.Named(purpose), path);
        return path;
    }

    // A new file with these records and a valid signature, the way a faulty writer would leave it.
    private static void Rewrite(string path, Action<byte[]> edit)
    {
        byte[] file = File.ReadAllBytes(path), blob = SaveContainer.Decode(file).Blob;
        edit(blob);
        File.WriteAllBytes(path, SaveContainer.Encode(file, blob));
    }

    private static void BreakBlock(string path) => Rewrite(path, blob =>
    {
        var block = ParcLayout.Parse(blob).Blocks.First(b => b.Name == "InventorySaveData");
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(block.Offset), 0xFFF);
    });

    [TestMethod]
    [TestCategory("Scenario")]
    [TestCategory("PrivateFixtures")]
    public void EverySaveGetsItsKnownVerdictAndNothingIsWritten()
    {
        string root = FixtureSaves.Root ?? throw new AssertInconclusiveException("Set SAVEULTIMATE_FIXTURES.");
        var failures = new List<string>();
        foreach (string path in Directory.GetFiles(root, "*.save", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(path), folder = Path.GetFileName(Path.GetDirectoryName(path))!;
            byte[] before = File.ReadAllBytes(path);
            var checks = Run(path);
            var odd = checks.Where(c => c.Status != CheckStatus.Ok).Select(c => c.Id).ToArray();
            string[] expected = folder switch
            {
                "damaged" => ["mission_rows"],
                // Raised by another editor, and worth pointing out.
                "u2" => ["sockets"],
                "unsupported" => checks.Select(c => c.Id).SkipWhile(id => id != "game_version").ToArray(),
                _ => []
            };
            if (!odd.SequenceEqual(expected)) failures.Add($"{name}: {string.Join(", ", odd)}");
            CollectionAssert.AreEqual(before, File.ReadAllBytes(path), name);
        }
        Assert.AreEqual("", string.Join(Environment.NewLine, failures));
    }

    [TestMethod]
    [TestCategory("Scenario")]
    [TestCategory("PrivateFixtures")]
    public void WhatCannotBeReadStopsTheChecksThatNeedIt()
    {
        using var temp = new TempFolder();
        string directory = temp.Root;
        string path = Copy(directory, "editing_baseline");
        byte[] good = File.ReadAllBytes(path);

        File.WriteAllBytes(path, good[..100]);
        var cut = Run(path);
        Assert.AreEqual(CheckStatus.Error, Check(cut, "container").Status);
        Assert.IsTrue(cut.Skip(1).All(c => c.Status == CheckStatus.NotRun));

        byte[] flipped = (byte[])good.Clone(); flipped[200] ^= 0xFF;
        File.WriteAllBytes(path, flipped);
        var damaged = Run(path);
        Assert.AreEqual(CheckStatus.Error, Check(damaged, "integrity").Status);
        if (Check(damaged, "integrity").Fix != SaveFix.None) Assert.AreEqual(CheckStatus.Ok, Check(damaged, "pointers").Status, "Only readable contents may be signed again.");

        File.WriteAllBytes(path, good);
        BreakBlock(path);
        var broken = Run(path);
        Assert.AreEqual(CheckStatus.Ok, Check(broken, "integrity").Status);
        Assert.AreEqual(CheckStatus.Error, Check(broken, "blocks").Status);
        Assert.AreEqual(1, Check(broken, "blocks").Count);
        StringAssert.StartsWith(Check(broken, "blocks").Detail, "InventorySaveData:");
        Assert.AreEqual(CheckStatus.NotRun, Check(broken, "items").Status);
    }

    [TestMethod]
    [TestCategory("Scenario")]
    [TestCategory("PrivateFixtures")]
    public void ABadSignatureAloneIsRepairedWithoutTouchingTheRecords()
    {
        using var temp = new TempFolder();
        string directory = temp.Root;
        string path = Copy(directory, "editing_baseline");
        byte[] good = File.ReadAllBytes(path), blob = SaveContainer.Decode(good).Blob;
        byte[] unsigned = (byte[])good.Clone(); unsigned[0x2a] ^= 0xFF;
        File.WriteAllBytes(path, unsigned);

        var checks = Run(path);
        Assert.AreEqual(SaveFix.Integrity, Check(checks, "integrity").Fix);
        Assert.IsTrue(checks.Where(c => c.Id != "integrity").All(c => c.Status == CheckStatus.Ok));
        Assert.ThrowsExactly<InvalidDataException>(() => SaveDocument.Open(path));

        SaveDocument.Repair(path, directory, new HashSet<SaveFix> { SaveFix.Integrity });
        CollectionAssert.AreEqual(blob, SaveContainer.Decode(File.ReadAllBytes(path)).Blob);
        Assert.IsTrue(Run(path).All(c => c.Status == CheckStatus.Ok));
        CollectionAssert.AreEqual(unsigned, File.ReadAllBytes(SaveBackups.List(path).Single()), "The damaged file is kept.");
    }

    [TestMethod]
    [TestCategory("Scenario")]
    [TestCategory("PrivateFixtures")]
    public void RepairDoesOnlyWhatWasChosen()
    {
        using var temp = new TempFolder();
        string directory = temp.Root;
        string path = Copy(directory, "mission_rows_8");
        byte[] damaged = File.ReadAllBytes(path);
        SaveDocument.Repair(path, directory, new HashSet<SaveFix>());
        CollectionAssert.AreEqual(damaged, File.ReadAllBytes(path));

        SaveDocument.Repair(path, directory, new HashSet<SaveFix> { SaveFix.MissionRows });
        var checks = Run(path);
        Assert.AreEqual(CheckStatus.Ok, Check(checks, "mission_rows").Status);
        Assert.IsTrue(checks.All(c => c.Status == CheckStatus.Ok));
    }

    [TestMethod]
    [TestCategory("Scenario")]
    [TestCategory("PrivateFixtures")]
    public void EnchantsPastTheLimitAreSuspectAndLoweredOnlyOnRequest()
    {
        using var temp = new TempFolder();
        string directory = temp.Root;
        string path = Copy(directory, "editing_baseline");
        var document = SaveDocument.Open(path);
        var item = document.Items.First(i => i is { Source: ItemSource.Inventory, EnchantLevel: not null, MaxEnchant: > 0 });
        var others = document.Items.Where(i => i != item).Select(i => (i.Number, i.Key, i.EnchantLevel)).ToArray();
        Rewrite(path, blob => BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(item.EnchantOffset), (ushort)(item.MaxEnchant + 1)));

        var enchants = Check(Run(path), "enchants");
        Assert.AreEqual(CheckStatus.Suspect, enchants.Status);
        Assert.AreEqual((1, SaveFix.Enchants), (enchants.Count, enchants.Fix));

        SaveDocument.Repair(path, directory, new HashSet<SaveFix> { SaveFix.Enchants });
        Assert.AreEqual(CheckStatus.Ok, Check(Run(path), "enchants").Status);
        var after = SaveDocument.Open(path).Items;
        Assert.AreEqual((ushort?)item.MaxEnchant, after.Single(i => i.Number == item.Number && i.Source == item.Source).EnchantLevel);
        CollectionAssert.AreEqual(others, after.Where(i => !(i.Number == item.Number && i.Source == item.Source)).Select(i => (i.Number, i.Key, i.EnchantLevel)).ToArray());
    }

    [TestMethod]
    [TestCategory("Scenario")]
    [TestCategory("PrivateFixtures")]
    public void TheLatestGoodBackupRestoresWhatRepairCannot()
    {
        using var temp = new TempFolder();
        string directory = temp.Root;
        string path = Copy(directory, "editing_baseline");
        byte[] good = File.ReadAllBytes(path);
        string kept = SaveBackups.Create(path)!;
        BreakBlock(path);
        byte[] broken = File.ReadAllBytes(path);
        string useless = SaveBackups.Create(path)!;
        File.SetLastWriteTimeUtc(useless, File.GetLastWriteTimeUtc(kept).AddHours(1));

        Assert.AreEqual(Path.GetFullPath(kept), SaveDocument.LatestGoodBackup(path), "A newer backup that does not open is passed over.");
        SaveDocument.RestoreBackup(path, kept, directory);
        CollectionAssert.AreEqual(good, File.ReadAllBytes(path));
        Assert.IsTrue(SaveBackups.List(path).Any(b => File.ReadAllBytes(b).AsSpan().SequenceEqual(broken)), "The damaged file is kept.");
    }

    [TestMethod]
    [TestCategory("Scenario")]
    [TestCategory("PrivateFixtures")]
    public void ABackupWithDamagedRowsIsNotOffered()
    {
        using var temp = new TempFolder();
        string directory = temp.Root;
        string path = Copy(directory, "mission_rows_8");
        SaveBackups.Create(path);
        Assert.IsNull(SaveDocument.LatestGoodBackup(path));
    }

    internal static SlotRow Slot(string path) => new(new SaveSlot(path, "manual", "Custom", "", Path.GetFileNameWithoutExtension(path), File.GetLastWriteTime(path)));

    [STATestMethod]
    [TestCategory("UI")]
    [DoNotParallelize]
    [TestCategory("PrivateFixtures")]
    public void TheWindowShowsEachVerdictAndOffersOnlyWhatCanBeDone()
    {
        TestUi.EnsureApp();
        var old = SettingsStore.Current;
        SettingsStore.Save(old with { SaveRoot = FixtureSaves.Root ?? throw new AssertInconclusiveException("Set SAVEULTIMATE_FIXTURES."), AutoGameFolder = false });
        var doctor = TestUi.Hidden(new DoctorWindow(new MainViewModel()));
        try
        {
            doctor.Show();
            WindowTests.Pump(doctor.Scan);
            var listed = doctor.Slots.Items.Cast<SlotRow>().ToArray();
            Assert.IsTrue(listed.Length > 10);
            Assert.AreEqual(CheckStatus.Error, listed.Single(r => r.Path.EndsWith("mission_rows_41.save")).Status);
            Assert.AreEqual(CheckStatus.Error, listed.Single(r => r.Path.EndsWith("item_tail_zeros.save")).Status, "The list agrees with what opening the slot shows.");
            Assert.AreEqual(CheckStatus.Suspect, listed.Single(r => r.Path.EndsWith("other_editor_layout.save")).Status);

            WindowTests.Pump(() => doctor.Inspect(Slot(FixtureSaves.Named("late_latest_progress"))));
            Assert.AreEqual(CheckStatus.Ok, doctor.View.Status);
            Assert.IsFalse(doctor.View.HasFixes); Assert.AreEqual(0, doctor.View.Issues.Length);
            WindowTests.Capture(doctor, "doctor-clean");

            WindowTests.Pump(() => doctor.Inspect(Slot(FixtureSaves.Named("mission_rows_8"))));
            Assert.AreEqual(CheckStatus.Error, doctor.View.Status);
            Assert.IsTrue(doctor.View.CanRepair, "Errors with a repair start out selected.");
            WindowTests.Capture(doctor, "doctor-damaged");

            WindowTests.Pump(() => doctor.Inspect(Slot(FixtureSaves.Named("other_editor_layout"))));
            Assert.AreEqual(CheckStatus.Suspect, doctor.View.Status);
            Assert.IsFalse(doctor.View.HasFixes);
            WindowTests.Capture(doctor, "doctor-suspect");

            using var temp = new TempFolder();
            string broken = Copy(temp.Root, "editing_baseline");
            SaveBackups.Create(broken);
            BreakBlock(broken);
            WindowTests.Pump(() => doctor.Inspect(Slot(broken)));
            Assert.AreEqual(CheckStatus.Error, doctor.View.Status);
            Assert.IsFalse(doctor.View.HasFixes);
            Assert.IsTrue(doctor.View.CanRestore, "A good backup exists.");
            Assert.AreEqual(CheckStatus.NotRun, doctor.Checks["items"].Status);
            WindowTests.Capture(doctor, "doctor-broken");
        }
        finally { doctor.Close(); SettingsStore.Save(old); }
    }
}
