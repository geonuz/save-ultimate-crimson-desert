using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;

namespace SaveUltimate.App;

public sealed class SlotsWindow : Window
{
    private readonly MainViewModel model;
    public DataGrid Rows { get; } = new() { IsReadOnly = true, AutoGenerateColumns = false, SelectionMode = DataGridSelectionMode.Single };
    private readonly TextBlock path = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 12) };
    private readonly TextBlock heading = Ui.Text("ui.slots");
    public Button OpenButton { get; }
    public Button RemoveButton { get; }
    public SlotsWindow(MainViewModel model)
    {
        this.model = model; Ui.Setup(this, "ui.slots", 1120, 600);
        var root = new DockPanel { Margin = new(20) }; root.SetResourceReference(LayoutTransformProperty, "UiTransform"); Content = root;
        var top = new DockPanel { Margin = new(0, 0, 0, 12) }; DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(actions, Dock.Right); top.Children.Add(actions);
        actions.Children.Add(Ui.Button("ui.folder_settings", (_, _) => { if (Owner is MainWindow main) main.ShowSettings(); Refresh(); }));
        actions.Children.Add(Ui.Button("Refresh", (_, _) => Refresh())); top.Children.Add(heading);
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); footer.Children.Add(path);
        var buttons = new DockPanel(); footer.Children.Add(buttons);
        buttons.Children.Add(Ui.Button("ui.open_file", async (_, _) => await OpenDirect()));
        RemoveButton = Ui.Button("ui.remove_recent", (_, _) => RemoveRecent()); buttons.Children.Add(RemoveButton);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Ui.Button("Cancel", (_, _) => DialogResult = false); cancel.IsCancel = true; right.Children.Add(cancel);
        OpenButton = Ui.Button("ui.open_slot", async (_, _) => await OpenSelected(), "AccentButton"); OpenButton.IsDefault = true; right.Children.Add(OpenButton); buttons.Children.Add(right);
        foreach (var (key, property, width) in new[] { ("ui.slot", "Name", 140d), ("ui.path", "Folder", -1d), ("ui.saved_at", "SavedAt", 180d), ("ui.filename", "FileName", 140d) })
            Rows.Columns.Add(new DataGridTextColumn { Header = Tr.Text(key), Binding = new Binding(property), Width = width < 0 ? new(1, DataGridLengthUnitType.Star) : new(width) });
        var template = new DataTemplate();
        var label = new FrameworkElementFactory(typeof(TextBlock)); label.SetBinding(TextBlock.TextProperty, new Binding("Name")); label.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold); template.VisualTree = label;
        var groupTemplate = new ControlTemplate(typeof(GroupItem));
        var expander = new FrameworkElementFactory(typeof(Expander)); expander.SetValue(Expander.IsExpandedProperty, true); expander.SetBinding(HeaderedContentControl.HeaderProperty, new Binding("Name"));
        expander.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter))); groupTemplate.VisualTree = expander;
        Rows.GroupStyle.Add(new GroupStyle { HeaderTemplate = template, ContainerStyle = new Style(typeof(GroupItem)) { Setters = { new Setter(Control.TemplateProperty, groupTemplate) } } });
        Rows.SelectionChanged += (_, _) => SelectionChanged();
        Rows.MouseDoubleClick += async (_, e) => { if (ItemsControl.ContainerFromElement(Rows, e.OriginalSource as DependencyObject) is DataGridRow) await OpenSelected(); };
        Rows.PreviewKeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await OpenSelected(); } };
        var menu = new ContextMenu();
        void Action(string key, RoutedEventHandler action) { var item = new MenuItem { Header = Tr.Text(key) }; item.Click += action; menu.Items.Add(item); }
        Action("Load Save", async (_, _) => await OpenSelected());
        Action("Open File Location", (_, _) => { if (Rows.SelectedItem is SaveSlot slot && Directory.Exists(Path.GetDirectoryName(slot.Path))) Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", slot.Path }, UseShellExecute = true }); });
        Action("Copy File Path", (_, _) => { if (Rows.SelectedItem is SaveSlot slot) Clipboard.SetText(slot.Path); });
        Rows.ContextMenu = menu; root.Children.Add(Rows); Refresh();
    }
    public void Refresh()
    {
        var rows = SaveLocations.Find(SettingsStore.Current); var view = new ListCollectionView(rows);
        var grouping = new PropertyGroupDescription(nameof(SaveSlot.Group));
        foreach (string category in new[] { "default", "additional", "manual" }) grouping.GroupNames.Add(Tr.Text("ui.slots_" + category));
        view.GroupDescriptions.Add(grouping);
        Rows.ItemsSource = view; Rows.SelectedItem = rows.FirstOrDefault(r => string.Equals(r.Path, model.LoadedPath, StringComparison.OrdinalIgnoreCase));
        heading.Text = Tr.Text(rows.Length == 0 ? "ui.no_slots" : "ui.slots"); SelectionChanged();
    }
    private void SelectionChanged()
    {
        var row = Rows.SelectedItem as SaveSlot; path.Text = row?.Path ?? "";
        OpenButton.IsEnabled = row is not null && File.Exists(row.Path); RemoveButton.IsEnabled = row?.Category == "manual";
    }
    public void RemoveRecent()
    {
        if (Rows.SelectedItem is not SaveSlot { Category: "manual" } row) return;
        try { SettingsStore.Save(SettingsStore.Current with { RecentSaveFiles = SettingsStore.Current.RecentSaveFiles.Where(p => !string.Equals(Path.GetFullPath(p), row.Path, StringComparison.OrdinalIgnoreCase)).ToArray() }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { ThemedMessageBox.Show(this, error.Message, Tr.Text("Settings"), MessageBoxButton.OK, MessageBoxImage.Warning); }
        Refresh();
    }
    private async Task OpenSelected() { if (Rows.SelectedItem is SaveSlot row && File.Exists(row.Path)) await Open(row.Path); }
    private async Task OpenDirect()
    {
        var dialog = new OpenFileDialog { Title = Tr.Text("Open Save File"), Filter = "Save Files (*.save)|*.save|All Files (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) await Open(dialog.FileName);
    }
    private async Task Open(string file)
    {
        if (!await model.ConfirmLeaveAsync()) return;
        IsEnabled = false;
        try
        {
            if (await model.TryLoadFileAsync(file))
            {
                try { SettingsStore.Remember(file); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { ThemedMessageBox.Show(this, error.Message, Tr.Text("Settings"), MessageBoxButton.OK, MessageBoxImage.Warning); }
                DialogResult = true;
            }
        }
        finally { IsEnabled = true; }
    }
}
