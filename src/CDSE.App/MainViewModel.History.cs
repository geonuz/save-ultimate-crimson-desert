using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using CDSE.Core.Saves;

namespace CDSE.App;

/// <summary>What an edit did and to what, worded in the current language each time it is shown.</summary>
public sealed record EditNote(AppPage Page, Func<string> Action, Func<string>? Target)
{
    public string PageName => Tr.Text(AppPages.All.First(p => p.Page == Page).Title);
}

/// <summary>One state of the save the player can go to. Position 0 is the save before every edit still kept.</summary>
public sealed record HistoryRow(int Position, string Action, string Detail, string Time, bool Current, bool Undone, bool Saved)
{
    public string Badge => string.Join(" · ", new[] { Current ? Tr.Text("history.current") : "", Saved ? Tr.Text("history.saved") : "" }.Where(s => s.Length > 0));
    public string Label => string.Join(", ", new[] { Action, Detail, Badge }.Where(s => s.Length > 0));
}

public partial class MainViewModel
{
    /// <summary>Every state still kept, newest first.</summary>
    public HistoryRow[] HistoryRows
    {
        get
        {
            if (document is null) return [];
            HistoryRow Row(int position, string action, string detail, string time) =>
                new(position, action, detail, time, position == document.Position, position > document.Position, position == document.SavedPosition);
            return document.History.Select((entry, i) => Row(i + 1, ActionName(entry), entry.Note is EditNote note ? Join(note.PageName, note.Target?.Invoke() ?? "") : "",
                entry.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture)))
                .Prepend(Row(0, Tr.Text(document.HistoryTrimmed ? "history.oldest" : "history.opened"), "", "")).Reverse().ToArray();
        }
    }
    public string UndoHint => "Ctrl+Z" + (document?.CanUndo == true ? " · " + ActionName(document.History[document.Position - 1]) : "");
    public string RedoHint => "Ctrl+Y" + (document?.CanRedo == true ? " · " + ActionName(document.History[document.Position]) : "");
    private static string ActionName(HistoryEntry entry) => entry.Note is EditNote note ? note.Action() : Tr.Text("history.edit");
    private static string Join(params string[] parts) => string.Join(" · ", parts.Where(p => p.Length > 0));
    private void RefreshHistory()
    {
        foreach (string property in new[] { nameof(HistoryRows), nameof(UndoHint), nameof(RedoHint) }) OnPropertyChanged(property);
    }

    // Runs an edit and notes what it did on the history entry it made, for the list, Undo and Redo.
    private Task EditAsync(string message, Func<string> action, Func<string>? target, Func<Task> edit) => RunAsync(message, async () =>
    {
        var page = CurrentPage;
        // An edit stands once recorded, even if reading a page afterwards fails.
        try { await edit(); }
        finally
        {
            foreach (var entry in document!.History.Where(e => e.Note is null)) entry.Note = new EditNote(page, action, target);
            RefreshHistory();
        }
    });
    private Task EditAsync(string key, Func<string>? target, Func<Task> edit) => EditAsync(Tr.Text(key), () => Tr.Text(key), target, edit);
    // A button that opens a window ends in an ellipsis; the edit itself does not.
    private static string Caption(string key) => Tr.Text(key).TrimEnd('…', '.');
    private static string Some(IEnumerable<string> names, int count) => names.FirstOrDefault() is { } first ? count > 1 ? $"{first} +{count - 1:N0}" : first : "";
    private static string ItemNames(IReadOnlyList<uint> keys) => Some(keys.Take(1).Select(ItemCatalog.Shared.Name), keys.Count);
    private static string KnowledgeNames(IReadOnlyList<uint> keys) => Some(keys.Take(1).Select(ItemCatalog.Shared.KnowledgeName), keys.Count);

    private bool CanRedo() => !Busy && document?.CanRedo == true;
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private Task UndoAsync() => MoveAsync(() => document!.Undo(), "Last edit undone.");
    [RelayCommand(CanExecute = nameof(CanRedo))]
    private Task RedoAsync() => MoveAsync(() => document!.Redo(), "history.redone");
    /// <summary>Goes to a state of the history list. States passed on the way stay in the list until the next edit.</summary>
    public async Task GoToHistoryAsync(int position)
    {
        if (!CanSave() || position == document!.Position) return;
        await MoveAsync(() => document!.MoveTo(position), "history.moved");
    }

    private Task MoveAsync(Func<string[]> move, string done) => RunAsync(Tr.Text("history.moving"), async () =>
    {
        var changed = await Task.Run(move);
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
        // A trust edit that only synchronizes field NPC copies leaves FriendlySaveData as it was.
        if (Friendship is not null && (changed.Contains("FriendlySaveData") || changed.Contains("FieldNPCSaveData"))) Friendship = await Task.Run(() => document!.ReadFriendship());
        if (Management is not null && changed.Contains("FactionSaveData")) Management = await Task.Run(() => document!.ReadManagement());
        // Activation and switching a house to free construction change the house list in ContentsMiscSaveData.
        if (Housing is not null && (changed.Contains("CustomHouseSaveData") || changed.Contains("ContentsMiscSaveData"))) Housing = await Task.Run(() => document!.ReadHousing());
        if (Clock is not null && changed.Contains("TransformSaveData")) Clock = await Task.Run(() => document!.ReadFieldTime());
        Refresh(); Status = Tr.Text(done);
    });
}
