using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.App;

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

public sealed partial class CheckRow(string id) : StatusRow
{
    private SaveCheck? result;
    public string Id => id;
    public string Name => Tr.Text("ui.doctor_check_" + id);
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
        "enchants" or "sockets" or "unknown_items" when result?.Detail.Length > 0 => Tr.Format("ui.doctor_item_keys", ("keys", result.Detail)),
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

public sealed record CheckGroup(string Name, CheckRow[] Checks);
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
    public CheckGroup[] Groups { get; } = SaveCheckup.Plan
        .Select(g => new CheckGroup(Tr.Text("ui.doctor_group_" + g.Group), g.Ids.Select(id => new CheckRow(id)).ToArray())).ToArray();
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
public sealed partial class DoctorWindow : Window
{
    private readonly MainViewModel model;
    private readonly List<SlotRow> rows = [];
    private readonly Dictionary<string, CheckRow> checks;
    private string? backup;
    private int inspection, scan;
    private bool closed, scanning;
    public DoctorView View { get; } = new();
    public IReadOnlyDictionary<string, CheckRow> Checks => checks;

    public DoctorWindow(MainViewModel model)
    {
        this.model = model;
        InitializeComponent();
        Ui.Setup(this, "ui.doctor", 1180, 760);
        MinWidth = 1000; MinHeight = 560;
        checks = View.Groups.SelectMany(g => g.Checks).ToDictionary(c => c.Id);
        foreach (var check in checks.Values) check.SelectionChanged += () => View.CanRepair = checks.Values.Any(c => c.CanFix && c.Selected);
        DataContext = View;
        Loaded += async (_, _) => await Scan();
        Closed += (_, _) => closed = true;
    }

    public async Task Scan()
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
            if (!slot.Inspected)
            {
                var found = await Task.Run(() => SaveCheckup.Run(slot.Path, Ui.GameVersion));
                if (closed || run != scan) return;
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
        if (closed || run != inspection) return;
        foreach (var check in found) checks[check.Id].Show(check);
        slot.Inspected = true; slot.Status = Worst(found);
        View.Status = slot.Status;
        int errors = found.Count(c => c.Status == CheckStatus.Error), suspects = found.Count(c => c.Status == CheckStatus.Suspect);
        View.Verdict = errors > 0 ? Tr.Format("ui.doctor_verdict_error", ("count", errors.ToString("N0")))
            : suspects > 0 ? Tr.Format("ui.doctor_verdict_suspect", ("count", suspects.ToString("N0"))) : Tr.Text("ui.doctor_verdict_ok");
        if (found.FirstOrDefault(c => c.Id == "game_version") is { Detail.Length: > 0 } version) View.Subtitle += " · " + Tr.Format("ui.doctor_saved_by", ("version", version.Detail));
        View.Counts = new[] { CheckStatus.Ok, CheckStatus.Suspect, CheckStatus.Error, CheckStatus.NotRun }
            .Select(status => new CountRow(status, found.Count(c => c.Status == status))).Where(c => c.Count > 0 || c.Status == CheckStatus.Ok).ToArray();
        View.Issues = checks.Values.Where(c => c.IsIssue).OrderByDescending(c => c.Status).ToArray();
        View.HasFixes = checks.Values.Any(c => c.CanFix);
        View.CanRepair = checks.Values.Any(c => c.CanFix && c.Selected);
        if (!scanning) UpdateSummary();
        if (checks.Values.Any(c => c.Unfixable))
        {
            string? good = await Task.Run(() => SaveDocument.LatestGoodBackup(slot.Path));
            if (closed || run != inspection) return;
            backup = good; View.CanRestore = good is not null;
        }
    }

    private async void OnBrowse(object sender, RoutedEventArgs e)
    {
        if (Ui.PickSave(this) is not { } file) return;
        var row = rows.FirstOrDefault(r => string.Equals(r.Path, file, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            row = new SlotRow(new SaveSlot(Path.GetFullPath(file), "manual", "Custom", "", Path.GetFileNameWithoutExtension(file), File.GetLastWriteTime(file)));
            rows.Add(row); ShowSlots();
        }
        Slots.SelectedItem = row;
        await inspected;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // Both actions replace a save on disk, so they share the same refusals and the reload of a save that is open.
    private async Task<bool> Change(SlotRow slot, string confirm, Action action)
    {
        if (Process.GetProcessesByName("CrimsonDesert").Length != 0)
        {
            ThemedMessageBox.Show(this, Tr.Text("ui.doctor_game_running"), Tr.Text("ui.doctor"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (ThemedMessageBox.Show(this, confirm, Tr.Text("ui.doctor"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return false;
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
            ThemedMessageBox.Show(this, ErrorCodes.Describe(error), Tr.Text("ui.doctor"), MessageBoxButton.OK, MessageBoxImage.Warning);
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
            ThemedMessageBox.Show(this, Tr.Text("ui.doctor_done"), Tr.Text("ui.doctor"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (Slots.SelectedItem is not SlotRow slot || backup is not { } chosen) return;
        string date = File.GetLastWriteTime(chosen).ToString("yyyy-MM-dd HH:mm");
        if (await Change(slot, Tr.Format("ui.doctor_restore_prompt", ("date", date)), () => SaveDocument.RestoreBackup(slot.Path, chosen, AppContext.BaseDirectory)))
            ThemedMessageBox.Show(this, Tr.Text("ui.doctor_restore_done"), Tr.Text("ui.doctor"), MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
