namespace CDSE.Core.Saves;

/// <summary>One edit of the document: the blocks it changed, when, and whatever the screen notes about it.</summary>
public sealed class HistoryEntry
{
    internal SaveDocument.Patch[]? Patches { get; }
    internal SaveDocument.Snapshot? Before { get; }
    internal SaveDocument.Snapshot? After { get; }
    public string[] Changed { get; }
    public DateTime Time { get; } = DateTime.Now;
    public object? Note { get; set; }
    internal HistoryEntry(string[] changed, SaveDocument.Patch[]? patches, SaveDocument.Snapshot? before, SaveDocument.Snapshot? after)
        => (Changed, Patches, Before, After) = (changed, patches, before, after);
}

// Same-size edits keep only the bytes that differ; an edit that moves records keeps both whole states compressed, and
// neighbouring ones share theirs. Saving keeps the history, so a state from before a save can come back.
public sealed partial class SaveDocument
{
    // About 45 edits that move records on a late save, 2-3 MB each. Older entries make room.
    internal const long HistoryLimit = 128 << 20;
    private readonly List<HistoryEntry> history = [];
    /// <summary>Every edit still kept, oldest first. Entries from <see cref="Position"/> on are undone and can come back.</summary>
    public IReadOnlyList<HistoryEntry> History => history;
    public int Position { get; private set; }
    /// <summary>The position last written to a file, or -1 when the history no longer reaches it. It starts at the opened file.</summary>
    public int SavedPosition { get; private set; }
    /// <summary>Whether older entries were dropped, so position 0 is no longer the opened file.</summary>
    public bool HistoryTrimmed { get; private set; }
    public bool CanUndo => Position > 0;
    public bool CanRedo => Position < history.Count;

    private void Record(HistoryEntry entry)
    {
        history.RemoveRange(Position, history.Count - Position);
        if (SavedPosition > Position) SavedPosition = -1;
        history.Add(entry);
        Position++;
        long size = HistorySize();
        while (size > HistoryLimit && history.Count > 1)
        {
            history.RemoveAt(0);
            Position--;
            if (SavedPosition >= 0) SavedPosition--;
            HistoryTrimmed = true;
            size = HistorySize();
        }
    }

    internal long HistorySize() => history.Sum(e => e.Patches?.Sum(p => 2L * p.After.Length) ?? 0)
        + history.SelectMany(e => new[] { e.Before, e.After }).OfType<Snapshot>().Distinct(ReferenceEqualityComparer.Instance).Cast<Snapshot>().Sum(s => (long)s.Packed.Length);

    public string[] Undo() => CanUndo ? MoveTo(Position - 1) : [];
    public string[] Redo() => CanRedo ? MoveTo(Position + 1) : [];

    /// <summary>Goes to the state after the first <paramref name="target"/> edits and returns the blocks that may differ.
    /// The whole way is checked as one edit: a state that does not match its record leaves the document as it was.</summary>
    public string[] MoveTo(int target)
    {
        if (target < 0 || target > history.Count) throw new ArgumentOutOfRangeException(nameof(target));
        if (target == Position) return [];
        bool back = target < Position;
        var steps = back ? history[target..Position].AsEnumerable().Reverse().ToArray() : history[Position..target].ToArray();
        byte[] state = blob;
        bool writable = false;
        foreach (var step in steps)
        {
            if (step.Patches is { } patches)
            {
                if (!writable)
                {
                    byte[] copy = EditBuffer(state.Length);
                    state.CopyTo(copy, 0);
                    (state, writable) = (copy, true);
                }
                foreach (var patch in patches)
                {
                    var (from, to) = back ? (patch.After, patch.Before) : (patch.Before, patch.After);
                    if (!state.AsSpan(patch.Offset, from.Length).SequenceEqual(from)) throw new InvalidOperationException("Edit snapshot mismatch.");
                    to.CopyTo(state, patch.Offset);
                }
                continue;
            }
            var (current, next) = back ? (step.After!, step.Before!) : (step.Before!, step.After!);
            if (!current.Matches(state)) throw new InvalidOperationException("Undo snapshot mismatch.");
            // Returning to the saved bytes can reuse that immutable buffer without expanding another full copy.
            if (step == steps[^1] && next.Matches(savedBlob)) (state, writable) = (savedBlob, false);
            else (state, writable) = (next.Expand(writable && state.Length == next.Length ? state : EditBuffer(next.Length)), true);
        }
        string[] changed = steps.SelectMany(s => s.Changed).Distinct().ToArray();
        Adopt(state, changed, null, restoring: true);
        Position = target;
        if (Position == 0)
        {
            snapshotBuffer.SetLength(0);
            snapshotBuffer.Capacity = 0;
        }
        if (!ReferenceEquals(blob, savedBlob) && blob.AsSpan().SequenceEqual(savedBlob))
        {
            if (ReferenceEquals(editBuffer, savedBlob)) editBuffer = blob;
            blob = savedBlob;
        }
        return changed;
    }
}
