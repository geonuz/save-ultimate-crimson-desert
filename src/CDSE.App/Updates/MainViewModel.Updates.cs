using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CDSE.App;

// Everything in Updates/ is left out of a build with OnlineUpdate=false (see Directory.Build.props).
public partial class MainViewModel
{
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasUpdate))] private AppRelease? availableUpdate;
    public bool HasUpdate => AvailableUpdate is not null;
    public bool CanUpdate => AppUpdate.CanInstall;

    // At startup a failure stays silent, since offline use is normal; the menu command reports it.
    public async Task FindUpdateAsync()
    {
        if (!CanUpdate) return;
        try { AvailableUpdate = await AppUpdate.CheckAsync(); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    [RelayCommand]
    private async Task CheckForUpdateAsync()
    {
        bool answered = false;
        await RunAsync(Tr.Text("ui.update_checking"), async () => { AvailableUpdate = await AppUpdate.CheckAsync(); answered = true; });
        if (!answered) return;
        Status = FileState;
        if (AvailableUpdate is null) ThemedMessageBox.Show(Tr.Format("ui.update_latest", ("version", AppUpdate.Current)));
        else await InstallUpdateAsync();
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (Busy || AvailableUpdate is not { } release) return;
        if (ThemedMessageBox.Show(Tr.Format("ui.update_prompt", ("version", release.Version), ("current", AppUpdate.Current), ("notes", release.NotesIn(Tr.Language))), Tr.Text("ui.update_title"),
            MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.OK, new() { [MessageBoxResult.OK] = Tr.Text("ui.update_now"), [MessageBoxResult.Cancel] = Tr.Text("ui.later") }) != MessageBoxResult.OK ||
            !await ConfirmLeaveAsync()) return;
        await RunAsync(Tr.Format("ui.update_downloading", ("percent", 0)), async () =>
        {
            string package = await AppUpdate.DownloadAsync(release, AppContext.BaseDirectory, new Progress<int>(percent => Status = Tr.Format("ui.update_downloading", ("percent", percent))));
            Status = Tr.Text("ui.update_installing");
            var rollBack = await Task.Run(() => AppUpdate.Install(package, AppContext.BaseDirectory));
            Status = Tr.Text("ui.update_starting");
            if (await AppUpdate.StartNewVersionAsync(AppContext.BaseDirectory)) { Application.Current.Shutdown(); return; }
            await Task.Run(rollBack);
            _ = AppUpdate.CleanUpAsync(AppContext.BaseDirectory);
            Status = Tr.Text("ui.update_rolled_back");
            ThemedMessageBox.Show(Status, Tr.Text("ui.update_title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }
}
