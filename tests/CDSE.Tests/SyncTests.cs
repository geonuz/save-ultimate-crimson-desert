using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using CDSE.Core.Catalogs;
using CDSE.Core.Saves;
using CDSE.Tools;

namespace CDSE.Tests;

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
        // 2.06 names the records in the header; before, each record opened with its name and the header held key and offset only.
        h.Write((ushort)rows.Length);
        uint row = 0;
        foreach (var (key, name, payload) in rows)
        {
            long start = body.Length;
            if (old) { h.Write(key); h.Write((uint)start); b.Write((uint)name.Length); b.Write(Encoding.UTF8.GetBytes(name)); b.Write(key); }
            else { b.Write(key); b.Write(row++); }
            b.Write((byte)0); b.Write(payload);
            if (!old) { h.Write(key); h.Write((uint)name.Length); h.Write(Encoding.UTF8.GetBytes(name)); h.Write((uint)start); h.Write((uint)(body.Length - start)); }
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
    [TestCategory("Unit")]
    public void PublishingRemovesPicturesTheManifestNoLongerLists()
    {
        using var temp = new TempFolder();
        string destination = temp.Named("cache");
        foreach (string name in new[] { "images/knowledge/1.png", "images/knowledge/1.webp", "data/item_names.json" })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(destination, name))!);
            File.WriteAllText(Path.Combine(destination, name), "x");
        }
        GameDataSync.RemoveUnlisted(destination, new Dictionary<string, CacheFile> { ["images/knowledge/1.webp"] = new("", 1, 0) });
        Assert.IsFalse(File.Exists(Path.Combine(destination, "images/knowledge/1.png")));
        Assert.IsTrue(File.Exists(Path.Combine(destination, "images/knowledge/1.webp")));
        Assert.IsTrue(File.Exists(Path.Combine(destination, "data/item_names.json")), "Only pictures are cleaned up.");
    }

    [TestMethod]
    [TestCategory("Game")]
    public void InstalledGameSyncMatchesShippedMetadataAndReusesValidatedCache()
    {
        string game = Environment.GetEnvironmentVariable("CDSE_GAME") ?? throw new AssertInconclusiveException("Set game.");
        using var temp = new TempFolder(); // The sync's lock file sits beside its cache, so the cache gets a folder of its own.
        string destination = temp.Named("cache");
        string resources = Path.Combine(AppContext.BaseDirectory, "resources");
        Assert.IsFalse(GameDataSync.IsCurrent(game, resources, destination));
        var result = GameDataSync.Run(game, resources, destination);
        Assert.IsFalse(result.Reused); Assert.IsTrue(result.Items > 0 && result.Images > 0);
        Assert.IsTrue(GameDataSync.IsCurrent(game, resources, destination));
        // refresh-items ships exactly what a sync of the installed game writes.
        JsonNode Load(string folder, string name) => JsonNode.Parse(File.ReadAllBytes(Path.Combine(folder, name)))!;
        string repository = TestFiles.Resources;
        foreach (string name in new[] { "data/item_names.json", "data/max_enchant_map.json" }.Concat(new[] { "ko", "ja", "ru", "fr", "de", "es", "pt" }.Select(c => $"locale/names_{c}.json")))
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

    // The rotation keeps these lists by hand; a game patch that adds parts shows here.
    [TestMethod]
    [TestCategory("Game")]
    public void HouseRotationKnowsEveryPartOfTheInstalledGame()
    {
        string game = Environment.GetEnvironmentVariable("CDSE_GAME") ?? throw new AssertInconclusiveException("Set game.");
        var parts = ClientTables.Records16(new CDSE.Core.Archives.GameFiles(game).Tables("housingobjectinfo")).ToDictionary(p => (int)p.Key, p =>
        {
            // 2.06 put one more byte before the placement (+23 → +24).
            byte[] fields = p.Value.Reader.Take(25).ToArray();
            return (Category: fields[0], Place: fields[24]);
        });
        Assert.AreEqual(SaveDocument.LastHousingObject, parts.Keys.Max());
        CollectionAssert.AreEquivalent(SaveDocument.Walls.ToArray(), parts.Where(p => p.Value.Category == 2).Select(p => p.Key).ToArray());
        CollectionAssert.AreEquivalent(SaveDocument.Pillars.ToArray(), parts.Where(p => p.Value.Category == 3).Select(p => p.Key).ToArray());
        CollectionAssert.AreEquivalent(parts.Keys.Where(SaveDocument.Hangs).ToArray(), parts.Where(p => p.Value.Place == 2).Select(p => p.Key).ToArray());
    }

    // Exercises every client-table reader against the installed game and catches shipped resources drifting from it.
    [TestMethod]
    [TestCategory("Game")]
    public void ShippedCatalogsAreWhatTheBuildersReadFromTheInstalledGame()
    {
        string game = Environment.GetEnvironmentVariable("CDSE_GAME") ?? throw new AssertInconclusiveException("Set game.");
        JsonNode Load(string name) => JsonNode.Parse(File.ReadAllBytes(Path.Combine(TestFiles.Resources, name)))!;
        var files = new CDSE.Core.Archives.GameFiles(game);
        var outputs = KnowledgeCatalogBuilder.Build(new ClientSource(files), Load)
            .Concat(CharacterCatalogBuilder.Build(new ClientSource(files), Load))
            .Concat(MapCatalogBuilder.Build(new ClientSource(files), Load))
            .Concat(SkillCatalogBuilder.Build(new ClientSource(files), Load))
            .Append(new("data/dlc_content.json", DlcContentBuilder.Build(new ClientSource(files))))
            .Append(new("data/collection_catalog.json", CollectionCatalogBuilder.Build(new ClientSource(files))))
            .Append(new("data/gear_catalog.json", GearCatalogBuilder.Build(files)))
            .Append(new("data/learn_sources.json", LearnSourceBuilder.Build(new ClientSource(files))))
            .Append(new("data/challenge_catalog.json", ChallengeCatalogBuilder.Build(new ClientSource(files), Load("data/challenge_catalog.json").AsObject())));
        foreach (var (name, node) in outputs) Assert.IsTrue(JsonNode.DeepEquals(Load(name), node), name);
    }
}
