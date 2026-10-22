using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SaveUltimate.Core.Catalogs;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.App;

public sealed record ItemRow(InventoryItem Item, string Name, string Category = "", string InternalName = "")
{
    public (ItemSource, ulong, uint, ushort, ulong, ushort, ushort, string) Identity =>
        (Item.Source, Item.Number, Item.Key, Item.Bag, Item.OwnerNumber, Item.Slot, Item.StoreKey, Item.OwnerSlot);
    public int ActualEndurance => Item.Endurance & 0xff;
    public string SocketText => Item.Sockets.Error is null && SocketState.Limit(Item.Key) > 0 ? $"{Item.Sockets.Slots.Count(s => s.Key != 0)} / {Item.Sockets.Unlocked} / {SocketState.Limit(Item.Key)}" : "—";
    public string EnchantText => Item.EnchantLevel is ushort level ? $"+{level} / {(Item.MaxEnchant < 0 ? "?" : Item.MaxEnchant.ToString())}" : "—";
    public string EnchantValue => Item.EnchantLevel is ushort level ? $"+{level}" : "-";
    public string OwnerText => ItemCatalog.Shared.Owner(Item);
    public string VendorName => ItemCatalog.Shared.Store(Item.StoreKey);
    public string CategoryHint => ItemCatalog.Shared.CategoryHint(Item.Key);
    public System.Windows.Media.Imaging.BitmapImage? Icon => ItemImages.Get(Item.Key, 32);
    public System.Windows.Media.Imaging.BitmapImage? DetailImage => ItemImages.Get(Item.Key, 180);
    public string Stats => ItemCatalog.Shared.Stats(Item);
    public string Description => ItemCatalog.Shared.Description(Item.Key);
    public string BagName => Item.Source == ItemSource.Equipment ? Tr.Text("Equipment Window")
        : Item.Source == ItemSource.Vendor ? Tr.Text("Vendor") + " · " + ItemCatalog.Shared.Store(Item.StoreKey) : Item.Source == ItemSource.Mercenary ? Tr.Text("Mercenary") : Item.Bag switch
    {
        1 => Tr.Text("Money"), 2 => Tr.Text("tab.inventory"), 5 => Tr.Text("quest.type_quest"), 8 => Tr.Text("Personal Storage"), 9 => Tr.Text("Trade Storage"), 10 => Tr.Text("Bank"),
        13 => Tr.Text("Kuku Pot"), 14 => Tr.Text("Default Equipment"), 15 => Tr.Text("Wardrobe"), 16 => Tr.Text("Food Storage"), 18 => Tr.Text("Collectibles Storage"),
        19 => Tr.Text("Gathering Storage"), 20 => Tr.Text("Bird Feeder"), _ => Tr.Format("port.bag", ("id", Item.Bag))
    };
}

public partial class StorageFilter(string name, ushort? Bag = null, bool Other = false, ItemSource? Source = null) : ObservableObject
{
    public string Name => Tr.Text(name);
    public void RefreshLanguage() => OnPropertyChanged(nameof(Label));
    public ushort? Bag { get; } = Bag;
    public bool Other { get; } = Other;
    public ItemSource? Source { get; } = Source;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Label))] private int count;
    public string Label => $"{Name} ({Count:N0})";
}
public partial class MainViewModel : ObservableObject
{
    public event Action? ViewChanging;
    public event Action? ViewChanged;
    public event Action? ViewReset;
    private SaveDocument? document;
    private bool savedInSession, lastLoadSucceeded;
    private string gameFolder = SaveLocations.GameFolder(SettingsStore.Current);
    public string? LoadedPath => document?.SourcePath;
    public bool HasDocument => document is not null;
    public bool HasUnsavedChanges => !Busy && document?.IsDirty == true;
    public string FileName => LoadedPath is null ? Tr.Text("No file loaded") : SaveSlot.FriendlyName(Path.GetFileName(Path.GetDirectoryName(LoadedPath))!) + " · " + Path.GetFileName(LoadedPath);
    public string FileState => Tr.Text(document is null ? "ui.choose_save" : document.IsDirty ? "ui.unsaved" : savedInSession ? "ui.applied" : "ui.saved");
    public string FileTone => document is null ? "" : document.IsDirty ? "warning" : savedInSession ? "success" : "";
    public string SaveFolderText => Tr.Text("ui.save_folder") + ": " + (LoadedPath is null ? Tr.Text("ui.path_not_found") : Path.GetDirectoryName(LoadedPath));
    public string GameFolderText => Tr.Text("ui.game_folder") + ": " + (gameFolder.Length > 0 ? gameFolder : Tr.Text("ui.path_not_found"));
    public string ItemPageTitle => Tr.Text(ItemHelpTopic);
    public string ItemHelpTopic => IsInventory ? "tab.inventory" : IsRepurchase ? "tab.repurchase" : "tab.equipment";
    private ItemRow[] selection = [];
    private bool changingView;
    private InventoryItem? socketTarget;
    private readonly SocketRow?[] socketFilters = new SocketRow?[5];
    [ObservableProperty] private SocketRow[] socketRows = [];
    [ObservableProperty] private int[] socketCounts = [];
    [ObservableProperty] private int unlockedSockets;
    public IEnumerable<UnlockChoice> SocketOptions => Enumerable.Range(0, SocketRows.Length + 1).Select(i => new UnlockChoice(i, SocketCounts.Contains(i)));
    public bool HasSocketEditor => SocketRows.Length > 0;
    partial void OnSocketRowsChanged(SocketRow[] value) { OnPropertyChanged(nameof(SocketOptions)); OnPropertyChanged(nameof(SocketPreview)); OnPropertyChanged(nameof(HasSocketEditor)); }
    partial void OnSocketCountsChanged(int[] value) => OnPropertyChanged(nameof(SocketOptions));
    [ObservableProperty] private int pageIndex;
    [ObservableProperty] private int itemPageIndex;
    partial void OnItemPageIndexChanged(int value) { if (IsItems) PageIndex = value == 3 ? 11 : value; }
    private readonly string[] pageSearches = ["", "", "", "", "", "", "", "", "", "", "", ""];
    public bool IsDatabase => PageIndex == 11;
    public bool IsItemEditor => PageIndex < 3;
    public bool IsInventory => PageIndex == 0;
    public bool IsRepurchase => PageIndex == 1;
    public bool IsEquipment => PageIndex == 2;
    public bool HasQuantityEditor => IsInventory || IsRepurchase;
    public string PageTitle => "Crimson Desert · " + (IsInventory ? Tr.Text("tab.inventory") : IsRepurchase ? Tr.Text("tab.repurchase") : IsEquipment ? Tr.Text("tab.equipment") : IsSkills ? Tr.Text("skills.title") : IsJournal ? Tr.Text("tab.knowledge_quest") : IsMap ? Tr.Text("tab.maps") : IsNpc ? Tr.Text("tab.mercenary") : Tr.Text("tab.backup"));
    public ItemRow? Detail => HasQuantityEditor && selection.Length == 1 ? selection[0] : null;
    public string SocketPreview => string.Join(" ", Enumerable.Range(0, SocketRows.Length).Select(i => i < UnlockedSockets ? "●" : "○")) + $"  {UnlockedSockets} / {SocketRows.Length}";
    partial void OnPageIndexChanging(int value)
    {
        changingView = true;
        ViewChanging?.Invoke();
        pageSearches[PageIndex] = Search;
    }
    partial void OnPageIndexChanged(int value)
    {
        if (value < 3) ItemPageIndex = value;
        if (value == 11) ItemPageIndex = 3;
        if (value is 5 or 6 or 7) JournalPageIndex = value - 5;
        if (value is 9 or 10) NpcPageIndex = value - 9;
        SetSelection([]); Selected = null;
        Search = pageSearches[value];
        ItemsView.Refresh();
        foreach (string property in new[] { nameof(IsInventory), nameof(IsRepurchase), nameof(IsEquipment), nameof(HasQuantityEditor), nameof(CountText), nameof(Detail), nameof(PageTitle), nameof(IsBackups), nameof(IsItems), nameof(IsSkills), nameof(IsKnowledge), nameof(IsJournal), nameof(IsChallenges), nameof(IsFishing), nameof(IsMap), nameof(IsNpc), nameof(IsCompanions), nameof(IsFriendship) }) OnPropertyChanged(property);
        RefreshCommands();
        OnPropertyChanged(nameof(IsDatabase)); OnPropertyChanged(nameof(IsItemEditor));
        // Listing backups is a quick directory read; keep the progress window and status for the explicit refresh button.
        if (IsBackups) ReadBackups();
        ViewChanged?.Invoke();
        changingView = false;
        RefreshSockets();
        if (IsSkills && Skills is null && document is not null) _ = LoadSkillsAsync();
    }
    [ObservableProperty] private string socketInfo = Tr.Text("Select one item to edit sockets.");
    public IReadOnlyList<StorageFilter> StorageFilters { get; } = [
        new("map.all"), new("Equipment Window", Source: ItemSource.Equipment), new("tab.inventory", 2), new("quest.type_quest", 5), new("Personal Storage", 8), new("Wardrobe", 15),
        new("Collectibles Storage", 18), new("Gathering Storage", 19), new("Food Storage", 16),
        new("Bank", 10), new("Money", 1), new("Mercenary", Source: ItemSource.Mercenary), new("Other", Other: true)];
    [ObservableProperty] private StorageFilter storage;
    [ObservableProperty] private bool busy;
    [ObservableProperty] private string status = Tr.Text("Open a save file. Ctrl+O");
    [ObservableProperty] private string fileText = "";
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string quantityText = "1";
    [ObservableProperty] private ushort enchantLevel = 1;
    public IReadOnlyList<ushort> EnchantLevels { get; } = Enumerable.Range(1, 10).Select(i => (ushort)i).ToArray();
    [ObservableProperty] private ItemRow? selected;
    [ObservableProperty] private ICollectionView itemsView;
    public bool IsEmpty => document is null;
    public bool CanEditSelection => !Busy;
    public string CountText
    {
        get
        {
            if (document is null) return "";
            var items = document.Items.Where(MatchesPage).ToArray();
            string count = Tr.Count(items.Length, ItemsView.Cast<object>().Count());
            if (!IsInventory || Storage.Bag is null) return count;
            decimal slots = 0;
            foreach (var item in items)
            {
                ulong max = ItemCatalog.Shared.MaxStack(item.Key);
                if (max == 0) return count + Tr.Text(" · Slot estimate unavailable");
                slots += decimal.Ceiling((decimal)item.Quantity / max);
            }
            return count + Tr.Format("port.slots", ("count", slots.ToString("N0")));
        }
    }
    public string SelectionTitle => selection.Length == 1 ? selection[0].Name : selection.Length == 0 ? Tr.Text("ui.select_item") : SelectionText;
    public string EnchantHint => Tr.Format("equipment.enchant_hint", ("eligible", selection.Count(r => r.Item.EnchantLevel.HasValue)), ("total", selection.Length));
    public string SelectionText => Tr.Format("port.selected", ("count", selection.Length.ToString("N0"))) + (selection.Any(r => !r.Item.CanEditQuantity)
        ? Tr.Format("port.restricted", ("count", selection.Count(r => !r.Item.CanEditQuantity).ToString("N0"))) : "");

    public MainViewModel(SaveDocument? document = null)
    {
        this.document = document;
        storage = StorageFilters[0];
        itemsView = CollectionViewSource.GetDefaultView(Array.Empty<ItemRow>());
        if (document is not null) { Refresh(); Status = Tr.Format("port.inventory_loaded", ("count", document.Items.Count.ToString("N0"))); }
    }

    private bool CanWork() => !Busy;
    private bool CanSave() => !Busy && document is not null;
    private bool CanUndo() => !Busy && document?.CanUndo == true;
    private bool CanApply() => !Busy && HasQuantityEditor && (selection.Length == 0 ? Selected?.Item.CanEditQuantity == true : selection.Any(r => r.Item.CanEditQuantity));
    private bool CanEnchant() => !Busy && IsEquipment && (selection.Length == 0 ? Selected?.Item.EnchantLevel.HasValue == true : selection.Any(r => r.Item.EnchantLevel.HasValue));
    private bool CanSockets() => !Busy && IsEquipment && socketTarget is not null && SocketRows.Any(r => r.IsEditable);
    private bool CanSwap() => !Busy && IsRepurchase && SelectedItems() is { Length: > 0 } items && items.All(i => i.Source == ItemSource.Vendor);
    private bool CanDelete() => IsInventory && CanApply();
    private bool CanUnlockSockets() => !Busy && IsEquipment && socketTarget is not null && SocketCounts.Contains(UnlockedSockets);
    partial void OnUnlockedSocketsChanged(int value) { UnlockSocketsCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(SocketPreview)); }
    partial void OnBusyChanged(bool value) { RefreshCommands(); OnPropertyChanged(nameof(CanEditSelection)); OnPropertyChanged(nameof(HasUnsavedChanges)); }
    partial void OnSelectedChanged(ItemRow? value)
    {
        if (value is not null) QuantityText = value.Item.Quantity.ToString(CultureInfo.InvariantCulture);
        if (value?.Item.EnchantLevel is ushort level) EnchantLevel = (ushort)Math.Clamp((int)level, 1, 10);
        EnchantCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        RefreshSockets();
        SwapCommand.NotifyCanExecuteChanged();
    }
    partial void OnSearchChanged(string value) => RefreshFilter();
    partial void OnStorageChanged(StorageFilter value) => RefreshFilter();
    private void RefreshFilter()
    {
        bool preserve = IsEquipment && !changingView;
        if (preserve) { changingView = true; ViewChanging?.Invoke(); }
        if (!changingView && !IsEquipment) { Selected = null; SetSelection([]); }
        ItemsView.Refresh();
        if (preserve) { ViewChanged?.Invoke(); changingView = false; RefreshSockets(); }
        OnPropertyChanged(nameof(CountText));
    }
    public void SetSelection(IEnumerable<ItemRow> rows)
    {
        selection = rows.ToArray();
        OnPropertyChanged(nameof(SelectionText)); OnPropertyChanged(nameof(SelectionTitle));
        OnPropertyChanged(nameof(EnchantHint));
        OnPropertyChanged(nameof(Detail));
        ApplyCommand.NotifyCanExecuteChanged(); DeleteCommand.NotifyCanExecuteChanged();
        EnchantCommand.NotifyCanExecuteChanged();
        RefreshSockets();
        SwapCommand.NotifyCanExecuteChanged();
    }
    private void RefreshCommands()
    {
        RevertCommand.NotifyCanExecuteChanged();
        LoadSkillsCommand.NotifyCanExecuteChanged();
        LoadKnowledgeCommand.NotifyCanExecuteChanged();
        LoadChallengesCommand.NotifyCanExecuteChanged();
        LoadFishingCommand.NotifyCanExecuteChanged();
        LoadMapCommand.NotifyCanExecuteChanged(); LoadCompanionsCommand.NotifyCanExecuteChanged(); LoadFriendshipCommand.NotifyCanExecuteChanged(); RevealMapCommand.NotifyCanExecuteChanged();
        RefreshBackupCommands();
        OpenCommand.NotifyCanExecuteChanged(); SaveCommand.NotifyCanExecuteChanged(); ReloadCommand.NotifyCanExecuteChanged();
        SwapCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged(); ApplyCommand.NotifyCanExecuteChanged(); DeleteCommand.NotifyCanExecuteChanged();
        EnchantCommand.NotifyCanExecuteChanged();
        SocketsCommand.NotifyCanExecuteChanged();
        UnlockSocketsCommand.NotifyCanExecuteChanged();
        SyncGameDataCommand.NotifyCanExecuteChanged();
    }

    public async Task<bool> ConfirmLeaveAsync()
    {
        if (Busy) return false;
        if (document?.IsDirty != true) return true;
        var reply = ThemedMessageBox.Show(Tr.Text("ui.unsaved_prompt"), Tr.Text("ui.unsaved_title"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question,
            MessageBoxResult.Cancel, new() { [MessageBoxResult.Yes] = "Save", [MessageBoxResult.No] = "Discard", [MessageBoxResult.Cancel] = "Cancel" });
        if (reply == MessageBoxResult.Yes) { await SaveAsync(); return document?.IsDirty != true; }
        return reply == MessageBoxResult.No;
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Reload() { socketTarget = null; Refresh(); ReadBackups(); }

    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task OpenAsync()
    {
        new SlotsWindow(this).ShowDialog();
        await Task.CompletedTask;
    }
    [RelayCommand(CanExecute = nameof(CanRevert))]
    private async Task RevertAsync()
    {
        if (ThemedMessageBox.Show(Tr.Text("ui.revert_prompt"), Tr.Text("ui.revert_changes"), MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel,
            new() { [MessageBoxResult.OK] = "Discard" }) == MessageBoxResult.OK)
            await LoadFileAsync(document!.SourcePath);
    }
    private bool CanRevert() => HasUnsavedChanges;
    public async Task<bool> TryLoadFileAsync(string path) { await LoadFileAsync(path, true); return lastLoadSucceeded; }

    public async Task LoadFileAsync(string path, bool createPristine = false)
    {
        lastLoadSucceeded = false;
        await RunAsync(Tr.Text("Validating the save and reading inventory…"), async () =>
        {
            var loaded = await Task.Run(() => SaveDocument.Open(path));
            document = loaded;
            savedInSession = false; lastLoadSucceeded = true;
            Search = "";
            Storage = StorageFilters[0];
            Refresh(resetSelection: true);
            Status = Tr.Format("port.inventory_loaded", ("count", document.Items.Count.ToString("N0")));
            // Like the original prepare_pages: read every page with the save; one unreadable section must not block the rest.
            var failed = new List<string>();
            T? Page<T>(string name, Func<T> read) where T : class
            {
                try { return read(); }
                catch (Exception error) when (error is not OutOfMemoryException) { failed.Add(name + ": " + error.Message); return null; }
            }
            var pages = await Task.Run(() => (Skills: Page("skills", loaded.ReadSkills), Knowledge: Page("knowledge", loaded.ReadKnowledge),
                Challenges: Page("challenges", loaded.ReadChallenges), Fishing: Page("fishing", loaded.ReadFishing), Map: Page("map", loaded.ReadMap),
                Companions: Page("companions", loaded.ReadCompanions), Friendship: Page("npc", loaded.ReadFriendship)));
            (Skills, SkillBaseline, Knowledge, Challenges, Fishing, Map, Companions, Friendship) =
                (pages.Skills, pages.Skills?.Levels, pages.Knowledge, pages.Challenges, pages.Fishing, pages.Map, pages.Companions, pages.Friendship);
            if (failed.Count > 0) Status += " " + string.Join(" · ", failed);
            if (createPristine)
            {
                try { await Task.Run(() => SaveBackups.Pristine(path)); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Status += Tr.Text(" PRISTINE backup failed: ") + error.Message; }
            }
            ReadBackups();
        });
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (!ulong.TryParse(QuantityText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong quantity) || quantity is 0 or > 999_999_999)
        {
            Status = Tr.Text("Enter a whole quantity from 1 to 999,999,999.");
            return;
        }
        var selectedItems = SelectedItems().Where(i => i.CanEditQuantity).ToArray();
        int skipped = SelectedItems().Length - selectedItems.Length;
        await RunAsync(Tr.Text("Validating changes…"), async () =>
        {
            await Task.Run(() => document!.SetQuantity(selectedItems, quantity));
            Refresh();
            Status = Tr.Format("port.quantity_applied", ("count", selectedItems.Length.ToString("N0")), ("quantity", quantity.ToString("N0")), ("skipped", skipped.ToString("N0")));
        });
    }

    private InventoryItem[] SelectedItems() => selection.Length == 0 ? Selected is null ? [] : [Selected.Item] : selection.Select(row => row.Item).ToArray();

    [RelayCommand(CanExecute = nameof(CanSwap))]
    private async Task SwapAsync()
    {
        var item = SelectedItems()[0];
        var dialog = new ItemSearchWindow() { Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) };
        if (dialog.ShowDialog() != true || dialog.SelectedKey is not uint key) return;
        await RunAsync(Tr.Text("Validating items and linked references…"), async () =>
        {
            await Task.Run(() => document!.SwapVendorItem(item, key));
            Refresh(); Status = Tr.Text("Sold item replaced. Save the file, then repurchase it in the game.");
        });
    }

    private void RefreshSockets()
    {
        if (changingView || !IsEquipment) return;
        var items = SelectedItems();
        var target = document is not null && items.Length == 1 && document.Items.Contains(items[0])
            ? document.SocketTarget(items[0]) : null;
        if (target is not null && ReferenceEquals(target, socketTarget)) return;
        socketTarget = null; SocketRows = []; SocketCounts = [];
        SocketInfo = Tr.Text("Select one item to edit sockets.");
        if (target is not null)
        {
            var state = target.Sockets;
            int limit = SocketState.Limit(target.Key);
            SocketInfo = SocketText(target, !ReferenceEquals(target, items[0]));
            if (state.Error is null && limit > 0 && !state.Slots.Skip(limit).Any(s => s.HasGem))
            {
                SocketRows = Enumerable.Range(0, limit).Select(i => new SocketRow(i,
                    i < state.Slots.Count ? state.Slots[i].Key : 0,
                    i < state.Unlocked && i < state.Slots.Count, i >= state.Unlocked, socketFilters[i])).ToArray();
                for (int i = 0; i < SocketRows.Length; i++) socketFilters[i] = SocketRows[i];
                socketTarget = target;
                SocketCounts = Enumerable.Range(0, Math.Min(limit, state.Capacity) + 1).ToArray();
                UnlockedSockets = state.Unlocked;
                OnPropertyChanged(nameof(UnlockedSockets));
            }
            else if (state.Error is null) SocketInfo += Tr.Text("\nCannot edit: no supported limit or equipped sockets exceed the limit.");
        }
        SocketsCommand.NotifyCanExecuteChanged();
        UnlockSocketsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanUnlockSockets))]
    private async Task UnlockSocketsAsync()
    {
        var target = socketTarget!;
        int count = UnlockedSockets;
        await RunAsync(Tr.Text("Validating unlocked sockets and structure…"), async () =>
        {
            await Task.Run(() => document!.SetUnlockedSockets(target, count));
            Refresh(); Status = Tr.Text("Unlocked socket count changed. Save the file to apply.");
        });
    }

    [RelayCommand(CanExecute = nameof(CanSockets))]
    private async Task SocketsAsync()
    {
        var target = socketTarget!;
        uint[] keys = SocketRows.Where(row => row.IsEditable).Select(row => row.SelectedKey).ToArray();
        await RunAsync(Tr.Text("Validating socket structure and changes…"), async () =>
        {
            await Task.Run(() => document!.SetSockets(target, keys));
            Refresh(); Status = Tr.Text("Socket changes validated. Save the file to apply.");
        });
    }

    [RelayCommand(CanExecute = nameof(CanEnchant))]
    private async Task EnchantAsync()
    {
        var selectedItems = SelectedItems();
        ushort level = EnchantLevel;
        int eligible = selectedItems.Count(i => i.EnchantLevel.HasValue);
        await RunAsync(Tr.Text("Validating enchant limits and changes…"), async () =>
        {
            await Task.Run(() => document!.SetEnchant(selectedItems, level));
            Refresh();
            Status = Tr.Format("port.enchanted", ("count", eligible.ToString("N0")), ("level", level), ("skipped", (selectedItems.Length - eligible).ToString("N0")));
        });
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        var selectedItems = SelectedItems().Where(i => i.CanEditQuantity).ToArray();
        int skipped = SelectedItems().Length - selectedItems.Length;
        string names = string.Join("\n", (selection.Length == 0 ? [Selected!] : selection).Where(r => r.Item.CanEditQuantity).Take(10).Select(row => row.Name));
        if (ThemedMessageBox.Show(Tr.Format("port.delete_prompt", ("count", selectedItems.Length.ToString("N0")), ("skipped", skipped.ToString("N0")), ("names", names)),
            Tr.Text("Delete Items"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await RunAsync(Tr.Text("Validating deletion…"), async () =>
        {
            await Task.Run(() => document!.Delete(selectedItems));
            Refresh(); Status = Tr.Format("port.deleted", ("count", selectedItems.Length.ToString("N0")), ("skipped", skipped.ToString("N0")));
        });
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync() => await RunAsync(Tr.Text("Undoing…"), async () =>
    {
        var changed = await Task.Run(() => document!.Undo());
        if (changed.Contains("KnowledgeSaveData"))
        {
            if (Skills is not null) Skills = await Task.Run(() => document!.ReadSkills());
            if (Knowledge is not null || Fishing is not null || Map is not null)
                UpdateKnowledgeViews(Skills?.Levels ?? await Task.Run(() => document!.ReadKnowledge()));
        }
        if (changed.Contains("QuestSaveData") && (Challenges is not null || Map is not null))
        {
            var progress = await Task.Run(() => document!.ReadChallenges());
            if (Challenges is not null) Challenges = progress;
            if (Map is not null) Map = Map with { Progress = progress };
        }
        if (Fishing is not null && changed.Contains("ContentsMiscSaveData")) Fishing = await Task.Run(() => document!.ReadFishing());
        if (Map is not null && (changed.Contains("FogSaveData") || changed.Contains("DiscoveredLevelGimmickSceneObjectSaveData"))) Map = await Task.Run(() => document!.ReadMap());
        if (Companions is not null && changed.Contains("MercenaryClanSaveData")) Companions = await Task.Run(() => document!.ReadCompanions());
        if (Friendship is not null && changed.Contains("FriendlySaveData")) Friendship = await Task.Run(() => document!.ReadFriendship());
        Refresh(); Status = Tr.Text("Last edit undone.");
    });

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync() => await SavePathAsync(document!.SourcePath);

    public bool CanSyncGameData() => !Busy && gameFolder.Length > 0;
    [RelayCommand(CanExecute = nameof(CanSyncGameData))]
    private async Task SyncGameDataAsync() => await RunAsync(Tr.Text("sync.metadata"), async () =>
    {
        var progress = new Progress<SyncProgress>(p => Status = Tr.Format(p.Stage, ("current", p.Completed.ToString("N0")), ("total", p.Total.ToString("N0"))));
        var result = await Task.Run(() => GameDataSync.Run(gameFolder, CatalogFiles.Resources, CatalogFiles.Cache, progress));
        CatalogFiles.Reload(); RefreshLanguage();
        Status = Tr.Format("sync.result", ("items", result.Items), ("images", result.Images), ("missing", result.Unresolved), ("seconds", result.Seconds.ToString("F1")));
    });

    private async Task RunAsync(string message, Func<Task> action)
    {
        if (Busy) return;
        Busy = true; Status = message;
        var owner = Application.Current?.Dispatcher.CheckAccess() == true
            ? Application.Current.Windows.OfType<MainWindow>().FirstOrDefault(w => w.DataContext == this && w.IsVisible && w.Left > -5000) : null;
        ProgressWindow? progress = null;
        try
        {
            if (owner is not null) { progress = new ProgressWindow(Ui.Owner ?? owner, this); progress.Show(); }
            await action();
        }
        catch (Exception error)
        {
            Status = Tr.Text("Operation failed: ") + error.Message;
            ThemedMessageBox.Show(Status, "SaveUltimate", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { progress?.Finish(); Busy = false; }
    }

    private void Refresh(bool resetSelection = false)
    {
        if (resetSelection) { Skills = null; SkillBaseline = null; Knowledge = null; Challenges = null; Fishing = null; Map = null; Companions = null; Friendship = null; }
        else if (Skills is not null && document is not null) Skills = Skills.WithInventory(document.Items);
        changingView = true;
        if (resetSelection) socketTarget = null;
        if (resetSelection) ViewReset?.Invoke(); else ViewChanging?.Invoke();
        var selectedIdentity = resetSelection ? null : Selected;
        SetSelection([]);
        var catalog = ItemCatalog.Shared;
        var rows = document?.Items.OrderBy(i => i.RecordOffset).Select(i => new ItemRow(i, catalog.Name(i.Key), catalog.Category(i.Key), catalog.InternalName(i.Key))).ToArray() ?? [];
        foreach (var filter in StorageFilters) filter.Count = document?.Items.Count(i => MatchesStorage(i, filter)) ?? 0;
        ItemsView = CollectionViewSource.GetDefaultView(rows);
        ItemsView.Filter = row => row is ItemRow item && MatchesPage(item.Item) &&
            (item.Name.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase) || item.Item.Key.ToString().Contains(Search.Trim()) || item.Category.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase) || item.InternalName.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase) || (IsRepurchase ? "Sold to Vendor" : item.Item.Source == ItemSource.Vendor ? "" : item.OwnerText).Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase));
        Selected = rows.FirstOrDefault(r => r.Identity == selectedIdentity?.Identity && ItemsView.Contains(r));
        FileText = document is null ? "" : document.SourcePath + (document.IsDirty ? Tr.Text(" · Unsaved changes") : "");
        OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(CountText)); RefreshCommands();
        RefreshAppearance();
        ViewChanged?.Invoke();
        changingView = false;
        RefreshSockets();
    }
    public void RefreshAppearance(bool settingsChanged = false)
    {
        if (settingsChanged) { gameFolder = SaveLocations.GameFolder(SettingsStore.Current); SyncGameDataCommand.NotifyCanExecuteChanged(); }
        foreach (string property in new[] { nameof(LoadedPath), nameof(HasDocument), nameof(HasUnsavedChanges), nameof(FileName), nameof(FileState), nameof(FileTone), nameof(SaveFolderText), nameof(GameFolderText), nameof(ItemPageTitle), nameof(ItemHelpTopic) }) OnPropertyChanged(property);
    }
    public void RefreshLanguage()
    {
        foreach (var filter in StorageFilters) filter.RefreshLanguage();
        foreach (var row in SocketRows) row.RefreshLanguage();
        SocketInfo = socketTarget is null ? Tr.Text("Select one item to edit sockets.") : SocketText(socketTarget, !ReferenceEquals(socketTarget, Selected?.Item));
        string? backupPath = SelectedBackup?.Path;
        Backups = Backups.Select(b => b with { }).ToArray();
        SelectedBackup = Backups.FirstOrDefault(b => b.Path == backupPath);
        OnPropertyChanged(nameof(BackupCount));
        Refresh();
        if (!Busy) Status = FileState;
        foreach (string property in new[] { nameof(Knowledge), nameof(Challenges), nameof(Fishing), nameof(Map), nameof(Companions), nameof(Friendship), nameof(Skills) }) OnPropertyChanged(property);
    }
    private static string SocketText(InventoryItem target, bool owned) =>
        (target.Sockets.Error ?? Tr.Format("port.socket_info", ("unlocked", target.Sockets.Unlocked), ("capacity", target.Sockets.Capacity), ("limit", SocketState.Limit(target.Key)))) +
        (owned ? Tr.Text("\nApplies to the matching owned inventory item.") : "");

    private bool MatchesPage(InventoryItem item) => IsInventory ? MatchesStorage(item) : IsRepurchase ? item.Source == ItemSource.Vendor : item.EnchantLevel.HasValue || SocketState.Limit(item.Key) > 0;

    private bool MatchesStorage(InventoryItem item) => MatchesStorage(item, Storage);
    private bool MatchesStorage(InventoryItem item, StorageFilter filter) => filter.Source is not null ? filter.Source == item.Source
        : filter.Other ? item.Source == ItemSource.Vendor || item.Source == ItemSource.Inventory && !StorageFilters.Any(f => f.Bag == item.Bag)
        : filter.Bag is null || item.Source == ItemSource.Inventory && filter.Bag == item.Bag;
}

