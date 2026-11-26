using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CDSE.Core.Saves;

namespace CDSE.App;

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
        Func<string> name = action switch
        {
            KnowledgeAction.Unlearn => () => Tr.Text("Unlearn Selected"),
            KnowledgeAction.Level => () => Join(Tr.Text("Apply Knowledge Level"), level.ToString()),
            KnowledgeAction.Maximum => () => Tr.Text("Apply Maximum Level"),
            KnowledgeAction.AllMaximum => () => Tr.Text("Learn All Knowledge && Apply Maximum Levels").Replace("&&", "&"),
            _ => () => Tr.Text("Learn Selected")
        };
        await EditAsync(Tr.Text("Validating knowledge changes…"), name, () => KnowledgeNames(keys), async () =>
        {
            var state = await Task.Run(() => document!.EditKnowledge(keys, action, level));
            UpdateKnowledgeViews(state.Levels);
            if (Skills is not null) Skills = state.Skills;
            Refresh();
            Status = Tr.Text("Knowledge changed. Save the file to apply.");
        });
    }
}
