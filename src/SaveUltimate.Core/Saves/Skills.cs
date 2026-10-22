using System.Collections.ObjectModel;
using System.Text.Json;

namespace SaveUltimate.Core.Saves;

public sealed record SkillRoute(int Mode, ulong[][] Items, uint[][] Requires, uint[][] Unlocks);
public sealed record SkillLevel(SkillRoute[] Routes, uint[][] Grants, string[] Name, string[] Description, uint Icon);
public sealed record SkillDefinition(uint Key, uint Skill, int Default, int Owner, bool Watch, SkillLevel[] Levels, uint[]? Related);
public sealed record SkillGuide(int Parent, double[][] Points);
public sealed record SkillNode(int Id, uint? Key, double[] Xy, SkillGuide[] Guides, double[] Color);
public sealed record SkillCatalog(Dictionary<uint, SkillDefinition> Skills, Dictionary<int, SkillNode[]> Trees, Dictionary<uint, string[]> Names)
{
    public static SkillCatalog Shared { get; } = Load();
    private static SkillCatalog Load()
    {
        using var stream = typeof(SkillCatalog).Assembly.GetManifestResourceStream("Skills.json")!;
        return JsonSerializer.Deserialize<SkillCatalog>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
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

public sealed record SkillPoints(ulong Current, ulong Total);
public sealed record SkillPayment(ulong Spent, int Owner);
public sealed record SkillState(IReadOnlyDictionary<uint, uint> Levels, IReadOnlyDictionary<uint, SkillPayment> Paid,
    IReadOnlyDictionary<int, SkillPoints> Points, IReadOnlyDictionary<uint, uint> Follow, IReadOnlyDictionary<uint, ulong> Inventory)
{
    public uint Level(uint key) => Levels.GetValueOrDefault(key);
    public bool Observed(uint key) => SkillCatalog.Shared.Skills[key].Watch && Level(key) > 0 && Follow.GetValueOrDefault(key) is 2 or 7 or 15 or 22;
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
                        throw new InvalidDataException("스킬 기록 형식이 지원되지 않습니다.");
                    return field.Present ? field.Scalar(raw, f.Type, f.Size) : 0;
                }).ToArray();
                if (row[0] == 0 && fields[0].Type != "SkillPointOwnerType" || !values.TryAdd(checked((uint)row[0]), row[1..]))
                    throw new InvalidDataException("중복되거나 잘못된 스킬 기록입니다.");
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
            throw new InvalidDataException("지원되지 않는 스킬 소유자입니다.");
        for (int owner = 0; owner < 3; owner++)
        {
            points.TryAdd(owner, new(0, 0));
            ulong spent = paid.Values.Where(p => p.Owner == owner).Aggregate(0UL, (sum, p) => checked(sum + p.Spent));
            if (checked(points[owner].Current + spent) != points[owner].Total)
                throw new InvalidDataException("스킬 투자 기록과 포인트 합계가 일치하지 않습니다.");
        }
        if (points.Values.Select(p => p.Total).Distinct().Count() != 1) throw new InvalidDataException("캐릭터 간 스킬 포인트 합계가 다릅니다.");
        foreach (var (key, definition) in SkillCatalog.Shared.Skills)
        {
            if (levels.GetValueOrDefault(key) > definition.Levels.Length || paid.TryGetValue(key, out var payment) && payment.Owner != definition.Owner)
                throw new InvalidDataException("스킬 단계 또는 투자 소유자가 잘못되었습니다.");
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
        if (!SkillCatalog.Shared.Skills.TryGetValue(key, out var definition)) throw new InvalidOperationException("스킬을 선택하세요.");
        uint previous = state.Level(key);
        if (watch && (previous > 0 || !definition.Watch)) throw new InvalidOperationException("관찰 습득할 수 없는 스킬입니다.");
        if (target <= previous || target > definition.Levels.Length) throw new InvalidOperationException("현재보다 높은 유효한 단계만 적용할 수 있습니다.");
        var levels = new Dictionary<uint, uint>(state.Levels);
        var changes = new Dictionary<uint, uint>();
        var follow = new Dictionary<uint, uint>();
        var costs = new Dictionary<uint, ulong>();
        for (uint number = previous + 1; number <= target; number++)
        {
            bool observing = watch && number == 1;
            var level = definition.Levels[number - 1];
            var routes = level.Routes.Where(r => observing ? r.Mode is 1 or 2 : r.Mode == 3).ToArray();
            if (routes.Length != 1) throw new InvalidOperationException("게임에서 해금해야 하는 스킬입니다.");
            var route = routes[0];
            foreach (var requirement in route.Requires.Concat(route.Unlocks))
                if (levels.GetValueOrDefault(requirement[0]) < requirement[1])
                    throw new InvalidOperationException($"선행 조건: {SkillCatalog.Shared.Name(requirement[0])} {requirement[1]}단계");
            foreach (var cost in route.Items)
            {
                uint item = checked((uint)cost[0]);
                costs[item] = checked(costs.GetValueOrDefault(item) + cost[1]);
            }
            levels[key] = changes[key] = number;
            foreach (var grant in level.Grants)
                if (levels.GetValueOrDefault(grant[0]) < grant[1]) levels[grant[0]] = changes[grant[0]] = grant[1];
            if (observing) follow[key] = 7;
        }
        ulong investment = costs.GetValueOrDefault(75001u), bonds = Math.Min(state.Points[definition.Owner].Current, investment);
        ulong artifacts = investment - bonds;
        // The original editor funds missing artifacts; only existing bag-2 artifacts are consumed.
        costs[75001] = Math.Min(artifacts, state.Inventory.GetValueOrDefault(75001u));
        foreach (var (item, need) in costs)
            if (need > state.Inventory.GetValueOrDefault(item)) throw new InvalidOperationException($"재료 {item} 부족: {need}개 필요");
        var points = new Dictionary<int, SkillPoints>();
        foreach (var (owner, point) in state.Points)
        {
            ulong current = checked(point.Current + artifacts) - (owner == definition.Owner ? investment : 0);
            ulong total = checked(point.Total + artifacts);
            if (current > ushort.MaxValue || total > ushort.MaxValue) throw new InvalidOperationException("스킬 포인트 한도를 초과합니다.");
            points[owner] = new(current, total);
        }
        return new(key, target, definition.Owner, changes, follow, costs, artifacts, bonds, investment, points);
    }
}
