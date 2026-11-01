using System.Text.Json.Nodes;

namespace SaveUltimate.Core.Catalogs;

// Content the DLC adds: keys found only in a ".<dlc>" table layer and scene placements found only in a DLC scene file.
// Keys a DLC layer overrides (242 items, 70 quests in dlc_01) stay base content.
public static class DlcContentBuilder
{
    public static JsonObject Build(ClientSource source)
    {
        static JsonArray Json<T>(IEnumerable<T> values) => new(values.Select(v => JsonValue.Create(v)!).ToArray<JsonNode?>());
        JsonArray Added(string table)
        {
            var layers = source.Tables(table);
            var baseKeys = ClientTables.Records(layers.Where(l => l.Tag.Length == 0)).Keys.ToHashSet();
            return Json(ClientTables.Records(layers.Where(l => l.Tag.Length != 0)).Keys.Where(k => !baseKeys.Contains(k)).Order());
        }
        var types = ClientTables.Records(source.Tables("levelgimmicksceneobjectinfo"));
        var layers = source.Misc("levelgimmicksceneobjectinfo_misc")
            .Select(l => (l.Tag, Ids: MapCatalogBuilder.Scenes(l.Data, types.Values.First().Compact, types).Select(s => s.Id).ToHashSet())).ToArray();
        var baseScenes = layers.Where(l => l.Tag.Length == 0).SelectMany(l => l.Ids).ToHashSet();
        return new JsonObject
        {
            ["items"] = Added("iteminfo"), ["knowledge"] = Added("knowledgeinfo"), ["quests"] = Added("questinfo"), ["missions"] = Added("missioninfo"),
            ["scenes"] = Json(layers.Where(l => l.Tag.Length != 0).SelectMany(l => l.Ids).Where(id => !baseScenes.Contains(id)).Distinct().Order(StringComparer.Ordinal)),
            ["sourceSha256"] = source.Digests()
        };
    }
}
