using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.App;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.Tests;

[TestClass]
public sealed class AppearanceTests
{
    [STATestMethod]
    [TestCategory("UI")]
    [DoNotParallelize]
    public void TranslatedNameLookupMatchesEveryEntryAndPreservesFallbacks()
    {
        string previous = Tr.Language;
        try
        {
            foreach (string language in new[] { "en", "ko", "ja", "ru" })
            {
                Tr.SetLanguage(language);
                Assert.AreEqual("fallback", Tr.Name("missing-category", "missing-key", "fallback"));
                if (language == "en") continue;
                using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(
                    Path.Combine(AppContext.BaseDirectory, "resources", "locale", $"names_{language}.json")));
                foreach (var category in json.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == System.Text.Json.JsonValueKind.Object))
                {
                    Assert.AreEqual("fallback", Tr.Name(category.Name, "missing-key", "fallback"));
                    foreach (var entry in category.Value.EnumerateObject())
                        Assert.AreEqual(entry.Value.GetString() ?? "fallback", Tr.Name(category.Name, entry.Name, "fallback"));
                }
            }
        }
        finally { Tr.SetLanguage(previous); }
    }

    [TestMethod]
    [TestCategory("UI")]
    [DoNotParallelize]
    public void SettingsRoundTripAndSaveDiscoveryPreserveFilesAndMissingRecents()
    {
        using var temp = new TempFolder();
        string root = temp.Root;
        string oldPath = SettingsStore.FilePath; var old = SettingsStore.Current;
        try
        {
            string account = Path.Combine(root, "account/slot100"); Directory.CreateDirectory(account);
            File.WriteAllText(Path.Combine(account, "save.save"), "read-only save");
            File.WriteAllText(Path.Combine(root, "lobby.save"), "excluded");
            Directory.CreateDirectory(Path.Combine(root, "backups")); File.WriteAllText(Path.Combine(root, "backups", "hidden.save"), "excluded");
            var settings = new EditorSettings { Language = "ko", SaveRoot = root, AutoGameFolder = false, RecentSaveFiles = [Path.Combine(root, "missing.save")],
                Theme = "custom", FontScale = 1.25, UiScale = 110, CustomThemeColors = new() { ["bg"] = "#123456" } };
            SettingsStore.FilePath = Path.Combine(root, "config.json"); SettingsStore.Save(settings);
            var read = SettingsStore.Read(SettingsStore.FilePath);
            Assert.AreEqual(settings.FontScale, read.FontScale); Assert.AreEqual("#123456", read.CustomThemeColors["bg"]);
            string malformed = Path.Combine(root, "malformed.json");
            File.WriteAllText(malformed, """{"theme":null,"save_root":null,"recent_save_files":[null,"relative.save"],"custom_theme_colors":null}""");
            var defaults = SettingsStore.Read(malformed);
            Assert.AreEqual("dark", defaults.Theme); Assert.AreEqual("", defaults.SaveRoot); Assert.AreEqual(0, defaults.RecentSaveFiles.Length);
            var rows = SaveLocations.Find(read);
            Assert.IsTrue(rows.Any(r => r.Name == "Save Slot 1"));
            Assert.IsFalse(rows.Any(r => r.FileName is "lobby.save" or "hidden.save"));
            Assert.IsTrue(rows.Any(r => r.Category == "manual" && r.Modified is null));
            Assert.AreEqual("read-only save", File.ReadAllText(Path.Combine(account, "save.save")));
        }
        finally
        {
            SettingsStore.Save(old); SettingsStore.FilePath = oldPath;
        }
    }
}
