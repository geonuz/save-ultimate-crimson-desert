using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.App;

public sealed class NpcRow(NpcFriendship record) : ObservableObject
{
    public NpcFriendship Record { get; private set; } = record;
    public uint Key => Record.Key;
    public string Name => CharacterText.Get(Key).Name;
    public string Area => CharacterText.Get(Key).Area is { Length: > 0 } s ? s : "—";
    public string Service => CharacterText.Get(Key).Service is { Length: > 0 } s ? s : "—";
    public string Title => CharacterText.Get(Key).Title;
    public object Value => Record.Value is ulong value ? value : Tr.Text("npc.not_stored");
    public string Status => Tr.Text(Record.Editable ? Record.NeedsSync ? "npc.sync_needed" : "npc.editable" : Record.Value is null ? "npc.not_stored" : "npc.unsupported");
    internal void Update(NpcFriendship r) { Record = r; OnPropertyChanged(""); }
}
public partial class NpcPage : UserControl
{
    private NpcRow[] entries = [];
    private MainViewModel Model => (MainViewModel)DataContext;
    public NpcPage()
    {
        InitializeComponent(); Value.ItemsSource = Enumerable.Range(0, 21).Select(i => i * 5).ToArray();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old) old.PropertyChanged -= Changed;
            if (e.NewValue is MainViewModel model) { model.PropertyChanged += Changed; Refresh(); }
        };
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(MainViewModel.Friendship)) Refresh(); }
    private void Refresh()
    {
        var records = Model.Friendship ?? [];
        if (entries.Select(r => r.Key).SequenceEqual(records.Select(r => r.Key)))
        { for (int i = 0; i < entries.Length; i++) entries[i].Update(records[i]); Select(); }
        else { entries = records.Select(r => new NpcRow(r)).ToArray(); Filter(); }
    }
    private void Filter()
    {
        var selected = Rows.SelectedItem as NpcRow;
        var sorts = CollectionViewSource.GetDefaultView(Rows.ItemsSource ?? Array.Empty<NpcRow>()).SortDescriptions.ToArray();
        string query = Search.Text.Trim();
        Rows.ItemsSource = entries.Where(r => $"{r.Key} {r.Name} {r.Area} {r.Service} {r.Title}".Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var sort in sorts) CollectionViewSource.GetDefaultView(Rows.ItemsSource).SortDescriptions.Add(sort);
        if (selected is not null && Rows.Items.Contains(selected)) Rows.SelectedItem = selected;
        Count.Text = Model.Friendship is null ? Tr.Text("npc.load_prompt") : Tr.Count(entries.Length, Rows.Items.Count); Select();
    }
    private void Select()
    {
        if (Value is null) return;
        var row = Rows.SelectedItem as NpcRow;
        SelectedTitle.Text = row?.Name ?? Tr.Text("ui.select_item");
        Value.IsEnabled = row?.Record.Editable == true;
        Value.SelectedItem = row?.Record is { Editable: true, Value: ulong value } && value % 5 == 0 ? (int)value : null;
        Hint.Text = row?.Record.Editable == true ? "" : Tr.Text(row is null ? "npc.select" : row.Record.Value is null ? "npc.absent_hint" : "npc.unsupported_hint");
        Hint.Visibility = Hint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateApply();
    }
    private void UpdateApply() { if (Apply is not null) Apply.IsEnabled = Rows.SelectedItem is NpcRow { Record.Editable: true } && Value.SelectedItem is int; }
    private void OnSelect(object sender, SelectionChangedEventArgs e) => Select();
    private void OnValue(object sender, SelectionChangedEventArgs e) => UpdateApply();
    private void OnSearch(object sender, TextChangedEventArgs e) { if (DataContext is MainViewModel) Filter(); }
    private async void OnApply(object sender, RoutedEventArgs e) { if (Rows.SelectedItem is NpcRow { Record.Editable: true } row && Value.SelectedItem is int value) await Model.SetFriendshipAsync(row.Key, row.Record.Value!.Value, value); }
}
