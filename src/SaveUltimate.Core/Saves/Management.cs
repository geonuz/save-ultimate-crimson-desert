using System.Buffers.Binary;

namespace SaveUltimate.Core.Saves;

/// <summary>A DLC business (sawmill, restaurant, forge, clothier) from the client's FactionManagement table. The table's two festivals are not businesses and are left out.</summary>
public sealed record ManagementShop(ushort Key, string Name, string Region, uint PlaceKnowledge, string Kind, uint MaxLevel);
public sealed record ManagementCatalog(Dictionary<string, Dictionary<string, string>> Regions, ManagementShop[] Shops)
{
    public static ManagementCatalog Shared { get; } = Embedded.Load<ManagementCatalog>("Management.json");
    public string RegionName(string region, string language) => Regions.TryGetValue(region, out var names)
        ? names.GetValueOrDefault(language) ?? names.GetValueOrDefault("en") ?? region : region;
}

/// <summary>A business as the save holds it. Operating means the save has its record with a renown level.</summary>
public sealed record ManagementState(ManagementShop Shop, bool InSave, uint? Level, uint? HighLevel, uint? Score, uint? RewardedLevel, string? CustomName, ulong? Customers)
{
    public bool Operating => Level is not null;
    public bool Editable => Operating && Shop.MaxLevel > 0;
}

public sealed partial class SaveDocument
{
    private const string FactionBlock = "FactionSaveData";

    private static (SaveField Row, ManagementState State)[] ManagementRows(byte[] blob, ParcLayout Layout)
    {
        var found = new List<(SaveField, ManagementState)>();
        if (Layout.Blocks.Any(b => b.Name == FactionBlock))
        {
            var factions = SaveRecords.Read(blob, Layout, FactionBlock).Fields.Named("_factionElementSaveDataList");
            foreach (var faction in factions.Elements("FactionElementSaveData"))
            {
                var fields = faction.ChildFields;
                if (fields.FirstOrDefault(f => f.Name == "_factionManagementSaveDataList") is not { Present: true } list) continue;
                var rewards = new Dictionary<ushort, uint?>();
                if (fields.FirstOrDefault(f => f.Name == "_factionManagementRewardSaveDataList") is { Present: true } rewardList)
                    foreach (var r in rewardList.Elements("FactionManagementRewardSaveData"))
                        rewards[(ushort)r.ChildFields.Named("_managementKey").Scalar(blob, "FactionManagementKey", 2)] = Value32(blob, r.ChildFields.Named("_rewardedLevel"));
                foreach (var row in list.Elements("FactionManagementSaveData"))
                {
                    var c = row.ChildFields;
                    ushort key = (ushort)c.Named("_managementKey").Scalar(blob, "FactionManagementKey", 2);
                    var shop = ManagementCatalog.Shared.Shops.FirstOrDefault(s => s.Key == key) ?? new(key, $"Management {key}", "", 0, "unknown", 0);
                    var name = c.Named("_managementName");
                    var customers = c.Named("_visitedCustomerCount");
                    found.Add((row, new(shop, true, Value32(blob, c.Named("_level")), Value32(blob, c.Named("_highLevel")), Value32(blob, c.Named("_managementScore")),
                        rewards.GetValueOrDefault(key), name.Present ? name.Text(blob) : null, customers.Present ? customers.Scalar(blob, "TStackCount", 8) : null)));
                }
            }
        }
        if (found.Select(f => f.Item2.Shop.Key).Distinct().Count() != found.Count) throw new InvalidDataException("A business appears twice.");
        return found.ToArray();
    }
    private static uint? Value32(byte[] blob, SaveField field) => field.Present ? (uint)field.Scalar(blob, field.TypeName, 4) : null;

    

    /// <summary>Every business in the catalog, then any other running one the save holds, with what the save records for it.</summary>
    public ManagementState[] ReadManagement() => ManagementStates(blob, Layout);
    private static ManagementState[] ManagementStates(byte[] blob, ParcLayout Layout)
    {
        var rows = ManagementRows(blob, Layout).ToDictionary(r => r.State.Shop.Key, r => r.State);
        return [.. ManagementCatalog.Shared.Shops.Select(s => rows.GetValueOrDefault(s.Key) ?? new(s, false, null, null, null, null, null, null)),
            .. rows.Values.Where(r => r.Operating && !ManagementCatalog.Shared.Shops.Any(s => s.Key == r.Shop.Key)).OrderBy(r => r.Shop.Key)];
    }

    /// <summary>
    /// Sets a running business's renown level. The highest level reached rises with it, and the progress toward the next level
    /// (score and the three condition counts) starts over, the shape a level-5 business has in the game. The reward record is
    /// left alone, so the game still owes the rewards of levels above the last one it paid.
    /// </summary>
    public ManagementState[] SetManagementLevel(ushort key, uint level)
    {
        var (row, state) = ManagementRows(blob, Layout).SingleOrDefault(r => r.State.Shop.Key == key);
        if (row is null || !state.Editable) throw new InvalidOperationException("This business is not running, or its renown cannot be edited.");
        if (level < 1 || level > state.Shop.MaxLevel) throw new ArgumentOutOfRangeException(nameof(level), $"Renown level must be 1–{state.Shop.MaxLevel}.");
        var c = row.ChildFields;
        SaveField score = c.Named("_managementScore"), levelField = c.Named("_level"), high = c.Named("_highLevel"), next = c.Named("_nextLevelConditionCountData");
        if (levelField.FieldIndex != score.FieldIndex + 1 || high.FieldIndex != levelField.FieldIndex + 1 || !high.Present ||
            levelField.TypeName != "uint32" || high.TypeName != "uint32" || score.Present && score.EndOffset != levelField.StartOffset || levelField.EndOffset != high.StartOffset)
            throw new InvalidDataException("Unsupported business record layout.");
        uint newHigh = Math.Max(state.HighLevel ?? level, level);
        bool resetNext = next.Present && next.ChildFields.Any(f => f.Present);
        if (state.Level == level && newHigh == state.HighLevel && !score.Present && !resetNext) return ReadManagement();
        byte[] values = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(values, level);
        BinaryPrimitives.WriteUInt32LittleEndian(values.AsSpan(4), newHigh);
        var regions = new List<(int Start, int End, Func<int, byte[]> Encode)>();
        int mask = row.StartOffset + 2 + score.FieldIndex / 8;
        if (score.Present) regions.Add((mask, mask + 1, _ => [(byte)(blob[mask] & ~(1 << (score.FieldIndex % 8)))]));
        regions.Add((score.Present ? score.StartOffset : levelField.StartOffset, high.EndOffset, _ => values));
        if (resetNext)
        {
            // The counts are fixed fields of a nested object: clear its mask and drop them; Relocate fixes its size.
            regions.Add((next.StartOffset + 2, next.StartOffset + 2 + next.ChildMaskByteCount, _ => new byte[next.ChildMaskByteCount]));
            if (next.ChildFields.Any(f => f.Present && f.MetaKind is not 0 and not 2)) throw new InvalidDataException("Unsupported business progress layout.");
            regions.Add((next.ChildPayloadOffset + 4, next.EndOffset - 4, _ => []));
        }
        var before = ReadManagement();
        var expected = before.Select(s => s.Shop.Key == key ? s with { Level = level, HighLevel = newHigh, Score = null } : s).ToArray();
        byte[] candidate = RecordLists.RewriteRegions(blob, EditBuffer, regions);
        ManagementState[] after = [];
        Transact(candidate, [FactionBlock], (layout, _) =>
        {
            after = ManagementStates(candidate, layout);
            if (!after.SequenceEqual(expected)) throw new InvalidDataException("Business level verification failed.");
        });
        return after;
    }
}
