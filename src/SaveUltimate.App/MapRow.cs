using SaveUltimate.Core.Saves;

namespace SaveUltimate.App;

/// <summary>One activatable map row: a place, a memory fragment or a bell.</summary>
public sealed class MapRow(MapEntry entry, string name, string category, bool done, string internalName = "")
{
    // Several places share one game name; their internal names tell them apart, as on the Knowledge page.
    public string InternalName { get; } = internalName.Replace("Knowledge_", "").Replace('_', ' ');
    public MapEntry Entry { get; } = entry;
    public string Name { get; } = name;
    public string Category { get; } = category;
    public bool Done { get; } = done;
    public string State => Tr.Text(Done ? "map.state_active" : "map.state_inactive");

    /// <summary>Whether the map tabs' filters show the row: state is the filter's index (all, active, inactive).</summary>
    public bool Shown(int state, string query, bool byCategory = false) => (state != 1 || Done) && (state != 2 || !Done) &&
        (query.Length == 0 || $"{(byCategory ? Category + " " : "")}{Name} {InternalName}".Contains(query, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>The group's caption as the game names it, shared by the map tabs.</summary>
    internal static string ActionName(MapAction a)
    {
        string name = a.LabelKey is not null ? Tr.Text(a.LabelKey) : GameText.Display(a.Names.GetValueOrDefault(Tr.Language, a.Names["en"]));
        return a.LabelRef is { Length: 2 } reference ? GameText.Display(Tr.Name(reference[0].GetString()!, reference[1].ToString(), name)) : name;
    }
    private static string KnowledgeName(uint key) => KnowledgeText.Find(key)?.Name is { Length: > 0 } name ? GameText.Display(name) : key.ToString();

    /// <summary>The rows of the chosen groups: a knowledge group gives one row per counted key, an icon group (bells) one per map record.</summary>
    internal static IEnumerable<MapRow> Build(MapState state, Func<MapAction, bool> include)
    {
        bool Learned(MapAction a, uint key) => state.Knowledge.GetValueOrDefault(key) >= a.TargetLevel(key);
        bool Found(MapAction a, string scene) => state.Discoveries.TryGetValue(scene, out var record) && (a.Pivots?.ContainsKey(scene) != true || record.Pivot is not null);
        for (int i = 0; i < MapAction.All.Length; i++)
        {
            var a = MapAction.All[i];
            if (!include(a)) continue;
            string category = ActionName(a);
            if (a.Mode == "icons")
                foreach (string scene in a.Scenes)
                {
                    string name = a.SceneNames?.TryGetValue(scene, out var names) == true ? names.GetValueOrDefault(Tr.Language, names["en"]) : category;
                    yield return new(new MapEntry(i, Scene: scene), name, category, Found(a, scene) && a.Keys.All(k => Learned(a, k)));
                }
            else
                foreach (uint key in a.CountKeys ?? a.Keys)
                    yield return new(new MapEntry(i, Key: key), KnowledgeName(key), category, Learned(a, key), KnowledgeText.Find(key)?.InternalName ?? "");
        }
    }
}
