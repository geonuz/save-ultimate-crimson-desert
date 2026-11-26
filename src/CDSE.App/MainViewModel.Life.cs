using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CDSE.Core.Saves;

namespace CDSE.App;

// The Life section (business renown, housing) and the Misc section (skills learned by watching, world clock, Save Doctor).
public partial class MainViewModel
{
    public bool IsManagement => CurrentPage == AppPage.Management;
    public bool IsHousing => CurrentPage == AppPage.Housing;
    public bool IsFieldTime => CurrentPage == AppPage.FieldTime;
    public bool IsLife => Array.IndexOf(LifePages, CurrentPage) >= 0;
    private static readonly AppPage[] LifePages = [AppPage.Management, AppPage.Housing];
    public bool IsMisc => Array.IndexOf(MiscPages, CurrentPage) >= 0;
    private static readonly AppPage[] MiscPages = [AppPage.FieldTime, AppPage.Doctor];
    public bool IsDoctor => CurrentPage == AppPage.Doctor;
    [ObservableProperty] private int lifePageIndex;
    partial void OnLifePageIndexChanged(int value) { if (IsLife) CurrentPage = LifePages[value]; }
    [ObservableProperty] private int miscPageIndex;
    partial void OnMiscPageIndexChanged(int value) { if (IsMisc) CurrentPage = MiscPages[value]; }
    [ObservableProperty] private ManagementState[]? management;
    [ObservableProperty] private HousingPlot[]? housing;
    [ObservableProperty] private FieldClock? clock;
    public bool CanAddWorkers => document?.CanAddWorkers == true;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task LoadManagementAsync() => await RunAsync(Tr.Text("ui.load_data"), async () => { Management = await Task.Run(() => document!.ReadManagement()); Status = ""; });
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task LoadHousingAsync() => await RunAsync(Tr.Text("ui.load_data"), async () => { Housing = await Task.Run(() => document!.ReadHousing()); Status = ""; });
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task LoadClockAsync() => await RunAsync(Tr.Text("ui.load_data"), async () => { Clock = await Task.Run(() => document!.ReadFieldTime()); Status = ""; });

    public async Task SetManagementLevelAsync(ushort key, uint level)
    {
        if (!CanSave() || Management is null) return;
        var shop = Management.FirstOrDefault(r => r.Shop.Key == key);
        await EditAsync(Tr.Text("management.apply"), () => Join(Tr.Text("management.apply"), level.ToString()), shop is null ? null : () => new ManagementRow(shop).Name, async () =>
        {
            Management = await Task.Run(() => document!.SetManagementLevel(key, level));
            Refresh(); Status = Tr.Text("management.changed");
        });
    }

    /// <summary>Writes the plot's house to a .suhouse file. The save is not changed.</summary>
    public async Task ExportHouseAsync(uint plot, string path)
    {
        if (!CanSave()) return;
        await RunAsync(Tr.Text("housing.export"), async () =>
        {
            await Task.Run(() => HouseFiles.Write(path, document!.ExportHouse(plot)));
            Status = Tr.Format("housing.exported", ("file", Path.GetFileName(path)));
        });
    }

    /// <summary>Reads a house file for a preview; null with the reason shown when it cannot go into this save.</summary>
    public HouseLayout? ReadHouseFile(string path, out string problem)
    {
        problem = "";
        try
        {
            var layout = HouseFiles.Read(path);
            if (document?.UnknownHouseTypes(layout) is { Length: > 0 } unknown)
                problem = Tr.Format("housing.unknown_types", ("types", string.Join(", ", unknown.Select(HousingText.TypeName))));
            return layout;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            problem = Tr.Format("housing.unreadable", ("reason", error.Message));
            return null;
        }
    }

    /// <summary>Part kinds the import adds to this save's schema, named as the player builds them.</summary>
    public string[] AddedHouseTypes(HouseLayout layout) => document?.AddedHouseTypes(layout).Select(HousingText.TypeName).ToArray() ?? [];

    public async Task ImportHouseAsync(uint plot, HouseLayout layout)
    {
        if (!CanSave()) return;
        var info = Housing?.FirstOrDefault(r => r.Info.Key == plot)?.Info;
        await EditAsync(Tr.Text("housing.import"), () => Caption("housing.import"), info is null ? null : () => HousingText.PlotName(info), async () =>
        {
            Housing = await Task.Run(() => document!.ImportHouse(plot, layout));
            Refresh(); Status = Tr.Text("housing.imported");
        });
    }

    public async Task RotateHouseAsync(uint plot)
    {
        if (!CanSave()) return;
        var info = Housing?.FirstOrDefault(r => r.Info.Key == plot)?.Info;
        await EditAsync("housing.rotate", info is null ? null : () => HousingText.PlotName(info), async () =>
        {
            Housing = await Task.Run(() => document!.RotateHouse(plot));
            Refresh(); Status = Tr.Text("housing.rotated");
        });
    }

    public async Task ActivatePlotsAsync(uint[] plots)
    {
        if (!CanSave() || plots.Length == 0) return;
        var info = Housing?.FirstOrDefault(r => r.Info.Key == plots[0])?.Info;
        await EditAsync("housing.activate", info is null ? null : () => Some([HousingText.PlotName(info)], plots.Length), async () =>
        {
            Housing = await Task.Run(() => document!.ActivatePlots(plots));
            // Activation teaches each plot's knowledge.
            UpdateKnowledgeViews(document!.ReadKnowledge());
            Refresh(); Status = Tr.Format("housing.activated", ("count", plots.Length.ToString("N0")));
        });
    }

    public async Task AdvanceClockAsync(FieldClock target)
    {
        if (!CanSave() || Clock is null) return;
        await EditAsync("fieldtime.apply", () => FieldTimePage.Describe(target), async () =>
        {
            Clock = await Task.Run(() => document!.AdvanceFieldTime(target));
            Refresh(); Status = Tr.Text("fieldtime.changed");
        });
    }
}
