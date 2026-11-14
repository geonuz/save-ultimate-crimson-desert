using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.App;

// The collection box tab: every item the box can hold, filled one by one, by class, or all at once.
public partial class MainViewModel
{
    public bool IsCollection => CurrentPage == AppPage.Collection;
    [ObservableProperty] private CollectionState[]? collection;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task LoadCollectionAsync() => await RunAsync(Tr.Text("ui.load_data"), async () => { Collection = await Task.Run(() => document!.ReadCollection()); Status = ""; });

    public async Task FillCollectionAsync(uint[] keys, bool learn)
    {
        if (!CanSave() || Collection is null) return;
        await RunAsync(Tr.Text("collection.fill"), async () =>
        {
            int before = Collection.Count(s => s.Status == CollectionStatus.Collected);
            await Task.Run(() => document!.FillCollection(keys, learn));
            if (learn)
            {
                if (Skills is not null) Skills = await Task.Run(() => document!.ReadSkills());
                if (Knowledge is not null || Fishing is not null || Map is not null) UpdateKnowledgeViews(await Task.Run(() => document!.ReadKnowledge()));
            }
            Refresh();
            Status = Tr.Format("collection.filled", ("count", Collection.Count(s => s.Status == CollectionStatus.Collected) - before));
        });
    }
}
