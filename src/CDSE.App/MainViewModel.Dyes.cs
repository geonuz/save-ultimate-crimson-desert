using System.IO;
using CDSE.Core.Saves;

namespace CDSE.App;

public partial class MainViewModel
{
    /// <summary>Every owned item that takes dye. The page asks while it is shown; this reads records the item list does not keep.</summary>
    public DyedItem[] ReadDyes()
    {
        try { return document?.ReadDyes() ?? []; }
        catch (InvalidDataException error) { Status = ErrorCodes.Describe(error); return []; }
    }
    public bool CanDye => document?.CanDye == true;
    public DyeTarget[] PlanDyes(IReadOnlyList<DyeEntry> entries, bool useOwned) => document?.PlanDyes(entries, useOwned) ?? [];
    public InventoryItem[] DyeCandidates() => document?.DyeCandidates() ?? [];
    public (decimal Used, int Total) InventorySpace() => document?.BagSpace() ?? default;

    /// <summary>Writes a dye file; unlike an edit it changes nothing in the save.</summary>
    public void ExportDyes(string path, DyeEntry[] entries)
    {
        try { DyeFiles.Write(path, entries); Status = Tr.Format("dye.exported", ("count", entries.Length.ToString("N0")), ("file", Path.GetFileName(path))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Status = Tr.Text("Operation failed: ") + ErrorCodes.Describe(error);
            ThemedMessageBox.Show(Status, Tr.Text("dye.title"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    /// <summary>Puts the dye code of the entries on the clipboard, for pasting in a message.</summary>
    public void CopyDyeCode(DyeEntry[] entries)
    {
        if (entries.Length == 0) return;
        string code = DyeFiles.ToCode(entries);
        try
        {
            System.Windows.Clipboard.SetText(code);
            Status = Tr.Format("dye.code_copied", ("count", entries.Length.ToString("N0")), ("length", code.Length.ToString("N0")));
        }
        catch (System.Runtime.InteropServices.COMException error)
        {
            // Another program may hold the clipboard.
            Status = Tr.Text("Operation failed: ") + error.Message;
            ThemedMessageBox.Show(Status, Tr.Text("dye.title"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    /// <summary>Reads a dye code; null with the reason shown when the text holds none.</summary>
    public static DyeEntry[]? ReadDyeCode(string text, out string problem)
    {
        problem = "";
        if (string.IsNullOrWhiteSpace(text)) { problem = Tr.Text("dye.no_code"); return null; }
        try { return DyeFiles.FromCode(text); }
        catch (InvalidDataException error)
        {
            problem = error.Message == "Not a dye code." ? Tr.Text("dye.no_code") : Tr.Text("dye.bad_code");
            return null;
        }
    }

    /// <summary>Reads a dye file; null with the reason shown when it cannot be read.</summary>
    public static DyeEntry[]? ReadDyeFile(string path, out string problem)
    {
        problem = "";
        try { return DyeFiles.Read(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            problem = Tr.Format("dye.unreadable", ("reason", error is InvalidDataException ? Tr.Text(error.Message) : ErrorCodes.Describe(error)));
            return null;
        }
    }

    public async Task ApplyDyesAsync(DyeTarget[] targets)
    {
        if (!CanSave() || targets.Length == 0) return;
        uint[] keys = targets.Select(t => t.Entry.Item).ToArray();
        int created = targets.Count(t => t.Item is null);
        await EditAsync(Tr.Text("dye.import"), () => Caption("dye.import"), () => ItemNames(keys), async () =>
        {
            await Task.Run(() => document!.ApplyDyes(targets));
            Refresh(); Status = Tr.Format("dye.imported", ("count", (targets.Length - created).ToString("N0")), ("added", created.ToString("N0")));
        });
    }
}
