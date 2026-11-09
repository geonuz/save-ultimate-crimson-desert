using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.App;

public partial class MainViewModel
{
    public bool IsMap => CurrentPage == AppPage.Map;
    public bool IsCompanions => CurrentPage == AppPage.Companions;
    public bool IsFriendship => CurrentPage == AppPage.Friendship;
    public bool IsPets => CurrentPage == AppPage.Pets;
    public bool IsNpc => IsCompanions || IsFriendship || IsPets;
    [ObservableProperty] private int npcPageIndex;
    partial void OnNpcPageIndexChanged(int value) { if (IsNpc) CurrentPage = NpcPages[value]; }
    [ObservableProperty] private MapState? map;
    [ObservableProperty] private Companion[]? companions;
    [ObservableProperty] private NpcFriendship[]? friendship;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task LoadMapAsync() => await RunAsync(Tr.Text("map.load"), async () => { Map = await Task.Run(() => document!.ReadMap()); Status = ""; });
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task LoadCompanionsAsync() => await RunAsync(Tr.Text("mercenary.scanning"), async () => { Companions = await Task.Run(() => document!.ReadCompanions()); Status = ""; });
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task LoadFriendshipAsync() => await RunAsync(Tr.Text("npc.load"), async () => { Friendship = await Task.Run(() => document!.ReadFriendship()); Status = ""; });
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task RevealMapAsync()
    {
        if (Map is null) return;
        await RunAsync(Tr.Text("map.reveal"), async () => { var fog = await Task.Run(() => document!.RevealMap()); Map = Map with { Fog = fog }; Refresh(); Status = Tr.Text("map.fog_full"); });
    }
    public async Task ActivateMapAsync(int index)
    {
        if (!CanSave() || Map is null) return;
        await RunAsync(Tr.Text("map.activate_all"), async () =>
        {
            if (index >= 0 && index < MapAction.All.Length && MapAction.All[index].Mode != "icons")
            {
                var result = await Task.Run(() => document!.LearnMapLocations(index));
                UpdateKnowledgeViews(result.Levels);
                if (Skills is not null) Skills = result.Skills;
            }
            else
            {
                var result = await Task.Run(() => document!.ActivateMap(index));
                Map = result.Map; UpdateKnowledgeViews(result.Map.Knowledge);
                if (Skills is not null) Skills = result.Skills;
                if (Challenges is not null) Challenges = result.Map.Progress;
            }
            Refresh(); Status = Tr.Text("Map activation applied. Save the file to apply.");
        });
    }
    private void UpdateKnowledgeViews(IReadOnlyDictionary<uint, uint> levels)
    {
        if (Knowledge is not null) Knowledge = levels;
        if (Fishing is not null) Fishing = Fishing.Select(r => r with { Level = levels.GetValueOrDefault(r.Fish.Key) }).ToArray();
        if (Map is not null) Map = Map with { Knowledge = levels };
    }
    public async Task SetFriendshipAsync(uint key, ulong expected, int value)
    {
        if (!CanSave() || Friendship is null) return;
        await RunAsync(Tr.Text("npc.apply"), async () => { Friendship = await Task.Run(() => document!.SetFriendship(key, expected, value)); Refresh(); Status = Tr.Text("NPC affinity changed."); });
    }
    public async Task SetAffinityAsync(uint key, uint playable, ulong expected, int value)
    {
        if (!CanSave() || Friendship is null) return;
        await RunAsync(Tr.Text("npc.apply"), async () => { Friendship = await Task.Run(() => document!.SetAffinity(key, playable, expected, value)); Refresh(); Status = Tr.Text("npc.affinity_changed"); });
    }
    public Dictionary<ulong, CompanionDetail> CompanionDetails() => document?.ReadCompanionDetails() ?? [];
    public HashSet<ulong> FactionWorkers() => document?.FactionWorkers() ?? [];
    public async Task EditWorkersAsync(uint add, Companion? remove)
    {
        if (!CanSave() || Companions is null) return;
        await RunAsync(Tr.Text(remove is null ? "companion.robot_add" : "companion.robot_remove"), async () =>
        {
            Companions = await Task.Run(() => remove is null ? document!.AddWorker(add) : document!.RemoveWorker(remove));
            Refresh(); Status = Tr.Text("companion.robot_changed");
        });
    }
    public async Task SwapCompanionsAsync(Companion first, Companion second)
    {
        if (!CanSave() || Companions is null) return;
        await RunAsync(Tr.Text("companion.move"), async () => { Companions = await Task.Run(() => document!.SwapCompanions(first, second)); Refresh(); Status = Tr.Text("companion.moved"); });
    }
    public async Task RenameCompanionsAsync(Companion[] selected, string? name)
    {
        if (!CanSave() || Companions is null) return;
        await RunAsync(Tr.Text("Rename Selected"), async () => { Companions = await Task.Run(() => document!.RenameCompanions(selected, name)); Refresh(); Status = Tr.Text("Companion name changed."); });
    }
}
