using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.App;

public sealed record CompanionRow(Companion Record)
{
    public string Name => CharacterText.Get(Record.Key).Name;
    public string Custom => string.IsNullOrEmpty(Record.Name) ? Tr.Text("(unnamed)") : Record.Name;
    public string Category => Tr.Text("companion.group." + Record.Group);
    public string Hint => Record.Editable ? CompanionTypes.Shared.Types.GetValueOrDefault(CompanionTypes.Shared.Characters.GetValueOrDefault(Record.Key), "") : Tr.Text("companion.internal_note");
    public ImageSource? Icon => ItemImages.GetFile(Path.Combine(AppContext.BaseDirectory, "icons_mercenary", $"{Record.Key}.webp"), 32);
}
public partial class CompanionPage : UserControl
{
    private sealed record Choice(string Key, string Label);
    private bool updating;
    private MainViewModel Model => (MainViewModel)DataContext;
    public CompanionPage()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old) old.PropertyChanged -= Changed;
            if (e.NewValue is MainViewModel model) { model.PropertyChanged += Changed; Refresh(); }
        };
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(MainViewModel.Companions)) Refresh(); }
    private void Refresh()
    {
        string selected = (Group.SelectedItem as Choice)?.Key ?? "all";
        updating = true;
        Group.ItemsSource = CompanionTypes.Groups.Select(g => new Choice(g, $"{Tr.Text("companion.group." + g)} ({Model.Companions?.Count(c => g == "all" || c.Group == g) ?? 0})")).ToArray();
        Group.SelectedItem = Group.Items.Cast<Choice>().Single(c => c.Key == selected);
        updating = false; Filter();
    }
    private void Filter()
    {
        ulong? selected = (Rows.SelectedItem as CompanionRow)?.Record.Number;
        string group = (Group.SelectedItem as Choice)?.Key ?? "all";
        Rows.ItemsSource = (Model.Companions ?? []).Where(c => group == "all" || c.Group == group).Select(c => new CompanionRow(c)).ToArray();
        Rows.SelectedItem = Rows.Items.Cast<CompanionRow>().FirstOrDefault(r => r.Record.Number == selected);
        Count.Text = Model.Companions is null ? Tr.Text("ui.load_prompt") : Tr.Count(Model.Companions.Length, Rows.Items.Count);
        ClearAll.IsEnabled = Model.Companions?.Any(c => c.Editable && c.Name is not null) == true;
        Select();
    }
    private void Select()
    {
        if (Rename is null) return;
        var row = Rows.SelectedItem as CompanionRow;
        SelectedTitle.Text = row?.Name ?? Tr.Text("ui.select_item");
        Rename.IsEnabled = Clear.IsEnabled = row?.Record.Editable == true;
        Rename.ToolTip = Clear.ToolTip = row is { Record.Editable: false } ? Tr.Text("companion.internal_note") : null;
    }
    private void OnGroup(object sender, SelectionChangedEventArgs e) { if (!updating && DataContext is MainViewModel) Filter(); }
    private void OnSelect(object sender, SelectionChangedEventArgs e) => Select();
    private async void OnRename(object sender, RoutedEventArgs e)
    {
        if (Rows.SelectedItem is not CompanionRow { Record.Editable: true } row) return;
        var dialog = new NameDialog(Tr.Format("merc.rename_prompt", ("index", Array.FindIndex(Model.Companions!, c => c.Number == row.Record.Number)), ("key", row.Record.Key), ("current", row.Record.Name ?? Tr.Text("merc.unnamed"))), row.Record.Name ?? "") { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) await Model.RenameCompanionsAsync([row.Record], dialog.Value);
    }
    private async void OnClear(object sender, RoutedEventArgs e)
    {
        if (Rows.SelectedItem is not CompanionRow { Record.Editable: true, Record.Name: not null } row) return;
        if (ThemedMessageBox.Show(Window.GetWindow(this), Tr.Format("merc.clear_confirm", ("name", row.Record.Name)), Tr.Text("merc.clear_name"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            await Model.RenameCompanionsAsync([row.Record], null);
    }
    private async void OnClearAll(object sender, RoutedEventArgs e)
    {
        var rows = Model.Companions?.Where(r => r.Editable && r.Name is not null).ToArray() ?? [];
        if (rows.Length > 0 && ThemedMessageBox.Show(Window.GetWindow(this), Tr.Format("merc.clear_all_confirm", ("count", rows.Length)), Tr.Text("merc.reset_all"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            await Model.RenameCompanionsAsync(rows, null);
    }
}
