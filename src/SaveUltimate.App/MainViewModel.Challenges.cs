using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.App;

public partial class MainViewModel
{
    public bool IsChallenges => PageIndex == 6;
    public bool IsJournal => IsKnowledge || IsChallenges || IsFishing;
    [ObservableProperty] private int journalPageIndex;
    partial void OnJournalPageIndexChanged(int value) { if (IsJournal) PageIndex = value + 5; }
    [ObservableProperty] private ChallengeState? challenges;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task LoadChallengesAsync() => await RunAsync(Tr.Text("Load Challenges"), async () =>
    {
        Challenges = await Task.Run(() => document!.ReadChallenges());
        Status = "";
    });

    public async Task CompleteChallengesAsync(uint[] roots, uint[]? checkpoints = null)
    {
        if (!CanSave() || Challenges is null) return;
        await RunAsync(Tr.Text("challenge.completing"), async () =>
        {
            var result = await Task.Run(() => document!.CompleteChallenges(roots, checkpoints));
            Challenges = result.State;
            if (Map is not null) Map = Map with { Progress = result.State };
            Refresh();
            Status = Tr.Text("challenge.applied").Replace("{goals}", result.Completed.ToString());
        });
    }
}
