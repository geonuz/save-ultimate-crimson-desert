using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.App;

public partial class MainViewModel
{
    public bool IsSkills => PageIndex == 4;
    [ObservableProperty] private SkillState? skills;
    public IReadOnlyDictionary<uint, uint>? SkillBaseline { get; private set; }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task LoadSkillsAsync() => await RunAsync(Tr.Text("Reading skills and investment records…"), async () =>
    {
        var state = await Task.Run(() => document!.ReadSkills());
        SkillBaseline ??= state.Levels;
        Skills = state;
        Status = Tr.Text("Skills and investment records loaded.");
    });

    public async Task LearnSkillAsync(uint key, bool watch)
    {
        if (Skills is null || !CanSave()) return;
        uint target = watch ? 1 : Skills.Level(key) + 1;
        await RunAsync(Tr.Text("Validating skills, investments and materials…"), async () =>
        {
            Skills = await Task.Run(() => document!.LearnSkill(key, target, watch));
            if (Knowledge is not null || Fishing is not null || Map is not null) UpdateKnowledgeViews(await Task.Run(() => document!.ReadKnowledge()));
            Refresh();
            Status = Tr.Format("port.skill_applied", ("name", ItemCatalog.Shared.KnowledgeName(key)), ("level", target));
        });
    }
}
