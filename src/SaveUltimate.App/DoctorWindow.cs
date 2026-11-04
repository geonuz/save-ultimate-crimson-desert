using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.App;

public sealed record DoctorRow(SaveSlot Slot, int Damaged, string Status)
{
    public string Name => Slot.Name;
    public string Folder => Slot.Folder;
    public string SavedAt => Slot.SavedAt;
}

// Experimental: finds and repairs saves that released versions of this editor damaged.
public sealed class DoctorWindow : Window
{
    private readonly MainViewModel model;
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 12) };
    public DataGrid Rows { get; } = new() { IsReadOnly = true, AutoGenerateColumns = false, SelectionMode = DataGridSelectionMode.Single };
    public Button RepairButton { get; }
    public DoctorWindow(MainViewModel model)
    {
        this.model = model; Ui.Setup(this, "ui.doctor", 1040, 600);
        var root = new DockPanel { Margin = new(20) }; Content = root;
        var intro = Ui.Text("ui.doctor_intro"); intro.Margin = new(0, 0, 0, 12); DockPanel.SetDock(intro, Dock.Top); root.Children.Add(intro);
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); footer.Children.Add(summary);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; footer.Children.Add(buttons);
        buttons.Children.Add(Ui.Button("Refresh", async (_, _) => await Scan()));
        var close = Ui.Button("Close", (_, _) => Close()); close.IsCancel = true; buttons.Children.Add(close);
        RepairButton = Ui.Button("ui.doctor_repair", async (_, _) => await Repair(), "AccentButton"); RepairButton.IsEnabled = false; buttons.Children.Add(RepairButton);
        foreach (var (key, property, width) in new[] { ("ui.slot", "Name", 140d), ("ui.path", "Folder", -1d), ("ui.saved_at", "SavedAt", 160d), ("ui.doctor_status", "Status", 220d) })
            Rows.Columns.Add(new DataGridTextColumn { Header = Tr.Text(key), Binding = new Binding(property), Width = width < 0 ? new(1, DataGridLengthUnitType.Star) : new(width) });
        root.Children.Add(Rows);
        Loaded += async (_, _) => await Scan();
    }

    private DoctorRow[] Current => Rows.ItemsSource as DoctorRow[] ?? [];
    public async Task Scan()
    {
        IsEnabled = false; summary.Text = Tr.Text("ui.doctor_scanning");
        try
        {
            var slots = SaveLocations.Find(SettingsStore.Current).Where(s => File.Exists(s.Path)).ToArray();
            Rows.ItemsSource = await Task.Run(() => slots.Select(slot =>
            {
                try
                {
                    int damaged = SaveDocument.Diagnose(slot.Path).DamagedRows;
                    return new DoctorRow(slot, damaged, damaged == 0 ? Tr.Text("ui.doctor_ok") : Tr.Format("ui.doctor_damaged", ("count", damaged.ToString("N0"))));
                }
                catch (Exception error) when (error is not OutOfMemoryException) { return new DoctorRow(slot, 0, ErrorCodes.Describe(error)); }
            }).ToArray());
            int count = Current.Count(r => r.Damaged > 0);
            RepairButton.IsEnabled = count > 0;
            summary.Text = Tr.Format("ui.doctor_summary", ("total", Current.Length.ToString("N0")), ("count", count.ToString("N0")));
        }
        finally { IsEnabled = true; }
    }

    private async Task Repair()
    {
        var targets = Current.Where(r => r.Damaged > 0).ToArray();
        if (targets.Length == 0) return;
        if (Process.GetProcessesByName("CrimsonDesert").Length != 0)
        {
            ThemedMessageBox.Show(this, Tr.Text("ui.doctor_game_running"), Tr.Text("ui.doctor"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (ThemedMessageBox.Show(this, Tr.Format("ui.doctor_confirm", ("count", targets.Length.ToString("N0"))), Tr.Text("ui.doctor"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        string? loaded = targets.Select(r => r.Slot.Path).FirstOrDefault(p => string.Equals(p, model.LoadedPath, StringComparison.OrdinalIgnoreCase));
        if (loaded is not null && !await model.ConfirmLeaveAsync()) return;
        IsEnabled = false;
        var failed = new List<string>();
        try
        {
            foreach (var target in targets)
                try { await Task.Run(() => SaveDocument.Repair(target.Slot.Path, AppContext.BaseDirectory)); }
                catch (Exception error) when (error is not OutOfMemoryException) { failed.Add($"{target.Name}: {ErrorCodes.Describe(error)}"); }
            if (loaded is not null) await model.LoadFileAsync(loaded);
        }
        finally { IsEnabled = true; }
        await Scan();
        ThemedMessageBox.Show(this, failed.Count == 0 ? Tr.Text("ui.doctor_done") : string.Join("\n", failed), Tr.Text("ui.doctor"),
            MessageBoxButton.OK, failed.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }
}
