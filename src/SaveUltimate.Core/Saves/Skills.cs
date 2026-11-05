using System.Collections.ObjectModel;

namespace SaveUltimate.Core.Saves;

public sealed record SkillRoute(int Mode, ulong[][] Items, uint[][] Requires, uint[][] Unlocks);
public sealed record SkillLevel(SkillRoute[] Routes, uint[][] Grants, string[] Name, string[] Description, uint Icon);
public sealed record SkillDefinition(uint Key, uint Skill, int Default, int Owner, bool Watch, SkillLevel[] Levels, uint[]? Related);
public sealed record SkillGuide(int Parent, double[][] Points);
public sealed record SkillNode(int Id, uint? Key, double[] Xy, SkillGuide[] Guides, double[] Color);
public sealed record SkillCatalog(Dictionary<uint, SkillDefinition> Skills, Dictionary<int, SkillNode[]> Trees, Dictionary<uint, string[]> Names)
{
    public static SkillCatalog Shared { get; } = Embedded.Load<SkillCatalog>("Skills.json");
    public string Name(uint key) => Names.TryGetValue(key, out var names) ? names[1] : key.ToString();
    public uint[] Related(uint key)
    {
        var result = new List<uint> { key };
        for (int i = 0; i < result.Count; i++)
            foreach (uint child in Skills[result[i]].Related ?? [])
                if (!result.Contains(child)) result.Add(child);
        return result.ToArray();
    }
}

// The game's KnowledgeLearnFromType in its own order (name table in CrimsonDesert.exe); saves keep one per knowledge as
// _learnFromTypeRaw. Investment writes none.
public enum LearnFrom : uint
{
    Dialog, DialogGlossary, Sequencer, Threat, Region, Cooking, Eavesdrop, FollowComplete, Login, ForceLockOn, Read, Tool, Zoom, Gimmick,
    Following, FollowManualLockOn, Battle, BattleComplete, Meditation, SkillPoint, LearnedPriorKnowledge, Shop, ResetKnowledgeLevel,
    RemoveKnowledge, MiniGame, None, Friendly
}

public sealed record SkillPoints(ulong Current, ulong Total);
public sealed record SkillPayment(ulong Spent, int Owner);
public sealed record SkillState(IReadOnlyDictionary<uint, uint> Levels, IReadOnlyDictionary<uint, SkillPayment> Paid,
    IReadOnlyDictionary<int, SkillPoints> Points, IReadOnlyDictionary<uint, uint> Follow, IReadOnlyDictionary<uint, ulong> Inventory)
{
    public uint Level(uint key) => Levels.GetValueOrDefault(key);
    // The record the game writes when observing: FollowManualLockOn for a demonstration in a region (route mode 2, e.g. Wind
    // Arrow, Pull Up), FollowComplete for a monster or boss in combat (mode 1, e.g. Drop Kick, Gale Slash).
    internal static uint ObservedFrom(SkillDefinition definition) =>
        (uint)(definition.Levels[0].Routes.Any(r => r.Mode == 2) ? LearnFrom.FollowManualLockOn : LearnFrom.FollowComplete);
    // Observed level 1 survives a skill reset, so these count too: Sequencer (Snap Kick and Escape learned in scripted fights)
    // and ResetKnowledgeLevel (a reset of an observed skill above level 1). LearnedPriorKnowledge or no record is lost on reset.
    public bool Observed(uint key) => SkillCatalog.Shared.Skills[key].Watch && Level(key) > 0 &&
        (LearnFrom)Follow.GetValueOrDefault(key, (uint)LearnFrom.None) is LearnFrom.Sequencer or LearnFrom.FollowComplete or LearnFrom.FollowManualLockOn or LearnFrom.ResetKnowledgeLevel;
    // A pending edit changes the level or the learn-from record (observing a learned skill changes only the record).
    public bool Changed(SkillState baseline, uint key) => Level(key) != baseline.Level(key) || Follow.GetValueOrDefault(key) != baseline.Follow.GetValueOrDefault(key);
    internal static Dictionary<uint, ulong> InventoryTotals(IEnumerable<InventoryItem> items)
    {
        var result = new Dictionary<uint, ulong>();
        foreach (var item in items.Where(i => i.Source == ItemSource.Inventory && i.Bag == 2))
            result[item.Key] = checked(result.GetValueOrDefault(item.Key) + item.Quantity);
        return result;
    }
    public SkillState WithInventory(IEnumerable<InventoryItem> items)
    {
        var totals = InventoryTotals(items);
        return totals.Count == Inventory.Count && totals.All(p => Inventory.TryGetValue(p.Key, out var quantity) && quantity == p.Value)
            ? this : this with { Inventory = new ReadOnlyDictionary<uint, ulong>(totals) };
    }

    internal static SkillState Read(byte[] raw, ParcLayout layout, IReadOnlyList<InventoryItem> items)
        => Read(raw, SaveRecords.Read(raw, layout, "KnowledgeSaveData"), items);

    internal static SkillState Read(byte[] raw, SaveObject block, IReadOnlyList<InventoryItem> items)
    {
        Dictionary<uint, ulong[]> ReadList(string list, string type, params (string Name, string Type, int Size)[] fields)
        {
            var values = new Dictionary<uint, ulong[]>();
            foreach (var element in block.Fields.Named(list).Elements(type))
            {
                var row = fields.Select(f =>
                {
                    var field = element.ChildFields.Named(f.Name);
                    if (field.TypeName != f.Type || field.MetaSize != f.Size || field.MetaKind is not 0 and not 2)
                        throw new InvalidDataException("Unsupported skill record format.");
                    return field.Present ? field.Scalar(raw, f.Type, f.Size) : 0;
                }).ToArray();
                if (row[0] == 0 && fields[0].Type != "SkillPointOwnerType" || !values.TryAdd(checked((uint)row[0]), row[1..]))
                    throw new InvalidDataException("Duplicate or invalid skill record.");
            }
            return values;
        }
        var levels = SaveDocument.KnowledgeLevels(raw, block);
        var paid = ReadList("_skillLearnSaveDataList", "SkillLearnElementSaveData", ("_knowledgeKey", "KnowledgeKey", 4),
            ("_usedArtifactCount", "TStackCount", 8), ("_skillPointOwnerType", "SkillPointOwnerType", 1))
            .ToDictionary(p => p.Key, p => new SkillPayment(p.Value[0], checked((int)p.Value[1])));
        var points = ReadList("_skillPointSaveDataList", "SkillPointSaveData", ("_skillPointOwnerType", "SkillPointOwnerType", 1),
            ("_currentSkillPoint", "TSkillPoint", 2), ("_elapsedSkillPoint", "TSkillPoint", 2))
            .ToDictionary(p => (int)p.Key, p => new SkillPoints(p.Value[0], p.Value[1]));
        var follow = ReadList("_learnedFollowLearnKnowledge", "FollowLearnElementSaveData", ("_knowledgeKey", "KnowledgeKey", 4),
            ("_learnFromTypeRaw", "uint8", 1)).ToDictionary(p => p.Key, p => (uint)p.Value[0]);
        if (points.Keys.Any(k => k is < 0 or > 2) || paid.Values.Any(p => p.Owner is < 0 or > 2))
            throw new InvalidDataException("Unsupported skill owner.");
        for (int owner = 0; owner < 3; owner++)
        {
            points.TryAdd(owner, new(0, 0));
            ulong spent = paid.Values.Where(p => p.Owner == owner).Aggregate(0UL, (sum, p) => checked(sum + p.Spent));
            if (checked(points[owner].Current + spent) != points[owner].Total)
                throw new InvalidDataException("Skill investments do not match the point total.");
        }
        if (points.Values.Select(p => p.Total).Distinct().Count() != 1) throw new InvalidDataException("Skill point totals differ between characters.");
        foreach (var (key, definition) in SkillCatalog.Shared.Skills)
        {
            if (levels.GetValueOrDefault(key) > definition.Levels.Length || paid.TryGetValue(key, out var payment) && payment.Owner != definition.Owner)
                throw new InvalidDataException("Invalid skill level or investment owner.");
            if (definition.Default != 0) levels[key] = Math.Max(1, levels.GetValueOrDefault(key));
        }
        bool changed;
        do
        {
            changed = false;
            foreach (var (key, definition) in SkillCatalog.Shared.Skills)
                foreach (var grant in definition.Levels.Take((int)levels.GetValueOrDefault(key)).SelectMany(l => l.Grants))
                    if (levels.GetValueOrDefault(grant[0]) < grant[1]) { levels[grant[0]] = grant[1]; changed = true; }
        } while (changed);
        return new(new ReadOnlyDictionary<uint, uint>(levels), new ReadOnlyDictionary<uint, SkillPayment>(paid),
            new ReadOnlyDictionary<int, SkillPoints>(points), new ReadOnlyDictionary<uint, uint>(follow),
            new ReadOnlyDictionary<uint, ulong>(InventoryTotals(items)));
    }
}

public sealed record SkillPlan(uint Key, uint Target, int Owner, Dictionary<uint, uint> Changes, Dictionary<uint, uint> Follow,
    Dictionary<uint, ulong> Costs, ulong Artifacts, ulong Bonds, ulong Investment, Dictionary<int, SkillPoints> Points)
{
    public static SkillPlan Create(SkillState state, uint key, uint target, bool watch = false)
    {
        if (!SkillCatalog.Shared.Skills.TryGetValue(key, out var definition)) throw new InvalidOperationException("Select a skill.");
        DlcContent.Refuse(DlcContent.Shared.Knowledge.Contains(key));
        uint previous = state.Level(key);
        if (watch && !definition.Watch) throw new InvalidOperationException("This skill cannot be learned by observation.");
        // A skill learned another way can still be observed (saves hold invested skills with an observation record); only the
        // observation is recorded. The save keeps one learn-from record for nearly every knowledge (skills granted with a prior one hold LearnedPriorKnowledge),
        // so a record that is not an observation is replaced, not treated as one.
        if (watch && previous > 0)
        {
            if (state.Observed(key)) throw new InvalidOperationException("This skill is already learned by observation.");
            return new(key, previous, definition.Owner, [], new() { [key] = SkillState.ObservedFrom(definition) }, [], 0, 0, 0, new(state.Points));
        }
        if (target <= previous || target > definition.Levels.Length) throw new InvalidOperationException("Only a valid level above the current one can be applied.");
        var levels = new Dictionary<uint, uint>(state.Levels);
        var changes = new Dictionary<uint, uint>();
        var follow = new Dictionary<uint, uint>();
        var costs = new Dictionary<uint, ulong>();
        for (uint number = previous + 1; number <= target; number++)
        {
            bool observing = watch && number == 1;
            var level = definition.Levels[number - 1];
            var routes = level.Routes.Where(r => observing ? r.Mode is 1 or 2 : r.Mode == 3).ToArray();
            if (routes.Length != 1) throw new InvalidOperationException("This skill must be unlocked in game.");
            var route = routes[0];
            foreach (var requirement in route.Requires.Concat(route.Unlocks))
                if (levels.GetValueOrDefault(requirement[0]) < requirement[1])
                    throw new InvalidOperationException($"Requires {SkillCatalog.Shared.Name(requirement[0])} level {requirement[1]}");
            foreach (var cost in route.Items)
            {
                uint item = checked((uint)cost[0]);
                costs[item] = checked(costs.GetValueOrDefault(item) + cost[1]);
            }
            levels[key] = changes[key] = number;
            foreach (var grant in level.Grants)
                if (levels.GetValueOrDefault(grant[0]) < grant[1]) levels[grant[0]] = changes[grant[0]] = grant[1];
            if (observing) follow[key] = SkillState.ObservedFrom(definition);
        }
        ulong investment = costs.GetValueOrDefault(75001u), bonds = Math.Min(state.Points[definition.Owner].Current, investment);
        ulong artifacts = investment - bonds;
        // The original editor funds missing artifacts; only existing bag-2 artifacts are consumed.
        costs[75001] = Math.Min(artifacts, state.Inventory.GetValueOrDefault(75001u));
        foreach (var (item, need) in costs)
            if (need > state.Inventory.GetValueOrDefault(item)) throw new InvalidOperationException($"Not enough material {item}: {need} needed");
        var points = new Dictionary<int, SkillPoints>();
        foreach (var (owner, point) in state.Points)
        {
            ulong current = checked(point.Current + artifacts) - (owner == definition.Owner ? investment : 0);
            ulong total = checked(point.Total + artifacts);
            if (current > ushort.MaxValue || total > ushort.MaxValue) throw new InvalidOperationException("Exceeds the skill point limit.");
            points[owner] = new(current, total);
        }
        return new(key, target, definition.Owner, changes, follow, costs, artifacts, bonds, investment, points);
    }
}
