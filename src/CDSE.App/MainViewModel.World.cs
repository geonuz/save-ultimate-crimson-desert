using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CDSE.Core.Saves;

namespace CDSE.App;

public partial class MainViewModel
{
    public bool IsMapPlaces => CurrentPage == AppPage.Map;
    public bool IsMapFragments => CurrentPage == AppPage.MapFragments;
    public bool IsMapEnvironment => CurrentPage == AppPage.MapEnvironment;
    public bool IsMap => Array.IndexOf(MapPages, CurrentPage) >= 0;
    private static readonly AppPage[] MapPages = [AppPage.Map, AppPage.MapFragments, AppPage.MapEnvironment];
    [ObservableProperty] private int mapPageIndex;
    partial void OnMapPageIndexChanged(int value) { if (IsMap) CurrentPage = MapPages[value]; }
    public bool IsCompanions => CurrentPage == AppPage.Companions;
    public bool IsFriendship => CurrentPage == AppPage.Friendship;
    public bool IsPets => CurrentPage == AppPage.Pets;
    public bool IsNpc => IsCompanions || IsFriendship;
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
        await EditAsync("map.reveal", null, async () => { var fog = await Task.Run(() => document!.RevealMap()); Map = Map with { Fog = fog }; Refresh(); Status = Tr.Text("map.fog_full"); });
    }
    public async Task ActivateMapAsync(int index)
    {
        if (!CanSave() || Map is null) return;
        await EditAsync("map.activate_all", index >= 0 && index < MapAction.All.Length ? () => MapRow.ActionName(MapAction.All[index]) : null, async () =>
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
    /// <summary>Activates the chosen rows of the place and Abyss tables as one undo step.</summary>
    public async Task ActivateMapEntriesAsync(IReadOnlyCollection<MapEntry> entries)
    {
        if (!CanSave() || Map is null || entries.Count == 0) return;
        await EditAsync("map.activate_selected", () => Tr.Format("port.selected", ("count", entries.Count.ToString("N0"))), async () =>
        {
            var result = await Task.Run(() => document!.ActivateMapEntries(entries));
            Map = result.Map; UpdateKnowledgeViews(result.Map.Knowledge);
            if (Skills is not null) Skills = result.Skills;
            if (Challenges is not null) Challenges = result.Map.Progress;
            Refresh(); Status = Tr.Text("Map activation applied. Save the file to apply.");
        });
    }
    private void UpdateKnowledgeViews(IReadOnlyDictionary<uint, uint> levels)
    {
        // Edits that teach knowledge also write its learn-from record, so the records are read again with the levels.
        if (Knowledge is not null) { LearnRecords = document!.ReadLearnRecords(); Knowledge = levels; }
        if (Fishing is not null) Fishing = Fishing.Select(r => r with { Level = levels.GetValueOrDefault(r.Fish.Key) }).ToArray();
        if (Map is not null) Map = Map with { Knowledge = levels };
    }
    public async Task SetFriendshipAsync(uint key, ulong expected, int value)
    {
        if (!CanSave() || Friendship is null) return;
        await EditAsync(Tr.Text("npc.apply"), () => Join(Tr.Text("npc.apply"), value.ToString("N0")), () => CharacterText.Get(key).Name, async () => { Friendship = await Task.Run(() => document!.SetFriendship(key, expected, value)); Refresh(); Status = Tr.Text("NPC affinity changed."); });
    }
    public async Task SetAffinityAsync(uint key, uint playable, ulong expected, int value)
    {
        if (!CanSave() || Friendship is null) return;
        await EditAsync(Tr.Text("npc.apply"), () => Join(Tr.Text("npc.affinity_apply"), value.ToString("N0")), () => CharacterText.Get(key).Name, async () => { Friendship = await Task.Run(() => document!.SetAffinity(key, playable, expected, value)); Refresh(); Status = Tr.Text("npc.affinity_changed"); });
    }
    public Dictionary<ulong, CompanionDetail> CompanionDetails() => document?.ReadCompanionDetails() ?? [];
    public HashSet<ulong> FactionWorkers() => document?.FactionWorkers() ?? [];
    public async Task EditWorkersAsync(uint add, Companion? remove)
    {
        if (!CanSave() || Companions is null) return;
        string key = remove is null ? "companion.robot_add" : "companion.robot_remove";
        uint character = remove?.Key ?? add;
        await EditAsync(Tr.Text(key), () => Caption(key), () => CharacterText.Get(character).Name, async () =>
        {
            Companions = await Task.Run(() => remove is null ? document!.AddWorker(add) : document!.RemoveWorker(remove));
            Refresh(); Status = Tr.Text("companion.robot_changed");
        });
    }
    public bool CanAddTo(string group) => document?.CanAddTo(group) == true;
    public HashSet<ulong> QuestLinked() => document?.QuestLinked() ?? [];
    public string? ReleaseBlock(Companion companion, IReadOnlySet<ulong> questLinked) => document?.ReleaseBlock(companion, questLinked);
    public GearSlot[] GearOf(Companion companion) => document?.GearOf(companion) ?? [];
    public uint[] GearChoices(Companion companion, int slot) => document?.GearChoices(companion, slot) ?? [];
    public async Task AddCompanionAsync(uint key)
    {
        if (!CanSave() || Companions is null) return;
        await EditAsync(Tr.Text("companion.add"), () => Caption("companion.add"), () => CharacterText.Get(key).Name, async () =>
        {
            Companions = await Task.Run(() => document!.AddCompanion(key));
            Refresh(); Status = Tr.Text("companion.added");
        });
    }
    public async Task RemoveCompanionAsync(Companion companion)
    {
        if (!CanSave() || Companions is null) return;
        await EditAsync("companion.remove", () => CompanionName(companion), async () =>
        {
            Companions = await Task.Run(() => document!.RemoveCompanion(companion));
            Refresh(); Status = Tr.Text("companion.removed");
        });
    }
    /// <summary>Puts an item in a gear slot of a pet or mount, or empties it (key 0).</summary>
    public async Task SetGearAsync(Companion companion, int slot, uint key)
    {
        if (!CanSave() || Companions is null) return;
        await EditAsync("companion.gear_change", () => Join(CompanionName(companion), key == 0 ? Tr.Text("companion.gear_off") : ItemCatalog.Shared.Name(key)), async () =>
        {
            Companions = await Task.Run(() => document!.SetGear(companion, slot, key));
            Refresh(); Status = Tr.Text("companion.gear_changed");
        });
    }
    public async Task SwapCompanionsAsync(Companion first, Companion second)
    {
        if (!CanSave() || Companions is null) return;
        await EditAsync("companion.move", () => CompanionName(first), async () => { Companions = await Task.Run(() => document!.SwapCompanions(first, second)); Refresh(); Status = Tr.Text("companion.moved"); });
    }
    private static string CompanionName(Companion c) => string.IsNullOrEmpty(c.Name) ? CharacterText.Get(c.Key).Name : c.Name;
    public async Task RenameCompanionsAsync(Companion[] selected, string? name)
    {
        if (!CanSave() || Companions is null) return;
        await EditAsync(Tr.Text("Rename Selected"), () => Tr.Text(name is null ? "Clear Name" : "Rename Selected"),
            () => Some(selected.Take(1).Select(CompanionName), selected.Length) + (name is null ? "" : " → " + name), async () => { Companions = await Task.Run(() => document!.RenameCompanions(selected, name)); Refresh(); Status = Tr.Text("Companion name changed."); });
    }
}
