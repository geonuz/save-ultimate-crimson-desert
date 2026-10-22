using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.Core.Catalogs;

namespace SaveUltimate.Tests;

[TestClass]
public sealed class SyncTests
{
    [TestMethod]
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

    [TestMethod]
    public void PublishingNewCacheMovesCompleteDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "sync-publish-" + Guid.NewGuid().ToString("N"));
        string stage = Path.Combine(root, "stage"), destination = Path.Combine(root, "cache");
        Directory.CreateDirectory(Path.Combine(stage, "images"));
        try
        {
            File.WriteAllText(Path.Combine(stage, "images", "sample.png"), "image");
            File.WriteAllText(Path.Combine(stage, "manifest.json"), "manifest");
            GameDataSync.Publish(stage, destination);
            Assert.IsFalse(Directory.Exists(stage));
            Assert.AreEqual("image", File.ReadAllText(Path.Combine(destination, "images", "sample.png")));
            Assert.AreEqual("manifest", File.ReadAllText(Path.Combine(destination, "manifest.json")));
        }
        finally
        {
            Assert.IsTrue(Path.GetFullPath(root).StartsWith(Path.Combine(Path.GetTempPath(), "sync-publish-"), StringComparison.OrdinalIgnoreCase));
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void PublishingFailureRestoresPreviouslyReplacedFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "sync-publish-" + Guid.NewGuid().ToString("N"));
        string stage = Path.Combine(root, "stage"), destination = Path.Combine(root, "cache");
        Directory.CreateDirectory(stage); Directory.CreateDirectory(destination);
        try
        {
            File.WriteAllText(Path.Combine(destination, "data.json"), "original");
            File.WriteAllText(Path.Combine(destination, "manifest.json"), "original manifest");
            File.WriteAllText(Path.Combine(stage, "data.json"), "replacement");
            File.WriteAllText(Path.Combine(stage, "manifest.json"), "replacement manifest");
            using (var locked = File.Open(Path.Combine(destination, "manifest.json"), FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.Throws<IOException>(() => GameDataSync.Publish(stage, destination));
            Assert.AreEqual("original", File.ReadAllText(Path.Combine(destination, "data.json")));
            Assert.AreEqual("original manifest", File.ReadAllText(Path.Combine(destination, "manifest.json")));
        }
        finally
        {
            Assert.IsTrue(Path.GetFullPath(root).StartsWith(Path.Combine(Path.GetTempPath(), "sync-publish-"), StringComparison.OrdinalIgnoreCase));
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    [TestCategory("PrivateFixtures")]
    public void InstalledGameSyncMatchesPythonMetadataAndReusesValidatedCache()
    {
        string game = Environment.GetEnvironmentVariable("SAVEULTIMATE_GAME") ?? throw new AssertInconclusiveException("Set game.");
        string root = Environment.GetEnvironmentVariable("SAVEULTIMATE_SYNC_REFERENCE") ?? throw new AssertInconclusiveException("Set sync reference.");
        string destination = Path.Combine(root, Environment.GetEnvironmentVariable("SAVEULTIMATE_SYNC_DEST") ?? "csharp"), reference = Path.Combine(root, "python");
        string resources = Path.Combine(AppContext.BaseDirectory, "resources");
        var result = GameDataSync.Run(game, resources, destination);
        File.WriteAllText(Path.Combine(root, result.Reused ? "csharp-reuse.json" : "csharp-cold.json"),
            JsonSerializer.Serialize(new { Result = result, PeakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64, PeakPaged = Process.GetCurrentProcess().PeakPagedMemorySize64 }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (string name in new[] { "data/item_names.json", "data/item_details.json", "data/max_enchant_map.json", "locale/names_ko.json", "locale/names_ja.json", "locale/names_ru.json" })
        {
            var expected = JsonNode.Parse(File.ReadAllBytes(Path.Combine(reference, name)));
            var actual = JsonNode.Parse(File.ReadAllBytes(Path.Combine(destination, name)));
            Assert.IsTrue(JsonNode.DeepEquals(expected, actual), name);
        }
        using var python = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(reference, "manifest.json")));
        var expectedImages = python.RootElement.GetProperty("images").GetProperty("records").EnumerateObject().Select(p => p.Name).Order().ToArray();
        var images = Directory.GetFiles(Path.Combine(destination, "images"), "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(Path.Combine(destination, "images"), p).Replace('\\', '/')).Order().ToArray();
        CollectionAssert.AreEqual(expectedImages, images);
        foreach (string name in images.Where((_, i) => i % 100 == 0 || i == images.Length - 1))
        {
            using var expected = new ImageMagick.MagickImage(Path.Combine(reference, "images", name));
            using var actual = new ImageMagick.MagickImage(Path.Combine(destination, "images", name));
            Assert.IsTrue(Math.Abs((long)expected.Width - actual.Width) <= 1 && Math.Abs((long)expected.Height - actual.Height) <= 1, name);
        }
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
}
