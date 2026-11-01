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
    public object Value => Record.Value is ulong value ? value : "—";
    // Each playable character keeps a separate bond with the same NPC.
    public string Affinity => Record.Affinity.Length == 0 ? "—" : string.Join(" · ", Record.Affinity.Select(a => $"{BondEditor.PlayableName(a.Playable)} {a.Value}"));
    public string Status => Tr.Text(Record.Editable ? Record.NeedsSync ? "npc.sync_needed" : "npc.editable" : Record.Value is null ? "npc.not_stored" : "npc.unsupported");
    internal void Update(NpcFriendship r) { Record = r; OnPropertyChanged(""); }
}
public sealed partial class BondEditor(NpcAffinity gauge) : ObservableObject
{
    public NpcAffinity Gauge { get; } = gauge;
    // Bonds only go up; see SaveDocument.SetGauge.
    public int[] Choices => NpcPage.Choices(Gauge.Value, raiseOnly: true);
    // Playable keys 1, 6 and 4 are Kliff, Oongka and Damiane.
    internal static string PlayableName(uint key) => Tr.Text(key switch { 1 => "skills.character.0", 4 => "skills.character.2", _ => "skills.character.1" });
    public string Character => PlayableName(Gauge.Playable);
    public string Partner => Tr.Format("npc.partner", ("name", PlayableName(Gauge.Playable))) +
        (Gauge.MaritalState == 1 ? " · " + Tr.Text("npc.dating") : "");
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanApply))] private object? value = gauge is { Editable: true, Value: ulong v } ? (int)v : null;
    public bool CanApply => Gauge.Editable && Value is int v && (ulong)v != Gauge.Value;
}
public partial class NpcPage : UserControl
{
    // Steps of 5 plus the stored value, which the game may leave between steps (37).
    public static int[] Choices(ulong? current, bool raiseOnly = false) => Enumerable.Range(0, 21).Select(i => i * 5)
        .Concat(current is <= 100 ? [(int)current.Value] : []).Where(v => !raiseOnly || current is null || (ulong)v >= current).Distinct().Order().ToArray();
    private NpcRow[] entries = [];
    private MainViewModel Model => (MainViewModel)DataContext;
    public NpcPage()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old) old.PropertyChanged -= Changed;
            if (e.NewValue is MainViewModel model) { model.PropertyChanged += Changed; Refresh(); }
        };
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(MainViewModel.Friendship)) Refresh(); }
    private void Refresh()
    {
        // Pets and mounts (game companion types) keep friendship too; they are left to the companion pages.
        var records = (Model.Friendship ?? []).Where(r => CompanionTypes.Shared.Group(r.Key) is "unknown" or "mercenary" or "playable" or "internal").ToArray();
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
        Value.ItemsSource = Choices(row?.Record.Value);
        Value.SelectedItem = row?.Record is { Editable: true, Value: ulong value } ? (int)value : null;
        Hint.Text = row?.Record.Editable == true ? "" : Tr.Text(row is null ? "npc.select" : row.Record.Value is null ? "npc.absent_hint" : "npc.unsupported_hint");
        Hint.Visibility = Hint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        // Keep the selected character's tab when the same NPC is refreshed after an edit.
        uint? tab = (Bonds.SelectedItem as BondEditor)?.Gauge.Playable;
        var bonds = row?.Record.Affinity.Select(a => new BondEditor(a)).ToArray() ?? [];
        Bonds.ItemsSource = bonds;
        Bonds.SelectedItem = bonds.FirstOrDefault(b => b.Gauge.Playable == tab) ?? bonds.FirstOrDefault();
        Bonds.Visibility = bonds.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateApply();
    }
    private void UpdateApply() { if (Apply is not null) Apply.IsEnabled = Rows.SelectedItem is NpcRow { Record.Editable: true } && Value.SelectedItem is int; }
    private async void OnApplyAffinity(object sender, RoutedEventArgs e)
    {
        if (Rows.SelectedItem is NpcRow row && (sender as FrameworkElement)?.DataContext is BondEditor { CanApply: true, Gauge.Value: ulong current, Value: int value } bond)
            await Model.SetAffinityAsync(row.Key, bond.Gauge.Playable, current, value);
    }
    private void OnSelect(object sender, SelectionChangedEventArgs e) => Select();
    private void OnValue(object sender, SelectionChangedEventArgs e) => UpdateApply();
    private TypingDelay? typing;
    private void OnSearch(object sender, TextChangedEventArgs e) => (typing ??= new(() => { if (DataContext is MainViewModel) Filter(); })).Restart(sender);
    private async void OnApply(object sender, RoutedEventArgs e) { if (Rows.SelectedItem is NpcRow { Record.Editable: true } row && Value.SelectedItem is int value) await Model.SetFriendshipAsync(row.Key, row.Record.Value!.Value, value); }
}
