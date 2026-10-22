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
        string? cacheError = null;
        try { CatalogFiles.Reload(); }
        catch (Exception error) when (error is System.IO.IOException or System.Text.Json.JsonException or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { cacheError = error.Message; }
        var model = new MainViewModel();
        if (cacheError is not null) model.Status = Tr.Text("sync.cache_error") + " " + cacheError;
        var window = new MainWindow(model);
        MainWindow = window;
        window.Show();
        if (firstRun && !CatalogFiles.Synced) window.RecommendSync();
        if (e.Args.Length == 1) await model.LoadFileAsync(e.Args[0], createPristine: true);
    }
}
