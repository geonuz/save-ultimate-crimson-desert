using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.App;

public partial class MainViewModel
{
    public bool IsKnowledge => PageIndex == 5;
    [ObservableProperty] private IReadOnlyDictionary<uint, uint>? knowledge;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task LoadKnowledgeAsync() => await RunAsync(Tr.Text("Reading knowledge…"), async () =>
    {
        Knowledge = await Task.Run(() => document!.ReadKnowledge());
        Status = Tr.Text("Knowledge loaded.");
    });

    public async Task EditKnowledgeAsync(uint[] keys, KnowledgeAction action, uint level = 1)
    {
        if (!CanSave() || Knowledge is null && Fishing is null) return;
        await RunAsync(Tr.Text("Validating knowledge changes…"), async () =>
        {
            var state = await Task.Run(() => document!.EditKnowledge(keys, action, level));
            UpdateKnowledgeViews(state.Levels);
            if (Skills is not null) Skills = state.Skills;
            Refresh();
            Status = Tr.Text("Knowledge changed. Save the file to apply.");
        });
    }
}
