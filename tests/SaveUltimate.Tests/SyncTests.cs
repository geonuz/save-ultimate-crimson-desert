using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.Core.Catalogs;
using SaveUltimate.Tools;

namespace SaveUltimate.Tests;

[TestClass]
public sealed class SyncTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public void ClientTablesRejectTruncationAndDuplicateLocalizedKeys()
    {
        Assert.Throws<InvalidDataException>(() => ClientTables.Records([1, 0], []));
        Assert.Throws<InvalidDataException>(() => ClientTables.Strings([0, 0]));
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory);
        for (int i = 0; i < 2; i++)
        {
            writer.Write(0ul); writer.Write(1u); writer.Write(Encoding.UTF8.GetBytes("x"));
            writer.Write(1u); writer.Write(Encoding.UTF8.GetBytes("y"));
        }
        writer.Write(2u);
        Assert.Throws<InvalidDataException>(() => ClientTables.Strings(memory.ToArray()));
        Assert.Throws<InvalidDataException>(() => ClientTables.Strings(memory.ToArray(), new HashSet<string> { "unrelated" }));
        var reader = new ClientReader(new byte[4]);
        Assert.Throws<InvalidDataException>(() => reader.Take(5));
    }

    private static (byte[] Header, byte[] Body) Table(bool old, params (uint Key, string Name, byte Payload)[] rows)
    {
        using var header = new MemoryStream(); using var h = new BinaryWriter(header);
        using var body = new MemoryStream(); using var b = new BinaryWriter(body);
        h.Write((ushort)rows.Length);
        foreach (var (key, name, payload) in rows)
        {
            h.Write(key); h.Write((uint)body.Length);
            if (old) b.Write(key);
            b.Write((uint)name.Length); b.Write(Encoding.UTF8.GetBytes(name));
            if (!old) b.Write(key);
            b.Write((byte)0); b.Write(payload);
        }
        return (header.ToArray(), body.ToArray());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ClientTablesRefuseTheOldRecordLayoutAndLetDlcReplaceBaseRecords()
    {
        var (h, b) = Table(true, (7, "Old", 1));
        Assert.Throws<InvalidDataException>(() => ClientTables.Records(h, b));
        var base_ = Table(false, (7, "Base", 1), (8, "Kept", 2));
        var dlc = Table(false, (7, "Dlc", 3), (9, "New", 4));
        var merged = ClientTables.Records([("", base_.Header, base_.Body), (".dlc_01", dlc.Header, dlc.Body)]);
        CollectionAssert.AreEquivalent(new uint[] { 7, 8, 9 }, merged.Keys.ToArray());
        Assert.AreEqual("Dlc", merged[7].Name);
        Assert.AreEqual(3, merged[7].Bytes.Span[merged[7].Prefix]);
        var (badHeader, badBody) = Table(false, (7, "Base", 1)); badBody[^2] = 1;
        Assert.Throws<InvalidDataException>(() => ClientTables.Records(badHeader, badBody));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void PublishingNewCacheMovesCompleteDirectory()
    {
        using var temp = new TempFolder();
        string stage = temp.Named("stage"), destination = temp.Named("cache");
        Directory.CreateDirectory(Path.Combine(stage, "images"));
        File.WriteAllText(Path.Combine(stage, "images", "sample.png"), "image");
        File.WriteAllText(Path.Combine(stage, "manifest.json"), "manifest");
        GameDataSync.Publish(stage, destination);
        Assert.IsFalse(Directory.Exists(stage));
        Assert.AreEqual("image", File.ReadAllText(Path.Combine(destination, "images", "sample.png")));
        Assert.AreEqual("manifest", File.ReadAllText(Path.Combine(destination, "manifest.json")));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void PublishingFailureRestoresPreviouslyReplacedFiles()
    {
        using var temp = new TempFolder();
        string stage = temp.Named("stage"), destination = temp.Named("cache");
        Directory.CreateDirectory(stage); Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "data.json"), "original");
        File.WriteAllText(Path.Combine(destination, "manifest.json"), "original manifest");
        File.WriteAllText(Path.Combine(stage, "data.json"), "replacement");
        File.WriteAllText(Path.Combine(stage, "manifest.json"), "replacement manifest");
        using (var locked = File.Open(Path.Combine(destination, "manifest.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => GameDataSync.Publish(stage, destination));
        Assert.AreEqual("original", File.ReadAllText(Path.Combine(destination, "data.json")));
        Assert.AreEqual("original manifest", File.ReadAllText(Path.Combine(destination, "manifest.json")));
    }

    [TestMethod]
    [TestCategory("Scenario")]
    [TestCategory("PrivateFixtures")]
    public void InstalledGameSyncMatchesShippedMetadataAndReusesValidatedCache()
    {
        string game = Environment.GetEnvironmentVariable("SAVEULTIMATE_GAME") ?? throw new AssertInconclusiveException("Set game.");
        using var temp = new TempFolder(); // The sync's lock file sits beside its cache, so the cache gets a folder of its own.
        string destination = temp.Named("cache");
        string resources = Path.Combine(AppContext.BaseDirectory, "resources");
        Assert.IsFalse(GameDataSync.IsCurrent(game, resources, destination));
        var result = GameDataSync.Run(game, resources, destination);
        Assert.IsFalse(result.Reused); Assert.IsTrue(result.Items > 0 && result.Images > 0);
        Assert.IsTrue(GameDataSync.IsCurrent(game, resources, destination));
        // refresh-items ships exactly what a sync of the installed game writes.
        var shipped = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(shipped.FullName, "resources/data/max_enchant_map.json"))) shipped = shipped.Parent ?? throw new AssertInconclusiveException("Repository resources not found.");
        JsonNode Load(string folder, string name) => JsonNode.Parse(File.ReadAllBytes(Path.Combine(folder, name)))!;
        string repository = Path.Combine(shipped.FullName, "resources");
        foreach (string name in new[] { "data/item_names.json", "data/max_enchant_map.json", "locale/names_ko.json", "locale/names_ja.json", "locale/names_ru.json" })
            Assert.IsTrue(JsonNode.DeepEquals(Load(repository, name), Load(destination, name)), name);
        foreach (string part in new[] { "items", "stats" })
            Assert.IsTrue(JsonNode.DeepEquals(Load(repository, "data/item_details.json")[part], Load(destination, "data/item_details.json")[part]), part);
        ItemRules.LoadCache(destination);
        var warm = GameDataSync.Run(game, resources, destination);
        Assert.IsTrue(warm.Reused); Assert.AreEqual(0, warm.Decoded); Assert.AreEqual(0, warm.Written);
        byte[] manifest = File.ReadAllBytes(Path.Combine(destination, "manifest.json"));
        Assert.Throws<OperationCanceledException>(() => GameDataSync.Run(game, resources, destination, cancel: new CancellationToken(true)));
        CollectionAssert.AreEqual(manifest, File.ReadAllBytes(Path.Combine(destination, "manifest.json")));
        string metadata = Path.Combine(destination, "data/max_enchant_map.json");
        byte[] valid = File.ReadAllBytes(metadata); File.WriteAllText(metadata, "{}");
        var repair = GameDataSync.Run(game, resources, destination);
        Assert.IsFalse(repair.Reused); CollectionAssert.AreEqual(valid, File.ReadAllBytes(metadata));
        Assert.AreEqual(0, repair.Decoded, "Metadata repair must preserve valid images.");
    }

    // Exercises every client-table reader against the installed game and catches shipped resources drifting from it.
    [TestMethod]
    [TestCategory("Scenario")]
    [TestCategory("PrivateFixtures")]
    public void ShippedCatalogsAreWhatTheBuildersReadFromTheInstalledGame()
    {
        string game = Environment.GetEnvironmentVariable("SAVEULTIMATE_GAME") ?? throw new AssertInconclusiveException("Set game.");
        var resources = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(resources.FullName, "resources/data/knowledge_ui.json"))) resources = resources.Parent ?? throw new AssertInconclusiveException("Repository resources not found.");
        JsonNode Load(string name) => JsonNode.Parse(File.ReadAllBytes(Path.Combine(resources.FullName, "resources", name)))!;
        var files = new SaveUltimate.Core.Archives.GameFiles(game);
        var outputs = KnowledgeCatalogBuilder.Build(new ClientSource(files), Load)
            .Concat(CharacterCatalogBuilder.Build(new ClientSource(files), Load))
            .Concat(MapCatalogBuilder.Build(new ClientSource(files), Load))
            .Concat(SkillCatalogBuilder.Build(new ClientSource(files), Load))
            .Append(new("data/dlc_content.json", DlcContentBuilder.Build(new ClientSource(files))))
            .Append(new("data/challenge_catalog.json", ChallengeCatalogBuilder.Build(new ClientSource(files), Load("data/challenge_catalog.json").AsObject())));
        foreach (var (name, node) in outputs) Assert.IsTrue(JsonNode.DeepEquals(Load(name), node), name);
    }
}
