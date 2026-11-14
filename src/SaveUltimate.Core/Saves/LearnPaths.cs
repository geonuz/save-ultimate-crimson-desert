namespace SaveUltimate.Core.Saves;

/// <summary>
/// Learn-from records held in saves played without an editor (Tools learn-paths): per knowledge, how many players held each
/// record, <see cref="NoRecord"/> for none. The game records the event that first taught a knowledge, so one knowledge can
/// hold different records in different saves (met in the story or shared by a friend, bought or found); an edit writes the
/// record most players hold. Knowledge no player was seen learning falls back on the kinds of client events that teach it
/// (learn_sources.json, Tools LearnSourceBuilder) under a fixed priority.
/// </summary>
public sealed record LearnPathCatalog(Dictionary<uint, Dictionary<string, int>> Knowledge)
{
    public const string NoRecord = "-";
    /// <summary>The record each kind of client event writes, highest priority first. Measured on the observed table: an item's
    /// own knowledge is None even when the item can also be read, inspect entries are Read or Zoom, regions Region, type 7
    /// region nodes mostly none, character knowledge mostly None (met in the story), meditation only where nothing else
    /// teaches it.</summary>
    internal static readonly (string Source, string Path)[] Priority =
    [
        ("item", "None"), ("read", "Read"), ("zoom", "Zoom"), ("region", "Region"), ("node", NoRecord), ("character", "None"),
        ("meditation", "Meditation")
    ];
    public static LearnPathCatalog Shared { get; } = Load();
    private Dictionary<uint, Dictionary<string, int>> Groups { get; init; } = [];
    internal Dictionary<uint, string[]> Sources { get; init; } = [];

    private sealed record SourceCatalog(Dictionary<uint, string[]> Knowledge);

    private static LearnPathCatalog Load()
    {
        var catalog = Embedded.Load<LearnPathCatalog>("LearnPaths.json");
        if (catalog.Knowledge.Count == 0 || catalog.Knowledge.Values.Any(c => c.Count == 0 || c.Any(p => p.Value < 1 ||
            p.Key != NoRecord && (!Enum.TryParse<LearnFrom>(p.Key, out var from) || !Enum.IsDefined(from) || from.ToString() != p.Key))))
            throw new InvalidDataException("Invalid learn path catalog.");
        var sources = Embedded.Load<SourceCatalog>("LearnSources.json").Knowledge;
        if (sources.Count == 0 || sources.Values.Any(s => s.Length == 0 || s.Any(k => !Priority.Any(p => p.Source == k))))
            throw new InvalidDataException("Invalid learn source catalog.");
        // A knowledge no player has learned takes the records of its journal groups.
        var groups = new Dictionary<uint, Dictionary<string, int>>();
        foreach (var (key, counts) in catalog.Knowledge)
            foreach (uint group in KnowledgeCatalog.Shared.ItemPaths.GetValueOrDefault(key, []))
            {
                var sum = groups.TryGetValue(group, out var g) ? g : groups[group] = [];
                foreach (var (path, count) in counts) sum[path] = sum.GetValueOrDefault(path) + count;
            }
        return catalog with { Groups = groups, Sources = sources };
    }

    /// <summary>The record to write when an edit teaches <paramref name="key"/>, from the first of: the one most players hold
    /// for it; the one most held in its own journal group; the client's fixed priority (<see cref="Priority"/>); the one most
    /// held in the nearest ancestor group with any. Null writes none. Skill knowledge is null: its record follows how the
    /// skill was learned (<see cref="SkillState.Observe"/>).</summary>
    public uint? Choose(uint key)
    {
        if (SkillCatalog.Shared.Skills.ContainsKey(key)) return null;
        uint[] groups = KnowledgeCatalog.Shared.ItemPaths.GetValueOrDefault(key, []);
        string? path = Held(Knowledge.GetValueOrDefault(key))
            ?? (groups.Length > 0 ? Held(Groups.GetValueOrDefault(groups[^1])) : null)
            ?? Client(key)
            ?? groups.Reverse().Skip(1).Select(g => Held(Groups.GetValueOrDefault(g))).FirstOrDefault(p => p is not null);
        return path is null or NoRecord ? null : (uint)Enum.Parse<LearnFrom>(path);
    }

    /// <summary>The record the client's events give <paramref name="key"/> under the fixed priority, or null when no event
    /// the client lists teaches it.</summary>
    internal string? Client(uint key) => Sources.TryGetValue(key, out var kinds) ? Priority.First(p => kinds.Contains(p.Source)).Path : null;

    private static string? Held(Dictionary<string, int>? counts)
        => counts?.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).First().Key;

    /// <summary>For an edit that knows which kind of event teaches <paramref name="key"/> (reading an item: Read or Zoom): the
    /// one of <paramref name="among"/> most players hold for it, or null when none holds any of them.</summary>
    public uint? Among(uint key, params LearnFrom[] among)
    {
        string? held = Knowledge.GetValueOrDefault(key, []).Where(p => p.Key != NoRecord && among.Contains(Enum.Parse<LearnFrom>(p.Key)))
            .OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key).FirstOrDefault();
        return held is null ? null : (uint)Enum.Parse<LearnFrom>(held);
    }
}
