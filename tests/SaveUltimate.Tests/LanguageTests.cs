using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.App;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.Tests;

[TestClass]
public sealed class LanguageTests
{
    [TestMethod]
    [TestCategory("UI")]
    [DoNotParallelize]
    public void DefaultEnglishAndFourLanguagesHaveMatchingKeysAndPlaceholders()
    {
        Assert.AreEqual("en", new EditorSettings().Language);
        Assert.AreEqual("en", SettingsStore.Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")).Language);
        var packs = new[] { "en", "ko", "ja", "ru" }.ToDictionary(l => l, l =>
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
            var socket = new SocketRow(0, 4294967294, true, false);
            foreach (string language in packs.Keys)
            {
                Tr.SetLanguage(language);
                socket.RefreshLanguage();
                Assert.AreEqual(packs[language]["tab.fishing"], Tr.Text("tab.fishing"));
                Assert.AreEqual(packs[language]["ui.undo"], Tr.Text("ui.undo"));
                Assert.AreNotEqual("ui.revert_changes", Tr.Text("ui.revert_changes"));
                Assert.AreEqual(Tr.Text("tab.inventory"), storage.Name);
                Assert.AreEqual(4294967294u, socket.SelectedKey);
                Assert.IsTrue(socket.Choices.Any(c => c.Key == socket.SelectedKey));
                Assert.AreEqual(Tr.Text("map.all"), socket.Category);
                if (language == "en")
                {
                    var name = SkillCatalog.Shared.Names.First();
                    Assert.AreEqual(name.Value[0], ItemCatalog.Shared.KnowledgeName(name.Key));
                }
            }
        }
        finally { Tr.SetLanguage("ko"); }
    }
}
