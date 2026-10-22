using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.App;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.Tests;

[TestClass]
public sealed class BackupTests
{
    [TestMethod]
    public void RetentionIsPerSaveAndPreservesPristineAndUnrecognizedNames()
    {
        string root = Path.Combine(Path.GetTempPath(), "saveultimate-backups-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "slot.save");
            File.WriteAllText(source, "backup content");
            string directory = SaveBackups.DirectoryFor(source);
            Directory.CreateDirectory(directory);
            foreach (string name in new[] { "slot.save.PRISTINE.bak", "slot.save.manual.bak", "slot.save.20000101_000000.bak", "other.save.20000101_000000.bak" })
                File.WriteAllText(Path.Combine(directory, name), "keep");
            for (int i = 0; i < 12; i++) SaveBackups.Create(source);
            Assert.AreEqual(12, SaveBackups.List(source).Length);
            Assert.AreEqual(13, Directory.GetFiles(directory).Length);
            string backup = SaveBackups.Create(source)!;
            SaveBackups.Mirror(source, backup, Path.Combine(root, "app"));
            string mirror = Path.Combine(SaveBackups.MirrorDirectory(source, Path.Combine(root, "app")), Path.GetFileName(backup));
            CollectionAssert.AreEqual(File.ReadAllBytes(source), File.ReadAllBytes(mirror));
            Assert.Throws<IOException>(() => SaveBackups.Mirror(source, backup, Path.Combine(root, "app")));
            CollectionAssert.AreEqual(File.ReadAllBytes(source), File.ReadAllBytes(mirror));
            Assert.IsNull(SaveBackups.Create(Path.Combine(root, "missing.save")));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SaveAndRestoreValidateBackUpAndRejectExternalChanges()
    {
        string? fixtures = Environment.GetEnvironmentVariable("SAVEULTIMATE_FIXTURES");
        if (fixtures is null) Assert.Inconclusive("Set SAVEULTIMATE_FIXTURES.");
        string root = Path.Combine(Path.GetTempPath(), "saveultimate-write-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "slot.save");
            File.Copy(Path.Combine(fixtures, "save_v2.03.02.save"), source);
            byte[] original = File.ReadAllBytes(source);
            SaveBackups.Pristine(source);
            var document = SaveDocument.Open(source);
            var item = document.Items.First(i => i.CanEditQuantity);
            document.SetQuantity(item, item.Quantity + 1);
            string? backup = document.SaveTo(source);
            Assert.IsNotNull(backup);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(backup));
            Assert.IsFalse(document.IsDirty);
            Assert.IsFalse(document.CanUndo);
            Assert.AreEqual(document.BlobSha256, SaveDocument.Open(source).BlobSha256);
            byte[] edited = File.ReadAllBytes(source);
            SaveBackups.Pristine(source);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(SaveBackups.PristinePath(source)));
            string? recovery = SaveBackups.Restore(source, backup, SHA256.HashData(edited));
            CollectionAssert.AreEqual(edited, File.ReadAllBytes(recovery!));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(source));
            Assert.Throws<IOException>(() => document.SaveTo(source), "Loaded document is stale after an external restore.");
            CollectionAssert.AreEqual(original, File.ReadAllBytes(source));
            string bad = Path.Combine(SaveBackups.DirectoryFor(source), "slot.save.bad.bak");
            File.WriteAllText(bad, "invalid");
            Assert.Throws<InvalidDataException>(() => SaveBackups.Restore(source, bad, SHA256.HashData(original)));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(source));
            string output = Path.Combine(root, "other.save");
            document.SaveTo(output);
            Assert.AreEqual(output, document.SourcePath);
            Assert.AreEqual(document.BlobSha256, SaveDocument.Open(output).BlobSha256);
            string existing = Path.Combine(root, "existing.save");
            File.WriteAllBytes(existing, original);
            Assert.Throws<IOException>(() => document.SaveTo(existing));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(existing));
            document.SaveTo(existing, expectedDestinationHash: SHA256.HashData(original));
            Assert.AreEqual(existing, document.SourcePath);
            Assert.AreEqual(document.BlobSha256, SaveDocument.Open(existing).BlobSha256);
            SaveBackups.Pristine(source, replace: true);
            File.WriteAllText(source, "invalid pristine source");
            Assert.Throws<InvalidDataException>(() => SaveBackups.Pristine(source, replace: true));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(SaveBackups.PristinePath(source)));
            Assert.IsFalse(Directory.GetFiles(root).Any(p => Path.GetFileName(p).StartsWith(".")));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void BackupFailureCannotOverwriteTheDestination()
    {
        string? fixtures = Environment.GetEnvironmentVariable("SAVEULTIMATE_FIXTURES");
        if (fixtures is null) Assert.Inconclusive("Set SAVEULTIMATE_FIXTURES.");
        string root = Path.Combine(Path.GetTempPath(), "saveultimate-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "slot.save");
            File.Copy(Path.Combine(fixtures, "save_v2.03.02.save"), source);
            byte[] original = File.ReadAllBytes(source);
            var document = SaveDocument.Open(source);
            var item = document.Items.First(i => i.CanEditQuantity);
            document.SetQuantity(item, item.Quantity + 1);
            File.WriteAllText(Path.Combine(root, "backups"), "block directory creation");
            Assert.Throws<IOException>(() => document.SaveTo(source));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(source));
            Assert.IsTrue(document.IsDirty);
            Assert.IsTrue(document.CanUndo);
            document.SaveTo(source, backup: false);
            Assert.IsFalse(document.IsDirty);
            Assert.IsFalse(Directory.GetFiles(root).Any(p => Path.GetFileName(p).StartsWith(".")));
        }
        finally { Directory.Delete(root, true); }
    }

    [STATestMethod]
    public void BackupTabListsOnlyCurrentSaveAndDisablesMutationsWhileBusy()
    {
        string? fixtures = Environment.GetEnvironmentVariable("SAVEULTIMATE_FIXTURES");
        if (fixtures is null) Assert.Inconclusive("Set SAVEULTIMATE_FIXTURES.");
        if (Application.Current is null) { var app = new App.App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown; }
        string root = Path.Combine(Path.GetTempPath(), "saveultimate-backup-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "slot.save");
            File.Copy(Path.Combine(fixtures, "save_v2.03.02.save"), source);
            SaveBackups.Create(source);
            var model = new MainViewModel(SaveDocument.Open(source));
            var window = new MainWindow(model) { Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false };
            try
            {
                window.Show();
                WindowTests.Pump(() => model.LoadFileAsync(source, createPristine: true));
                model.PageIndex = 3; window.UpdateLayout();
                Assert.IsTrue(model.IsBackups);
                Assert.IsFalse(model.HasQuantityEditor);
                Assert.AreEqual(2, model.Backups.Length);
                Assert.IsFalse(model.RestoreBackupCommand.CanExecute(null));
                ((ListBox)window.FindName("BackupList")).SelectedIndex = 0;
                Assert.IsTrue(model.RestoreBackupCommand.CanExecute(null));
                Assert.IsTrue(model.DeleteBackupCommand.CanExecute(null));
                model.Busy = true;
                Assert.IsFalse(model.RestoreBackupCommand.CanExecute(null));
                Assert.IsFalse(model.SaveAsCommand.CanExecute(null));
                model.Busy = false;
                WindowTests.Capture(window, "backups");
                window.Width = 800; window.UpdateLayout();
                Assert.AreEqual(2, Grid.GetRow((ScrollViewer)window.FindName("BackupActions")));
                WindowTests.Capture(window, "backups-small");
                model.PageIndex = 0;
                Assert.IsTrue(model.IsInventory);
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, true); }
    }
}
