using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.App;

public partial class MainViewModel
{
    public bool IsFishing => PageIndex == 7;
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
        await RunAsync(Tr.Text("fishing.apply"), async () =>
        {
            Fishing = await Task.Run(() => document!.SetFishingRecord(key, size));
            Refresh();
            Status = Tr.Text("Catch record changed. Save the file to apply.");
        });
    }
}
