using System.IO;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CDSE.Core.Saves;

namespace CDSE.App;

// Anything drawn with a status badge; null means the check has not reported yet.
public abstract partial class StatusRow : ObservableObject
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Glyph))] private CheckStatus? status;
    public string Glyph => Status switch
    {
        CheckStatus.Ok => "status_ok", CheckStatus.Suspect => "status_suspect", CheckStatus.Error => "status_error",
        CheckStatus.NotRun => "status_skip", _ => "status_wait"
    };
}

public sealed partial class CheckRow(string id, string area) : StatusRow
{
    private SaveCheck? result;
    public string Id => id;
    public string Name => Tr.Text("ui.doctor_check_" + id);
    /// <summary>The problem list names the area too, as the overview shows areas only.</summary>
    public string Title => $"{area} · {Name}";
    public string State => Tr.Text(Status switch
    {
        CheckStatus.Ok => "ui.doctor_state_ok", CheckStatus.Suspect => "ui.doctor_state_suspect", CheckStatus.Error => "ui.doctor_state_error",
        CheckStatus.NotRun => "ui.doctor_state_notrun", _ => "ui.doctor_state_waiting"
    });
    public SaveFix Fix => result?.Fix ?? SaveFix.None;
    public bool CanFix => Fix != SaveFix.None;
    public string FixLabel => Tr.Text("ui.doctor_fix_" + Fix.ToString().ToLowerInvariant());
    public bool IsIssue => Status is CheckStatus.Suspect or CheckStatus.Error;
    public bool Unfixable => Status == CheckStatus.Error && !CanFix;
    public string Problem
    {
        get
        {
            // The pages share one explanation. An old game version and a new one differ, as do a signature that can and cannot be replaced.
            string key = id switch
            {
                "skills" or "knowledge" or "challenges" or "fishing" or "map" or "companions" or "friendship" => "page",
                "game_version" when Status == CheckStatus.Error => "game_version_old",
                "integrity" when !CanFix => "integrity_lost",
                _ => id
            };
            return Tr.Format("ui.doctor_problem_" + key, ("count", (result?.Count ?? 0).ToString("N0")));
        }
    }
    // The game version is already in the header; the others name item keys or quote the failing code.
    public string Reason => id switch
    {
        "game_version" => "",
        "enchants" or "sockets" or "unknown_items" or "important_items" when result?.Detail.Length > 0 => Tr.Format("ui.doctor_item_keys", ("keys", result.Detail)),
        _ => Tr.Text(result?.Detail ?? "")
    };
    public bool HasReason => Reason.Length > 0;
    [ObservableProperty] private bool selected;
    public event Action? SelectionChanged;
    partial void OnSelectedChanged(bool value) => SelectionChanged?.Invoke();
    public void Show(SaveCheck? check)
    {
        result = check; Status = check?.Status;
        // A suspect value may be deliberate, so only errors start out selected.
        Selected = check is { Status: CheckStatus.Error, Fix: not SaveFix.None };
        foreach (string name in new[] { nameof(State), nameof(CanFix), nameof(FixLabel), nameof(Problem), nameof(Reason), nameof(HasReason) }) OnPropertyChanged(name);
    }
}

/// <summary>
/// One area of the save on the overview, such as Items: it shows its worst finding, so a check added to the area
/// changes no layout. Each problem is listed on its own above the areas, with its repair.
/// </summary>
public sealed partial class CheckArea : StatusRow
{
    // File and structure are the doctor's own words; the other areas are the editor's menus.
    private static readonly Dictionary<string, string> captions = new()
    {
        ["file"] = "ui.doctor_group_file", ["structure"] = "ui.doctor_group_structure", ["items"] = "tab.items",
        ["journal"] = "tab.knowledge_quest", ["map"] = "tab.maps", ["npc"] = "tab.mercenary", ["other"] = "tab.misc"
    };
    public CheckArea(string id, string[] ids)
    {
        Name = Tr.Text(captions[id]);
        Checks = ids.Select(c => new CheckRow(c, Name)).ToArray();
        foreach (var check in Checks) check.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Status)) Update(); };
    }
    public string Name { get; }
    public CheckRow[] Checks { get; }
    public string State => Tr.Text(Status switch
    {
        CheckStatus.Ok => "ui.doctor_state_ok", CheckStatus.Suspect => "ui.doctor_state_suspect", CheckStatus.Error => "ui.doctor_state_error",
        CheckStatus.NotRun => "ui.doctor_state_notrun", _ => "ui.doctor_state_waiting"
    });
    public string Details => string.Join("\n", Checks.Select(c => $"{c.Name}: {c.State}"));
    private void Update()
    {
        var states = Checks.Select(c => c.Status).ToArray();
        Status = states.Contains(CheckStatus.Error) ? CheckStatus.Error : states.Contains(CheckStatus.Suspect) ? CheckStatus.Suspect
            : states.Contains(null) ? null : states.Contains(CheckStatus.NotRun) ? CheckStatus.NotRun : CheckStatus.Ok;
        OnPropertyChanged(nameof(State)); OnPropertyChanged(nameof(Details));
    }
}
public sealed class CountRow : StatusRow
{
    public CountRow(CheckStatus status, int count) { Status = status; Count = count; }
    public int Count { get; }
    public string Label => Tr.Text("ui.doctor_state_" + Status.ToString()!.ToLowerInvariant());
}

public sealed class SlotRow(SaveSlot slot) : StatusRow
{
    public SaveSlot Slot => slot;
    public string Path => slot.Path;
    public string Name => slot.Name;
    public string SavedAt => slot.SavedAt;
    public string Group => slot.Group;
    public bool Inspected { get; set; }
}

public sealed partial class DoctorView : StatusRow
{
    public CheckArea[] Areas { get; } = SaveCheckup.Plan.Select(a => new CheckArea(a.Area, a.Ids)).ToArray();
    [ObservableProperty] private string scanText = "";
    [ObservableProperty] private string verdict = "";
    [ObservableProperty] private string subtitle = "";
    [ObservableProperty] private string filePath = "";
    [ObservableProperty] private CountRow[] counts = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasIssues))] private CheckRow[] issues = [];
    [ObservableProperty] private bool canRepair;
    [ObservableProperty] private bool hasFixes;
    [ObservableProperty] private bool canRestore;
    public bool HasIssues => Issues.Length > 0;
}

// Checks one save at a time while the list keeps every slot in view: a damaged slot stops the game even when the player never selects it.
// A page of the Misc section; every time it is shown it scans the slots again, since the game or this app may have saved since.
public sealed partial class DoctorPage : UserControl
{
    private MainViewModel model => (MainViewModel)DataContext;
    private readonly List<SlotRow> rows = [];
    private Dictionary<string, CheckRow> checks = [];
    private string? backup;
    private int inspection, scan;
    private bool scanning;
    private Task scanned = Task.CompletedTask;
    public DoctorView View { get; private set; } = null!;
    public IReadOnlyDictionary<string, CheckRow> Checks => checks;

    public DoctorPage()
    {
        InitializeComponent();
        Build();
        // Queued on the dispatcher so the scan and its continuations stay on the UI thread.
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) Dispatcher.InvokeAsync(Scan); };
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old) old.LanguageChanged -= OnLanguageChanged;
            if (e.NewValue is MainViewModel now) now.LanguageChanged += OnLanguageChanged;
        };
    }

    // The check names and group titles are read once, so a new language builds the view again and shows the slot anew.
    private void Build()
    {
        View = new();
        checks = View.Areas.SelectMany(a => a.Checks).ToDictionary(c => c.Id);
        foreach (var check in checks.Values) check.SelectionChanged += () => View.CanRepair = checks.Values.Any(c => c.CanFix && c.Selected);
        Root.DataContext = View;
    }
    private void OnLanguageChanged()
    {
        var selected = Slots.SelectedItem;
        Build();
        if (rows.Count == 0) return;
        ShowSlots();
        Slots.SelectedItem = selected;
        if (!scanning) UpdateSummary();
    }

    /// <summary>Scans every slot; while a scan runs, showing the page or pressing the button waits for it instead of starting over.</summary>
    public Task Scan() => scanned.IsCompleted ? scanned = ScanSlots() : scanned;

    private async Task ScanSlots()
    {
        int run = ++scan;
        rows.Clear();
        rows.AddRange(SaveLocations.Find(SettingsStore.Current).Where(s => File.Exists(s.Path)).Select(s => new SlotRow(s)));
        ShowSlots();
        Slots.SelectedItem = rows.FirstOrDefault(r => string.Equals(r.Path, model.LoadedPath, StringComparison.OrdinalIgnoreCase)) ?? rows.FirstOrDefault();
        scanning = true;
        foreach (var (slot, done) in rows.Select((r, i) => (r, i + 1)).ToArray())
        {
            View.ScanText = Tr.Format("ui.doctor_scanning", ("done", done.ToString("N0")), ("total", rows.Count.ToString("N0")));
            // Selecting a slot already checks it, so wait for that result instead of checking the same file twice.
            if (slot == Slots.SelectedItem) { await inspected; if (run != scan) return; }
            if (!slot.Inspected)
            {
                var found = await Task.Run(() => SaveCheckup.Run(slot.Path, Ui.GameVersion));
                if (run != scan) return;
                if (!slot.Inspected) slot.Status = Worst(found);
            }
        }
        scanning = false;
        UpdateSummary();
        await inspected;
    }

    private void ShowSlots() => Slots.ItemsSource = Ui.SlotGroups(rows);

    private void UpdateSummary() => View.ScanText = Tr.Format("ui.doctor_summary", ("total", rows.Count.ToString("N0")),
        ("count", rows.Count(r => r.Status is CheckStatus.Error or CheckStatus.Suspect).ToString("N0")));

    private static CheckStatus Worst(IEnumerable<SaveCheck> found) => found.Any(c => c.Status == CheckStatus.Error) ? CheckStatus.Error
        : found.Any(c => c.Status == CheckStatus.Suspect) ? CheckStatus.Suspect : CheckStatus.Ok;

    private Task inspected = Task.CompletedTask;
    private async void OnSlotSelected(object sender, SelectionChangedEventArgs e) => await (inspected = Inspect(Slots.SelectedItem as SlotRow));

    public async Task Inspect(SlotRow? slot)
    {
        int run = ++inspection;
        foreach (var check in checks.Values) check.Show(null);
        View.Issues = []; View.Counts = []; View.CanRepair = false; View.HasFixes = false; View.CanRestore = false; backup = null; View.Status = null;
        View.Verdict = Tr.Text(slot is null ? "ui.no_slots" : "ui.doctor_running");
        View.Subtitle = slot is null ? "" : $"{slot.Name} · {slot.SavedAt}"; View.FilePath = slot?.Path ?? "";
        if (slot is null) return;
        var found = await Task.Run(() => SaveCheckup.Run(slot.Path, Ui.GameVersion, report: c => Dispatcher.BeginInvoke(() => { if (run == inspection) checks[c.Id].Show(c); })));
        if (run != inspection) return;
        foreach (var check in found) checks[check.Id].Show(check);
        slot.Inspected = true; slot.Status = Worst(found);
        View.Status = slot.Status;
        int errors = found.Count(c => c.Status == CheckStatus.Error), suspects = found.Count(c => c.Status == CheckStatus.Suspect);
        View.Verdict = errors > 0 ? Tr.Format("ui.doctor_verdict_error", ("count", errors.ToString("N0")))
            : suspects > 0 ? Tr.Format("ui.doctor_verdict_suspect", ("count", suspects.ToString("N0"))) : Tr.Text("ui.doctor_verdict_ok");
        if (found.FirstOrDefault(c => c.Id == "game_version") is { Detail.Length: > 0 } version) View.Subtitle += " · " + Tr.Format("ui.doctor_saved_by", ("version", version.Detail));
        // The counts are of the area cards below; the verdict counts the problems listed.
        View.Counts = new[] { CheckStatus.Ok, CheckStatus.Suspect, CheckStatus.Error, CheckStatus.NotRun }
            .Select(status => new CountRow(status, View.Areas.Count(a => a.Status == status))).Where(c => c.Count > 0 || c.Status == CheckStatus.Ok).ToArray();
        View.Issues = checks.Values.Where(c => c.IsIssue).OrderByDescending(c => c.Status).ToArray();
        View.HasFixes = checks.Values.Any(c => c.CanFix);
        View.CanRepair = checks.Values.Any(c => c.CanFix && c.Selected);
        if (!scanning) UpdateSummary();
        if (checks.Values.Any(c => c.Unfixable))
        {
            string? good = await Task.Run(() => SaveDocument.LatestGoodBackup(slot.Path));
            if (run != inspection) return;
            backup = good; View.CanRestore = good is not null;
        }
    }

    private async void OnBrowse(object sender, RoutedEventArgs e)
    {
        if (Ui.PickSave(Owner!) is not { } file) return;
        var row = rows.FirstOrDefault(r => string.Equals(r.Path, file, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            row = new SlotRow(new SaveSlot(Path.GetFullPath(file), "manual", "Custom", "", Path.GetFileNameWithoutExtension(file), File.GetLastWriteTime(file)));
            rows.Add(row); ShowSlots();
        }
        Slots.SelectedItem = row;
        await inspected;
    }

    private async void OnRescan(object sender, RoutedEventArgs e) => await Scan();
    private Window? Owner => Window.GetWindow(this);

    // Both actions replace a save on disk, so they share the confirmation and the reload of a save that is open. They run
    // while the game is running too, as the editor's own saves do; each keeps a backup of the save it replaces.
    private async Task<bool> Change(SlotRow slot, string confirm, Action action)
    {
        if (ThemedMessageBox.Show(Owner, confirm, Tr.Text("ui.doctor"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return false;
        bool loaded = string.Equals(slot.Path, model.LoadedPath, StringComparison.OrdinalIgnoreCase);
        if (loaded && !await model.ConfirmLeaveAsync()) return false;
        IsEnabled = false;
        try
        {
            await Task.Run(action);
            if (loaded) await model.LoadFileAsync(slot.Path);
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            ThemedMessageBox.Show(Owner, ErrorCodes.Describe(error), Tr.Text("ui.doctor"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally { IsEnabled = true; await (inspected = Inspect(slot)); }
    }

    private async void OnRepair(object sender, RoutedEventArgs e) => await RepairSelected();

    public async Task RepairSelected()
    {
        if (Slots.SelectedItem is not SlotRow slot) return;
        var chosen = checks.Values.Where(c => c.CanFix && c.Selected).ToArray();
        if (chosen.Length == 0) return;
        var fixes = chosen.Select(c => c.Fix).ToHashSet();
        string list = string.Join("\n", chosen.Select(c => "• " + c.FixLabel));
        if (await Change(slot, Tr.Format("ui.doctor_confirm", ("list", list)), () => SaveDocument.Repair(slot.Path, AppContext.BaseDirectory, fixes)))
            ThemedMessageBox.Show(Owner, Tr.Text("ui.doctor_done"), Tr.Text("ui.doctor"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (Slots.SelectedItem is not SlotRow slot || backup is not { } chosen) return;
        string date = File.GetLastWriteTime(chosen).ToString("yyyy-MM-dd HH:mm");
        if (await Change(slot, Tr.Format("ui.doctor_restore_prompt", ("date", date)), () => SaveDocument.RestoreBackup(slot.Path, chosen, AppContext.BaseDirectory)))
            ThemedMessageBox.Show(Owner, Tr.Text("ui.doctor_restore_done"), Tr.Text("ui.doctor"), MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
