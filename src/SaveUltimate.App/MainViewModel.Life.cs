using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.App;

// The Life section (business renown) and the Misc section (skills learned by watching, world clock).
public partial class MainViewModel
{
    public bool IsManagement => CurrentPage == AppPage.Management;
    public bool IsFieldTime => CurrentPage == AppPage.FieldTime;
    public bool IsLife => IsManagement;
    public bool IsMisc => Array.IndexOf(MiscPages, CurrentPage) >= 0;
    private static readonly AppPage[] MiscPages = [AppPage.Skills, AppPage.FieldTime];
    [ObservableProperty] private int lifePageIndex;
    [ObservableProperty] private int miscPageIndex;
    partial void OnMiscPageIndexChanged(int value) { if (IsMisc) CurrentPage = MiscPages[value]; }
    [ObservableProperty] private ManagementState[]? management;
    [ObservableProperty] private FieldClock? clock;
    public bool CanAddWorkers => document?.CanAddWorkers == true;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task LoadManagementAsync() => await RunAsync(Tr.Text("ui.load_data"), async () => { Management = await Task.Run(() => document!.ReadManagement()); Status = ""; });
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task LoadClockAsync() => await RunAsync(Tr.Text("ui.load_data"), async () => { Clock = await Task.Run(() => document!.ReadFieldTime()); Status = ""; });

    public async Task SetManagementLevelAsync(ushort key, uint level)
    {
        if (!CanSave() || Management is null) return;
        await RunAsync(Tr.Text("management.apply"), async () =>
        {
            Management = await Task.Run(() => document!.SetManagementLevel(key, level));
            Refresh(); Status = Tr.Text("management.changed");
        });
    }

    public async Task AdvanceClockAsync(FieldClock target)
    {
        if (!CanSave() || Clock is null) return;
        await RunAsync(Tr.Text("fieldtime.apply"), async () =>
        {
            Clock = await Task.Run(() => document!.AdvanceFieldTime(target));
            Refresh(); Status = Tr.Text("fieldtime.changed");
        });
    }
}
