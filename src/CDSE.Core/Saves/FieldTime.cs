using System.Buffers.Binary;

namespace CDSE.Core.Saves;

/// <summary>The world clock, TransformSaveData._fieldTimeRaw of the field the player is in, and the day and time the game shows for it.</summary>
public sealed record FieldClock(ulong Raw)
{
    // A day is 75,600,000 units: the farm day counter (MercenaryClanSaveData._currentFarmUpdateDay) is Raw / UnitsPerDay in
    // every save compared (19 saves, 2026-11-14), and stamps kept in seconds sit at Raw / 875.
    public const ulong UnitsPerDay = 75_600_000;
    // The shown time is not linear in the day. Two readings on the game screen, a morning and an evening of the same save
    // (2026-11-14), put the day at 1000 units a shown second; 75.6 million units for 24 shown hours then leave the night
    // twice as fast. Night is taken as 21:00-03:00, which fits both readings with the minutes that pass while a save loads.
    public const ulong DayUnitsPerSecond = 1000, NightUnitsPerSecond = 500;
    private const ulong Dawn = 3 * 3600, Dusk = 21 * 3600, DawnRaw = Dawn * NightUnitsPerSecond, DuskRaw = DawnRaw + (Dusk - Dawn) * DayUnitsPerSecond;
    public ulong Days => Raw / UnitsPerDay;
    /// <summary>The shown second of the day, 0-86399.</summary>
    public ulong SecondOfDay => ShownSecond(Raw % UnitsPerDay);
    /// <summary>Shown seconds since the clock's zero; a difference is the time the game shows as passed.</summary>
    public ulong Seconds => Days * 86400 + SecondOfDay;
    public TimeSpan TimeOfDay => TimeSpan.FromSeconds(SecondOfDay);
    private static ulong ShownSecond(ulong raw) => raw < DawnRaw ? raw / NightUnitsPerSecond
        : raw < DuskRaw ? Dawn + (raw - DawnRaw) / DayUnitsPerSecond : Dusk + (raw - DuskRaw) / NightUnitsPerSecond;
    private static ulong RawOfSecond(ulong second) => second < Dawn ? second * NightUnitsPerSecond
        : second < Dusk ? DawnRaw + (second - Dawn) * DayUnitsPerSecond : DuskRaw + (second - Dusk) * NightUnitsPerSecond;
    /// <summary>The first raw value of a shown second counted from the clock's zero.</summary>
    public static FieldClock FromSeconds(ulong seconds) => new(checked(seconds / 86400 * UnitsPerDay + RawOfSecond(seconds % 86400)));
    // Keeps the units past the start of the current shown second, so moving changes the clock by whole shown seconds.
    private FieldClock Keep(FieldClock target)
    {
        ulong part = Raw - FromSeconds(Seconds).Raw;
        ulong rate = target.SecondOfDay is >= Dawn and < Dusk ? DayUnitsPerSecond : NightUnitsPerSecond;
        return new(checked(target.Raw + Math.Min(part, rate - 1)));
    }
    public FieldClock After(ulong days, ulong hours, ulong minutes) => Keep(FromSeconds(checked(Seconds + days * 86400 + hours * 3600 + minutes * 60)));
    /// <summary>The clock on a chosen day and minute. The seconds within the minute are kept, so the change from this clock is whole minutes.</summary>
    public FieldClock At(ulong day, uint hour, uint minute) => Keep(FromSeconds(checked(day * 86400 + hour * 3600UL + minute * 60UL + SecondOfDay % 60)));
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
