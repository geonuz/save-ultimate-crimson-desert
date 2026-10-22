using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.App;

public sealed class FishingRow(FishingRecord record) : ObservableObject
{
    public FishingRecord Record { get; private set; } = record;
    public uint Key => Record.Fish.Key;
    public string Name => KnowledgeText.Get(Key).Name;
    public string Category => KnowledgePage.GroupName(KnowledgeCatalog.Shared.Groups.Single(g => g.Key == Record.Fish.GroupKey));
    public string Level => $"{Record.Level} / {KnowledgeCatalog.Shared.LevelCounts[Key]}";
    public string Size => Record.Size?.ToString("F3", CultureInfo.InvariantCulture) ?? Tr.Text("fishing.missing");
    public string Availability => Record.Level == 0 ? Tr.Text("fishing.unlearned") : "";
    internal void Update(FishingRecord value) { Record = value; OnPropertyChanged(""); }
}

public partial class FishingPage : UserControl
{
    private MainViewModel Model => (MainViewModel)DataContext;
    private FishingRow? Selected => Rows.SelectedItem as FishingRow;
    public FishingPage()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old) old.PropertyChanged -= Changed;
            if (e.NewValue is MainViewModel model) { model.PropertyChanged += Changed; Refresh(); }
        };
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(MainViewModel.Fishing)) Refresh(); }
    private void Refresh()
    {
        var records = Model.Fishing?.Where(r => r.Fish.RecordSupported).ToArray() ?? [];
        var old = Rows.Items.Cast<FishingRow>().ToDictionary(r => r.Key);
        if (old.Keys.ToHashSet().SetEquals(records.Select(r => r.Fish.Key)))
            foreach (var record in records) old[record.Fish.Key].Update(record);
        else Rows.ItemsSource = records.Select(r => new FishingRow(r)).ToArray();
        SetupBanner.Visibility = records.Any(r => !r.RecordTypeAvailable) ? Visibility.Visible : Visibility.Collapsed;
        Count.Text = Model.Fishing is null ? Tr.Text("ui.load_prompt") : Tr.Count(records.Length, records.Length);
        Select();
    }
    private void Select()
    {
        var row = Selected;
        SelectedTitle.Text = row?.Name ?? Tr.Text("ui.select_item");
        Learn.IsEnabled = row?.Record.Level == 0;
        SizeEditor.IsEnabled = row is { Record.Level: > 0, Record.RecordTypeAvailable: true };
        Apply.ToolTip = row is { Record.RecordTypeAvailable: false } ? Tr.Text("fishing.setup_title") : null;
        if (row?.Record.Size is float size) SizeInput.Text = Math.Clamp(size, .001f, 1000).ToString("F3", CultureInfo.InvariantCulture);
        Description.Text = row is null ? "" : KnowledgeText.Get(row.Key).Description is { Length: > 0 } text ? text : Tr.Text("No description available");
        Picture.Source = row is null ? null : ItemImages.GetFile(Path.Combine(AppContext.BaseDirectory, "game_data", "images", "knowledge", $"{row.Key}.png"), 280);
        UpdateApply();
    }
    private bool SizeValue(out double size) => double.TryParse(SizeInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out size) &&
        double.IsFinite(size) && size >= .001 && size <= 1000;
    private void UpdateApply() { if (Apply is not null) Apply.IsEnabled = Selected is { Record.Level: > 0, Record.RecordTypeAvailable: true } && SizeValue(out _); }
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => Select();
    private void OnSizeChanged(object sender, TextChangedEventArgs e) => UpdateApply();
    private void OnSizeLostFocus(object sender, RoutedEventArgs e) { if (SizeValue(out double value)) SizeInput.Text = Math.Round(value, 3).ToString("F3", CultureInfo.InvariantCulture); }
    private void Step(int step) { if (SizeValue(out double value)) SizeInput.Text = Math.Clamp(value + step, .001, 1000).ToString("F3", CultureInfo.InvariantCulture); }
    private void OnStep(object sender, RoutedEventArgs e) => Step(int.Parse((string)((FrameworkElement)sender).Tag, CultureInfo.InvariantCulture));
    private void OnSizeKey(object sender, KeyEventArgs e) { if (e.Key is Key.Up or Key.Down) { Step(e.Key == Key.Up ? 1 : -1); e.Handled = true; } }
    private async void OnApply(object sender, RoutedEventArgs e) { if (Selected is { } row && Apply.IsEnabled && SizeValue(out double size)) await Model.SetFishingRecordAsync(row.Key, Math.Round(size, 3)); }
    private async void OnLearn(object sender, RoutedEventArgs e) { if (Selected is { Record.Level: 0 } row) await Model.EditKnowledgeAsync([row.Key], KnowledgeAction.Learn); }
}

