using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.App;

// The Misc section's skills tab: skills learned by watching them, learned that way here as in the game.
public partial class MainViewModel
{
    public bool IsSkills => CurrentPage == AppPage.Skills;
    [ObservableProperty] private SkillState? skills;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task LoadSkillsAsync() => await RunAsync(Tr.Text("ui.load_data"), async () => { Skills = await Task.Run(() => document!.ReadSkills()); Status = ""; });

    public async Task ObserveSkillsAsync(uint[] keys)
    {
        if (Skills is null || !CanSave()) return;
        await RunAsync(Tr.Text("skills.observe"), async () =>
        {
            int before = keys.Count(Skills.Observed);
            Skills = await Task.Run(() => document!.ObserveSkills(keys));
            if (Knowledge is not null || Fishing is not null || Map is not null) UpdateKnowledgeViews(await Task.Run(() => document!.ReadKnowledge()));
            Refresh();
            Status = Tr.Format("skills.observed", ("count", keys.Count(Skills.Observed) - before));
        });
    }
}
