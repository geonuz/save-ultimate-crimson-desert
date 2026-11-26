using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using CDSE.App;
using CDSE.Core.Saves;

namespace CDSE.Tests;

[TestClass]
public sealed class LanguageTests
{
    [TestMethod]
    [TestCategory("UI")]
    [DoNotParallelize]
    public void DefaultEnglishAndEveryLanguageHaveMatchingKeysAndPlaceholders()
    {
        Assert.AreEqual("en", new EditorSettings().Language);
        Assert.AreEqual("en", SettingsStore.Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")).Language);
        // The settings list and the game language groups must name the same languages.
        CollectionAssert.AreEquivalent(CDSE.Core.Catalogs.ClientMetadata.Languages.Keys.ToArray(), Tr.Languages.Select(l => l.Code).ToArray());
        var packs = Tr.Languages.Select(l => l.Code).ToDictionary(l => l, l =>
            JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "resources/locale", l + ".json")))!);
        foreach (var (language, pack) in packs)
            foreach (var (key, value) in packs["en"])
            {
                Assert.IsTrue(pack.ContainsKey(key), $"{language}: {key}");
                CollectionAssert.AreEqual(Regex.Matches(value, @"\{\w+\}").Select(m => m.Value).Distinct().Order().ToArray(),
                    Regex.Matches(pack[key], @"\{\w+\}").Select(m => m.Value).Distinct().Order().ToArray(), $"{language}: {key}");
            }
        try
        {
            var storage = new StorageFilter("tab.inventory");
            var socket = new SocketRow(0, 0, 4294967294, true, false);
            foreach (string language in packs.Keys)
            {
                Tr.SetLanguage(language);
                socket.RefreshLanguage();
                Assert.AreEqual(packs[language]["tab.fishing"], Tr.Text("tab.fishing"));
                Assert.AreEqual(packs[language]["ui.undo"], Tr.Text("ui.undo"));
                Assert.AreNotEqual("ui.revert_changes", Tr.Text("ui.revert_changes"));
                Assert.AreEqual(Tr.Text("tab.inventory"), storage.Name);
                Assert.AreEqual(4294967294u, socket.SelectedKey);
                Assert.IsTrue(socket.Choices().Any(c => c.Key == socket.SelectedKey));
                Assert.AreEqual(Tr.Format("port.socket", ("number", 1)), socket.Label);
                if (language == "en")
                {
                    var name = SkillCatalog.Shared.Names.First();
                    Assert.AreEqual(name.Value[0], ItemCatalog.Shared.KnowledgeName(name.Key));
                }
            }
        }
        finally { Tr.SetLanguage("ko"); }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void EveryLiteralCaptionHasALocaleEntry()
    {
        // A missing key shows the key itself (English) in every language. XAML escapes & as &amp;, so a key scan of the
        // raw text misses "A && B" captions: the 1.8.2 cleanup dropped one that way.
        var english = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(Path.Combine(TestFiles.Resources, "locale/en.json")))!;
        string app = Path.Combine(Path.GetDirectoryName(TestFiles.Resources)!, "src", "CDSE.App");
        var missing = new List<string>();
        foreach (string path in Directory.GetFiles(app, "*.xaml", SearchOption.AllDirectories).Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
            foreach (Match m in Regex.Matches(File.ReadAllText(path), @"\{local:Tr (?:'([^']*)'|([^}']+))\}"))
            {
                string key = System.Net.WebUtility.HtmlDecode(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value.Trim());
                if (!Known(key)) missing.Add($"{Path.GetFileName(path)}: {key}");
            }
        foreach (string path in Directory.GetFiles(app, "*.cs", SearchOption.AllDirectories).Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
            // Whole literals only: Tr.Text("prefix." + name) builds its key at run time.
            foreach (Match m in Regex.Matches(File.ReadAllText(path), @"Tr\.(?:Text\(""((?:[^""\\]|\\.)*)""\)|Format\(""((?:[^""\\]|\\.)*)"",)|Tr\.Bind\([^;]*?,\s*""((?:[^""\\]|\\.)*)""\)"))
            {
                string key = Regex.Unescape(m.Groups.Values.Skip(1).First(g => g.Success).Value);
                if (!Known(key)) missing.Add($"{Path.GetFileName(path)}: {key}");
            }
        // ThemedMessageBox labels its buttons with the result's name, built at run time.
        foreach (string key in new[] { "OK", "Cancel", "Yes", "No" })
            if (!english.ContainsKey(key)) missing.Add("ThemedMessageBox: " + key);
        Assert.AreEqual(0, missing.Count, string.Join("\n", missing.Distinct()));
        // Tr.Text also takes an English caption ("View") and finds its key by value.
        bool Known(string key) => english.ContainsKey(key) || english.ContainsValue(key);
    }
}
