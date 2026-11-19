using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.App;

public partial class MainViewModel
{
    public bool IsKnowledge => CurrentPage == AppPage.Knowledge;
    [ObservableProperty] private IReadOnlyDictionary<uint, uint>? knowledge;
    /// <summary>The learn-from record of each knowledge, read with <see cref="Knowledge"/> and set before it.</summary>
    public IReadOnlyDictionary<uint, uint> LearnRecords { get; private set; } = new Dictionary<uint, uint>();

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task LoadKnowledgeAsync() => await RunAsync(Tr.Text("Reading knowledge…"), async () =>
    {
        var (levels, records) = await Task.Run(() => (document!.ReadKnowledge(), document!.ReadLearnRecords()));
        LearnRecords = records;
        Knowledge = levels;
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
