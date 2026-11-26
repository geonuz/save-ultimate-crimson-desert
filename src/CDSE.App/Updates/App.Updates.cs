namespace CDSE.App;

public partial class App
{
    partial void StartUpdates(MainViewModel model)
    {
        AppUpdate.ReportStarted();
        _ = AppUpdate.CleanUpAsync(AppContext.BaseDirectory);
        _ = model.FindUpdateAsync();
    }
}
