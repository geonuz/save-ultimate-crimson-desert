using System.Buffers.Binary;
using System.Security.Cryptography;
using System.IO.Compression;

namespace SaveUltimate.Core.Saves;

public sealed partial class SaveDocument
{
    private byte[] originalFile;
    private byte[] blob;
    // Share immutable saved bytes; never reuse them as a writable scratch buffer.
    private byte[] savedBlob;
    private byte[]? editBuffer;
    private readonly MemoryStream snapshotBuffer = new();
    private Guid snapshot = Guid.NewGuid();
    public int DamagedRows { get; private set; }
    private sealed record Patch(int Offset, byte[] Before, byte[] After);
    private sealed record Snapshot(byte[] Packed, int Length)
    {
        public static Snapshot Capture(byte[] raw, MemoryStream output)
        {
            output.SetLength(0);
            using (var stream = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true)) stream.Write(raw);
            return new(output.ToArray(), raw.Length);
        }
        private ZLibStream Open() => new(new MemoryStream(Packed, false), CompressionMode.Decompress);

        public byte[] Expand(byte[] target)
        {
            using var stream = Open();
            stream.ReadExactly(target);
            if (stream.ReadByte() != -1) throw new InvalidDataException("Undo snapshot length mismatch.");
            return target;
        }

        public bool Matches(byte[] raw)
        {
            if (raw.Length != Length) return false;
            using var stream = Open();
            Span<byte> buffer = stackalloc byte[4096];
            for (int offset = 0; offset < raw.Length;)
            {
                var part = buffer[..Math.Min(buffer.Length, raw.Length - offset)];
                stream.ReadExactly(part);
                if (!part.SequenceEqual(raw.AsSpan(offset, part.Length))) return false;
                offset += part.Length;
            }
            return stream.ReadByte() == -1;
        }
    }
    private readonly Stack<(Patch[]? Patches, Snapshot? Before, Snapshot? After, string[] Changed)> undo = new();
    public string SourcePath { get; private set; }
    public ParcLayout Layout { get; private set; }
    public bool IsDirty => !blob.AsSpan().SequenceEqual(savedBlob);
    public bool CanUndo => undo.Count != 0;
    public string BlobSha256 => Convert.ToHexString(SHA256.HashData(blob));
    public IReadOnlyList<InventoryItem> Items { get; private set; }

    private SaveDocument(string path, byte[] file, byte[] raw)
    {
        SourcePath = Path.GetFullPath(path);
        originalFile = file;
        blob = raw;
        savedBlob = raw;
        Layout = ParcLayout.Parse(raw);
        DamagedRows = ParcReader.Validate(raw, Layout).DamagedRows;
        RefuseOldGameVersion(raw, Layout);
        Items = Inventory.Read(raw, Layout, snapshot);
    }

    // No edit was ever checked in game on a 1.x save.
    private static void RefuseOldGameVersion(byte[] raw, ParcLayout layout)
    {
        const string block = "InventoryItemContentsSaveData";
        const uint firstSupported = 2 << 16;
        if (layout.Blocks.Any(b => b.Name == block) && SaveRecords.Read(raw, layout, block).Fields.Named("_savedGameVersion") is { Present: true } version &&
            version.Scalar(raw, version.TypeName, 4) < firstSupported)
            throw new InvalidDataException("Saves from game version 1.x are not supported.");
    }

    public static SaveDocument Open(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        return new(path, file, SaveContainer.Decode(file).Blob);
    }

    public void SetQuantity(InventoryItem item, ulong value)
        => SetQuantity([item], value);

    public void SetQuantity(IEnumerable<InventoryItem> selected, ulong value)
    {
        if (value == 0 || value > 999_999_999) throw new ArgumentOutOfRangeException(nameof(value), "Quantity must be 1–999,999,999.");
        byte[] bytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Edit(ValidateSelection(selected).Select(item => CreatePatch(item.QuantityOffset, bytes)));
    }

    public void Delete(IEnumerable<InventoryItem> selected)
    {
        Edit(ValidateSelection(selected).SelectMany(item => new[]
        {
            CreatePatch(item.KeyOffset, new byte[4]), CreatePatch(item.QuantityOffset, new byte[8])
        }));
    }

    public void SetEnchant(IEnumerable<InventoryItem> selected, ushort level)
    {
        if (level is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(level), "Enchant level must be 1–10.");
        var eligible = ValidateSelection(selected, requireStackEditable: false).Where(item => item.EnchantLevel.HasValue).ToArray();
        foreach (var item in eligible)
            if (item.MaxEnchant >= 0 && level > item.MaxEnchant)
                throw new InvalidOperationException($"Item {item.Key} has maximum enchant +{item.MaxEnchant}.");
        byte[] bytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, level);
        Edit(eligible.Select(item => CreatePatch(item.EnchantOffset, bytes)));
    }

    private InventoryItem[] ValidateSelection(IEnumerable<InventoryItem> selected, bool requireStackEditable = true)
    {
        var selection = selected.Distinct().ToArray();
        var current = Items.ToHashSet();
        if (selection.Any(item => item.Snapshot != snapshot || !current.Contains(item)))
            throw new InvalidOperationException("Selected item is stale; select it again.");
        if (requireStackEditable && selection.Any(item => !item.CanEditQuantity))
            throw new InvalidOperationException("Mercenary quantities and deletion are read-only.");
        return selection;
    }

    public void SwapVendorItem(InventoryItem item, uint key)
    {
        if (key is 0 or > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(key));
        DlcContent.Refuse(DlcContent.Shared.Items.Contains(key));
        ValidateSelection([item], requireStackEditable: false);
        if (item.Source != ItemSource.Vendor) throw new InvalidOperationException("Only sold vendor items can be swapped for repurchase.");
        if (key == item.Key) return;
        if (!item.CanBecome(key)) throw new InvalidOperationException("The sold item's quantity, enchant level, gems or other state do not fit the replacement item.");
        string[] blocks = ["InventorySaveData", "EquipmentSaveData", "StoreSaveData", "MercenaryClanSaveData",
            "InventoryItemContentsSaveData", "GameEventSaveData", "FieldSaveData", "FriendlySaveData"];
        var offsets = new HashSet<int>();
        var identities = new HashSet<int>();
        foreach (string name in blocks.Where(name => Layout.Blocks.Any(b => b.Name == name)))
            foreach (var record in SaveRecords.ItemReferences(blob, Layout, name, item))
            {
                var fields = record.ChildFields;
                var version = fields.Named("_saveVersion");
                var field = fields.Named("_itemKey");
                if (version.Scalar(blob, "uint32", 4) != 1 || fields.Named("_itemNo").Scalar(blob, "ItemNo", 8) != item.Number ||
                    field.Scalar(blob, "ItemKey", 4) != item.Key || !identities.Add(version.StartOffset))
                    throw new InvalidDataException("Ambiguous or invalid item reference.");
                offsets.Add(field.StartOffset);
                var transferred = fields.Named("_transferredItemKey");
                if (transferred.Present && transferred.Scalar(blob, "ItemKey", 4) == item.Key) offsets.Add(transferred.StartOffset);
            }
        if (!offsets.Contains(item.KeyOffset)) throw new InvalidDataException("Selected item reference was not decoded.");
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, key);
        Edit(offsets.Order().Select(offset => CreatePatch(offset, bytes)));
    }

    private Patch CreatePatch(int offset, byte[] after) => new(offset, blob.AsSpan(offset, after.Length).ToArray(), after);

    private byte[] EditBuffer(int length)
        => editBuffer is { } buffer && buffer.Length == length && !ReferenceEquals(buffer, savedBlob)
            ? buffer : editBuffer = new byte[length];

    private byte[] CopyForEdit()
    {
        byte[] candidate = EditBuffer(blob.Length);
        blob.CopyTo(candidate, 0);
        return candidate;
    }

    private void Edit(IEnumerable<Patch> changes)
    {
        var patches = changes.Where(p => !p.Before.AsSpan().SequenceEqual(p.After)).ToArray();
        if (patches.Length == 0) return;
        byte[] candidate = CopyForEdit();
        foreach (var patch in patches) patch.After.CopyTo(candidate, patch.Offset);
        Transact(candidate, Layout.Blocks.Where(b => patches.Any(p => p.Offset >= b.Offset && p.Offset + p.After.Length <= b.Offset + b.Size)).Select(b => b.Name).Distinct().ToArray());
    }

    // The byte runs that differ, or null when the edit changed the size and nothing lines up.
    private Patch[]? Difference(byte[] candidate)
    {
        if (candidate.Length != blob.Length) return null;
        var patches = new List<Patch>();
        ReadOnlySpan<byte> before = blob, after = candidate;
        for (int at = before.CommonPrefixLength(after); at < before.Length; at += before[at..].CommonPrefixLength(after[at..]))
        {
            int start = at;
            while (at < before.Length && before[at] != after[at]) at++;
            patches.Add(new(start, before[start..at].ToArray(), after[start..at].ToArray()));
        }
        return patches.ToArray();
    }

    // Every edit ends here: the candidate replaces the document only if it passes all checks, and Undo can always return from it.
    // changed names the blocks the edit means to touch; verify is the edit's own check of its result.
    internal void Transact(byte[] candidate, string[] changed, Action<ParcLayout, IReadOnlyList<InventoryItem>>? verify = null)
    {
        var patches = Difference(candidate);
        // The last structural edit's result is this edit's input unless another edit intervened.
        var before = patches is not null ? null : undo.TryPeek(out var previous) && previous.Patches is null ? previous.After! : Snapshot.Capture(blob, snapshotBuffer);
        var after = patches is not null ? null : Snapshot.Capture(candidate, snapshotBuffer);
        Adopt(candidate, changed, verify, restoring: false);
        if (patches is not { Length: 0 }) undo.Push((patches, before, after, changed));
    }

    private void Adopt(byte[] candidate, string[] changed, Action<ParcLayout, IReadOnlyList<InventoryItem>>? verify, bool restoring)
    {
        Guid next = Guid.NewGuid();
        var layout = Layout.Reparse(candidate, blob);
        int damaged = ParcReader.Validate(candidate, layout).DamagedRows;
        var items = Inventory.Read(candidate, layout, next);
        if (!restoring)
        {
            // Rows like these crash the game at the title screen, so no edit may add one. Undo may bring back what Save Doctor repaired.
            if (damaged > DamagedRows) throw new InvalidDataException("The edit would damage mission rows.");
            if (!candidate.AsSpan(0, layout.SchemaEnd).SequenceEqual(blob.AsSpan(0, Layout.SchemaEnd)) ||
                !layout.Blocks.Select(b => (b.Name, b.TypeIndex)).SequenceEqual(Layout.Blocks.Select(b => (b.Name, b.TypeIndex))))
                throw new InvalidDataException("Edit changed the save schema.");
            if (Layout.Blocks.Zip(layout.Blocks).Any(p => !changed.Contains(p.First.Name) && p.First.Size != p.Second.Size))
                throw new InvalidDataException("Edit changed an unrelated block layout.");
            static IEnumerable<(ItemSource, ulong, uint, ulong)> Others(IEnumerable<InventoryItem> all, string[] changed) => all
                .Where(i => !changed.Contains(Inventory.ClassName(i.Source))).Select(i => (i.Source, i.Number, i.Key, i.Quantity));
            if (!Others(Items, changed).SequenceEqual(Others(items, changed))) throw new InvalidDataException("Edit changed an unrelated item.");
        }
        verify?.Invoke(layout, items);
        // Retire the old blob only after validation; it can serve the next edit or Undo.
        editBuffer = ReferenceEquals(candidate, editBuffer) || ReferenceEquals(candidate, savedBlob) ? blob : null;
        (blob, snapshot, Items, Layout, DamagedRows) = (candidate, next, items, layout, damaged);
    }

    public InventoryItem SocketTarget(InventoryItem item)
    {
        ValidateSelection([item], requireStackEditable: false);
        return item.Source == ItemSource.Equipment
            ? Items.SingleOrDefault(i => i.Source == ItemSource.Inventory && i.Number == item.Number && i.Key == item.Key) ?? item : item;
    }

    public void SetSockets(InventoryItem item, IEnumerable<uint> values)
    {
        item = SocketTarget(item);
        uint[] gems = values.ToArray();
        CommitSockets(item, item.Sockets.Replace(blob, Layout, item.Key, gems, EditBuffer), item.Sockets.Unlocked, gems);
    }

    public void SetUnlockedSockets(InventoryItem item, int count)
    {
        item = SocketTarget(item);
        CommitSockets(item, item.Sockets.SetUnlocked(blob, item.Key, count, EditBuffer), count, []);
    }

    private void CommitSockets(InventoryItem item, byte[] candidate, int unlocked, uint[] gems)
    {
        if (ReferenceEquals(blob, candidate)) return;
        Transact(candidate, [Inventory.ClassName(item.Source)], (_, items) =>
        {
            var updated = items.Single(i => i.Source == item.Source && i.Number == item.Number && i.Key == item.Key && i.OwnerNumber == item.OwnerNumber).Sockets;
            var previous = item.Sockets;
            if (updated.Error is not null || updated.Capacity != previous.Capacity || updated.Unlocked != unlocked || updated.Slots.Count != previous.Slots.Count)
                throw new InvalidDataException("Socket count verification failed.");
            for (int i = 0; i < updated.Slots.Count; i++)
            {
                uint key = i < gems.Length ? gems[i] : previous.Slots[i].Key;
                bool hasGem = i < gems.Length ? key != 0 : previous.Slots[i].HasGem;
                ushort endurance = i >= gems.Length || hasGem && previous.Slots[i].HasGem ? previous.Slots[i].Endurance : hasGem ? ushort.MaxValue : (ushort)0;
                if (updated.Slots[i].Key != key || updated.Slots[i].HasGem != hasGem || updated.Slots[i].Endurance != endurance)
                    throw new InvalidDataException("Socket value verification failed.");
            }
        });
    }

    public string[] Undo()
    {
        if (!undo.TryPeek(out var edit)) return [];
        byte[] candidate;
        if (edit.Patches is not null)
        {
            candidate = CopyForEdit();
            foreach (var patch in edit.Patches)
            {
                if (!candidate.AsSpan(patch.Offset, patch.After.Length).SequenceEqual(patch.After)) throw new InvalidOperationException("Edit snapshot mismatch.");
                patch.Before.CopyTo(candidate, patch.Offset);
            }
        }
        else
        {
            if (!edit.After!.Matches(blob)) throw new InvalidOperationException("Undo snapshot mismatch.");
            var before = edit.Before!;
            // Returning to the saved bytes can reuse that immutable buffer without expanding another full copy.
            candidate = undo.Count == 1 && before.Matches(savedBlob) ? savedBlob : before.Expand(EditBuffer(before.Length));
        }
        Adopt(candidate, edit.Changed, null, restoring: true);
        undo.Pop();
        if (undo.Count == 0)
        {
            snapshotBuffer.SetLength(0);
            snapshotBuffer.Capacity = 0;
            if (blob.AsSpan().SequenceEqual(savedBlob))
            {
                if (ReferenceEquals(editBuffer, savedBlob)) editBuffer = blob;
                blob = savedBlob;
            }
        }
        return edit.Changed;
    }

    public void SaveCopy(string destination)
    {
        destination = Path.GetFullPath(destination);
        if (File.Exists(destination) || destination.Equals(SourcePath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a new file name. Existing saves are preserved.");
        SaveContainer.Write(originalFile, blob, destination);
        savedBlob = blob;
    }

    public string? SaveTo(string destination, bool backup = true, byte[]? expectedDestinationHash = null)
    {
        destination = Path.GetFullPath(destination);
        if (destination.Equals(SourcePath, StringComparison.OrdinalIgnoreCase))
            expectedDestinationHash = SHA256.HashData(originalFile);
        string prepared = Path.Combine(Path.GetDirectoryName(destination)!, $".prepared-{Guid.NewGuid():N}.save");
        try
        {
            SaveContainer.Write(originalFile, blob, prepared);
            byte[] written = File.ReadAllBytes(prepared);
            string? recovery = SaveBackups.ReplacePrepared(prepared, destination, expectedDestinationHash, backup);
            originalFile = written;
            SourcePath = destination;
            savedBlob = blob;
            undo.Clear();
            snapshotBuffer.SetLength(0);
            snapshotBuffer.Capacity = 0;
            return recovery;
        }
        finally
        {
            if (File.Exists(prepared)) File.Delete(prepared);
        }
    }
}
