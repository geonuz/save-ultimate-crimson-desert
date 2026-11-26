using System.IO;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using CDSE.Core.Saves;

namespace CDSE.App;

public static class HousingText
{
    // The name the game gives the place: the plot's knowledge (a DLC plot), the island (an Ocean plot), or Kliff's house.
    public static string PlotName(HousingPlotInfo plot)
        => plot.NameKnowledge != 0 && KnowledgeText.Find(plot.NameKnowledge) is { } text ? text.Name
            : plot.Kliff ? Tr.Text("housing.kliff_house") : plot.Name;

    public static string RegionName(HousingPlotInfo plot)
        => plot.Region.Length > 0 ? ManagementCatalog.Shared.RegionName(plot.Region, Tr.Language) : plot.NameKnowledge != 0 ? Tr.Text("housing.ocean") : "";

    public static string KindName(HouseKind kind) => kind switch
    {
        HouseKind.Free => Tr.Text("housing.kind_free"), HouseKind.Large => Tr.Text("housing.kind_large"),
        HouseKind.Medium => Tr.Text("housing.kind_medium"), HouseKind.Small => Tr.Text("housing.kind_small"), _ => "",
    };

    // The part kinds a save may lack, named as the player builds them.
    public static string TypeName(string type) => type switch
    {
        "HousingObjectSaveData" => Tr.Text("housing.kind.parts"),
        "FenceHousingObjectSaveData" => Tr.Text("housing.kind.fences"),
        "RoofHousingObjectSaveData" => Tr.Text("housing.kind.roofs"),
        "StairHousingObjectSaveData" => Tr.Text("housing.kind.stairs"),
        "CustomHouseRoomSaveData" => Tr.Text("housing.kind.rooms"),
        "MoldingHousingObjectSaveData" => Tr.Text("housing.kind.moldings"),
        "HousingPlacedPathSubGridSaveData" => Tr.Text("housing.kind.paths"),
        "CustomHouseHoleAnchorSaveData" => Tr.Text("housing.kind.holes"),
        _ when type.StartsWith("CurvedWall", StringComparison.Ordinal) => Tr.Text("housing.kind.curved"),
        _ => type,
    };

    public static string Counts(HouseCounts c) => Tr.Format("housing.counts", ("parts", c.Parts.ToString("N0")), ("fences", c.Fences.ToString("N0")),
        ("roofs", c.Roofs.ToString("N0")), ("stairs", c.Stairs.ToString("N0")), ("rooms", c.Rooms.ToString("N0")), ("paths", c.PathTiles.ToString("N0")));
}

public sealed class HousingRow(HousingPlot record) : ObservableObject
{
    public HousingPlot Record { get; private set; } = record;
    public uint Key => Record.Key;
    public string Name => HousingText.PlotName(Record.Info);
    public string Region => HousingText.RegionName(Record.Info);
    public string Kind => HousingText.KindName(Record.Kind);
    public string State => Tr.Text(Record.Active ? "housing.active" : "housing.inactive");
    // A free construction house counts what its row holds. A ready-built house has no row: the save keeps only the furniture
    // placed on its plot, so its structures stay blank. An inactive plot shows neither.
    public int? Structures => Record.FreeBuild ? Record.Counts.Structures : null;
    public int? Furnishings => Record.FreeBuild ? Record.Counts.Decorations : Record.Owned ? Record.Placed : null;
    // Active plots show green, the others grey.
    public bool Done => Record.Active;
    public bool Dim => !Record.Active;
    internal void Update(HousingPlot value) { Record = value; OnPropertyChanged(""); }
}

/// <summary>The house plots: activated singly, by selection or all at once; a house exported from one and imported into another.</summary>
public partial class HousingPage : UserControl
{
    private MainViewModel Model => (MainViewModel)DataContext;
    private HousingRow[] Selected => Rows.SelectedItems.Cast<HousingRow>().ToArray();
    private HousingRow? Single => Selected is [var row] ? row : null;
    public HousingPage()
    {
        InitializeComponent();
        Ui.Follow(this, nameof(MainViewModel.Housing), Refresh);
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Ui.FitAutoColumns(Rows); };
    }

    private void Refresh()
    {
        var records = Model.Housing ?? [];
        var old = Rows.Items.Cast<HousingRow>().ToDictionary(r => r.Key);
        if (old.Keys.ToHashSet().SetEquals(records.Select(r => r.Key)))
            foreach (var record in records) old[record.Key].Update(record);
        else Rows.ItemsSource = records.Select(r => new HousingRow(r)).ToArray();
        Ui.FitAutoColumns(Rows);
        Count.Text = Model.Housing is null ? Tr.Text("ui.load_prompt")
            : Tr.Count(records.Length, records.Length, "housing.count", records.Count(r => r.Active));
        ActivateAll.IsEnabled = records.Any(r => r.CanActivate);
        if (Rows.SelectedItem is null && Rows.Items.Count > 0) Rows.SelectedItem = Rows.Items.Cast<HousingRow>().FirstOrDefault(r => r.Record.FreeBuild);
        Select();
    }

    private void Select()
    {
        var rows = Selected;
        var row = Single;
        SelectedTitle.Text = rows.Length switch { 0 => Tr.Text("ui.select_item"), 1 => rows[0].Name, _ => Tr.Format("housing.selected", ("count", rows.Length)) };
        string about = row?.Record.Info.NameKnowledge is uint key and not 0 ? KnowledgeText.Find(key)?.Description ?? "" : "";
        string counts = row is { Record.FreeBuild: true } ? HousingText.Counts(row.Record.Counts) : "";
        Summary.Text = string.Join("\n\n", new[] { about, counts }.Where(t => t.Length > 0));
        Summary.Visibility = Summary.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        // Why the selected plot cannot take a house, or its house cannot turn.
        string? block = row is { Record.FreeBuild: true } ? SaveDocument.RotationBlock(row.Record) : null;
        Hint.Text = row is null ? "" : block is not null ? Tr.Text("housing.rotate_block." + block) : row.Record.CanImport ? "" : !row.Record.Active ? Tr.Text("housing.activate_first")
            : Tr.Format("housing.furnished", ("count", row.Record.Placed.ToString("N0")));
        Hint.Visibility = Hint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ActivateSelected.IsEnabled = rows.Any(r => r.Record.CanActivate);
        // A page is disabled while an edit runs, and its buttons then read as disabled whatever they were set to.
        bool built = row is { Record.FreeBuild: true } && row.Record.Counts.Total > 0;
        Export.IsEnabled = built;
        Import.IsEnabled = row is { Record.CanImport: true };
        Rotate.IsEnabled = built && block is null;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => Select();
    private async void OnActivateSelected(object sender, RoutedEventArgs e) => await Model.ActivatePlotsAsync(Selected.Where(r => r.Record.CanActivate).Select(r => r.Key).ToArray());
    private async void OnActivateAll(object sender, RoutedEventArgs e) => await Model.ActivatePlotsAsync((Model.Housing ?? []).Where(r => r.CanActivate).Select(r => r.Key).ToArray());

    private async void OnRotate(object sender, RoutedEventArgs e) { if (Single is { } row && Rotate.IsEnabled) await Model.RotateHouseAsync(row.Key); }

    private static string FileName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (Single is not { Record.FreeBuild: true } row) return;
        var dialog = new SaveFileDialog { Filter = Tr.Text("housing.export_filter"), DefaultExt = HouseFiles.Extension, AddExtension = true,
            FileName = FileName(row.Name) + HouseFiles.Extension, OverwritePrompt = true };
        if (dialog.ShowDialog() == true) await Model.ExportHouseAsync(row.Key, dialog.FileName);
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        if (Single is not { Record.CanImport: true } row) return;
        var dialog = new OpenFileDialog { Filter = Tr.Text("housing.import_filter"), CheckFileExists = true };
        if (dialog.ShowDialog() != true) return;
        var layout = Model.ReadHouseFile(dialog.FileName, out string problem);
        if (layout is null || problem.Length > 0)
        {
            ThemedMessageBox.Show(problem, Tr.Text("housing.import"), MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        string source = layout.SourcePlot is uint plot ? HousingText.PlotName(HousingPlotInfo.Find(plot)) : Tr.Text("housing.unknown_plot");
        // The risks first, then what changes on this plot, one bullet each.
        var notes = new List<string> { Tr.Text("housing.import_risk"), Tr.Text("housing.replaces"), Tr.Text("housing.recall_first") };
        if (row.Record.Kind != HouseKind.Free) notes.Add(Tr.Text("housing.switches"));
        if (Model.AddedHouseTypes(layout) is { Length: > 0 } added) notes.Add(Tr.Format("housing.adds_types", ("types", string.Join(", ", added))));
        if (layout.LeftOut is { CurvedWalls: > 0 } curved) notes.Add(Tr.Format("housing.left_out_curved", ("count", curved.CurvedWalls.ToString("N0"))));
        if (layout.LeftOut is { Holes: true }) notes.Add(Tr.Text("housing.left_out_holes"));
        string prompt = Tr.Format("housing.import_prompt", ("file", Path.GetFileName(dialog.FileName)), ("source", source),
            ("counts", HousingText.Counts(layout.Counts)), ("notes", string.Join("\n", notes.Select(n => "• " + n))), ("plot", row.Name));
        if (ThemedMessageBox.Show(prompt, Tr.Text("housing.import"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await Model.ImportHouseAsync(row.Key, layout);
    }
}
