using System.Buffers.Binary;

namespace SaveUltimate.Core.Saves;

// A slot folder holds save.save and lobby.save. The lobby keeps the game's last handed-out item and mercenary number
// (LobbySaveData._generateNo); after a load the game numbers from there without checking the save, so a save.save that
// holds higher numbers than its lobby gets them handed out again as duplicates.
public sealed partial class SaveDocument
{
    private const string SlotSave = "save.save", SlotLobby = "lobby.save";
    // Blocks whose item and mercenary numbers the counter has passed. Field drops carry numbers from elsewhere, above it.
    private static readonly string[] CountedBlocks = ["InventorySaveData", "EquipmentSaveData", "MercenaryClanSaveData", "StoreSaveData", "QuestSaveData", "FactionSaveData"];
    private byte[]? lobbyFile;

    private static string? LobbyPath(string save) => Path.GetFileName(save).Equals(SlotSave, StringComparison.OrdinalIgnoreCase)
        ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(save))!, SlotLobby) : null;

    // A lobby that does not read leaves the document without one, as for a save outside a slot.
    private static byte[]? ReadLobbyBeside(string save)
    {
        if (LobbyPath(save) is not { } path || !File.Exists(path)) return null;
        byte[] file = File.ReadAllBytes(path);
        try { LobbyCounter(file); return file; }
        catch (InvalidDataException) { return null; }
    }

    private static (byte[] Blob, int Offset, ulong Value) LobbyCounter(byte[] file)
    {
        byte[] blob = SaveContainer.Decode(file).Blob;
        var field = SaveRecords.Read(blob, ParcLayout.Parse(blob), "LobbySaveData").Fields.Named("_generateNo");
        return (blob, field.StartOffset, field.Scalar(blob, "int64", 8));
    }

    /// <summary>The last number the game handed out: the lobby's counter, or a higher number this save already holds. Null without a lobby.</summary>
    public ulong? GeneratedNumber() => lobbyFile is null ? null : Math.Max(LobbyCounter(lobbyFile).Value, HighestNumber());
    private ulong HighestNumber() => ObjectNumbers(CountedBlocks, "_itemNo", "_reserveItemNo", "_mercenaryNo").Where(n => n != ulong.MaxValue).Max();

    // Writes the lobby beside a slot's save.save before the save itself: a counter ahead of its save only skips numbers.
    // A save from another slot brings its own lobby; one from outside a slot raises the lobby already there. The counter
    // never goes below what the lobby on disk holds, in case the game saved there since this document was read.
    private byte[]? WriteLobby(string destination, bool backup)
    {
        if (LobbyPath(destination) is not { } path) return lobbyFile;
        byte[]? current = ReadLobbyBeside(destination);
        if ((lobbyFile ?? current) is not { } source) return null;
        var (blob, offset, counter) = LobbyCounter(source);
        ulong next = Math.Max(counter, HighestNumber());
        if (current is not null)
        {
            ulong onDisk = LobbyCounter(current).Value;
            if (onDisk >= next && (lobbyFile is null || current.AsSpan().SequenceEqual(lobbyFile))) return current;
            next = Math.Max(next, onDisk);
        }
        BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(offset), next);
        string prepared = Path.Combine(Path.GetDirectoryName(path)!, $".prepared-{Guid.NewGuid():N}.lobby");
        try
        {
            SaveContainer.Write(source, blob, prepared);
            byte[] written = File.ReadAllBytes(prepared);
            if (!File.Exists(path)) File.Move(prepared, path);
            else
            {
                if (backup) SaveBackups.Create(path);
                File.Replace(prepared, path, null);
            }
            return written;
        }
        finally { if (File.Exists(prepared)) File.Delete(prepared); }
    }
}
