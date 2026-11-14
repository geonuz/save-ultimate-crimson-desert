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
    [TestCategory("Unit")]
    public void RetentionIsPerSaveAndPreservesPristineAndUnrecognizedNames()
    {
        using var temp = new TempFolder();
        string root = temp.Root;
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
}
