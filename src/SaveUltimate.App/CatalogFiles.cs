using System.IO;
using SaveUltimate.Core.Catalogs;

namespace SaveUltimate.App;

internal static class CatalogFiles
{
    private static bool enabled;
    public static bool Synced => enabled;
    public static string Resources => Path.Combine(AppContext.BaseDirectory, "resources");
    public static string Cache => Path.Combine(AppContext.BaseDirectory, "game_data");
    public static string Find(string relative)
    {
        string path = Path.Combine(Cache, relative);
        return enabled && File.Exists(path) ? path : Path.Combine(Resources, relative);
    }
    public static void Reload()
    {
        enabled = File.Exists(Path.Combine(Cache, "manifest.json"));
        try
        {
            if (enabled) ItemRules.LoadCache(Cache);
            Tr.ReloadNames(); ItemCatalog.Reset(); ItemImages.Clear(); GameText.Reset(); KnowledgeText.Reset(); CharacterText.Reset();
        }
        catch { enabled = false; ItemRules.Reset(); Tr.ReloadNames(); ItemCatalog.Reset(); throw; }
    }
}
