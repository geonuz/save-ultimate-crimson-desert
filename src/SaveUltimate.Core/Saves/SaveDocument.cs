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
    private sealed record Patch(int Offset, byte[] Before, byte[] After, bool CheckedScalar = false);
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
    private readonly Stack<(Patch[]? Patches, Snapshot? Before, Snapshot? After, string[]? Changed)> undo = new();
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
        Items = Inventory.Read(raw, Layout, snapshot);
    }

    public static SaveDocument Open(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        var container = SaveContainer.Decode(file);
        NativeSave.ValidateBlob(container.Blob);
        return new(path, file, container.Blob);
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
        ValidateSelection([item], requireStackEditable: false);
        if (item.Source != ItemSource.Vendor) throw new InvalidOperationException("Only sold vendor items can be swapped for repurchase.");
        if (key == item.Key) return;
        string[] blocks = ["InventorySaveData", "EquipmentSaveData", "StoreSaveData", "MercenaryClanSaveData",
            "InventoryItemContentsSaveData", "GameEventSaveData", "FieldSaveData", "FriendlySaveData"];
        var offsets = new HashSet<int>();
        var identities = new HashSet<int>();
        foreach (string name in blocks.Where(name => Layout.Blocks.Any(b => b.Name == name)))
            foreach (var record in SaveRecords.ReadObjects(blob, Layout, name, item).SelectMany(o => o.Fields))
            {
                SaveRecords.ValidateItem(blob, record);
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

    private (Snapshot Before, Snapshot After) PrepareSnapshots(byte[] before, byte[] after)
        // The last structural edit's result is this edit's input unless a scalar edit intervened.
        => (undo.TryPeek(out var previous) && previous.Patches is null ? previous.After! : Snapshot.Capture(before, snapshotBuffer),
            Snapshot.Capture(after, snapshotBuffer));

    private void Edit(IEnumerable<Patch> changes)
    {
        var patches = changes.Where(p => !p.Before.AsSpan().SequenceEqual(p.After)).ToArray();
        if (patches.Length == 0) return;
        Apply(patches, reverse: false);
        undo.Push((patches, null, null, null));
    }

    private void Apply(Patch[] patches, bool reverse)
    {
        byte[] candidate = CopyForEdit();
        foreach (var patch in patches)
        {
            var before = reverse ? patch.After : patch.Before;
            var after = reverse ? patch.Before : patch.After;
            if (!candidate.AsSpan(patch.Offset, before.Length).SequenceEqual(before))
                throw new InvalidOperationException("Edit snapshot mismatch.");
            after.CopyTo(candidate, patch.Offset);
        }
        var scalarFields = Items.Select(i => (i.QuantityOffset, 8))
            .Concat(Items.Where(i => i.EnchantLevel.HasValue).Select(i => (i.EnchantOffset, 2)))
            .Concat(Items.SelectMany(i => i.Sockets.ScalarFields)).ToHashSet();
        if (patches.All(p => p.CheckedScalar && p.Before.Length == p.After.Length || scalarFields.Contains((p.Offset, p.After.Length))))
        {
            // Fixed-size typed scalars preserve the validated layout; structural edits and writes still validate the full blob.
            Guid next = Guid.NewGuid();
            var changed = patches.Select(p => p.Offset).ToHashSet();
            var items = Items.Select(i => i.RefreshScalars(candidate, next,
                i.Sockets.ScalarFields.Any(f => changed.Contains(f.Offset)) ? i.Sockets.RefreshScalars(candidate) : null)).ToArray();
            (editBuffer, blob, snapshot, Items) = (blob, candidate, next, items);
        }
        else Commit(candidate, changed: PatchBlocks(patches));
    }

    private string[] PatchBlocks(Patch[] patches) => Layout.Blocks
        .Where(b => patches.Any(p => p.Offset >= b.Offset && p.Offset + p.After.Length <= b.Offset + b.Size))
        .Select(b => b.Name).Distinct().ToArray();

    private void Commit(byte[] candidate, Action<IReadOnlyList<InventoryItem>>? verify = null, InventoryItem? fixedSockets = null, string[]? changed = null, bool validate = true)
    {
        if (fixedSockets is not null && candidate.Length != blob.Length)
            throw new InvalidDataException("Scalar socket edit changed the layout.");
        Guid next = Guid.NewGuid();
        var layout = Layout;
        InventoryItem[] items;
        // Both readers use the immutable candidate; neither may commit before full validation completes.
        var validation = fixedSockets is null && validate ? Task.Run(() => NativeSave.ValidateBlob(candidate)) : null;
        try
        {
            layout = fixedSockets is null ? Layout.Reparse(candidate, blob) : Layout;
            var sockets = fixedSockets?.Sockets.RefreshScalars(candidate);
            items = fixedSockets is null ? changed is null ? Inventory.Read(candidate, layout, next)
                : Inventory.Refresh(blob, candidate, Layout, layout, Items, next, changed)
                : Items.Select(i => i.RefreshScalars(candidate, next, ReferenceEquals(i, fixedSockets) ? sockets : null)).ToArray();
            verify?.Invoke(items);
        }
        finally { validation?.GetAwaiter().GetResult(); }
        // Retire the old blob only after validation; it can serve the next edit or Undo.
        editBuffer = ReferenceEquals(candidate, editBuffer) || ReferenceEquals(candidate, savedBlob) ? blob : null;
        (blob, snapshot, Items, Layout) = (candidate, next, items, layout);
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
        byte[] candidate = item.Sockets.Replace(blob, item.Key, gems, EditBuffer);
        bool fixedLayout = gems.Select((key, i) => (key != 0) == item.Sockets.Slots[i].HasGem).All(same => same);
        CommitSockets(item, candidate, item.Sockets.Unlocked, gems, fixedLayout);
    }

    public void SetUnlockedSockets(InventoryItem item, int count)
    {
        item = SocketTarget(item);
        CommitSockets(item, item.Sockets.SetUnlocked(blob, item.Key, count, EditBuffer), count, [], item.Sockets.HasUnlockedField);
    }

    private void CommitSockets(InventoryItem item, byte[] candidate, int unlocked, uint[] gems, bool fixedLayout)
    {
        byte[] before = blob;
        if (ReferenceEquals(before, candidate)) return;
        var patches = fixedLayout ? item.Sockets.ScalarFields
            .Select(f => CreatePatch(f.Offset, candidate.AsSpan(f.Offset, f.Size).ToArray()))
            .Where(p => !p.Before.AsSpan().SequenceEqual(p.After)).ToArray() : null;
        var snapshots = fixedLayout ? default : PrepareSnapshots(before, candidate);
        Commit(candidate, items =>
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
        }, fixedLayout ? item : null, [Inventory.ClassName(item.Source)]);
        undo.Push((patches, snapshots.Before, snapshots.After, [Inventory.ClassName(item.Source)]));
    }

    public string[] Undo()
    {
        if (!undo.TryPeek(out var edit)) return [];
        var changed = edit.Changed ?? (edit.Patches is not null ? PatchBlocks(edit.Patches) : Layout.Blocks.Select(b => b.Name).Distinct().ToArray());
        if (edit.Patches is not null) Apply(edit.Patches, reverse: true);
        else
        {
            if (!edit.After!.Matches(blob)) throw new InvalidOperationException("Undo snapshot mismatch.");
            var before = edit.Before!;
            // Returning to the saved bytes can reuse that immutable buffer without expanding another full copy.
            // Before holds the exact bytes of a state that already passed full validation; zlib's checksum still rejects damage.
            Commit(undo.Count == 1 && before.Matches(savedBlob) ? savedBlob : before.Expand(EditBuffer(before.Length)), changed: edit.Changed, validate: false);
        }
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
        return changed;
    }

    public void SaveCopy(string destination)
    {
        destination = Path.GetFullPath(destination);
        if (File.Exists(destination) || destination.Equals(SourcePath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a new file name. Existing saves are preserved.");
        WriteCandidate(destination);
        savedBlob = blob;
    }

    private void WriteCandidate(string destination)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".saveultimate-source-{Guid.NewGuid():N}.save");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) file.Write(originalFile);
            NativeSave.Write(temporary, blob, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public string? SaveTo(string destination, bool backup = true, byte[]? expectedDestinationHash = null)
    {
        destination = Path.GetFullPath(destination);
        if (destination.Equals(SourcePath, StringComparison.OrdinalIgnoreCase))
            expectedDestinationHash = SHA256.HashData(originalFile);
        string prepared = Path.Combine(Path.GetDirectoryName(destination)!, $".prepared-{Guid.NewGuid():N}.save");
        try
        {
            WriteCandidate(prepared);
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
