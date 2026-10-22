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
    public void SettingsRoundTripAndSaveDiscoveryPreserveFilesAndMissingRecents()
    {
        string root = Path.Combine(Path.GetTempPath(), "save-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string oldPath = SettingsStore.FilePath; var old = SettingsStore.Current;
        try
        {
            string account = Path.Combine(root, "account/slot100"); Directory.CreateDirectory(account);
            File.WriteAllText(Path.Combine(account, "save.save"), "read-only fixture");
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
            Assert.AreEqual("read-only fixture", File.ReadAllText(Path.Combine(account, "save.save")));
        }
        finally
        {
            SettingsStore.Save(old); SettingsStore.FilePath = oldPath;
            Assert.IsTrue(Path.GetFullPath(root).StartsWith(Path.Combine(Path.GetTempPath(), "save-ui-"), StringComparison.OrdinalIgnoreCase));
            Directory.Delete(root, true);
        }
    }

    [STATestMethod]
    [TestCategory("PrivateFixtures")]
    public void OriginalThemeFontSettingsSlotsAndEveryPageRenderWithoutChangingSave()
    {
        if (Application.Current is null) { var app = new App.App(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown; }
        string fixtures = Environment.GetEnvironmentVariable("SAVEULTIMATE_FIXTURES") ?? throw new AssertInconclusiveException("Set fixtures.");
        var doc = SaveDocument.Open(Path.Combine(fixtures, "save_v2.03.02.save"));
        string hash = doc.BlobSha256; var model = new MainViewModel(doc);
        var old = SettingsStore.Current; string oldPath = SettingsStore.FilePath;
        string temporary = Path.Combine(Path.GetTempPath(), "ui-settings-" + Guid.NewGuid().ToString("N") + ".json");
        SettingsStore.FilePath = temporary;
        var settings = new EditorSettings { Language = "ko", SaveRoot = fixtures, AutoGameFolder = false, RecentSaveFiles = [Path.Combine(fixtures, "missing-review.save")] };
        SettingsStore.Save(settings); Appearance.Apply(settings);
        var window = Hidden(new MainWindow(model));
        window.Show();
        try
        {
            Assert.AreEqual("#FF16181B", ((SolidColorBrush)window.Background).Color.ToString());
            Assert.AreEqual(16d, window.FontSize);
            Assert.IsTrue(window.FontFamily.GetTypefaces().Any(t => t.TryGetGlyphTypeface(out var glyph) && glyph.FamilyNames.Values.Any(n => n.Contains("Hahmlet"))), "Bundled Hahmlet must resolve.");
            Assert.AreEqual("보기", Tr.Text("View"));
            var displayed = model.ItemsView.Cast<ItemRow>().ToArray();
            Assert.IsTrue(displayed.Zip(displayed.Skip(1)).All(p => p.First.Item.RecordOffset <= p.Second.Item.RecordOffset));
            WindowTests.Capture(window, "inventory");
            var inventory = (DataGrid)window.FindName("InventoryGrid");
            Assert.AreEqual(1, ((DataGridRow)inventory.ItemContainerGenerator.ContainerFromIndex(0)).Header);
            inventory.SelectedIndex = 0; WindowTests.Capture(window, "inventory-selected"); inventory.SelectedIndex = -1;
            foreach (var (page, name) in new[] { (1, "repurchase"), (2, "equipment"), (5, "knowledge"), (6, "challenges"), (7, "fishing"), (8, "map"), (9, "companions"), (10, "npc"), (3, "backups"), (11, "database"), (4, "skills") })
            {
                if (page == 4) WindowTests.Pump(() => model.LoadSkillsCommand.ExecuteAsync(null));
                model.PageIndex = page;
                if (page == 5) WindowTests.Pump(() => model.LoadKnowledgeCommand.ExecuteAsync(null));
                if (page == 6) WindowTests.Pump(() => model.LoadChallengesCommand.ExecuteAsync(null));
                if (page == 7) WindowTests.Pump(() => model.LoadFishingCommand.ExecuteAsync(null));
                if (page == 8) WindowTests.Pump(() => model.LoadMapCommand.ExecuteAsync(null));
                if (page == 9) WindowTests.Pump(() => model.LoadCompanionsCommand.ExecuteAsync(null));
                if (page == 10) WindowTests.Pump(() => model.LoadFriendshipCommand.ExecuteAsync(null));
                WindowTests.Capture(window, name);
                if (page is 1 or 2) { inventory.SelectedIndex = 0; WindowTests.Capture(window, name + "-selected"); inventory.SelectedIndex = -1; }
                Assert.IsFalse(model.Status.StartsWith("작업을 완료하지 못했습니다"), model.Status);
            }
            model.PageIndex = 0; window.ShowHelp("tab.inventory");
            WindowTests.Capture(window, "help"); window.ShowHelp("tab.inventory");
            var dialog = Hidden(new SettingsWindow(model.LoadedPath) { Owner = window });
            dialog.Show(); WindowTests.Capture(dialog, "settings-general"); dialog.Navigation.SelectedIndex = 1; WindowTests.Capture(dialog, "settings-theme"); dialog.Close();
            Assert.AreSame(settings, SettingsStore.Current, "Opening/canceling settings must not commit drafts.");
            var apply = Hidden(new SettingsWindow(model.LoadedPath) { Owner = window });
            apply.Dispatcher.BeginInvoke(new Action(() => Buttons(apply).Single(b => Equals(b.Content, Tr.Text("ui.apply_settings"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
            Assert.AreEqual(true, apply.ShowDialog()); Assert.IsNotNull(apply.Result);
            Assert.AreSame(settings, SettingsStore.Current, "The dialog returns validated settings; the caller persists them.");
            var slots = Hidden(new SlotsWindow(model) { Owner = window }); slots.Show();
            Assert.IsTrue(slots.Rows.Items.Count > 0); Assert.AreEqual(hash, doc.BlobSha256);
            slots.Rows.SelectedItem = slots.Rows.Items.Cast<SaveSlot>().First(r => r.Category == "manual");
            Assert.IsFalse(slots.OpenButton.IsEnabled); Assert.IsTrue(slots.RemoveButton.IsEnabled);
            WindowTests.Capture(slots, "slots"); slots.RemoveRecent(); Assert.AreEqual(0, SettingsStore.Current.RecentSaveFiles.Length); slots.Close();
            foreach (string theme in Appearance.Themes.Keys)
            {
                Appearance.Apply(settings with { Theme = theme }); WindowTests.Capture(window, "theme-" + theme);
                Assert.AreEqual(Appearance.Palette(settings with { Theme = theme })["bg"].ToUpperInvariant(), ((SolidColorBrush)window.Background).Color.ToString()[3..].Insert(0, "#"));
            }
            foreach (int scale in new[] { 100, 125, 150 })
            {
                Appearance.Apply(settings with { UiScale = scale, FontScale = 1.25, CompactMode = true });
                window.Width = 980; window.Height = 720; WindowTests.Capture(window, "scale-" + scale);
            }
            foreach (string language in new[] { "en", "ja", "ru", "ko" })
            {
                SettingsStore.Save(settings with { Language = language, UiScale = 100 });
                bool countUpdated = false;
                System.ComponentModel.PropertyChangedEventHandler countChanged = (_, e) => countUpdated |= e.PropertyName == nameof(MainViewModel.BackupCount);
                model.PropertyChanged += countChanged;
                Appearance.Apply(SettingsStore.Current);
                model.PropertyChanged -= countChanged;
                Assert.IsTrue(countUpdated, "Language changes must refresh bound backup counts.");
                window.Width = 1400; window.Height = 800;
                var undo = (Button)window.FindName("UndoButton");
                Assert.AreEqual(Tr.Text("ui.undo"), undo.Content);
                Assert.AreSame(model.UndoCommand, undo.Command);
                foreach (int page in new[] { 0, 2, 5, 6, 7, 4, 8, 9, 10, 11, 3 })
                {
                    model.PageIndex = page; WindowTests.Capture(window, $"language-{language}-{page}");
                }
                var translatedSettings = Hidden(new SettingsWindow(model.LoadedPath) { Owner = window });
                translatedSettings.Show(); WindowTests.Capture(translatedSettings, $"settings-{language}"); translatedSettings.Close();
                var translatedSlots = Hidden(new SlotsWindow(model) { Owner = window });
                translatedSlots.Show(); WindowTests.Capture(translatedSlots, $"slots-{language}"); translatedSlots.Close();
                window.Width = 920; WindowTests.Capture(window, $"narrow-{language}");
                foreach (Button action in ((Panel)window.FindName("HeaderActions")).Children)
                    Assert.IsTrue(action.TransformToAncestor(window).Transform(new Point(action.ActualWidth, 0)).X <= window.ActualWidth, language);
                foreach (string name in new[] { "SavePathText", "GamePathText" })
                {
                    var pathText = (TextBlock)window.FindName(name);
                    var sync = (FrameworkElement)window.FindName("SyncButton");
                    Assert.IsTrue(pathText.ActualWidth >= ((FrameworkElement)window.FindName("ContentArea")).ActualWidth - 42 - sync.ActualWidth - sync.Margin.Left, language);
                    Assert.AreEqual(TextTrimming.None, pathText.TextTrimming);
                    Assert.AreEqual(TextWrapping.Wrap, pathText.TextWrapping);
                }
            }
            Assert.AreEqual(hash, doc.BlobSha256); Assert.IsFalse(doc.CanUndo);
        }
        finally
        {
            while (doc.CanUndo) doc.Undo();
            window.Close(); SettingsStore.Save(old); SettingsStore.FilePath = oldPath; Appearance.Apply(old);
            File.Delete(temporary);
        }
    }
    private static T Hidden<T>(T window) where T : Window
    { window.Left = -10000; window.Top = -10000; window.WindowStartupLocation = WindowStartupLocation.Manual; window.ShowActivated = false; window.ShowInTaskbar = false; return window; }
    private static IEnumerable<Button> Buttons(DependencyObject root)
    {
        if (root is Button button) yield return button;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var item in Buttons(child)) yield return item;
    }
}
