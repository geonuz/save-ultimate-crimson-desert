using System.Windows;

namespace SaveUltimate.App;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
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
        AppUpdate.ReportStarted();
        if (firstRun && !CatalogFiles.Synced) window.RecommendSync();
        _ = AppUpdate.CleanUpAsync(AppContext.BaseDirectory);
        _ = model.FindUpdateAsync();
        if (e.Args.Length == 1) await model.LoadFileAsync(e.Args[0], createPristine: true);
        await model.CheckGameDataAsync();
    }
}
