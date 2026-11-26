using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CDSE.Core.Saves;

namespace CDSE.App;

public partial class MainViewModel
{
    public bool IsFishing => CurrentPage == AppPage.Fishing;
    [ObservableProperty] private FishingRecord[]? fishing;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task LoadFishingAsync() => await RunAsync(Tr.Text("ui.load_data"), async () =>
    {
        Fishing = await Task.Run(() => document!.ReadFishing());
        Status = "";
    });

    public async Task SetFishingRecordAsync(uint key, double size) 
    {
        if (!CanSave() || Fishing is null) return;
        await EditAsync(Tr.Text("fishing.apply"), () => Join(Tr.Text("fishing.apply"), size.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " cm"), () => KnowledgeNames([key]), async () =>
        {
            Fishing = await Task.Run(() => document!.SetFishingRecord(key, size));
            Refresh();
            Status = Tr.Text("Catch record changed. Save the file to apply.");
        });
    }
}
