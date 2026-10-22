using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.App;

public sealed record BackupRow(string Path, DateTime Modified, long Size)
{
    public string Label => $"{System.IO.Path.GetFileName(Path)}  |  {Modified:yyyy-MM-dd HH:mm:ss}  |  {Size:N0} bytes" +
        (Path.EndsWith(".PRISTINE.bak", StringComparison.Ordinal) ? Tr.Text("  [Reference]") : "");
}

public partial class MainViewModel
{
    public bool IsBackups => PageIndex == 3;
    public bool IsItems => PageIndex < 3 || IsDatabase;
    [ObservableProperty] private BackupRow[] backups = [];
    [ObservableProperty] private BackupRow? selectedBackup;
    public string BackupCount => Tr.Count(Backups.Length, Backups.Length);
    partial void OnBackupsChanged(BackupRow[] value) => OnPropertyChanged(nameof(BackupCount));
    partial void OnSelectedBackupChanged(BackupRow? value) => RefreshBackupCommands();
    private bool CanUseBackup() => CanSave() && SelectedBackup is not null;
    private void RefreshBackupCommands()
    {
        SaveAsCommand.NotifyCanExecuteChanged(); RefreshBackupsCommand.NotifyCanExecuteChanged();
        RestoreBackupCommand.NotifyCanExecuteChanged(); DeleteBackupCommand.NotifyCanExecuteChanged();
        LocalBackupCommand.NotifyCanExecuteChanged(); PristineCommand.NotifyCanExecuteChanged(); OpenBackupFolderCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task RefreshBackupsAsync() => await RunAsync(Tr.Text("Reading backups…"), () =>
    {
        ReadBackups(); Status = BackupCount; return Task.CompletedTask;
    });

    private void ReadBackups()
    {
        string? selected = SelectedBackup?.Path;
        Backups = document is null ? [] : SaveBackups.List(document.SourcePath).Select(p => new FileInfo(p))
            .Select(f => new BackupRow(f.FullName, f.LastWriteTime, f.Length)).ToArray();
        SelectedBackup = Backups.FirstOrDefault(b => b.Path == selected);
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsAsync()
    {
        var dialog = new SaveFileDialog { Filter = "Crimson Desert SAVE (*.save)|*.save", DefaultExt = ".save", AddExtension = true,
            FileName = Path.GetFileNameWithoutExtension(document!.SourcePath) + "_edited.save", OverwritePrompt = true };
        if (dialog.ShowDialog() == true) await SavePathAsync(dialog.FileName);
    }

    private async Task SavePathAsync(string path)
    {
        await RunAsync(Tr.Text("Preparing to save…"), async () =>
        {
            byte[]? expected = File.Exists(path) ? SaveBackups.Hash(path) : null;
            var reply = ThemedMessageBox.Show(Tr.Text("Back up the current file before saving changes?\nYou can restore it from Backup/Restore."),
                Tr.Text("Save Backup"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (reply == MessageBoxResult.Cancel) { Status = Tr.Text("Save canceled."); return; }
            string? recovery = await Task.Run(() => document!.SaveTo(path, reply == MessageBoxResult.Yes, expected));
            string warning = "";
            if (recovery is not null)
            {
                try { await Task.Run(() => SaveBackups.Mirror(path, recovery, AppContext.BaseDirectory)); }
                catch (IOException error) { warning = Tr.Text(" Secondary backup failed: ") + error.Message; }
                catch (UnauthorizedAccessException error) { warning = Tr.Text(" Secondary backup failed: ") + error.Message; }
            }
            SkillBaseline = Skills?.Levels;
            savedInSession = true;
            OnPropertyChanged(nameof(SkillBaseline));
            Refresh(); ReadBackups();
            Status = Tr.Format("port.saved", ("path", document!.SourcePath)) + warning;
        });
    }

    [RelayCommand(CanExecute = nameof(CanUseBackup))]
    private async Task RestoreBackupAsync()
    {
        string path = document!.SourcePath, backup = SelectedBackup!.Path;
        if (ThemedMessageBox.Show(Tr.Format("port.restore_prompt", ("backup", backup), ("path", path)),
            Tr.Text("Restore Backup"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes || !await ConfirmLeaveAsync()) return;
        await RunAsync(Tr.Text("Validating and restoring backup…"), async () =>
        {
            byte[] expected = SaveBackups.Hash(path);
            document = await Task.Run(() =>
            {
                _ = SaveDocument.Open(backup);
                SaveBackups.Restore(path, backup, expected);
                return SaveDocument.Open(path);
            });
            Refresh(resetSelection: true); ReadBackups(); Status = Tr.Text("Backup restored and save reloaded.");
        });
    }

    [RelayCommand(CanExecute = nameof(CanUseBackup))]
    private async Task DeleteBackupAsync()
    {
        string path = SelectedBackup!.Path;
        if (ThemedMessageBox.Show(Tr.Format("port.delete_backup", ("name", Path.GetFileName(path))), Tr.Text("Delete Backup"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await RunAsync(Tr.Text("Deleting backup…"), () => { File.Delete(path); ReadBackups(); Status = Tr.Text("Selected backup deleted."); return Task.CompletedTask; });
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task LocalBackupAsync() => await RunAsync(Tr.Text("Backing up to the local folder…"), async () =>
    {
        string path = document!.SourcePath;
        string? backup = await Task.Run(() => SaveBackups.Create(path, SaveBackups.MirrorDirectory(path, AppContext.BaseDirectory)));
        Status = backup is null ? Tr.Text("Save file not found on disk.") : Tr.Text("Backup location: ") + backup;
    });

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task PristineAsync()
    {
        if (ThemedMessageBox.Show(Tr.Text("Use the save currently on disk as the PRISTINE reference?\nThis replaces the existing PRISTINE and excludes unsaved edits."),
            Tr.Text("Set PRISTINE Reference"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await RunAsync(Tr.Text("Validating reference save…"), async () =>
        {
            await Task.Run(() => SaveBackups.Pristine(document!.SourcePath, replace: true));
            ReadBackups(); Status = Tr.Text("PRISTINE reference updated.");
        });
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task OpenBackupFolderAsync() => await RunAsync(Tr.Text("Opening backup folder…"), () =>
    {
        string path = SaveBackups.DirectoryFor(document!.SourcePath);
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        Status = path; return Task.CompletedTask;
    });
}
