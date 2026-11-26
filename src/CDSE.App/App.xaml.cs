using System.Windows;

namespace CDSE.App;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        // The close button of a dialog's title bar; a running task's window closes itself.
        System.Windows.Input.CommandManager.RegisterClassCommandBinding(typeof(Window), new(SystemCommands.CloseWindowCommand,
            (window, _) => SystemCommands.CloseWindow((Window)window), (window, e) => e.CanExecute = window is not ProgressWindow));
        Appearance.Apply(SettingsStore.Current);
    }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool firstRun = !System.IO.File.Exists(SettingsStore.FilePath);
        string? cacheError = CatalogFiles.TryReload();
        var model = new MainViewModel();
        if (cacheError is not null) model.Status = Tr.Text("sync.cache_error") + " " + cacheError;
        var window = new MainWindow(model);
        MainWindow = window;
        window.Show();
        // Before RecommendSync: its dialog would hold back the signal an updating version waits for.
        StartUpdates(model);
        if (firstRun && !CatalogFiles.Synced) window.RecommendSync();
        if (e.Args.Length == 1) await model.LoadFileAsync(e.Args[0], createPristine: true);
        await model.CheckGameDataAsync();
    }

    // Implemented in Updates/App.Updates.cs; without that file (OnlineUpdate=false) the compiler drops the call.
    partial void StartUpdates(MainViewModel model);
}
