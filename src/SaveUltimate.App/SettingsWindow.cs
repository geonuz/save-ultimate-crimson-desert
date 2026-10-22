using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SaveUltimate.Core.Catalogs;

namespace SaveUltimate.App;

public sealed class SettingsWindow : Window
{
    private readonly EditorSettings original;
    private readonly string? currentFile;
    private readonly ComboBox language = new(), theme = new(), font = new(), scale = new();
    private readonly CheckBox autoSave = new(), autoGame = new(), compact = new();
    private readonly TextBox saveRoot = new(), extraRoot = new(), gameRoot = new();
    private readonly Dictionary<string, TextBox> edits = [];
    private Dictionary<string, string> custom;
    private readonly Border preview = new() { Padding = new(10), BorderThickness = new(1) };
    private readonly Grid pages = new();
    private bool updating;
    private bool synchronizing;
    private readonly TextBlock syncStatus = Ui.Text("");
    public ListBox Navigation { get; } = new() { Width = 170, BorderThickness = new(0), Background = System.Windows.Media.Brushes.Transparent, Margin = new(0, 0, 12, 0) };
    public EditorSettings? Result { get; private set; }
    public SettingsWindow(string? currentFile = null, string page = "general", EditorSettings? settings = null)
    {
        original = settings ?? SettingsStore.Current; this.currentFile = currentFile; custom = Appearance.Palette(original with { Theme = "custom" });
        Ui.Setup(this, "Settings", 960, 720);
        ActionGlyph.SetShow(this, false);
        var root = new DockPanel { Margin = new(20) }; root.SetResourceReference(LayoutTransformProperty, "UiTransform"); Content = root;
        var footer = new DockPanel { Margin = new(0, 8, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(Ui.Button("About", (_, _) => new AboutWindow { Owner = this }.ShowDialog()));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Ui.Button("Cancel", (_, _) => DialogResult = false); cancel.IsCancel = true; buttons.Children.Add(cancel);
        buttons.Children.Add(Ui.Button("ui.apply_settings", (_, _) => Accept(), "AccentButton")); footer.Children.Add(buttons);
        DockPanel.SetDock(Navigation, Dock.Left); root.Children.Add(Navigation); root.Children.Add(pages);
        Navigation.Items.Add(Tr.Text("ui.general")); Navigation.Items.Add(Tr.Text("ui.theme"));
        Navigation.ItemContainerStyle = (Style)FindResource("NavigationItem");
        Navigation.SelectionChanged += (_, _) => { for (int i = 0; i < pages.Children.Count; i++) pages.Children[i].Visibility = i == Navigation.SelectedIndex ? Visibility.Visible : Visibility.Collapsed; };
        var general = Page(); var display = Group(general, "settings.language_group");
        foreach (var (code, name) in new[] { ("en", "English"), ("ko", "한국어"), ("ja", "日本語"), ("ru", "Русский") }) language.Items.Add(new Choice(code, name));
        language.DisplayMemberPath = nameof(Choice.Label); language.SelectedItem = language.Items.Cast<Choice>().FirstOrDefault(c => c.Key == original.Language) ?? language.Items[0];
        Row(display, "ui.language", language);
        var saves = Group(general, "settings.saves_group");
        autoSave.Content = Tr.Text("ui.auto_save_folder"); autoSave.IsChecked = original.SaveRoot.Length == 0; Row(saves, null, autoSave);
        PathRow(saves, "ui.save_folder", saveRoot, original.SaveRoot);
        var current = Ui.Button("ui.use_current_folder", (_, _) => { autoSave.IsChecked = false; saveRoot.Text = Path.GetDirectoryName(this.currentFile) ?? ""; });
        current.IsEnabled = currentFile is not null; Row(saves, null, current); PathRow(saves, "ui.additional_save_folder", extraRoot, original.AdditionalSaveRoot);
        autoSave.Click += (_, _) => UpdatePaths();
        var game = Group(general, "settings.game_group"); autoGame.Content = Tr.Text("ui.auto_game_folder"); autoGame.IsChecked = original.AutoGameFolder; Row(game, null, autoGame);
        PathRow(game, "ui.game_folder", gameRoot, original.GameInstallPath); Row(game, null, Ui.Text("ui.game_hint"));
        autoGame.Click += (_, _) => UpdatePaths();
        var sync = Ui.Button("Sync Items Local", async (_, _) => await Synchronize(), "AccentButton");
        Row(game, null, sync); Row(game, null, syncStatus); UpdatePaths();
        Closing += (_, e) => e.Cancel = synchronizing;
        var appearance = Page(); var screen = Group(appearance, "settings.display_group");
        foreach (var (key, value) in Appearance.Themes) theme.Items.Add(new Choice(key, Tr.Text(value.Name)));
        theme.Items.Add(new Choice("custom", Tr.Text("Custom Colors"))); theme.DisplayMemberPath = nameof(Choice.Label);
        theme.SelectedItem = theme.Items.Cast<Choice>().FirstOrDefault(c => c.Key == original.Theme) ?? theme.Items[0];
        Row(screen, "ui.theme", theme);
        Scales(font, original.FontScale); Scales(scale, original.UiScale / 100d);
        Row(screen, "Font Size", font); Row(screen, "UI Scale", scale);
        compact.Content = Tr.Text("Compact Mode"); compact.IsChecked = original.CompactMode; Row(screen, null, compact);
        var colors = Group(appearance, "settings.colors_group"); Row(colors, null, preview);
        Row(colors, null, Ui.Button("theme.copy", (_, _) => { custom = CurrentPalette(); theme.SelectedItem = theme.Items.Cast<Choice>().Single(c => c.Key == "custom"); UpdatePreview(); }));
        foreach (string key in new[] { "bg", "panel", "header", "accent", "text", "selected", "border" })
        {
            var edit = new TextBox { MaxLength = 7 }; edits.Add(key, edit);
            var row = new DockPanel();
            var pick = Ui.Button("theme.pick", (_, _) => { var dialog = new ColorWindow(edit.Text) { Owner = this }; if (dialog.ShowDialog() == true) { edit.Text = dialog.Color; EditColor(key); } });
            DockPanel.SetDock(pick, Dock.Right); row.Children.Add(pick); row.Children.Add(edit);
            edit.LostKeyboardFocus += (_, _) => EditColor(key); Row(colors, "theme." + key, row);
        }
        Row(colors, null, Ui.Text("theme.draft_hint")); theme.SelectionChanged += (_, _) => UpdatePreview(); UpdatePreview();
        Navigation.SelectedIndex = page == "theme" ? 1 : 0;
    }
    private sealed record Choice(string Key, string Label);
    private async Task Synchronize()
    {
        string path = gameRoot.Text.Trim();
        if (!SaveLocations.ValidGame(path)) { ThemedMessageBox.Show(this, Tr.Text("ui.invalid_game"), Tr.Text("Settings")); return; }
        if (synchronizing) return;
        synchronizing = true; IsEnabled = false;
        var model = (Owner as MainWindow)?.DataContext as MainViewModel;
        if (model is not null) model.Busy = true;
        try
        {
            var progress = new Progress<SyncProgress>(p => syncStatus.Text = Tr.Format(p.Stage, ("current", p.Completed.ToString("N0")), ("total", p.Total.ToString("N0"))));
            var result = await Task.Run(() => GameDataSync.Run(path, CatalogFiles.Resources, CatalogFiles.Cache, progress));
            CatalogFiles.Reload(); model?.RefreshLanguage();
            syncStatus.Text = Tr.Format("sync.result", ("items", result.Items), ("images", result.Images), ("missing", result.Unresolved), ("seconds", result.Seconds.ToString("F1")));
        }
        catch (Exception error) { syncStatus.Text = Tr.Text("sync.failed") + " " + error.Message; ThemedMessageBox.Show(this, syncStatus.Text, Tr.Text("Sync Items Local"), MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { if (model is not null) model.Busy = false; IsEnabled = true; synchronizing = false; }
    }
    private static void Scales(ComboBox combo, double value)
    {
        foreach (double n in new[] { 1d, 1.1, 1.25, 1.5, value }.Distinct().Order()) combo.Items.Add(new ComboBoxItem { Content = $"{n:P0}", Tag = n });
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().Single(i => (double)i.Tag == value);
    }
    private StackPanel Page()
    {
        var panel = new StackPanel { Margin = new(0, 0, 8, 0) }; pages.Children.Add(Ui.Scroll(panel)); return panel;
    }
    private static StackPanel Group(Panel parent, string key)
    {
        var panel = new StackPanel { Margin = new(16, 16, 16, 16) };
        Grid.SetIsSharedSizeScope(panel, true);
        var group = new GroupBox { Header = Tr.Text(key), Content = panel }; group.SetResourceReference(BackgroundProperty, "panel"); parent.Children.Add(group); return panel;
    }
    private static void Row(Panel parent, string? label, UIElement control)
    {
        var grid = new Grid { Margin = new(0, 0, 0, 12) }; grid.ColumnDefinitions.Add(new() { Width = label is null ? new(0) : GridLength.Auto, SharedSizeGroup = label is null ? null : "Labels" }); grid.ColumnDefinitions.Add(new());
        if (label is not null) { var text = Ui.Text(label); text.VerticalAlignment = VerticalAlignment.Center; text.MaxWidth = 160; text.Margin = new(0, 0, 16, 0); grid.Children.Add(text); System.Windows.Automation.AutomationProperties.SetName(control, Tr.Text(label)); }
        Grid.SetColumn(control, 1); grid.Children.Add(control); parent.Children.Add(grid);
    }
    private static void PathRow(Panel parent, string label, TextBox field, string value)
    {
        field.Text = value; var row = new DockPanel();
        var browse = Ui.Button("Browse...", (_, _) => { var dialog = new OpenFolderDialog { Title = Tr.Text("Browse...") }; if (dialog.ShowDialog() == true) field.Text = dialog.FolderName; });
        DockPanel.SetDock(browse, Dock.Right); row.Children.Add(browse); row.Children.Add(field); Row(parent, label, row);
    }
    private void UpdatePaths()
    {
        ((UIElement)saveRoot.Parent).IsEnabled = autoSave.IsChecked != true;
        saveRoot.Text = autoSave.IsChecked == true ? string.Join("; ", SaveLocations.Roots(original with { SaveRoot = "" }).Select(r => r.Path).Distinct()) : original.SaveRoot;
        ((UIElement)gameRoot.Parent).IsEnabled = autoGame.IsChecked != true;
        gameRoot.Text = autoGame.IsChecked == true ? SaveLocations.GameFolder(original with { AutoGameFolder = true }) : original.GameInstallPath;
    }
    private Dictionary<string, string> CurrentPalette() => Appearance.Palette(original with { Theme = ((Choice)theme.SelectedItem).Key, CustomThemeColors = custom });
    private void EditColor(string key)
    {
        if (updating) return;
        string value = edits[key].Text;
        var colors = CurrentPalette();
        if (!Appearance.ValidColor(value) || string.Equals(value, colors[key], StringComparison.OrdinalIgnoreCase)) { UpdatePreview(); return; }
        colors[key] = value;
        if (key == "bg") colors["input_bg"] = value;
        string? foreground = key switch { "panel" => "panel_text", "header" => "primary_text", "accent" => "accent_text", "selected" => "selection_text", _ => null };
        if (foreground is not null) colors[foreground] = Appearance.Contrast(value);
        custom = colors; theme.SelectedItem = theme.Items.Cast<Choice>().Single(c => c.Key == "custom"); UpdatePreview();
    }
    private void UpdatePreview()
    {
        updating = true; var colors = CurrentPalette();
        System.Windows.Media.Brush Brush(string key) => new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colors[key]));
        foreach (var (key, edit) in edits) edit.Text = colors[key];
        preview.Background = Brush("bg"); preview.BorderBrush = Brush("border");
        var panel = new StackPanel(); var label = Ui.Text("theme.preview"); label.Background = Brush("panel"); label.Foreground = Brush("text"); label.Padding = new(8); panel.Children.Add(label);
        var row = new Grid { Margin = new(0, 8, 0, 0) }; row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new());
        foreach (var (key, bg, fg, column) in new[] { ("theme.accent", "accent", "accent_text", 0), ("theme.selected", "selected", "selection_text", 1) })
        { var text = Ui.Text(key); text.Background = Brush(bg); text.Foreground = Brush(fg); text.Padding = new(8); Grid.SetColumn(text, column); row.Children.Add(text); }
        panel.Children.Add(row); preview.Child = panel; updating = false;
    }
    private void Accept()
    {
        foreach (var key in edits.Keys.ToArray()) EditColor(key);
        string root = autoSave.IsChecked == true ? "" : saveRoot.Text.Trim(), extra = extraRoot.Text.Trim(), game = gameRoot.Text.Trim();
        foreach (string path in new[] { root, extra }) if (path.Length > 0 && !Directory.Exists(path)) { ThemedMessageBox.Show(this, Tr.Format("ui.invalid_folder", ("path", path)), Tr.Text("Settings")); return; }
        if (game.Length > 0 && !SaveLocations.ValidGame(game)) { ThemedMessageBox.Show(this, Tr.Text("ui.invalid_game"), Tr.Text("Settings")); return; }
        Result = original with { Language = ((Choice)language.SelectedItem).Key, Theme = ((Choice)theme.SelectedItem).Key,
            FontScale = (double)((ComboBoxItem)font.SelectedItem).Tag, UiScale = (int)Math.Round((double)((ComboBoxItem)scale.SelectedItem).Tag * 100),
            CompactMode = compact.IsChecked == true, CustomThemeColors = new(custom), SaveRoot = root, AdditionalSaveRoot = extra,
            GameInstallPath = game, AutoGameFolder = autoGame.IsChecked == true };
        DialogResult = true;
    }
}

internal sealed class ColorWindow : Window
{
    private readonly TextBox hex;
    public string Color => hex.Text;
    public ColorWindow(string value)
    {
        Ui.Setup(this, "theme.pick", 480, 380); var root = new StackPanel { Margin = new(20) }; Content = root;
        ActionGlyph.SetShow(this, false);
        hex = new TextBox { Text = value, MaxLength = 7, Margin = new(0, 8, 0, 12) }; root.Children.Add(Ui.Text("#RRGGBB")); root.Children.Add(hex);
        var swatch = new Border { Height = 35, Margin = new(0, 0, 0, 12) }; root.Children.Add(swatch);
        var sliders = new Slider[3]; bool updating = false;
        for (int i = 0; i < sliders.Length; i++)
        {
            var row = new DockPanel { Margin = new(0, 0, 0, 8) }; row.Children.Add(new TextBlock { Text = new[] { "R", "G", "B" }[i], Width = 30 });
            var slider = sliders[i] = new Slider { Minimum = 0, Maximum = 255, TickFrequency = 1, IsSnapToTickEnabled = true };
            row.Children.Add(slider); root.Children.Add(row);
            slider.ValueChanged += (_, _) => { if (!updating && sliders.All(s => s is not null)) hex.Text = $"#{(int)sliders[0].Value:X2}{(int)sliders[1].Value:X2}{(int)sliders[2].Value:X2}"; };
        }
        void Refresh()
        {
            if (!Appearance.ValidColor(hex.Text)) return;
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex.Text);
            swatch.Background = new System.Windows.Media.SolidColorBrush(color); updating = true;
            sliders[0].Value = color.R; sliders[1].Value = color.G; sliders[2].Value = color.B; updating = false;
        }
        hex.TextChanged += (_, _) => Refresh(); Refresh();
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; root.Children.Add(footer);
        footer.Children.Add(Ui.Button("OK", (_, _) => { if (Appearance.ValidColor(hex.Text)) DialogResult = true; }));
        var cancel = Ui.Button("Cancel", (_, _) => DialogResult = false); cancel.IsCancel = true; footer.Children.Add(cancel);
    }
}
