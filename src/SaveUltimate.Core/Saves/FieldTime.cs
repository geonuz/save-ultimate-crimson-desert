using System.Buffers.Binary;

namespace SaveUltimate.Core.Saves;

/// <summary>The world clock, TransformSaveData._fieldTimeRaw of the field the player is in.</summary>
public sealed record FieldClock(ulong Raw)
{
    // Stamps kept in seconds (quest completion, shop accounting, FieldTimeSeconds fields) sit at fieldTimeRaw / 875 in every
    // save compared (2026-11-11), so the raw clock counts 875 units per in-game second.
    public const ulong UnitsPerSecond = 875;
    public const ulong UnitsPerDay = UnitsPerSecond * 86400;
    public ulong Seconds => Raw / UnitsPerSecond;
    public ulong Days => Raw / UnitsPerDay;
    public TimeSpan TimeOfDay => TimeSpan.FromSeconds(Seconds % 86400);
    public FieldClock After(ulong days, ulong hours, ulong minutes) => new(checked(Raw + days * UnitsPerDay + hours * UnitsPerSecond * 3600 + minutes * UnitsPerSecond * 60));
    /// <summary>The clock on a chosen day and minute. The seconds within the minute are kept, so the change from this clock is whole minutes.</summary>
    public FieldClock At(ulong day, uint hour, uint minute) => new(checked(day * UnitsPerDay + (hour * 3600UL + minute * 60UL) * UnitsPerSecond + Raw % (UnitsPerSecond * 60)));
}

public sealed partial class SaveDocument
{
    // Every field entry with its clock. The current one is the field the player was last in.
    private (SaveField Time, ulong Value, bool Current)[] FieldClocks()
    {
        var fields = SaveRecords.Read(blob, Layout, "TransformSaveData").Fields;
        var list = fields.Named("_fieldSaveDataList");
        var last = fields.Named("_lastfieldInfoKey");
        uint? current = last.Present ? (uint)last.Scalar(blob, "FieldInfoKey", 4) : null;
        var rows = list.Elements("TransformFieldSaveData").Select(r =>
        {
            var key = r.ChildFields.Named("_fieldInfoKey");
            var time = r.ChildFields.Named("_fieldTimeRaw");
            return (Key: key.Present ? (uint?)key.Scalar(blob, "FieldInfoKey", 4) : null, Time: time);
        }).Where(r => r.Time.Present).Select(r => (r.Key, r.Time, Value: r.Time.Scalar(blob, "uint64", 8))).ToArray();
        if (rows.Length == 0) throw new InvalidDataException("The save holds no field time.");
        var now = rows.FirstOrDefault(r => r.Key == current);
        if (now.Time is null) now = rows.MaxBy(r => r.Value);
        return rows.Select(r => (r.Time, r.Value, ReferenceEquals(r.Time, now.Time))).ToArray();
    }

    internal ulong FieldTime() => FieldClocks().Single(c => c.Current).Value;
    public FieldClock ReadFieldTime() => new(FieldTime());

    /// <summary>Moves the world clock forward to <paramref name="target"/>. Every field's clock moves by the same amount, so
    /// times already stamped in the save stay in the past. Going back is refused: those stamps would lie in the future.</summary>
    public FieldClock AdvanceFieldTime(FieldClock target)
    {
        var clocks = FieldClocks();
        ulong now = clocks.Single(c => c.Current).Value;
        if (target.Raw <= now) throw new ArgumentOutOfRangeException(nameof(target), "The time can only move forward.");
        // A hundred years is far beyond play and keeps every clock clear of overflow.
        if (target.Raw - now > 36500 * FieldClock.UnitsPerDay) throw new ArgumentOutOfRangeException(nameof(target), "Move the time forward by at most 100 years.");
        ulong delta = target.Raw - now;
        Edit(clocks.Select(c =>
        {
            byte[] bytes = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, checked(c.Value + delta));
            return CreatePatch(c.Time.StartOffset, bytes);
        }));
        return ReadFieldTime();
    }
}
