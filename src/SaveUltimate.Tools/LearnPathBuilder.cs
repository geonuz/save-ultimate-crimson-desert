using System.Security.Cryptography;
using System.Text.Json.Nodes;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.Tools;

// Counts the learn-from record of every learned knowledge over saves played without an editor, one save per player.
// Editors leave two traces that are skipped: rows with field time 0 (editor insertions copy no time) and rows sharing one
// field time with more than BulkRows others (a learn-all edit; the game's largest batch in five players' saves is 177).
public static class LearnPathBuilder
{
    private const int BulkRows = 500;

    public static JsonObject Build(IEnumerable<string> saves, Action<string> report)
    {
        var counts = new SortedDictionary<uint, SortedDictionary<string, int>>();
        var digests = new JsonArray();
        foreach (string path in saves)
        {
            var document = SaveDocument.Open(path);
            var block = SaveRecords.Read(document.blob, document.Layout, "KnowledgeSaveData");
            var follow = document.ReadSkills().Follow;
            var rows = block.Fields.Named("_list").Elements("KnowledgeElementSaveData").Select(r => (
                Key: checked((uint)r.ChildFields.Named("_key").Scalar(document.blob, "KnowledgeKey", 4)),
                Level: r.ChildFields.Named("_level") is { Present: true } level ? level.Scalar(document.blob, "TLevel", 4) : 0,
                Time: r.ChildFields.Named("_learnedFieldTime") is { Present: true } time ? time.Scalar(document.blob, "uint64", 8) : 0))
                .Where(r => r.Level > 0).ToArray();
            var bulk = rows.GroupBy(r => r.Time).Where(g => g.Count() > BulkRows).Select(g => g.Key).ToHashSet();
            var natural = rows.Where(r => r.Time != 0 && !bulk.Contains(r.Time)).ToArray();
            foreach (var row in natural)
            {
                bool recorded = follow.TryGetValue(row.Key, out uint from);
                if (recorded && !Enum.IsDefined((LearnFrom)from)) throw new InvalidDataException($"Unknown learn path {from}.");
                string record = recorded ? ((LearnFrom)from).ToString() : LearnPathCatalog.NoRecord;
                var held = counts.TryGetValue(row.Key, out var c) ? c : counts[row.Key] = new(StringComparer.Ordinal);
                held[record] = held.GetValueOrDefault(record) + 1;
            }
            digests.Add(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
            report($"{Path.GetFileName(path)}: {natural.Length} of {rows.Length} learned rows counted ({rows.Length - natural.Length} with an editor's trace)");
        }
        return new JsonObject
        {
            ["source"] = "_learnedFollowLearnKnowledge of learned knowledge in saves played without an editor, one per player; rows with field time 0 or in a batch over 500 rows are skipped. \"-\" is no record.",
            ["saveSha256"] = digests,
            ["knowledge"] = new JsonObject(counts.Select(p => KeyValuePair.Create<string, JsonNode?>(p.Key.ToString(),
                new JsonObject(p.Value.Select(v => KeyValuePair.Create<string, JsonNode?>(v.Key, v.Value))))))
        };
    }
}
