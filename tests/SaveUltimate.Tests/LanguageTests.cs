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
    private static string oldPath = "";
    private static EditorSettings old = new();
    [AssemblyInitialize]
    public static void Initialize(TestContext _)
    {
        old = SettingsStore.Current; oldPath = SettingsStore.FilePath;
        SettingsStore.FilePath = Path.Combine(Path.GetTempPath(), "save-tests-" + Guid.NewGuid().ToString("N") + ".json");
        SettingsStore.Save(old with { Language = "ko" });
        Tr.SetLanguage("ko");
    }
    [AssemblyCleanup]
    public static void Cleanup()
    {
        string temporary = SettingsStore.FilePath;
        SettingsStore.Save(old); SettingsStore.FilePath = oldPath; File.Delete(temporary);
    }
    [TestMethod]
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
    [STATestMethod]
    [TestCategory("PrivateFixtures")]
    public void EveryGridHeaderFitsItsColumnInEveryLanguage()
    {
        string fixtures = FixtureSaves.Scenarios ?? throw new AssertInconclusiveException("Set fixtures.");
        if (System.Windows.Application.Current is null) { var app = new App.App(); app.InitializeComponent(); app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown; }
        var clipped = new List<string>();
        try
        {
            foreach (string language in new[] { "ko", "en", "ja", "ru" })
            {
                Tr.SetLanguage(language);
                var model = new MainViewModel(SaveDocument.Open(Path.Combine(fixtures, "dlc_bonds_per_character_2.04.03.save")));
                var window = new MainWindow(model) { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
                window.Show();
                try
                {
                    void Check(string page)
                    {
                        window.UpdateLayout();
                        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                        foreach (var header in Find<System.Windows.Controls.Primitives.DataGridColumnHeader>(window).Where(h => h.IsVisible && h.Column is not null && h.Content is string { Length: > 0 }))
                            foreach (var text in Find<System.Windows.Controls.TextBlock>(header).Where(t => t.Text == (string)header.Content))
                            {
                                text.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                                // The text block keeps its full width; the header cell around it is what clips.
                                double room = header.ActualWidth - header.Padding.Left - header.Padding.Right;
                                if (text.DesiredSize.Width > room + 0.5) clipped.Add($"{language} {page}: {header.Content} ({room:F0} < {text.DesiredSize.Width:F0})");
                            }
                    }
                    var nav = (System.Windows.Controls.ListBox)window.FindName("PrimaryNavigation");
                    nav.SelectedIndex = 0;
                    var items = (System.Windows.Controls.TabControl)window.FindName("ItemTabs");
                    for (int i = 0; i < items.Items.Count; i++) { items.SelectedIndex = i; Check($"items {i}"); }
                    nav.SelectedIndex = 1;
                    var journal = (System.Windows.Controls.TabControl)window.FindName("JournalTabs");
                    journal.SelectedIndex = 0; WindowTests.Pump(() => model.LoadKnowledgeCommand.ExecuteAsync(null)); Check("knowledge");
                    journal.SelectedIndex = 1; WindowTests.Pump(() => model.LoadChallengesCommand.ExecuteAsync(null)); Check("challenges");
                    journal.SelectedIndex = 2; WindowTests.Pump(() => model.LoadFishingCommand.ExecuteAsync(null)); Check("fishing");
                    nav.SelectedIndex = 4;
                    var tabs = (System.Windows.Controls.TabControl)window.FindName("NpcTabs");
                    tabs.SelectedIndex = 0; WindowTests.Pump(() => model.LoadCompanionsCommand.ExecuteAsync(null)); Check("companions");
                    tabs.SelectedIndex = 1; Check("pets");
                    tabs.SelectedIndex = 2; WindowTests.Pump(() => model.LoadFriendshipCommand.ExecuteAsync(null)); Check("npc");
                }
                finally { window.Close(); }
            }
        }
        finally { Tr.SetLanguage("ko"); }
        Assert.AreEqual("", string.Join(Environment.NewLine, clipped.Distinct()));
    }
    private static IEnumerable<T> Find<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Find<T>(child)) yield return nested;
        }
    }
}
