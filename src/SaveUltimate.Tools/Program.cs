using System.CommandLine;
using System.Security.Cryptography;
using System.Text.Json;
using SaveUltimate.Core.Archives;
using SaveUltimate.Core.Catalogs;
using SaveUltimate.Core.Saves;
using SaveUltimate.Tools;

var root = new RootCommand("SaveUltimate C# tools — local files only");
// Match the shipped resources: readable non-ASCII text, indented unless compact; never overwrites.
void WriteJson(string path, System.Text.Json.Nodes.JsonNode node, bool compact = false)
{
    var options = new JsonSerializerOptions { WriteIndented = !compact, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    using var stream = new FileStream(path, FileMode.CreateNew);
    stream.Write(System.Text.Encoding.UTF8.GetBytes(node.ToJsonString(options) + "\n"));
}
var inspect = new Command("inspect", "Verify a SAVE and print its block inventory");
var savePath = new Argument<string>("save");
inspect.Arguments.Add(savePath);
inspect.SetAction(result =>
{
    string path = result.GetValue(savePath)!;
    var save = SaveContainer.Check(path);
    var layout = ParcLayout.Parse(save.Blob);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        save.Version, Bytes = save.Blob.Length, Sha256 = Convert.ToHexString(SHA256.HashData(save.Blob)),
        Blocks = layout.Blocks
    }, new JsonSerializerOptions { WriteIndented = true }));
});
root.Subcommands.Add(inspect);
var extract = new Command("extract", "Extract one exact virtual path from a PAZ group");
var group = new Argument<string>("group");
var virtualPath = new Argument<string>("path");
var output = new Argument<string>("output");
extract.Arguments.Add(group); extract.Arguments.Add(virtualPath); extract.Arguments.Add(output);
extract.SetAction(result =>
{
    var bytes = new GameArchive(result.GetValue(group)!).Extract(result.GetValue(virtualPath)!);
    using var stream = new FileStream(result.GetValue(output)!, FileMode.CreateNew);
    stream.Write(bytes);
    Console.WriteLine($"{bytes.Length} bytes; SHA256 {Convert.ToHexString(SHA256.HashData(bytes))}");
});
root.Subcommands.Add(extract);
var texture = new Command("texture", "Convert DDS to PNG or lossless WebP (maximum 256 × 256)");
var inputImage = new Argument<string>("dds");
var outputImage = new Argument<string>("output");
texture.Arguments.Add(inputImage); texture.Arguments.Add(outputImage);
texture.SetAction(result =>
{
    string target = result.GetValue(outputImage)!;
    string extension = Path.GetExtension(target).ToLowerInvariant();
    if (extension is not ".webp" and not ".png") throw new ArgumentException("Output must be .png or .webp");
    var bytes = TextureImage.Convert(File.ReadAllBytes(result.GetValue(inputImage)!), 256, 256, extension == ".webp");
    using var stream = new FileStream(target, FileMode.CreateNew);
    stream.Write(bytes);
});
root.Subcommands.Add(texture);
var refresh = new Command("refresh-items", "Sync item data from an installed game and write baseline resource candidates");
var gameFolder = new Argument<string>("game");
var resourceFolder = new Argument<string>("resources");
var refreshOutput = new Argument<string>("output");
refresh.Arguments.Add(gameFolder); refresh.Arguments.Add(resourceFolder); refresh.Arguments.Add(refreshOutput);
refresh.SetAction(result =>
{
    string target = Path.GetFullPath(result.GetValue(refreshOutput)!);
    if (Path.Exists(target)) throw new IOException("The output folder must be new.");
    string cache = Path.Combine(target, "game_data");
    var sync = GameDataSync.Run(result.GetValue(gameFolder)!, result.GetValue(resourceFolder)!, cache);
    foreach (string name in new[] { "data/item_names.json", "data/item_details.json", "data/max_enchant_map.json", "locale/names_ko.json", "locale/names_ja.json", "locale/names_ru.json" })
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(Path.Combine(cache, name)))!;
        if (name == "data/item_details.json")
            node = new System.Text.Json.Nodes.JsonObject { ["source"] = "Crimson Desert Enhanced installed client (synchronized)", ["items"] = node["items"]!.DeepClone(), ["stats"] = node["stats"]!.DeepClone() };
        WriteJson(Path.Combine(target, "resources", name), node, name == "data/item_details.json");
    }
    Console.WriteLine($"{sync.Items} items ({sync.Added} added, {sync.Changed} changed), {sync.Images} images, {sync.Unresolved} unresolved -> {target}");
});
root.Subcommands.Add(refresh);
var challenges = new Command("catalog-challenges", "Build the challenge catalog from an installed game, reusing reviewed objectives of the previous catalog");
var challengeGame = new Argument<string>("game");
var previousCatalog = new Argument<string>("previous");
var challengeOutput = new Argument<string>("output");
challenges.Arguments.Add(challengeGame); challenges.Arguments.Add(previousCatalog); challenges.Arguments.Add(challengeOutput);
challenges.SetAction(result =>
{
    var previous = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(result.GetValue(previousCatalog)!))!.AsObject();
    var catalog = ChallengeCatalogBuilder.Build(new ClientSource(new GameFiles(result.GetValue(challengeGame)!)), previous);
    WriteJson(result.GetValue(challengeOutput)!, catalog);
    Console.WriteLine($"{catalog["groups"]!.AsArray().Count} challenge categories, {catalog["missions"]!.AsObject().Count} mission definitions");
});
root.Subcommands.Add(challenges);
var completionCommand = new Command("challenge-completion", "Carry reviewed completion shapes to a new challenge catalog and learn from saves played without an editor: new shapes, shapes that replace series inference, used tags");
var newCatalogPath = new Argument<string>("catalog");
var oldCatalogPath = new Argument<string>("previous-catalog");
var oldCompletionPath = new Argument<string>("previous-completion");
var completionOutput = new Argument<string>("output");
var completedSaves = new Option<string[]>("--save") { Description = "Save in which the challenges to add are completed", AllowMultipleArgumentsPerToken = true };
foreach (var argument in new[] { newCatalogPath, oldCatalogPath, oldCompletionPath, completionOutput }) completionCommand.Arguments.Add(argument);
completionCommand.Options.Add(completedSaves);
completionCommand.SetAction(result =>
{
    System.Text.Json.Nodes.JsonObject Load(Argument<string> a) => System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(result.GetValue(a)!))!.AsObject();
    var catalog = Load(newCatalogPath); var oldCatalog = Load(oldCatalogPath); var old = Load(oldCompletionPath);
    var missions = catalog["missions"]!.AsObject(); var oldMissions = oldCatalog["missions"]!.AsObject();
    var shapes = new Dictionary<string, System.Text.Json.Nodes.JsonNode>();
    // A reviewed shape stays valid while the mission keeps its quest and children.
    foreach (var (key, mission) in missions)
        if (old["missions"]![key] is { } shape && oldMissions[key] is { } before &&
            before["quest"]!.GetValue<uint>() == mission!["quest"]!.GetValue<uint>() && System.Text.Json.Nodes.JsonNode.DeepEquals(before["children"], mission["children"]))
            shapes[key] = shape.DeepClone();
    var quests = old["quests"]!.AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<byte>());
    var supplements = new System.Text.Json.Nodes.JsonArray((old["supplementSha256"]?.AsArray() ?? []).Select(s => s!.DeepClone()).ToArray());
    var catalogMissions = missions.ToDictionary(p => p.Key, p => p.Value!["children"]!.AsArray().Select(c => c!.GetValue<uint>().ToString()).ToArray());
    string[] Walk(string key) => [key, .. catalogMissions[key].SelectMany(Walk)];
    var inferredRoots = (old["inferredRoots"]?.AsArray() ?? []).Select(r => r!.ToString()).Where(missions.ContainsKey).ToArray();
    var borrowed = inferredRoots.SelectMany(Walk).ToHashSet();
    var usedTags = (old["usedTags"]?.AsArray() ?? []).Select(t => t!.GetValue<uint>()).ToHashSet();
    foreach (string path in result.GetValue(completedSaves) ?? [])
    {
        var document = SaveDocument.Open(path); var progress = document.ReadChallenges(); var saved = document.ReadUsedTags(); int added = 0;
        foreach (var tags in saved.Values) usedTags.UnionWith(tags);
        foreach (var (key, _) in missions)
            if ((!shapes.ContainsKey(key) || borrowed.Contains(key)) && progress.Mission.GetValueOrDefault(uint.Parse(key)) is { State: 5, Completed: not null } row)
            {
                var shape = new System.Text.Json.Nodes.JsonObject { ["_completeCount"] = row.Count, ["_uiState"] = row.Ui, ["branched"] = row.Branched is not null };
                if (saved.TryGetValue(uint.Parse(key), out var tags) && tags.Length > 0 && !tags.Contains(0xEAC5E173)) shape["emptyTag"] = false;
                shapes[key] = shape; borrowed.Remove(key);
                added++;
            }
        // A quest's final state is only meaningful once every challenge mission of that quest is complete.
        foreach (var group in catalog["groups"]!.AsArray())
            foreach (var quest in group!["quests"]!.AsArray())
            {
                uint questKey = quest!.GetValue<uint>();
                bool done = missions.Where(p => p.Value!["quest"]!.GetValue<uint>() == questKey).All(p => progress.Mission.GetValueOrDefault(uint.Parse(p.Key))?.State == 5);
                if (done && !quests.ContainsKey(quest.ToString()) && progress.Quest.GetValueOrDefault(questKey)?.State is byte state and (2 or 5))
                    quests[quest.ToString()] = state;
            }
        supplements.Add(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        Console.WriteLine($"{Path.GetFileName(path)}: {added} completion shapes added");
    }
    bool Covered(string key) => shapes.ContainsKey(key) && catalogMissions[key].All(Covered);
    var roots = catalog["groups"]!.AsArray().SelectMany(g => g!["quests"]!.AsArray())
        .SelectMany(q => catalog["quests"]![q!.ToString()]!["missions"]!.AsArray()).Select(m => m!.GetValue<uint>()).ToArray();
    // A challenge series (same quest, or the same internal name up to its trailing number) shares one completion shape
    // per checkpoint position: on the 374 reviewed 2.03 challenges this predicts 252 of 260 exactly, the rest differing
    // only in _uiState/_branchedTime display fields. An uncovered root borrows the shape only when every covered
    // sibling with the same checkpoint tree agrees.
    string Tree(string key) => "(" + string.Concat(catalogMissions[key].Select(Tree)) + ")";
    // The empty tag varies between missions of one series, so a borrowed shape does not take it.
    System.Text.Json.Nodes.JsonObject Shape(string key) { var shape = shapes[key].DeepClone().AsObject(); shape.Remove("emptyTag"); return shape; }
    string Series(string key) => System.Text.RegularExpressions.Regex.Replace(missions[key]!["internalName"]!.GetValue<string>(), @"_(\d+|[IVX]+)$", "");
    string Quest(string key) => missions[key]!["quest"]!.ToString();
    var covered = roots.Select(r => r.ToString()).Where(Covered).ToArray();
    // Carried shapes keep their origin, so a later run still lists challenges whose shape was inferred.
    var inferred = new System.Text.Json.Nodes.JsonArray(inferredRoots.Where(r => Walk(r).Any(borrowed.Contains)).Select(r => (System.Text.Json.Nodes.JsonNode)uint.Parse(r)).ToArray());
    foreach (string root in roots.Select(r => r.ToString()).Where(r => !Covered(r)).ToArray())
    {
        var siblings = covered.Where(c => (Quest(c) == Quest(root) || Series(c) == Series(root)) && Tree(c) == Tree(root)).ToArray();
        var patterns = siblings.Select(c => string.Join("|", Walk(c).Select(k => Shape(k).ToJsonString()))).Distinct().ToArray();
        if (patterns.Length != 1) continue;
        foreach (var (target, source) in Walk(root).Zip(Walk(siblings[0])))
            shapes.TryAdd(target, Shape(source));
        inferred.Add(uint.Parse(root));
    }
    Console.WriteLine($"{inferred.Count} challenges take the completion shape of their series");
    var completable = roots.Where(r => Covered(r.ToString())).ToArray();
    var output = new System.Text.Json.Nodes.JsonObject
    {
        ["sourceSha256"] = old["sourceSha256"]!.DeepClone(),
        ["clientSources"] = catalog["sourceSha256"]!.DeepClone(),
        ["roots"] = new System.Text.Json.Nodes.JsonArray(completable.Select(r => (System.Text.Json.Nodes.JsonNode)r).ToArray()),
        ["missions"] = new System.Text.Json.Nodes.JsonObject(shapes.Where(p => missions.ContainsKey(p.Key)).Select(p => KeyValuePair.Create<string, System.Text.Json.Nodes.JsonNode?>(p.Key, p.Value))),
        ["quests"] = new System.Text.Json.Nodes.JsonObject(quests.Where(p => catalog["quests"]![p.Key] is not null).Select(p => KeyValuePair.Create<string, System.Text.Json.Nodes.JsonNode?>(p.Key, p.Value)))
    };
    output["usedTags"] = new System.Text.Json.Nodes.JsonArray(usedTags.Order().Select(t => (System.Text.Json.Nodes.JsonNode)t).ToArray());
    if (supplements.Count > 0) output["supplementSha256"] = supplements;
    if (inferred.Count > 0) output["inferredRoots"] = inferred;
    WriteJson(result.GetValue(completionOutput)!, output);
    Console.WriteLine($"{completable.Length} of {roots.Length} challenges completable; {roots.Length - completable.Length} need a completed reference save");
});
root.Subcommands.Add(completionCommand);
var catalogs = new Command("catalog-knowledge", "Build the knowledge, fishing, skill, character, NPC, map, collection and learn-source catalogs on top of the shipped resources");
var catalogGame = new Argument<string>("game");
var catalogResources = new Argument<string>("resources");
var catalogOutput = new Argument<string>("output");
catalogs.Arguments.Add(catalogGame); catalogs.Arguments.Add(catalogResources); catalogs.Arguments.Add(catalogOutput);
catalogs.SetAction(result =>
{
    string target = Path.GetFullPath(result.GetValue(catalogOutput)!), resources = result.GetValue(catalogResources)!;
    if (Path.Exists(target)) throw new IOException("The output folder must be new.");
    var game = new GameFiles(result.GetValue(catalogGame)!);
    // Later builders start from earlier outputs, so the name packs collect every section.
    var outputs = new Dictionary<string, System.Text.Json.Nodes.JsonNode>();
    System.Text.Json.Nodes.JsonNode Load(string name) => outputs.TryGetValue(name, out var built) ? built : System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(Path.Combine(resources, name)))!;
    foreach (var (name, node) in KnowledgeCatalogBuilder.Build(new ClientSource(game), Load)) outputs[name] = node;
    foreach (var (name, node) in CharacterCatalogBuilder.Build(new ClientSource(game), Load)) outputs[name] = node;
    foreach (var (name, node) in MapCatalogBuilder.Build(new ClientSource(game), Load)) outputs[name] = node;
    foreach (var (name, node) in SkillCatalogBuilder.Build(new ClientSource(game), Load)) outputs[name] = node;
    outputs["data/dlc_content.json"] = DlcContentBuilder.Build(new ClientSource(game));
    outputs["data/collection_catalog.json"] = CollectionCatalogBuilder.Build(new ClientSource(game));
    outputs["data/learn_sources.json"] = LearnSourceBuilder.Build(new ClientSource(game));
    foreach (var (name, node) in outputs) WriteJson(Path.Combine(target, name), node, name is "data/skills.json" or "data/map_actions.json" or "data/map_catalog.json" or "data/dlc_content.json" or "data/collection_catalog.json" or "data/learn_sources.json");
    Console.WriteLine($"{outputs["data/knowledge_ui.json"]["itemPaths"]!.AsObject().Count} visible knowledge entries, {outputs["data/fishing_catalog.json"]["fish"]!.AsArray().Count} fish, " +
        $"{outputs["data/skills.json"]["skills"]!.AsObject().Count} skill definitions, {outputs["data/character_names.json"].AsObject().Count} characters, {outputs["data/map_catalog.json"]["scenes"]!.AsArray().Count} map scenes -> {target}");
});
root.Subcommands.Add(catalogs);
var learnPaths = new Command("learn-paths", "Count the learn-from record of every learned knowledge in saves played without an editor, one save per player");
var learnOutput = new Argument<string>("output");
var learnSaves = new Argument<string[]>("saves") { Arity = ArgumentArity.OneOrMore };
learnPaths.Arguments.Add(learnOutput); learnPaths.Arguments.Add(learnSaves);
learnPaths.SetAction(result =>
{
    var catalog = LearnPathBuilder.Build(result.GetValue(learnSaves)!, Console.WriteLine);
    WriteJson(result.GetValue(learnOutput)!, catalog, compact: true);
    Console.WriteLine($"{catalog["knowledge"]!.AsObject().Count} knowledge entries");
});
root.Subcommands.Add(learnPaths);
return root.Parse(args).Invoke();
